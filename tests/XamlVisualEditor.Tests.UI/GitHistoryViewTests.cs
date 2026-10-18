using System;
using System.Globalization;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Fonts.Inter;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.SourceControl.UI.Behaviors;
using XamlVisualEditor.SourceControl.UI.Controls;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitHistoryViewTests
{
    [AvaloniaFact]
    public async Task Every_button_has_an_icon_a_tooltip_and_a_name_and_toolbar_buttons_have_a_label()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 150);
        fixture.Git.DiffText = "@@ -1 +1 @@\n-a\n+b\n@@ -5 +5 @@\n-c\n+d\n@@ -9 +9 @@\n-e\n+f\n";
        GitHistoryViewModel history = fixture.History;
        fixture.History.Show(new GitHistoryScope("src/a.cs"));
        Dispatcher.UIThread.RunJobs();
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = history };
        using ThemedWindow window = ThemedWindow.Show(view);
        GitCommitDetailsViewModel details = history.Details;
        // In the middle hunk, both hunk buttons are enabled.
        details.CurrentHunkIndex = 1;
        window.Pump();

        (ICommand Command, bool HasLabel)[] expected =
        [
            (history.ShowWholeRepositoryCommand, true),
            (history.SearchCommand, true),
            (history.ClearFilterCommand, true),
            (history.RefreshCommand, true),
            (history.LoadMoreCommand, true),
            (details.ToggleDiffCommand, false),
            (details.PreviousHunkCommand, true),
            (details.NextHunkCommand, true)
        ];
        Button[] buttons = view.GetVisualDescendants().OfType<Button>().Where(button => button.Command is not null).ToArray();
        Assert.Equal(expected.Length, buttons.Length);
        foreach ((ICommand command, bool hasLabel) in expected)
        {
            Button button = Assert.Single(buttons, candidate => ReferenceEquals(candidate.Command, command));
            Assert.True(button.IsEffectivelyVisible);
            PathIcon icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
            Assert.NotNull(icon.Data);
            Assert.False(icon.IsSet(TemplatedControl.ForegroundProperty));
            Assert.Equal(hasLabel, button.GetVisualDescendants().OfType<TextBlock>().Any(text => !string.IsNullOrWhiteSpace(text.Text)));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.IsType<string>(ToolTip.GetTip(button));
            Assert.True(command.CanExecute(null));
        }
    }

    [AvaloniaFact]
    public async Task Selecting_a_merge_commit_shows_its_header_files_and_the_diff_of_the_first_file()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 2);
        GitCommitInfo merge = ScriptedHistoryGitService.CreateCommit(3, "Anna", 1.ToString("x40"), 2.ToString("x40")) with
        {
            Refs = ["HEAD -> main", "tag: v1"]
        };
        fixture.Git.Commits.Insert(0, merge);
        await fixture.History.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view);

        DataGrid commits = Find<DataGrid>(view, "CommitGrid");
        DataGridRow[] rows = commits.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Contains(rows[0].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "HEAD -> main, tag: v1");
        commits.SelectedItem = fixture.History.Commits[0];
        window.Pump();

        Assert.Equal("Commit 3", Find<SelectableTextBlock>(view, "CommitTitle").Text);
        TextBlock mergeNote = Find<TextBlock>(view, "MergeNote");
        Assert.True(mergeNote.IsEffectivelyVisible);
        Assert.Equal(string.Format(GitStrings.MergeComparisonFormat, "0000001"), mergeNote.Text);
        Assert.Equal("Commit 3\n\nBody 3", Find<TextBox>(view, "CommitMessage").Text);
        DataGrid files = Find<DataGrid>(view, "FileGrid");
        Assert.Same(fixture.History.Details.Files[0], files.SelectedItem);
        UnifiedDiffEditor editor = Find<UnifiedDiffEditor>(view, "DiffEditor");
        Assert.Contains("+eins", editor.Text, StringComparison.Ordinal);
        Assert.Equal(2, editor.HunkCount);

        await fixture.History.Details.NextHunkCommand.Execute().ToTask();
        await fixture.History.Details.NextHunkCommand.Execute().ToTask();
        window.Pump();
        Assert.Equal(1, editor.CurrentHunkIndex);
        Assert.Equal("@@ -9 +9 @@", editor.Document.GetText(editor.Document.GetLineByNumber(editor.TextArea.Caret.Line)));

        // A reload replaces the rows of the bound grid, keeps the selection and does not read the details again.
        int commitReads = fixture.Git.CommitReads;
        await fixture.History.RefreshCommand.Execute().ToTask();
        window.Pump();
        Assert.Same(fixture.History.Commits[0], fixture.History.SelectedCommit);
        Assert.Same(fixture.History.Commits[0], commits.SelectedItem);
        Assert.Equal(commitReads, fixture.Git.CommitReads);
        Assert.True(fixture.History.Details.HasCommit);
    }

    [AvaloniaFact]
    public async Task Context_menus_have_icons_and_copy_the_commit_id_and_the_file_path()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 2);
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[1];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = history };
        using ThemedWindow window = ThemedWindow.Show(view);

        // The commit menu copies the id, starts a branch, creates a tag, cherry-picks and reverts; the file menu copies
        // the path and shows the file history.
        foreach ((string gridName, int itemCount) in new[] { ("CommitGrid", 5), ("FileGrid", 2) })
        {
            DataGrid grid = Find<DataGrid>(view, gridName);
            ContextMenu menu = grid.ContextMenu!;
            menu.Open(grid);
            window.Pump();
            MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
            Assert.Equal(itemCount, items.Length);
            Assert.All(items, item => Assert.IsType<PathIcon>(item.Icon));
            Assert.All(items, item => Assert.True(item.IsEnabled));
            menu.Close();
        }

        IClipboard clipboard = Assert.IsAssignableFrom<IClipboard>(window.Window.Clipboard);
        await history.CopyCommitIdCommand.Execute().ToTask();
        Assert.Equal(history.Commits[1].Sha, await clipboard.TryGetTextAsync());
        await history.Details.CopyPathCommand.Execute().ToTask();
        Assert.Equal("a.txt", await clipboard.TryGetTextAsync());
    }

    [AvaloniaFact]
    public async Task The_diff_pane_closes_and_opens_again()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        fixture.History.SelectedCommit = fixture.History.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view);
        Grid details = Find<Grid>(view, "DetailsGrid");
        Assert.True(details.ColumnDefinitions[2].Width.Value > 0);

        await fixture.History.Details.ToggleDiffCommand.Execute().ToTask();
        window.Pump();
        Assert.Equal(0, details.ColumnDefinitions[2].Width.Value);
        Assert.False(Find<UnifiedDiffEditor>(view, "DiffEditor").IsEffectivelyVisible);

        await fixture.History.Details.ToggleDiffCommand.Execute().ToTask();
        window.Pump();
        Assert.Equal(new GridLength(3, GridUnitType.Star), details.ColumnDefinitions[2].Width);
        Assert.True(Find<UnifiedDiffEditor>(view, "DiffEditor").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task The_view_marks_its_view_model_as_shown_while_it_is_attached()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1, shown: false);
        Assert.Empty(fixture.Git.Queries);

        GitHistoryView view = new() { DataContext = fixture.History };
        using (ThemedWindow.Show(view))
        {
            Assert.True(fixture.History.IsShown);
            Assert.Single(fixture.Git.Queries);
            Assert.Equal(GitStrings.HistoryScopeRepository, Find<TextBlock>(view, "ScopeText").Text);
        }

        Assert.False(fixture.History.IsShown);
    }

    [AvaloniaTheory]
    [InlineData(230)]
    [InlineData(740)]
    public async Task Filter_bar_commit_list_load_more_and_details_do_not_overlap_at_the_height_of_a_bottom_panel_and_when_tall(double height)
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 150);
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = history };
        // The row heights of the application, for which the minimum heights of the commit list are set.
        using ThemedWindow window = ThemedWindow.Show(view, 1136, height, appDensity: true);

        DataGrid grid = Find<DataGrid>(view, "CommitGrid");
        Button loadMore = Find<Button>(view, "LoadMoreButton");
        Rect filterBar = BoundsIn(view, Find<WrapPanel>(view, "FilterBar"));
        Rect commits = BoundsIn(view, grid);
        Rect more = BoundsIn(view, loadMore);
        Rect details = BoundsIn(view, Find<GitCommitDetailsView>(view, "CommitDetails"));

        Assert.True(loadMore.IsEffectivelyVisible);
        Assert.True(grid.Bounds.Y >= 0, $"The commit list starts at {grid.Bounds.Y} in its row.");
        Assert.True(commits.Top >= filterBar.Bottom, $"The commit list starts at {commits.Top}, above the end of the filter bar at {filterBar.Bottom}.");
        Assert.True(more.Top >= commits.Bottom, $"Load More starts at {more.Top}, above the end of the commit list at {commits.Bottom}.");
        Assert.True(details.Top >= more.Bottom, $"The details start at {details.Top}, above the end of Load More at {more.Bottom}.");
        Assert.True(details.Bottom <= view.Bounds.Height + 0.5, $"The details end at {details.Bottom}, below the view of height {view.Bounds.Height}.");
        DataGridRow first = grid.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsVisible && row.DataContext == history.Commits[0]);
        Rect firstRow = BoundsIn(view, first);
        Assert.True(firstRow.Top >= commits.Top && firstRow.Bottom <= commits.Bottom, $"The first row {firstRow} is outside the commit list {commits}.");

        // The commit and date columns show their whole text.
        TextBlock[] cells = first.GetVisualDescendants().OfType<TextBlock>().ToArray();
        AssertNotClipped(Assert.Single(cells, text => text.Text == history.Commits[0].ShortSha));
        AssertNotClipped(Assert.Single(cells, text => text.Text == history.Commits[0].DateText));
        AssertDetailsDoNotOverlap();

        // With the splitter moved to the minimum of the commit list, the details show a whole file row and the diff.
        Grid layout = Find<Grid>(view, "HistoryLayout");
        layout.RowDefinitions[0].Height = new GridLength(layout.RowDefinitions[0].MinHeight);
        window.Pump();
        AssertDetailsDoNotOverlap();
        Rect files = BoundsIn(view, Find<DataGrid>(view, "FileGrid"));
        Assert.Contains(
            Find<DataGrid>(view, "FileGrid").GetVisualDescendants().OfType<DataGridRow>(),
            row => row.IsVisible && row.Bounds.Height > 0 && BoundsIn(view, row).Top >= files.Top && BoundsIn(view, row).Bottom <= files.Bottom + 0.5);
        Assert.True(Find<UnifiedDiffEditor>(view, "DiffEditor").Bounds.Height > 0, "The diff editor has no height.");

        void AssertDetailsDoNotOverlap()
        {
            Rect area = BoundsIn(view, Find<GitCommitDetailsView>(view, "CommitDetails"));
            Rect header = BoundsIn(view, Find<ScrollViewer>(view, "HeaderScroller"));
            Rect fileList = BoundsIn(view, Find<DataGrid>(view, "FileGrid"));
            Rect diff = BoundsIn(view, Find<UnifiedDiffEditor>(view, "DiffEditor"));
            Assert.True(
                header.Top >= area.Top && header.Bottom <= fileList.Top + 0.5 && fileList.Bottom <= area.Bottom + 0.5
                && diff.Top >= area.Top && diff.Bottom <= area.Bottom + 0.5,
                $"Details {area}: header {header}, file list {fileList}, diff {diff}.");
        }
    }

    [AvaloniaFact]
    public async Task The_commit_header_stays_at_the_top_when_commits_are_selected_and_the_diff_is_moved_through()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 3);
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view, 1136, 230, appDensity: true);
        DataGrid commits = Find<DataGrid>(view, "CommitGrid");

        // Each selection shows a new diff, which moves the caret of the diff editor.
        foreach (GitCommitRowViewModel commit in fixture.History.Commits.Take(2))
        {
            commits.SelectedItem = commit;
            window.Pump();
            Assert.Equal(commit.Commit.Subject, Find<SelectableTextBlock>(view, "CommitTitle").Text);
            AssertHeaderAtTop();
        }

        // At this height the header scrolls; the change buttons and the caret scroll only the diff.
        ScrollViewer header = Find<ScrollViewer>(view, "HeaderScroller");
        UnifiedDiffEditor editor = Find<UnifiedDiffEditor>(view, "DiffEditor");
        Assert.True(header.Extent.Height > header.Viewport.Height, $"The header of height {header.Extent.Height} fits its viewport of {header.Viewport.Height}.");
        await fixture.History.Details.NextHunkCommand.Execute().ToTask();
        window.Pump();
        AssertHeaderAtTop();
        editor.TextArea.Caret.Line = editor.Document.LineCount;
        window.Pump();
        AssertHeaderAtTop();

        // A header the user scrolled starts at the top again for another commit.
        header.Offset = new Vector(0, header.Extent.Height);
        window.Pump();
        Assert.True(header.Offset.Y > 0, "The header did not scroll.");
        commits.SelectedItem = fixture.History.Commits[2];
        window.Pump();
        Assert.Equal(fixture.History.Commits[2].Commit.Subject, Find<SelectableTextBlock>(view, "CommitTitle").Text);
        AssertHeaderAtTop();

        void AssertHeaderAtTop()
        {
            SelectableTextBlock title = Find<SelectableTextBlock>(view, "CommitTitle");
            Assert.All(title.GetVisualAncestors().OfType<ScrollViewer>(), scroller => Assert.Equal(0, scroller.Offset.Y));
            Assert.Equal(BoundsIn(view, Find<GitCommitDetailsView>(view, "CommitDetails")).Top, BoundsIn(view, title).Top, 3);
        }
    }

    [AvaloniaFact]
    public async Task A_dispatcher_job_that_runs_while_the_test_waits_formats_in_the_culture_of_the_scope()
    {
        using CultureScope culture = new("fr-FR");

        string jobCulture = await Dispatcher.UIThread.InvokeAsync(() => CultureInfo.CurrentCulture.Name);

        Assert.Equal("fr-FR", jobCulture);
    }

    [AvaloniaFact]
    public async Task Whole_repository_and_the_change_buttons_are_disabled_when_they_cannot_change_anything()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = history };
        using ThemedWindow window = ThemedWindow.Show(view);
        Button wholeRepository = Find<Button>(view, "ShowWholeRepositoryButton");
        Button previous = Find<Button>(view, "PreviousChangeButton");
        Button next = Find<Button>(view, "NextChangeButton");

        // Two hunks and none selected yet: only the next change is possible. The commands alone disable the buttons.
        Assert.Equal(2, Find<UnifiedDiffEditor>(view, "DiffEditor").HunkCount);
        Assert.Equal(2, history.Details.HunkCount);
        Assert.Equal((false, false, true), (wholeRepository.IsEffectivelyEnabled, previous.IsEffectivelyEnabled, next.IsEffectivelyEnabled));
        Assert.All(new[] { wholeRepository, previous, next }, button => Assert.False(button.IsSet(Avalonia.Input.InputElement.IsEnabledProperty)));

        history.Show(new GitHistoryScope("a.txt"));
        window.Pump();
        Assert.True(wholeRepository.IsEffectivelyEnabled);

        // At the last hunk, only the previous change is possible.
        history.Details.CurrentHunkIndex = 1;
        window.Pump();
        Assert.Equal((true, false), (previous.IsEffectivelyEnabled, next.IsEffectivelyEnabled));
    }

    [AvaloniaFact]
    public async Task The_hunk_count_comes_from_the_editor_which_counts_only_the_shown_lines()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        System.Text.StringBuilder diff = new("diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1,25000 @@\n");
        for (int line = 1; line <= 25_000; line++)
        {
            diff.Append("+line ").Append(line).Append('\n');
        }

        fixture.Git.DiffText = diff.Append("@@ -30000 +30000 @@\n-x\n+y\n").ToString();
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitHistoryView view = new() { DataContext = history };
        using ThemedWindow window = ThemedWindow.Show(view);

        UnifiedDiffEditor editor = Find<UnifiedDiffEditor>(view, "DiffEditor");
        Assert.True(editor.IsTruncated);
        Assert.Equal((1, 1), (editor.HunkCount, history.Details.HunkCount));
        string notice = editor.Document.GetText(editor.Document.Lines[^1]);
        Assert.Equal(
            string.Format(GitStrings.DiffTooLargeFormat, GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, 4),
            notice);
        Assert.Equal((false, true), (Find<Button>(view, "PreviousChangeButton").IsEffectivelyEnabled, Find<Button>(view, "NextChangeButton").IsEffectivelyEnabled));
    }

    [AvaloniaTheory]
    [InlineData("en-US", 230)]
    [InlineData("en-US", 740)]
    [InlineData("de-DE", 230)]
    [InlineData("de-DE", 740)]
    public async Task The_date_column_shows_the_whole_date_when_the_commits_arrive_after_the_layout(string cultureName, double height)
    {
        using CultureScope culture = new(cultureName);
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 0);
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view, 1136, height, appDensity: true);

        // The commits arrive after the grid was laid out, as from git in the running application.
        fixture.Git.Commits.Add(ScriptedHistoryGitService.CreateCommit(1, "Anna") with { AuthorDate = new DateTimeOffset(2026, 10, 17, 23, 37, 51, TimeSpan.Zero) });
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(2, "Bob") with { AuthorDate = new DateTimeOffset(2026, 12, 28, 22, 58, 58, TimeSpan.Zero) });
        await fixture.History.RefreshCommand.Execute().ToTask();
        window.Pump();

        DataGrid grid = Find<DataGrid>(view, "CommitGrid");
        DataGridRow[] rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
        Assert.Equal(2, rows.Length);
        foreach (DataGridRow row in rows)
        {
            GitCommitRowViewModel commit = (GitCommitRowViewModel)row.DataContext!;
            Assert.Equal(commit.Commit.AuthorDate.ToLocalTime().ToString("G", CultureInfo.GetCultureInfo(cultureName)), commit.DateText);
            AssertNotClipped(Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == commit.DateText));
        }

        // Message is the only column that takes the remaining width; the date column fits every sample date.
        Assert.Equal(new[] { false, true, false, false }, grid.Columns.Select(column => column.Width.IsStar));
        TextBlock date = Assert.Single(rows[0].GetVisualDescendants().OfType<TextBlock>(), text => text.Text == ((GitCommitRowViewModel)rows[0].DataContext!).DateText);
        Assert.True(grid.Columns[3].MinWidth >= Math.Ceiling(ColumnWidthSamples.MeasureWidest(date, GitDateSamples.Texts, widestDigits: true) + date.Margin.Left + date.Margin.Right));
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task The_date_column_fits_the_widest_dates_in_the_font_of_the_application_before_they_are_listed(string cultureName)
    {
        using CultureScope culture = new(cultureName);
        using InterFontScope inter = new();
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 0);
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view, 1136, 900, appDensity: true);
        // A narrow date only: the column width comes from the samples, not from the listed dates.
        fixture.Git.Commits.Add(CommitAt(1, new DateTime(2011, 1, 1, 1, 11, 11)));
        await fixture.History.RefreshCommand.Execute().ToTask();
        window.Pump();

        DataGrid grid = Find<DataGrid>(view, "CommitGrid");
        TextBlock date = Assert.Single(grid.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == fixture.History.Commits[0].DateText);
        // The cells use Inter, as in the application, whose digits and day periods differ in width.
        Typeface typeface = new(date.FontFamily, date.FontStyle, date.FontWeight, date.FontStretch);
        Assert.Equal("Inter", typeface.GlyphTypeface.FamilyName);
        Assert.True(Width(date, "1") < Width(date, "4"), "The digits of the cell font have the same width.");
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{cultureName}: widest sample {ColumnWidthSamples.MeasureWidest(date, GitDateSamples.Texts, widestDigits: true):F2}, date text {date.Bounds.Width:F2}, column {grid.Columns[3].ActualWidth:F2}.");

        DateTime[] widest =
        [
            new(2034, 10, 30, 10, 44, 44),
            new(2044, 10, 30, 10, 44, 44),
            new(2028, 12, 28, 8, 8, 8),
            new(2088, 8, 28, 8, 8, 8),
            new(2034, 3, 3, 4, 44, 44),
            new(2000, 10, 30, 10, 0, 0),
            new(2026, 10, 17, 23, 37, 51)
        ];
        foreach (DateTime value in widest)
        {
            string text = value.ToString("G", CultureInfo.GetCultureInfo(cultureName));
            double needed = Width(date, text);
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"{cultureName} \"{text}\" needs {needed:F2}; the date text has {date.Bounds.Width:F2} in a column of {grid.Columns[3].ActualWidth:F2}.");
            Assert.True(date.Bounds.Width >= needed, $"\"{text}\" needs {needed} but the date text has {date.Bounds.Width}.");
        }

        // Listed, they show whole.
        fixture.Git.Commits.InsertRange(0, widest.Select((value, index) => CommitAt(index + 2, value)));
        await fixture.History.RefreshCommand.Execute().ToTask();
        window.Pump();
        DataGridRow[] rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
        Assert.Equal(widest.Length + 1, rows.Length);
        foreach (DataGridRow row in rows)
        {
            string dateText = ((GitCommitRowViewModel)row.DataContext!).DateText;
            AssertNotClipped(Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == dateText));
        }

        static GitCommitInfo CommitAt(int number, DateTime local) =>
            ScriptedHistoryGitService.CreateCommit(number, "Anna") with { AuthorDate = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)) };

        static double Width(TextBlock text, string value)
        {
            TextBlock probe = new() { Text = value, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
            probe.Measure(Size.Infinity);
            return probe.DesiredSize.Width;
        }
    }

    [AvaloniaFact]
    public async Task A_copied_commit_row_holds_every_cell_with_the_message_and_the_date()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 2);
        GitHistoryView view = new() { DataContext = fixture.History };
        using ThemedWindow window = ThemedWindow.Show(view);
        GitCommitRowViewModel row = fixture.History.Commits[1];

        string[] cells = await CopyRowAsync(window, Find<DataGrid>(view, "CommitGrid"), row);

        Assert.Equal(new[] { row.ShortSha, row.Subject, row.AuthorName, row.DateText }, cells);
    }

    /// <summary>
    /// Selects <paramref name="item"/> in <paramref name="grid"/>, presses Ctrl+C in the grid and returns the cells of
    /// the copied row.
    /// </summary>
    internal static async Task<string[]> CopyRowAsync(ThemedWindow window, DataGrid grid, object item)
    {
        IClipboard clipboard = Assert.IsAssignableFrom<IClipboard>(window.Window.Clipboard);
        await clipboard.ClearAsync();
        grid.SelectedItem = item;
        Assert.True(grid.Focus());
        window.Pump();

        window.Window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        window.Pump();

        string? text = await clipboard.TryGetTextAsync();
        Assert.NotNull(text);
        // The grid separates the cells with tabs and quotes each cell.
        return text.TrimEnd('\r', '\n')
            .Split('\t')
            .Select(cell => cell.Length >= 2 && cell[0] == '"' && cell[^1] == '"' ? cell[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal) : cell)
            .ToArray();
    }

    private static Rect BoundsIn(Visual root, Visual control)
    {
        Point topLeft = control.TranslatePoint(default, root) ?? throw new InvalidOperationException("The control is not below the root.");
        return new Rect(topLeft, control.Bounds.Size);
    }

    /// <summary>Asserts that a text block got at least the width its text needs.</summary>
    internal static void AssertNotClipped(TextBlock text)
    {
        TextBlock probe = new() { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
        probe.Measure(Size.Infinity);
        Assert.True(
            text.Bounds.Width >= probe.DesiredSize.Width - 0.5,
            $"\"{text.Text}\" is {text.Bounds.Width} wide but needs {probe.DesiredSize.Width}.");

        // A text block wider than its grid cell is cut at the edge of the cell.
        if (text.FindAncestorOfType<DataGridCell>() is { } cell)
        {
            double right = text.TranslatePoint(new Point(text.Bounds.Width, 0), cell)!.Value.X;
            Assert.True(right <= cell.Bounds.Width + 0.5, $"\"{text.Text}\" ends at {right} in a cell of width {cell.Bounds.Width}.");
        }
    }

    /// <summary>Finds a named control also inside the nested commit details view, which has its own name scope.</summary>
    private static T Find<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private sealed class Fixture : IDisposable
    {
        private Fixture(ScriptedHistoryGitService git)
        {
            Git = git;
            Session = new GitSession(new GitServiceSet(git) { History = git }, new FixedWorkspaceInfo());
            History = new GitHistoryViewModel(Session, (_, _) => Task.CompletedTask);
        }

        public ScriptedHistoryGitService Git { get; }

        public GitSession Session { get; }

        public GitHistoryViewModel History { get; }

        public static async Task<Fixture> CreateAsync(int commitCount, bool shown = true)
        {
            ScriptedHistoryGitService git = new();
            git.Commits.AddRange(ScriptedHistoryGitService.CreateCommits(commitCount));
            Fixture fixture = new(git);
            await fixture.Session.RefreshAsync();
            fixture.History.IsShown = shown;
            Dispatcher.UIThread.RunJobs();
            return fixture;
        }

        public void Dispose()
        {
            History.Dispose();
            Session.Dispose();
        }
    }
}

