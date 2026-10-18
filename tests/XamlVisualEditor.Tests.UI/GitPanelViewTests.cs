using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Controls;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.SourceControl.UI.Behaviors;
using XamlVisualEditor.SourceControl.UI.Controls;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.SourceControl.UI.Themes;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using GitChangeKind = XamlVisualEditor.Core.GitChangeKind;
using GitConflictKind = XamlVisualEditor.Core.GitConflictKind;
using GitDiff = XamlVisualEditor.Core.GitDiff;
using GitDiffRequest = XamlVisualEditor.Core.GitDiffRequest;
using GitFileChange = XamlVisualEditor.Core.GitFileChange;
using GitRepositoryStatus = XamlVisualEditor.Core.GitRepositoryStatus;
using GitServiceSet = XamlVisualEditor.Core.GitServiceSet;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitPanelViewTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task Git_refresh_coalesces_overlapping_requests()
    {
        ControlledGitService gitService = new();
        using PanelFixture panel = new(gitService);

        Task first = panel.ViewModel.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(WaitLimit);
        Task second = panel.ViewModel.RefreshAsync();
        Task third = panel.ViewModel.RefreshAsync();
        Assert.Equal(1, gitService.StatusCalls);
        gitService.ReleaseFirstStatus.TrySetResult(true);
        await Task.WhenAll(first, second, third).WaitAsync(WaitLimit);

        Assert.Equal(1, gitService.RootCalls);
        Assert.Equal(2, gitService.StatusCalls);
        Assert.Equal(1, gitService.MaxConcurrentStatusCalls);
    }

    [AvaloniaFact]
    public async Task Disposing_the_session_cancels_a_running_status()
    {
        ControlledGitService gitService = new();
        PanelFixture panel = new(gitService);
        _ = panel.ViewModel.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(WaitLimit);

        panel.Dispose();

        Assert.True(await gitService.FirstStatusCancelled.Task.WaitAsync(WaitLimit));
        Assert.Equal(1, gitService.StatusCalls);
    }

    [AvaloniaFact]
    public async Task Status_is_read_without_the_working_folder_watcher_and_lists_each_change_once()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        Assert.False(vm.IsGitMonitoringEnabled);
        Assert.Equal(1, gitService.StatusCalls);
        Assert.Equal(new[] { "a.cs", "b.cs" }, vm.StagedItems.Select(item => item.Path));
        // The conflicted file is only in the Conflicts list, where Mark Resolved stages it.
        Assert.Equal(new[] { "c.cs", "new.txt" }, vm.UnstagedItems.Select(item => item.Path));
        Assert.Equal(new[] { "conflict.cs" }, vm.ConflictItems.Select(item => item.Path));
    }

    [AvaloniaFact]
    public async Task Watcher_switch_only_controls_the_working_folder_watcher()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        Assert.Equal(UiStrings.GitMonitoringOff, vm.GitMonitoringLabel);

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.True(vm.IsGitMonitoringEnabled);
        Assert.Equal(UiStrings.GitMonitoringOn, vm.GitMonitoringLabel);
        // Switching the watcher on reads the status once, because files may have changed while it was off.
        Assert.Equal(2, gitService.StatusCalls);

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.False(vm.IsGitMonitoringEnabled);
        Assert.True(vm.IsRepository);
        Assert.Equal(2, gitService.StatusCalls);
        Assert.Equal(2, vm.StagedCount);
    }

    [AvaloniaFact]
    public async Task Buttons_run_the_git_commands_with_the_selected_path()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedStagedChange = Assert.Single(vm.StagedItems, item => item.Path == "a.cs");
        await vm.UnstageCommand.Execute().ToTask();
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        await vm.StageCommand.Execute().ToTask();
        await vm.DiscardCommand.Execute().ToTask();
        await vm.StageAllCommand.Execute().ToTask();
        await vm.UnstageAllCommand.Execute().ToTask();
        vm.CommitMessage = "Message";
        await vm.CommitCommand.Execute().ToTask();

        Assert.Equal(
            new (string, string?)[]
            {
                (GitCommands.UNSTAGE, "a.cs"),
                (GitCommands.STAGE, "c.cs"),
                (GitCommands.DISCARD, "c.cs"),
                (GitCommands.STAGE_ALL, null),
                (GitCommands.UNSTAGE_ALL, null),
                (GitCommands.COMMIT, null)
            },
            panel.Executed.Select(call => (call.CommandId, call.Arguments?.Single() as string)));
    }

    [AvaloniaFact]
    public async Task Discard_is_disabled_for_untracked_files()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "new.txt");
        Assert.False(((ICommand)vm.DiscardCommand).CanExecute(null));
        Assert.True(((ICommand)vm.StageCommand).CanExecute(null));

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        Assert.True(((ICommand)vm.DiscardCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_failing_command_is_shown_in_the_panel()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService, failure: new System.IO.IOException("pipe broken"));
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        Task<string?> shown = vm.WhenAnyValue(x => x.ErrorMessage)
            .FirstAsync(message => message is not null)
            .Timeout(WaitLimit)
            .ToTask();
        await Assert.ThrowsAsync<System.IO.IOException>(() => vm.StageCommand.Execute().ToTask());

        // ReactiveUI reports the failure through ThrownExceptions on the UI thread after the execution ends.
        Assert.Equal(string.Format(UiStrings.GitCommandFailed, "pipe broken"), await shown);
    }

    [AvaloniaFact]
    public async Task The_diff_of_the_selected_file_reloads_after_a_refresh_with_an_equal_status()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = "first" };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        Task<string> first = vm.WhenAnyValue(x => x.DiffText).FirstAsync(text => text == "first").Timeout(WaitLimit).ToTask();
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        await first;
        int diffCalls = gitService.DiffCalls;

        // The file changed again, but its status stays " M", so the session publishes no new snapshot.
        gitService.DiffText = "second";
        Task<string> second = vm.WhenAnyValue(x => x.DiffText).FirstAsync(text => text == "second").Timeout(WaitLimit).ToTask();
        await vm.RefreshAsync().WaitAsync(WaitLimit);

        Assert.Equal("second", await second);
        Assert.Equal(diffCalls + 1, gitService.DiffCalls);
        Assert.Equal("c.cs", vm.SelectedUnstagedChange?.Path);
    }

    [AvaloniaFact]
    public async Task A_diff_that_finishes_after_another_file_was_selected_is_not_shown()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = "diff of new.txt" };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        TaskCompletionSource<GitDiff> slowDiff = new();
        gitService.GatedDiffs["c.cs"] = slowDiff;
        int diffCalls = gitService.DiffCalls;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "new.txt");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("diff of new.txt", vm.DiffText);

        // The diff of c.cs completes after new.txt was selected and its diff is shown.
        slowDiff.SetResult(ControlledGitService.CreateDiff("diff of c.cs"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(slowDiff.Task.IsCompletedSuccessfully);
        Assert.Equal("diff of new.txt", vm.DiffText);
        Assert.Equal("new.txt", vm.SelectedUnstagedChange?.Path);
        Assert.Equal(diffCalls + 2, gitService.DiffCalls);
    }

    private static GitRepositoryStatus CreateStatus() => new()
    {
        RepositoryRoot = string.Empty,
        Changes = new[]
        {
            new GitFileChange { Path = "a.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "b.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "c.cs", WorkTreeStatus = GitChangeKind.Modified },
            new GitFileChange
            {
                Path = "conflict.cs",
                IndexStatus = GitChangeKind.Unmerged,
                WorkTreeStatus = GitChangeKind.Unmerged,
                ConflictKind = GitConflictKind.BothModified
            },
            new GitFileChange { Path = "new.txt", WorkTreeStatus = GitChangeKind.Untracked, IsUntracked = true }
        }
    };

    private const string TWO_HUNK_DIFF = "diff --git a/c.cs b/c.cs\n--- a/c.cs\n+++ b/c.cs\n@@ -1,3 +1,3 @@\n a\n-b\n+B\n c\n"
        + "@@ -10,2 +10,3 @@\n x\n+y\n z\n";

    [AvaloniaFact]
    public async Task GitPanelView_Renders_Diff_Lines()
    {
        using PanelFixture panel = await PanelFixture.CreateAsync(new ControlledGitService(CreateStatus(), blockFirstStatus: false) { DiffText = TWO_HUNK_DIFF });
        GitPanelViewModel vm = panel.ViewModel;
        await SelectWorkingTreeChangeAsync(vm, "c.cs");

        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                view.ApplyTemplate();
                ToggleButton toggle = Assert.Single(view.GetVisualDescendants().OfType<ToggleButton>(),
                    button => ReferenceEquals(button.Command, vm.ToggleMonitoringCommand));
                Assert.False(toggle.IsChecked);
                Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == vm.GitMonitoringLabel);
                Button discard = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => ReferenceEquals(button.Command, vm.DiscardCommand));
                Assert.True(ToolTip.GetShowOnDisabled(discard));
                Assert.Equal(UiStrings.GitDiscardTooltip, ToolTip.GetTip(discard));
                UnifiedDiffEditor editor = Assert.IsType<UnifiedDiffEditor>(view.FindControl<UnifiedDiffEditor>("GitDiffEditor"));
                Assert.Same(vm.DiffLines, editor.DiffLines);
                Assert.Equal(TWO_HUNK_DIFF.TrimEnd('\n'), editor.Text);
                Assert.Equal(12, vm.DiffLineCount);
                // The colours come from the theme keys of the diff colours.
                Assert.False(editor.IsSet(TemplatedControl.BackgroundProperty));
                Assert.False(editor.IsSet(TemplatedControl.ForegroundProperty));
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    [AvaloniaFact]
    public async Task Changed_lines_selected_in_the_diff_or_the_hunk_at_the_cursor_enable_the_selection_buttons()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = TWO_HUNK_DIFF };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        await SelectWorkingTreeChangeAsync(vm, "c.cs");
        GitPanelView view = new() { DataContext = vm };
        using ThemedWindow window = ThemedWindow.Show(view, 1400, 600);
        UnifiedDiffEditor editor = Assert.IsType<UnifiedDiffEditor>(view.FindControl<UnifiedDiffEditor>("GitDiffEditor"));
        ICommand stage = vm.StageSelectionCommand;
        Assert.False(stage.CanExecute(null));

        // Context lines alone hold nothing to stage.
        SelectLines(editor, 5, 5);
        window.Pump();
        Assert.Equal(new DiffLineRange(4, 1), vm.SelectedDiffRange);
        Assert.False(stage.CanExecute(null));

        SelectLines(editor, 5, 7);
        window.Pump();
        Assert.True(stage.CanExecute(null));
        Assert.True(((ICommand)vm.DiscardSelectionCommand).CanExecute(null));
        Assert.False(((ICommand)vm.UnstageSelectionCommand).CanExecute(null));
        Assert.Equal(
            new XamlVisualEditor.Core.GitDiffSelection("c.cs", XamlVisualEditor.Core.GitPatchAction.Stage, TWO_HUNK_DIFF, 4, 3),
            vm.GetDiffSelection(XamlVisualEditor.Core.GitPatchAction.Stage));

        // Without a selection the hunk at the cursor counts.
        editor.TextArea.ClearSelection();
        editor.CurrentHunkIndex = 1;
        window.Pump();
        Assert.Null(vm.SelectedDiffRange);
        Assert.Equal(1, vm.CurrentHunkIndex);
        XamlVisualEditor.Core.GitDiffSelection hunk = vm.GetDiffSelection(XamlVisualEditor.Core.GitPatchAction.Stage)!;
        Assert.Equal((8, 4), (hunk.FirstLine, hunk.LineCount));
        await vm.StageSelectionCommand.Execute().ToTask();
        Assert.Equal((GitCommands.STAGE_SELECTION, (IReadOnlyList<object?>?)null), panel.Executed[^1]);

        // An untracked file is only staged as a whole.
        gitService.DiffText = "diff --git a/new.txt b/new.txt\nnew file mode 100644\n--- /dev/null\n+++ b/new.txt\n@@ -0,0 +1 @@\n+n\n";
        await SelectWorkingTreeChangeAsync(vm, "new.txt");
        editor.CurrentHunkIndex = 0;
        window.Pump();
        Assert.Equal(0, vm.CurrentHunkIndex);
        Assert.False(stage.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_refresh_with_an_unchanged_diff_keeps_the_selection()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = TWO_HUNK_DIFF };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        await SelectWorkingTreeChangeAsync(vm, "c.cs");
        GitPanelView view = new() { DataContext = vm };
        using ThemedWindow window = ThemedWindow.Show(view, 1400, 600);
        UnifiedDiffEditor editor = Assert.IsType<UnifiedDiffEditor>(view.FindControl<UnifiedDiffEditor>("GitDiffEditor"));
        SelectLines(editor, 5, 7);
        window.Pump();
        TaskCompletionSource<GitDiff> reload = new();
        gitService.GatedDiffs["c.cs"] = reload;
        int diffCalls = gitService.DiffCalls;

        // The status stays the same, so the refresh reloads the diff of the selected file.
        await vm.RefreshAsync().WaitAsync(WaitLimit);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(diffCalls + 1, gitService.DiffCalls);
        reload.SetResult(ControlledGitService.CreateDiff(TWO_HUNK_DIFF));
        window.Pump();

        Assert.Equal(new DiffLineRange(4, 3), editor.SelectedLineRange);
        Assert.Equal(new DiffLineRange(4, 3), vm.SelectedDiffRange);
        Assert.True(((ICommand)vm.StageSelectionCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_carriage_return_inside_a_line_keeps_each_editor_line_on_its_diff_line()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        string[] lines = Enumerable.Range(1, 30).Select(number => "line" + number).ToArray();
        repository.CommitFileIn(repository.Path, "f.txt", string.Join('\n', lines) + "\n", "init");
        lines[2] = "line3 old\rmac";
        lines[25] = "line26 changed";
        repository.WriteFile("f.txt", string.Join('\n', lines) + "\n");
        XamlVisualEditor.Workspace.GitService git = new(options: repository.ProcessOptions);
        string diff = (await git.GetDiffAsync(repository.Path, new GitDiffRequest { Path = "f.txt" }, ct)).RawText!;
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(diff) };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 400);

        // Discard only the removed line: line26 comes back in front of the changed line.
        AvaloniaEdit.Document.DocumentLine removed = editor.Document.Lines.First(line => editor.Document.GetText(line) == "-line26");
        editor.Select(removed.Offset, removed.Length);
        window.Pump();
        DiffLineRange range = editor.SelectedLineRange!.Value;
        await git.ApplySelectionAsync(
            repository.Path,
            new XamlVisualEditor.Core.GitDiffSelection("f.txt", XamlVisualEditor.Core.GitPatchAction.Discard, diff, range.Start, range.Count),
            ct);

        lines[25] = "line26\nline26 changed";
        Assert.Equal(string.Join('\n', lines) + "\n", System.IO.File.ReadAllText(System.IO.Path.Combine(repository.Path, "f.txt")));
        Assert.Equal(editor.DiffLines!.Count, editor.Document.LineCount);
    }

    /// <summary>Selects the working tree change of <paramref name="path"/> and waits until its new diff text is shown.</summary>
    private static async Task SelectWorkingTreeChangeAsync(GitPanelViewModel vm, string path)
    {
        string previous = vm.DiffText;
        Task<string> loaded = vm.WhenAnyValue(x => x.DiffText).FirstAsync(text => text != previous).Timeout(WaitLimit).ToTask();
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == path);
        await loaded;
    }

    /// <summary>Selects the 1-based lines <paramref name="first"/> to <paramref name="last"/> of the editor.</summary>
    private static void SelectLines(UnifiedDiffEditor editor, int first, int last)
    {
        int start = editor.Document.GetLineByNumber(first).Offset;
        editor.Select(start, editor.Document.GetLineByNumber(last).EndOffset - start);
    }

    [AvaloniaFact]
    public async Task Diff_pane_hides_and_restores_the_resized_width()
    {
        using PanelFixture panel = new(new ControlledGitService(blockFirstStatus: false));
        GitPanelViewModel vm = panel.ViewModel;
        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view);
        try
        {
            Grid grid = Assert.IsType<Grid>(view.FindControl<Grid>("GitContentGrid"));
            GridSplitter splitter = Assert.IsType<GridSplitter>(view.FindControl<GridSplitter>("GitDiffSplitter"));
            DockPanel diff = Assert.IsType<DockPanel>(view.FindControl<DockPanel>("GitDiffPane"));
            DockPanel list = Assert.IsType<DockPanel>(view.FindControl<DockPanel>("GitListPane"));
            GridLength resizedWidth = new(280);
            await Dispatcher.UIThread.InvokeAsync(() => grid.ColumnDefinitions[2].Width = resizedWidth);

            await vm.ToggleDiffCommand.Execute().ToTask();
            Assert.False(vm.IsDiffVisible);
            Assert.Equal(UiStrings.GitShowDiff, vm.DiffToggleLabel);
            Assert.Equal(0, grid.ColumnDefinitions[1].Width.Value);
            Assert.Equal(0, grid.ColumnDefinitions[2].Width.Value);
            Assert.False(splitter.IsVisible);
            Assert.False(diff.IsVisible);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBox>(),
                box => box.PlaceholderText == UiStrings.CommitMessage);

            await vm.ToggleDiffCommand.Execute().ToTask();
            Assert.True(vm.IsDiffVisible);
            Assert.Equal(resizedWidth, grid.ColumnDefinitions[2].Width);
            Assert.True(splitter.IsVisible);
            Assert.True(diff.IsVisible);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    [AvaloniaFact]
    public async Task Every_panel_button_has_an_icon_a_label_and_a_tooltip()
    {
        using PanelFixture panel = new(new ControlledGitService(blockFirstStatus: false));
        GitPanelViewModel vm = panel.ViewModel;
        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view, width: 1400);
        try
        {
            ICommand[] commands =
            [
                vm.RefreshCommand, vm.ToggleDiffCommand, vm.ToggleMonitoringCommand, vm.StageCommand,
                vm.UnstageCommand, vm.DiscardCommand, vm.StageAllCommand, vm.UnstageAllCommand, vm.CommitCommand,
                vm.StageSelectionCommand, vm.UnstageSelectionCommand, vm.DiscardSelectionCommand, vm.AbortCommand,
                vm.ContinueCommand, vm.SkipCommand, vm.OpenConflictCommand, vm.AcceptOursCommand, vm.AcceptTheirsCommand,
                vm.MarkResolvedCommand
            ];
            Button[] buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Command is not null)
                .ToArray();
            Assert.Equal(commands.Length, buttons.Length);

            foreach (ICommand command in commands)
            {
                Button button = Assert.Single(buttons, candidate => ReferenceEquals(candidate.Command, command));
                // The content is read from the logical tree: a button of a hidden banner or list has no template yet.
                PathIcon icon = Assert.Single(button.GetLogicalDescendants().OfType<PathIcon>());
                Assert.NotNull(icon.Data);
                Assert.False(icon.IsSet(TemplatedControl.ForegroundProperty));
                Assert.Contains(button.GetLogicalDescendants().OfType<TextBlock>(), text => !string.IsNullOrWhiteSpace(text.Text));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.IsType<string>(ToolTip.GetTip(button));
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    [AvaloniaTheory]
    [InlineData(600)]
    [InlineData(900)]
    public async Task The_status_column_shows_every_status_text_whole(double height)
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelView view = new() { DataContext = panel.ViewModel };
        using ThemedWindow window = ThemedWindow.Show(view, 1136, height);

        TabItem[] tabs = view.GetVisualDescendants().OfType<TabControl>().Single().Items.OfType<TabItem>().ToArray();
        Assert.Equal(2, tabs.Length);
        int checkedCells = 0;
        foreach (TabItem tab in tabs)
        {
            tab.IsSelected = true;
            window.Pump();
            DataGrid grid = Assert.IsType<DataGrid>(tab.Content);
            foreach (DataGridRow row in grid.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible))
            {
                GitChangeEntryViewModel entry = (GitChangeEntryViewModel)row.DataContext!;
                GitHistoryViewTests.AssertNotClipped(Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == entry.StatusLabel));
                checkedCells++;
            }

            // Every possible status fits, not only the listed ones; the path column is the one that takes the rest.
            TextBlock sample = grid.GetVisualDescendants().OfType<TextBlock>().First(text => GitChangeEntryViewModel.StatusLabels.Contains(text.Text));
            double needed = Math.Ceiling(ColumnWidthSamples.MeasureWidest(sample, GitChangeEntryViewModel.StatusLabels) + sample.Margin.Left + sample.Margin.Right);
            Assert.True(
                grid.Columns[1].MinWidth >= needed,
                $"{tab.Header}: the status column has the minimum {grid.Columns[1].MinWidth} and is {grid.Columns[1].ActualWidth} wide; every status needs {needed}.");
            Assert.True(grid.Columns[0].Width.IsStar);

            // A copied row holds the status of the template column.
            GitChangeEntryViewModel copied = (GitChangeEntryViewModel)grid.GetVisualDescendants().OfType<DataGridRow>().First(row => row.IsVisible).DataContext!;
            Assert.Equal(new[] { copied.DisplayPath, copied.StatusLabel }, await GitHistoryViewTests.CopyRowAsync(window, grid, copied));
        }

        Assert.Equal(panel.ViewModel.StagedItems.Count + panel.ViewModel.UnstagedItems.Count, checkedCells);
    }

    [AvaloniaFact]
    public void Changes_diff_colors_follow_the_theme_and_an_application_resource_overrides_them()
    {
        GitDiffTextEditor editor = new()
        {
            DiffLines = new List<GitDiffLineViewModel> { new(GitDiffLineKind.Added, "added", "+", null, 1) }
        };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);
        Assert.Empty(editor.Resources.MergedDictionaries);

        window.Window.RequestedThemeVariant = ThemeVariant.Light;
        window.Pump();
        IBrush? light = FirstLineForeground(editor);
        window.Window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Pump();
        IBrush? dark = FirstLineForeground(editor);

        Assert.Equal(Color.Parse("#116329"), Assert.IsAssignableFrom<ISolidColorBrush>(light).Color);
        Assert.Equal(Color.Parse("#6CCB7E"), Assert.IsAssignableFrom<ISolidColorBrush>(dark).Color);

        // The editor redraws on its own when a resource changes, also without a change of the theme variant.
        Avalonia.Application.Current!.Resources[DiffColors.ADDED_FOREGROUND_KEY] = Brushes.Orange;
        try
        {
            window.Pump();
            Assert.Same(Brushes.Orange, FirstLineForeground(editor));
        }
        finally
        {
            Avalonia.Application.Current!.Resources.Remove(DiffColors.ADDED_FOREGROUND_KEY);
        }

        window.Pump();
        Assert.Equal(Color.Parse("#6CCB7E"), Assert.IsAssignableFrom<ISolidColorBrush>(FirstLineForeground(editor)).Color);

        static IBrush? FirstLineForeground(GitDiffTextEditor editor) =>
            editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(1)).Elements.First().TextRunProperties.ForegroundBrush;
    }

    private static async Task<Window> ShowInWindowAsync(Control control, double width = 900, double height = 600)
    {
        Window window = new()
        {
            Content = control,
            Width = width,
            Height = height
        };

        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        return window;
    }

    /// <summary>A Changes panel over a session of a git service; the commands it runs are recorded.</summary>
    private sealed class PanelFixture : IDisposable
    {
        public PanelFixture(IGitService git, Exception? failure = null)
        {
            Session = new GitSession(GitServiceSet.FromService(git), new TestWorkspaceInfo());
            ViewModel = new GitPanelViewModel(Session, (commandId, arguments) =>
            {
                Executed.Add((commandId, arguments));
                return failure is null ? Task.CompletedTask : Task.FromException(failure);
            });
        }

        public GitSession Session { get; }

        public GitPanelViewModel ViewModel { get; }

        public List<(string CommandId, IReadOnlyList<object?>? Arguments)> Executed { get; } = new();

        /// <summary>Creates the panel and shows the first status.</summary>
        public static async Task<PanelFixture> CreateAsync(IGitService git, Exception? failure = null)
        {
            PanelFixture panel = new(git, failure);
            await panel.ViewModel.RefreshAsync().WaitAsync(WaitLimit);
            await panel.ViewModel.WhenAnyValue(x => x.IsRepository).FirstAsync(isRepository => isRepository).Timeout(WaitLimit);
            return panel;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Session.Dispose();
        }
    }

    private sealed class TestWorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath => "test-workspace";
        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ControlledGitService : IGitService
    {
        private readonly GitRepositoryStatus? _status;
        private readonly bool _blockFirstStatus;
        private int _activeStatusCalls;
        public ControlledGitService(GitRepositoryStatus? status = null, bool blockFirstStatus = true)
        {
            _status = status;
            _blockFirstStatus = blockFirstStatus;
        }
        public int RootCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int MaxConcurrentStatusCalls { get; private set; }
        public TaskCompletionSource<bool> FirstStatusStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstStatusCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
        {
            RootCalls++;
            return Task.FromResult<string?>("test-repo");
        }

        public async Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
        {
            StatusCalls++;
            int active = Interlocked.Increment(ref _activeStatusCalls);
            MaxConcurrentStatusCalls = Math.Max(MaxConcurrentStatusCalls, active);
            try
            {
                if (StatusCalls == 1 && _blockFirstStatus)
                {
                    FirstStatusStarted.TrySetResult(true);
                    try
                    {
                        await ReleaseFirstStatus.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        FirstStatusCancelled.TrySetResult(true);
                        throw;
                    }
                }
                return _status ?? new GitRepositoryStatus { RepositoryRoot = string.Empty, Changes = Array.Empty<GitFileChange>() };
            }
            finally
            {
                Interlocked.Decrement(ref _activeStatusCalls);
            }
        }

        /// <summary>Gets or sets the raw text of every diff.</summary>
        public string DiffText { get; set; } = string.Empty;

        public int DiffCalls { get; private set; }

        /// <summary>
        /// Gets the diffs of these paths, which complete only when the test sets their result; the cancellation token is
        /// ignored, like a git process that ends before it is killed.
        /// </summary>
        public Dictionary<string, TaskCompletionSource<GitDiff>> GatedDiffs { get; } = new();

        public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default)
        {
            DiffCalls++;
            return request.Path is { } path && GatedDiffs.TryGetValue(path, out TaskCompletionSource<GitDiff>? gate)
                ? gate.Task
                : Task.FromResult(CreateDiff(DiffText));
        }

        public static GitDiff CreateDiff(string text) =>
            new() { Files = Array.Empty<XamlVisualEditor.Core.GitDiffFile>(), RawText = text };
        public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
