using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.CodeEditor;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Gutter margin that marks the source lines of the element selected in the designer, and in the error color the
/// lines of a diagnostic location opened from the problems list: an arrow at the first line and a bar along every
/// marked line.
/// </summary>
public sealed class SelectedElementMargin : AbstractMargin
{
    private const double MARGIN_WIDTH = 12;
    private const double BAR_LEFT = 8;
    private const double BAR_WIDTH = 3;
    private const double ARROW_HALF_HEIGHT = 5;
    private const double ARROW_TIP = 7;
    private static readonly Color FallbackAccentColor = Color.Parse("#0078D4");
    private static readonly Color FallbackErrorColor = Colors.Red;

    private SourceLineRange? _lines;
    private SourceLineRange? _diagnosticLines;

    public SelectedElementMargin()
    {
        IsHitTestVisible = false;
        Focusable = false;
    }

    /// <summary>Gets or sets the marked lines, or null when no element is selected.</summary>
    public SourceLineRange? Lines
    {
        get => _lines;
        set
        {
            if (_lines == value)
                return;
            _lines = value;
            InvalidateVisual();
        }
    }

    /// <summary>Gets or sets the lines marked in the error color, or null when no diagnostic location is marked.</summary>
    public SourceLineRange? DiagnosticLines
    {
        get => _diagnosticLines;
        set
        {
            if (_diagnosticLines == value)
                return;
            _diagnosticLines = value;
            InvalidateVisual();
        }
    }

    // The base class redraws the margin whenever the text view rebuilds its visual lines, which scrolling does too.
    protected override Size MeasureOverride(Size availableSize) => new(MARGIN_WIDTH, 0);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (TextView is not { VisualLinesValid: true } textView)
            return;

        // The diagnostic marker is drawn last, so it stays visible where it overlaps the selected element.
        if (_lines is { } lines)
            DrawMarker(context, textView, lines, ResolveColor("SystemAccentColor", FallbackAccentColor));
        if (_diagnosticLines is { } diagnosticLines)
            DrawMarker(context, textView, diagnosticLines, ResolveColor("SystemFillColorCritical", FallbackErrorColor));
    }

    private static void DrawMarker(DrawingContext context, TextView textView, SourceLineRange lines, Color color)
    {
        IBrush brush = new SolidColorBrush(color);
        double scrollTop = textView.ScrollOffset.Y;
        foreach (VisualLine line in textView.VisualLines)
        {
            int firstLine = line.FirstDocumentLine.LineNumber;
            int lastLine = line.LastDocumentLine.LineNumber;
            if (lastLine < lines.StartLine || firstLine > lines.EndLine)
                continue;

            double top = line.VisualTop - scrollTop;
            context.FillRectangle(brush, new Rect(BAR_LEFT, top, BAR_WIDTH, line.Height));
            if (firstLine <= lines.StartLine && lastLine >= lines.StartLine)
            {
                double middle = top + (line.Height / 2);
                context.DrawGeometry(brush, null, CreateArrow(middle));
            }
        }
    }

    private Color ResolveColor(string resourceKey, Color fallback) =>
        this.TryFindResource(resourceKey, ActualThemeVariant, out object? value) && value is Color color
            ? color
            : fallback;

    private static StreamGeometry CreateArrow(double middle)
    {
        StreamGeometry geometry = new();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, middle - ARROW_HALF_HEIGHT), true);
            context.LineTo(new Point(ARROW_TIP, middle));
            context.LineTo(new Point(0, middle + ARROW_HALF_HEIGHT));
            context.EndFigure(true);
        }

        return geometry;
    }
}
