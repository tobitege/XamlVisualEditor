using System;
using System.Globalization;
using Avalonia;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Designer.Core;

/// <summary>
/// Values of the coordinate strip below a design surface. Positions are logical pixels relative to the
/// top-left corner of the designed root element and do not depend on the zoom level.
/// </summary>
public sealed partial class DesignerCoordinateReadout : ReactiveObject
{
    // Pointer positions pass through a render transform; this keeps an exact pixel edge in its own pixel.
    private const double PixelTolerance = 1e-6;

    private Rect? _selection;
    private Rect? _changeOrigin;

    /// <summary>Gets the pixel column under the pointer, or null while the pointer is outside the viewport.</summary>
    [Reactive]
    public partial double? PointerX { get; private set; }

    /// <summary>Gets the pixel row under the pointer, or null while the pointer is outside the viewport.</summary>
    [Reactive]
    public partial double? PointerY { get; private set; }

    /// <summary>Gets the displayed pointer position.</summary>
    [Reactive]
    public partial string? PointerText { get; private set; }

    /// <summary>Gets the pointer position as <c>x,y</c> for the clipboard.</summary>
    [Reactive]
    public partial string? PointerValue { get; private set; }

    /// <summary>Gets the type and name of the element under the pointer, as <c>Type#Name</c>.</summary>
    [Reactive]
    public partial string? HoverText { get; private set; }

    /// <summary>Gets the displayed position and size of the selection.</summary>
    [Reactive]
    public partial string? SelectionText { get; private set; }

    /// <summary>Gets the selection bounds as <c>x,y,width,height</c> for the clipboard.</summary>
    [Reactive]
    public partial string? SelectionValue { get; private set; }

    /// <summary>Gets the change of the selection bounds since <see cref="BeginChange"/>, or null without a change.</summary>
    [Reactive]
    public partial string? ChangeText { get; private set; }

    /// <summary>Gets the start of the measured distance.</summary>
    [Reactive]
    public partial Point? MeasureStart { get; private set; }

    /// <summary>Gets the end of the measured distance.</summary>
    [Reactive]
    public partial Point? MeasureEnd { get; private set; }

    /// <summary>Gets the displayed measured distance.</summary>
    [Reactive]
    public partial string? MeasureText { get; private set; }

    /// <summary>Gets the measured distance as <c>dx,dy</c> for the clipboard.</summary>
    [Reactive]
    public partial string? MeasureValue { get; private set; }

    /// <summary>Sets the pointer position in root coordinates; null clears it.</summary>
    public void SetPointer(Point? position)
    {
        if (position is not { } point || !double.IsFinite(point.X) || !double.IsFinite(point.Y))
        {
            PointerX = null;
            PointerY = null;
            PointerText = null;
            PointerValue = null;
            return;
        }

        Point pixel = ToPixel(point);
        PointerX = pixel.X;
        PointerY = pixel.Y;
        PointerText = Format(UiStrings.CoordinateStripPointer, pixel.X, pixel.Y);
        PointerValue = FormatValue(pixel.X, pixel.Y);
    }

    /// <summary>Sets the element under the pointer; a null type name clears it.</summary>
    public void SetHover(string? typeName, string? name)
    {
        HoverText = string.IsNullOrEmpty(typeName) ? null
            : string.IsNullOrEmpty(name) ? typeName
            : typeName + "#" + name;
    }

    /// <summary>Sets the union of the selected elements in root coordinates; null clears it.</summary>
    public void SetSelection(Rect? bounds, int count)
    {
        _selection = bounds is { } rect && IsFinite(rect) ? rect : null;
        if (_selection is not { } selection)
        {
            SelectionText = null;
            SelectionValue = null;
        }
        else
        {
            string text = Format(UiStrings.CoordinateStripSelection, selection.X, selection.Y, selection.Width, selection.Height);
            SelectionText = count > 1 ? Format(UiStrings.CoordinateStripSelectionCount, text, count) : text;
            SelectionValue = FormatValue(selection.X, selection.Y, selection.Width, selection.Height);
        }

        UpdateChange();
    }

    /// <summary>Remembers the current selection bounds as the origin of a move or resize.</summary>
    public void BeginChange()
    {
        _changeOrigin = _selection;
        UpdateChange();
    }

    /// <summary>Ends a move or resize and clears its change.</summary>
    public void EndChange()
    {
        _changeOrigin = null;
        UpdateChange();
    }

    /// <summary>Sets a measured distance between two points in root coordinates.</summary>
    public void SetMeasure(Point start, Point end)
    {
        Point from = ToPixel(start);
        Point to = ToPixel(end);
        MeasureStart = from;
        MeasureEnd = to;
        double dx = to.X - from.X;
        double dy = to.Y - from.Y;
        MeasureText = Format(UiStrings.CoordinateStripMeasure, FormatSigned(dx), FormatSigned(dy),
            Math.Sqrt(dx * dx + dy * dy).ToString("0.#", CultureInfo.CurrentCulture));
        MeasureValue = FormatValue(dx, dy);
    }

    /// <summary>Clears the measured distance.</summary>
    public void ClearMeasure()
    {
        MeasureStart = null;
        MeasureEnd = null;
        MeasureText = null;
        MeasureValue = null;
    }

    /// <summary>Gets the pixel that contains a position; negative positions lie left of or above the root.</summary>
    public static Point ToPixel(Point position) =>
        new(Math.Floor(position.X + PixelTolerance), Math.Floor(position.Y + PixelTolerance));

    private void UpdateChange()
    {
        if (_changeOrigin is not { } origin || _selection is not { } current || AreClose(current, origin))
        {
            ChangeText = null;
            return;
        }

        ChangeText = Format(UiStrings.CoordinateStripChange,
            FormatSigned(current.X - origin.X), FormatSigned(current.Y - origin.Y),
            FormatSigned(current.Width - origin.Width), FormatSigned(current.Height - origin.Height));
    }

    // Differences below the displayed precision of two decimals count as no change.
    private static bool AreClose(Rect first, Rect second) =>
        Math.Abs(first.X - second.X) < 0.005 && Math.Abs(first.Y - second.Y) < 0.005
        && Math.Abs(first.Width - second.Width) < 0.005 && Math.Abs(first.Height - second.Height) < 0.005;

    private static bool IsFinite(Rect rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) && double.IsFinite(rect.Width) && double.IsFinite(rect.Height);

    private static string Format(string format, params object[] values)
    {
        for (int index = 0; index < values.Length; index++)
        {
            if (values[index] is double number) values[index] = FormatNumber(number);
        }

        return string.Format(CultureInfo.CurrentCulture, format, values);
    }

    private static string FormatNumber(double value) => Displayed(value).ToString("0.##", CultureInfo.CurrentCulture);

    private static string FormatSigned(double value) => Displayed(value) > 0 ? "+" + FormatNumber(value) : FormatNumber(value);

    private static string FormatValue(params double[] values)
    {
        string[] parts = new string[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            parts[index] = Displayed(values[index]).ToString("0.##", CultureInfo.InvariantCulture);
        }

        return string.Join(",", parts);
    }

    // Rounds to the displayed precision first, so that a tiny negative value does not show as "-0".
    private static double Displayed(double value)
    {
        double rounded = Math.Round(value, 2, MidpointRounding.AwayFromZero);
        return rounded == 0 ? 0 : rounded;
    }
}
