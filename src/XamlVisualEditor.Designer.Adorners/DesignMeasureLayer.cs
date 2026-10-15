using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace XamlVisualEditor.Designer.Adorners;

/// <summary>
/// Renders a measured distance over the artboard: the direct line and its horizontal and vertical legs.
/// </summary>
public sealed class DesignMeasureLayer : Control
{
    private const double EndpointRadius = 3.0;
    private static readonly IBrush LineBrush = new ImmutableSolidColorBrush(Color.Parse("#FF8C00"));

    /// <summary>Identifies the <see cref="Start"/> property.</summary>
    public static readonly StyledProperty<Point?> StartProperty =
        AvaloniaProperty.Register<DesignMeasureLayer, Point?>(nameof(Start));

    /// <summary>Identifies the <see cref="End"/> property.</summary>
    public static readonly StyledProperty<Point?> EndProperty =
        AvaloniaProperty.Register<DesignMeasureLayer, Point?>(nameof(End));

    /// <summary>Identifies the <see cref="Zoom"/> property.</summary>
    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<DesignMeasureLayer, double>(nameof(Zoom), 1.0);

    /// <summary>Gets or sets the start pixel of the distance in unscaled coordinates.</summary>
    public Point? Start
    {
        get => GetValue(StartProperty);
        set => SetValue(StartProperty, value);
    }

    /// <summary>Gets or sets the end pixel of the distance in unscaled coordinates.</summary>
    public Point? End
    {
        get => GetValue(EndProperty);
        set => SetValue(EndProperty, value);
    }

    /// <summary>Gets or sets the scale at which the layer is shown; lines stay one screen pixel wide.</summary>
    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    static DesignMeasureLayer()
    {
        AffectsRender<DesignMeasureLayer>(StartProperty, EndProperty, ZoomProperty);
    }

    public DesignMeasureLayer()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Start is { } start && End is { } end) Draw(context, start, end, Zoom);
    }

    /// <summary>Draws the distance between two pixels in unscaled coordinates for a surface shown at <paramref name="zoom"/>.</summary>
    public static void Draw(DrawingContext context, Point start, Point end, double zoom)
    {
        double scale = double.IsFinite(zoom) && zoom > 0 ? zoom : 1;
        double thickness = 1 / scale;
        // Both points name pixels; the lines run through the pixel centers.
        Vector center = new(0.5, 0.5);
        Point from = start + center;
        Point to = end + center;
        Point corner = new(to.X, from.Y);
        Pen solid = new(LineBrush, thickness);
        Pen dashed = new(LineBrush, thickness, new DashStyle(new double[] { 4, 3 }, 0));
        context.DrawLine(dashed, from, corner);
        context.DrawLine(dashed, corner, to);
        context.DrawLine(solid, from, to);
        double radius = EndpointRadius / scale;
        context.DrawEllipse(LineBrush, null, from, radius, radius);
        context.DrawEllipse(LineBrush, null, to, radius, radius);
    }
}
