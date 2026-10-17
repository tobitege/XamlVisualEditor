using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using Xunit;
using XamlVisualEditor.SourceControl.UI.Controls;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.SourceControl.UI.Themes;

namespace XamlVisualEditor.Tests.UI;

public sealed class UnifiedDiffEditorTests
{
    private const string DIFF =
        "diff --git a/a.txt b/a.txt\n" +
        "--- a/a.txt\n" +
        "+++ b/a.txt\n" +
        "@@ -1,2 +1,2 @@\n" +
        "-one\n" +
        "+eins\n" +
        " two\n" +
        "@@ -120,1 +120,1 @@\n" +
        "-hundred twenty\n" +
        "+hundertzwanzig\n";

    [AvaloniaFact]
    public void Shows_the_diff_read_only_with_a_line_number_margin_and_counts_the_hunks()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF), Width = 600, Height = 300 };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);

        Assert.True(editor.IsReadOnly);
        Assert.False(editor.ShowLineNumbers);
        Assert.Equal(DIFF.TrimEnd('\n'), editor.Text);
        Assert.Equal(2, editor.HunkCount);
        Assert.False(editor.IsTruncated);
        Assert.Equal(-1, editor.CurrentHunkIndex);
        AbstractMargin margin = Assert.IsAssignableFrom<AbstractMargin>(editor.TextArea.LeftMargins[0]);
        double wideMargin = margin.Bounds.Width;
        Assert.True(wideMargin > 0);

        // Three digits of line 120 make the margin wider than one digit.
        editor.DiffLines = DiffLineBuilder.Parse("@@ -1 +1 @@\n-a\n+b\n");
        window.Pump();
        Assert.True(margin.Bounds.Width < wideMargin);
        Assert.Equal(1, editor.HunkCount);
    }

    [AvaloniaFact]
    public void Setting_the_current_hunk_moves_the_caret_and_moving_the_caret_updates_it()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF) };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);

        editor.CurrentHunkIndex = 1;
        window.Pump();
        Assert.Equal(8, editor.TextArea.Caret.Line);

        editor.TextArea.Caret.Line = 5;
        window.Pump();
        Assert.Equal(0, editor.CurrentHunkIndex);
        editor.TextArea.Caret.Line = 2;
        window.Pump();
        Assert.Equal(-1, editor.CurrentHunkIndex);

        editor.CurrentHunkIndex = 7;
        window.Pump();
        Assert.Equal(2, editor.TextArea.Caret.Line);
    }

    [AvaloniaFact]
    public void A_diff_longer_than_the_limit_is_cut_and_ends_with_the_notice()
    {
        UnifiedDiffEditor editor = new()
        {
            MaxLineCount = 4,
            TruncationNotice = "The diff is cut.",
            DiffLines = DiffLineBuilder.Parse(DIFF)
        };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);

        Assert.True(editor.IsTruncated);
        Assert.Equal(4, editor.ShownLines.Count);
        Assert.Equal(1, editor.HunkCount);
        string[] lines = editor.Text.Split('\n');
        Assert.Equal(5, lines.Length);
        Assert.Equal("The diff is cut.", lines[^1]);

        editor.MaxLineCount = 100;
        window.Pump();
        Assert.False(editor.IsTruncated);
        Assert.Equal(10, editor.Text.Split('\n').Length);

        // Lines that were never read also cut the diff: all held lines are shown, followed by the notice.
        editor.HasMoreLines = true;
        window.Pump();
        Assert.True(editor.IsTruncated);
        Assert.Equal(10, editor.ShownLines.Count);
        Assert.Equal("The diff is cut.", editor.Text.Split('\n')[^1]);

        // A new notice replaces the shown one.
        editor.TruncationNotice = "Only 10 lines are shown.";
        window.Pump();
        Assert.Equal("Only 10 lines are shown.", editor.Text.Split('\n')[^1]);
    }

    [AvaloniaFact]
    public void The_margin_draws_the_old_and_new_line_number_of_every_line_and_none_for_headers_and_the_notice()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF), TruncationNotice = "cut" };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 400);

        Assert.Equal(
            new Dictionary<int, (string?, string?)>
            {
                [4] = ("1", null),
                [5] = (null, "1"),
                [6] = ("2", "2"),
                [8] = ("120", null),
                [9] = (null, "120")
            },
            DrawnNumbers(editor));

        // The notice of a cut diff is the eighth line and has no numbers.
        editor.MaxLineCount = 7;
        window.Pump();
        Assert.Equal(8, editor.Document.LineCount);
        Assert.Equal(
            new Dictionary<int, (string?, string?)>
            {
                [4] = ("1", null),
                [5] = (null, "1"),
                [6] = ("2", "2")
            },
            DrawnNumbers(editor));
    }

    [AvaloniaFact]
    public void Line_colors_come_from_the_theme_resources_and_a_set_color_wins()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF) };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);

        Assert.Null(editor.AddedForeground);
        window.Window.RequestedThemeVariant = ThemeVariant.Light;
        window.Pump();
        IBrush? light = AddedLineForeground(editor);
        window.Window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Pump();
        IBrush? dark = AddedLineForeground(editor);
        editor.AddedForeground = Brushes.Magenta;
        window.Pump();

        Assert.Equal(Color.Parse("#116329"), Assert.IsAssignableFrom<ISolidColorBrush>(light).Color);
        Assert.Equal(Color.Parse("#6CCB7E"), Assert.IsAssignableFrom<ISolidColorBrush>(dark).Color);
        Assert.Same(Brushes.Magenta, AddedLineForeground(editor));
    }

    [AvaloniaFact]
    public void An_application_resource_under_a_diff_color_key_wins_over_the_default_and_redraws_the_editor()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF) };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);
        window.Window.RequestedThemeVariant = ThemeVariant.Light;
        window.Pump();
        Assert.Empty(editor.Resources.MergedDictionaries);
        Assert.Equal(Color.Parse("#116329"), Assert.IsAssignableFrom<ISolidColorBrush>(AddedLineForeground(editor)).Color);

        // The editor redraws on its own when a resource changes.
        Application.Current!.Resources[DiffColors.ADDED_FOREGROUND_KEY] = Brushes.Orange;
        try
        {
            window.Pump();
            Assert.Same(Brushes.Orange, AddedLineForeground(editor));
        }
        finally
        {
            Application.Current!.Resources.Remove(DiffColors.ADDED_FOREGROUND_KEY);
        }

        window.Pump();
        Assert.Equal(Color.Parse("#116329"), Assert.IsAssignableFrom<ISolidColorBrush>(AddedLineForeground(editor)).Color);

        // A theme dictionary of the window wins as well, for its theme variant only, and a brush replaced in it shows
        // without a change of the theme variant.
        ResourceDictionary darkTheme = new() { [DiffColors.ADDED_FOREGROUND_KEY] = Brushes.Teal };
        window.Window.Resources.ThemeDictionaries[ThemeVariant.Dark] = darkTheme;
        window.Window.RequestedThemeVariant = ThemeVariant.Dark;
        window.Pump();
        Assert.Same(Brushes.Teal, AddedLineForeground(editor));
        darkTheme[DiffColors.ADDED_FOREGROUND_KEY] = Brushes.Purple;
        window.Pump();
        Assert.Same(Brushes.Purple, AddedLineForeground(editor));
        window.Window.RequestedThemeVariant = ThemeVariant.Light;
        window.Pump();
        Assert.Equal(Color.Parse("#116329"), Assert.IsAssignableFrom<ISolidColorBrush>(AddedLineForeground(editor)).Color);
    }

    /// <summary>Returns the foreground the colorizer gives the first added line, "+eins".</summary>
    private static IBrush? AddedLineForeground(UnifiedDiffEditor editor)
    {
        VisualLine line = editor.TextArea.TextView.GetOrConstructVisualLine(editor.Document.GetLineByNumber(6));
        return line.Elements.First().TextRunProperties.ForegroundBrush;
    }

    /// <summary>
    /// Renders the line number margin into a drawing and returns the numbers it drew per 0-based line: the old number
    /// in the left half of the margin, the new number in the right half.
    /// </summary>
    private static Dictionary<int, (string? Old, string? New)> DrawnNumbers(UnifiedDiffEditor editor)
    {
        AbstractMargin margin = Assert.IsAssignableFrom<AbstractMargin>(editor.TextArea.LeftMargins[0]);
        DrawingGroup drawing = new();
        using (DrawingContext context = drawing.Open())
        {
            margin.Render(context);
        }

        List<(Point Origin, string Text)> texts = new();
        CollectTexts(drawing, Matrix.Identity, texts);
        TextView textView = editor.TextArea.TextView;
        Dictionary<int, (string? Old, string? New)> numbers = new();
        foreach ((Point origin, string text) in texts)
        {
            VisualLine line = textView.VisualLines.Single(candidate =>
                origin.Y >= candidate.VisualTop - textView.ScrollOffset.Y
                && origin.Y <= candidate.VisualTop - textView.ScrollOffset.Y + candidate.Height);
            int index = line.FirstDocumentLine.LineNumber - 1;
            (string? old, string? @new) = numbers.GetValueOrDefault(index);
            numbers[index] = origin.X < margin.Bounds.Width / 2 ? (text, @new) : (old, text);
        }

        return numbers;
    }

    private static void CollectTexts(Drawing drawing, Matrix transform, List<(Point Origin, string Text)> texts)
    {
        switch (drawing)
        {
            case DrawingGroup group:
                Matrix groupTransform = group.Transform is { } own ? own.Value * transform : transform;
                foreach (Drawing child in group.Children)
                {
                    CollectTexts(child, groupTransform, texts);
                }

                break;
            case GlyphRunDrawing { GlyphRun: { } run }:
                texts.Add((run.BaselineOrigin.Transform(transform), run.Characters.ToString()));
                break;
        }
    }

    [AvaloniaFact]
    public void The_selection_is_reported_as_a_range_of_diff_lines()
    {
        UnifiedDiffEditor editor = new() { DiffLines = DiffLineBuilder.Parse(DIFF) };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);
        Assert.Null(editor.SelectedLineRange);

        int start = editor.Document.GetLineByNumber(5).Offset + 1;
        int end = editor.Document.GetLineByNumber(6).EndOffset;
        editor.Select(start, end - start);
        window.Pump();

        Assert.Equal(new DiffLineRange(4, 2), editor.SelectedLineRange);
        Assert.Equal(DiffLineKind.Removed, editor.ShownLines[editor.SelectedLineRange!.Value.Start].Kind);
        Assert.Equal(DiffLineKind.Added, editor.ShownLines[editor.SelectedLineRange!.Value.End].Kind);

        editor.TextArea.ClearSelection();
        window.Pump();
        Assert.Null(editor.SelectedLineRange);
    }

    [AvaloniaFact]
    public void A_changing_collection_of_lines_is_shown_again()
    {
        ObservableCollection<DiffLine> lines = new(DiffLineBuilder.Parse("@@ -1 +1 @@\n-a\n"));
        UnifiedDiffEditor editor = new() { DiffLines = lines };
        using ThemedWindow window = ThemedWindow.Show(editor, 600, 300);
        Assert.Equal("@@ -1 +1 @@\n-a", editor.Text);

        lines.Add(new DiffLine(DiffLineKind.Added, "+b", null, 1));
        window.Pump();

        Assert.Equal("@@ -1 +1 @@\n-a\n+b", editor.Text);
        Assert.Equal(DiffLineKind.Added, editor.ShownLines.Last().Kind);
    }
}
