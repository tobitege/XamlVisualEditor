using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Gutter margin that draws the line marks of its file: a bar along added and modified lines and a short tick at the
/// boundary of removed lines, in the theme brush of the kind. A click on a mark runs the click command of its layer
/// with the full path and the clicked line.
/// </summary>
public sealed class LineMarkMargin : AbstractMargin, ICustomHitTest
{
    private const double MARGIN_WIDTH = 6;
    private const double BAR_LEFT = 1.5;
    private const double BAR_WIDTH = 3;
    private const double TICK_HEIGHT = 4;

    // Indexed by TextEditorLineMarkKind.
    private static readonly string[] BrushKeys =
        ["SourceControlAddedBrush", "SourceControlModifiedBrush", "SourceControlDeletedBrush", "SourceControlConflictedBrush"];

    private readonly TextEditorLineMarkRegistry _registry;
    private readonly Action<string, IReadOnlyList<object?>>? _executeCommand;
    private string? _filePath;

    public LineMarkMargin(TextEditorLineMarkRegistry registry, Action<string, IReadOnlyList<object?>>? executeCommand)
    {
        _registry = registry;
        _executeCommand = executeCommand;
        Focusable = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    /// <summary>Gets or sets the full path of the file whose marks the margin draws.</summary>
    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (string.Equals(_filePath, value, StringComparison.Ordinal))
                return;
            _filePath = value;
            InvalidateVisual();
        }
    }

    // The base class redraws the margin whenever the text view rebuilds its visual lines, which scrolling does too.
    protected override Size MeasureOverride(Size availableSize) => new(MARGIN_WIDTH, 0);

    /// <summary>Takes the pointer over the whole margin, not only over the drawn marks, which are narrower.</summary>
    public bool HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        foreach ((Rect bounds, TextEditorLineMarkKind kind) in GetMarkBounds())
        {
            if (FindBrush(kind) is { } brush)
                context.FillRectangle(brush, bounds);
        }
    }

    /// <summary>Gets the rectangles the margin draws for the marks on the visible lines.</summary>
    internal IEnumerable<(Rect Bounds, TextEditorLineMarkKind Kind)> GetMarkBounds()
    {
        IReadOnlyList<TextEditorLineMark> marks = _registry.Find(_filePath, out _);
        if (marks.Count == 0 || TextView is not { VisualLinesValid: true } textView || Document is not { } document)
            yield break;

        double scrollTop = textView.ScrollOffset.Y;
        foreach (VisualLine line in textView.VisualLines)
        {
            double top = line.VisualTop - scrollTop;
            foreach (TextEditorLineMark mark in MarksOf(marks, line, document.LineCount))
            {
                // A boundary below the last line is drawn at the bottom of that line.
                double boundary = mark.StartLine > document.LineCount ? top + line.Height : top;
                yield return (mark.LineCount > 0
                    ? new Rect(BAR_LEFT, top, BAR_WIDTH, line.Height)
                    : new Rect(0, boundary - (TICK_HEIGHT / 2), MARGIN_WIDTH, TICK_HEIGHT), mark.Kind);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_executeCommand is null || _filePath is null || TextView is not { VisualLinesValid: true } textView
            || Document is not { } document || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        IReadOnlyList<TextEditorLineMark> marks = _registry.Find(_filePath, out string? commandId);
        VisualLine? line = textView.GetVisualLineFromVisualTop(e.GetPosition(this).Y + textView.ScrollOffset.Y);
        if (commandId is null || line is null || !MarksOf(marks, line, document.LineCount).Any())
            return;

        e.Handled = true;
        _executeCommand(commandId, [_filePath, line.FirstDocumentLine.LineNumber]);
    }

    // The marks on the document lines of a visual line; a removed-lines boundary belongs to the line below it, and a
    // boundary below the last line to the last line. The marks are sorted and do not overlap, so their ends are sorted.
    private static IEnumerable<TextEditorLineMark> MarksOf(IReadOnlyList<TextEditorLineMark> marks, VisualLine line, int lineCount)
    {
        int first = line.FirstDocumentLine.LineNumber;
        int last = line.LastDocumentLine.LineNumber;
        int low = 0;
        int high = marks.Count;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (LastLine(marks[middle], lineCount) < first)
                low = middle + 1;
            else
                high = middle;
        }

        for (int index = low; index < marks.Count && FirstLine(marks[index], lineCount) <= last; index++)
            yield return marks[index];
    }

    private static int FirstLine(TextEditorLineMark mark, int lineCount) =>
        mark.LineCount == 0 ? Math.Min(mark.StartLine, lineCount) : mark.StartLine;

    private static int LastLine(TextEditorLineMark mark, int lineCount) =>
        FirstLine(mark, lineCount) + Math.Max(mark.LineCount, 1) - 1;

    private IBrush? FindBrush(TextEditorLineMarkKind kind) =>
        this.TryFindResource(BrushKeys[(int)kind], ActualThemeVariant, out object? value) ? value as IBrush : null;
}
