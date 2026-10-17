using System.Globalization;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.SourceControl.UI.Models;

namespace XamlVisualEditor.SourceControl.UI.Controls;

/// <summary>
/// Margin of <see cref="UnifiedDiffEditor"/> with two right-aligned columns: the line number in the old file and the
/// line number in the new file.
/// </summary>
internal sealed class DiffLineNumberMargin : AbstractMargin
{
    private const double COLUMN_GAP = 1;
    private const double TRAILING_SPACE = 1;

    private readonly UnifiedDiffEditor _owner;
    private int _digits = 1;
    private double _digitWidth;

    public DiffLineNumberMargin(UnifiedDiffEditor owner)
    {
        _owner = owner;
        Focusable = false;
    }

    /// <summary>Gets the left edge of the new line numbers, after the old line numbers and their gap.</summary>
    private double NewColumnLeft => (_digits + COLUMN_GAP) * _digitWidth;

    // The base class redraws the margin whenever the text view rebuilds its visual lines, which scrolling does too.
    protected override Size MeasureOverride(Size availableSize)
    {
        int largest = 0;
        foreach (DiffLine line in _owner.ShownLines)
        {
            largest = Math.Max(largest, Math.Max(line.OldLineNumber ?? 0, line.NewLineNumber ?? 0));
        }

        _digits = Math.Max(1, largest.ToString(CultureInfo.InvariantCulture).Length);
        _digitWidth = CreateText("0").WidthIncludingTrailingWhitespace;
        return new Size(((2 * _digits) + COLUMN_GAP + TRAILING_SPACE) * _digitWidth, 0);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (TextView is not { VisualLinesValid: true } textView)
        {
            return;
        }

        IReadOnlyList<DiffLine> lines = _owner.ShownLines;
        double scrollTop = textView.ScrollOffset.Y;
        foreach (VisualLine visualLine in textView.VisualLines)
        {
            int index = visualLine.FirstDocumentLine.LineNumber - 1;
            if (index < 0 || index >= lines.Count)
            {
                continue;
            }

            double top = visualLine.VisualTop - scrollTop;
            DrawNumber(context, lines[index].OldLineNumber, _digits * _digitWidth, top);
            DrawNumber(context, lines[index].NewLineNumber, NewColumnLeft + (_digits * _digitWidth), top);
        }
    }

    private void DrawNumber(DrawingContext context, int? number, double right, double top)
    {
        if (number is not { } value)
        {
            return;
        }

        FormattedText text = CreateText(value.ToString(CultureInfo.InvariantCulture));
        context.DrawText(text, new Point(right - text.WidthIncludingTrailingWhitespace, top));
    }

    private FormattedText CreateText(string text)
    {
        return new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(_owner.FontFamily),
            _owner.FontSize,
            _owner.LineNumbersForeground ?? _owner.Foreground ?? Brushes.Gray);
    }
}