/// <summary>
/// A window with the Fluent, DataGrid and AvaloniaEdit themes of App.axaml and, on request, its density styles, which
/// give the compact grids their cell font and padding; the headless test application has no styles of its own.
/// Disposing closes the window and removes the styles.
/// </summary>
internal sealed class ThemedWindow : IDisposable
{
    private readonly IStyle[] _styles;

    private ThemedWindow(Control content, double width, double height, bool appDensity)
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        _styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") },
            new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") },
            .. appDensity ? new IStyle[] { new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorDensity.axaml") } } : []
        ];
        Application.Current!.Styles.AddRange(_styles);
        Window = new Window { Content = content, Width = width, Height = height };
    }

    public Window Window { get; }

    public static ThemedWindow Show(Control content, double width = 1400, double height = 900, bool appDensity = false)
    {
        ThemedWindow window = new(content, width, height, appDensity);
        window.Window.Show();
        window.Pump();
        return window;
    }

    /// <summary>Runs the queued jobs and layout passes, in which tab contents, rows and editors are created.</summary>
    public void Pump()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        Dispatcher.UIThread.RunJobs();
    }

    public void Dispose()
    {
        Window.Close();
        foreach (IStyle style in _styles)
        {
            Application.Current!.Styles.Remove(style);
        }
    }
}

