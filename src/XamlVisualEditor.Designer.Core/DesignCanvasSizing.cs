using Avalonia;

namespace XamlVisualEditor.Designer.Core;

/// <summary>
/// Computes the design artboard size for a document root. The artboard clips its
/// content, so a document without an explicit size must grow with what it renders.
/// </summary>
public static class DesignCanvasSizing
{
    /// <summary>Default artboard width for documents that declare nothing.</summary>
    public const double DefaultWidth = 800;

    /// <summary>Default artboard height for documents that declare nothing.</summary>
    public const double DefaultHeight = 600;

    /// <summary>
    /// Upper bound for measuring the rendered root. A finite bound keeps virtualizing
    /// panels and scroll viewers from realizing their entire content.
    /// </summary>
    public const double MaxMeasuredExtent = 4096;

    /// <summary>Gets the size constraint used to measure the rendered root.</summary>
    public static Size MeasureConstraint { get; } = new(MaxMeasuredExtent, MaxMeasuredExtent);

    /// <summary>
    /// Resolves the artboard size: an explicit size (DesignWidth/DesignHeight or
    /// Width/Height) wins unchanged; otherwise the declared minimum (or the default)
    /// grows to the measured content size when one is available.
    /// </summary>
    public static (double Width, double Height) Compute(
        double? explicitWidth,
        double? explicitHeight,
        double? minWidth,
        double? minHeight,
        double? measuredWidth,
        double? measuredHeight)
    {
        return (Resolve(explicitWidth, minWidth, measuredWidth, DefaultWidth),
            Resolve(explicitHeight, minHeight, measuredHeight, DefaultHeight));
    }

    private static double Resolve(double? explicitValue, double? minimum, double? measured, double fallback)
    {
        if (explicitValue.HasValue)
        {
            return explicitValue.Value;
        }

        double value = minimum ?? fallback;
        return measured is { } content && double.IsFinite(content) ? Math.Max(value, content) : value;
    }
}
