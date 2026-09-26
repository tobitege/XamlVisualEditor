using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XamlVisualEditor.Designer.Adorners;

/// <summary>
/// Renders a design-time grid over the artboard.
/// </summary>
public sealed class DesignGridLayer : Control
{
    /// <summary>Smallest on-screen distance between two drawn grid lines; denser lines are thinned out.</summary>
    public const double MinimumLineSpacing = 4.0;

    private const int MajorLineInterval = 8;
    private static readonly IBrush MinorBrush = new ImmutableSolidColorBrush(Color.FromArgb(45, 96, 96, 96));
    private static readonly IBrush MajorBrush = new ImmutableSolidColorBrush(Color.FromArgb(90, 96, 96, 96));

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<DesignGridLayer, bool>(nameof(ShowGrid));

    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<DesignGridLayer, double>(nameof(GridSize), 8.0);

    /// <summary>Identifies the <see cref="Zoom"/> property.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DesignGridLayer, double>(nameof(Zoom), 1.0);

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    public double GridSize
    {
        get => GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    /// <summary>Gets or sets the scale at which the layer is shown; lines stay one screen pixel wide.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    static DesignGridLayer()
    {
        AffectsRender<DesignGridLayer>(ShowGridProperty, GridSizeProperty, ZoomProperty);
    }

    public DesignGridLayer()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (ShowGrid) Draw(context, Bounds.Size, GridSize, Zoom);
    }

    /// <summary>
    /// Gets the distance between drawn lines: the grid size, doubled until the lines are at least
    /// <see cref="MinimumLineSpacing"/> screen pixels apart. Every drawn line lies on the grid. Returns 0 for an invalid grid size.
    /// </summary>
    public static double GetLineStep(double gridSize, double zoom)
    {
        if (!double.IsFinite(gridSize) || gridSize < 1)
        {
            return 0;
        }

        double scale = NormalizeZoom(zoom);
        double step = gridSize;
        while (step * scale < MinimumLineSpacing)
        {
            step *= 2;
        }

        return step;
    }

    /// <summary>Draws the grid in unscaled coordinates for a surface shown at <paramref name="zoom"/>.</summary>
    public static void Draw(DrawingContext context, Size size, double gridSize, double zoom)
    {
        double step = GetLineStep(gridSize, zoom);
        if (step <= 0 || !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        {
            return;
        }

        double scale = NormalizeZoom(zoom);
        double thickness = 1 / scale;
        // A line at x covers the first screen pixel of grid cell x instead of straddling two pixels.
        double inset = thickness / 2;
        Pen minorPen = new(MinorBrush, thickness);
        Pen majorPen = new(MajorBrush, thickness);
        double majorStep = Math.Max(step, gridSize * MajorLineInterval);

        for (int index = 0; index * step <= size.Width; index++)
        {
            double x = index * step;
            IPen pen = IsMultiple(x, majorStep) ? majorPen : minorPen;
            context.DrawLine(pen, new Point(x + inset, 0), new Point(x + inset, size.Height));
        }

        for (int index = 0; index * step <= size.Height; index++)
        {
            double y = index * step;
            IPen pen = IsMultiple(y, majorStep) ? majorPen : minorPen;
            context.DrawLine(pen, new Point(0, y + inset), new Point(size.Width, y + inset));
        }
    }

    private static double NormalizeZoom(double zoom) => double.IsFinite(zoom) && zoom > 0 ? zoom : 1;

    private static bool IsMultiple(double value, double step) => Math.Abs(Math.IEEERemainder(value, step)) < 0.001;
}
