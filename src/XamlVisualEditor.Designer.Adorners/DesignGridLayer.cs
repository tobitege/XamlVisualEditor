using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XamlVisualEditor.Designer.Adorners;

/// <summary>
/// Renders a design-time grid over the artboard.
/// </summary>
public sealed class DesignGridLayer : Control
{
    private static readonly IPen MinorPen = new Pen(new SolidColorBrush(Color.FromArgb(45, 96, 96, 96)), 1);
    private static readonly IPen MajorPen = new Pen(new SolidColorBrush(Color.FromArgb(90, 96, 96, 96)), 1);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<DesignGridLayer, bool>(nameof(ShowGrid));

    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<DesignGridLayer, double>(nameof(GridSize), 8.0);

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

    public DesignGridLayer()
    {
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (ShowGrid) Draw(context, Bounds.Size, GridSize);
    }

    public static void Draw(DrawingContext context, Size size, double gridSize)
    {
        if (!double.IsFinite(gridSize) || gridSize <= 1 ||
            !double.IsFinite(size.Width) || !double.IsFinite(size.Height))
        {
            return;
        }

        double width = size.Width;
        double height = size.Height;
        double majorStep = gridSize * 8;

        for (double x = 0; x <= width; x += gridSize)
        {
            IPen pen = Math.Abs(x % majorStep) < 0.1 ? MajorPen : MinorPen;
            context.DrawLine(pen, new Point(x, 0), new Point(x, height));
        }

        for (double y = 0; y <= height; y += gridSize)
        {
            IPen pen = Math.Abs(y % majorStep) < 0.1 ? MajorPen : MinorPen;
            context.DrawLine(pen, new Point(0, y), new Point(width, y));
        }
    }
}