/// <summary>
/// Adds the Inter fonts, which the application adds with <c>WithInterFont</c>, to the font manager, so the Fluent
/// theme font resolves to Inter instead of the font of the headless platform; disposing removes them.
/// </summary>
internal sealed class InterFontScope : IDisposable
{
    private readonly InterFontCollection _fonts = new();

    public InterFontScope() => FontManager.Current.AddFontCollection(_fonts);

    public void Dispose() => FontManager.Current.RemoveFontCollection(_fonts.Key);
}

/// <summary>
/// Sets the culture and UI culture of the test and the default ones of all threads; disposing restores the previous
/// ones. The current culture flows only with the async flow of the test, and Avalonia runs a dispatcher job in the
/// culture of the UI thread when the job starts, so a job that runs while the test waits, such as applying a load of
/// a view model, gets the default culture. The application sets the default UI culture as well. The tests of this
/// assembly run one at a time.
/// </summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _uiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo? _defaultCulture = CultureInfo.DefaultThreadCurrentCulture;
    private readonly CultureInfo? _defaultUiCulture = CultureInfo.DefaultThreadCurrentUICulture;

    public CultureScope(string name)
    {
        CultureInfo.CurrentCulture = CultureInfo.DefaultThreadCurrentCulture = CultureInfo.GetCultureInfo(name);
        CultureInfo.CurrentUICulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo(name);
    }

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _culture;
        CultureInfo.CurrentUICulture = _uiCulture;
        CultureInfo.DefaultThreadCurrentCulture = _defaultCulture;
        CultureInfo.DefaultThreadCurrentUICulture = _defaultUiCulture;
    }
}
