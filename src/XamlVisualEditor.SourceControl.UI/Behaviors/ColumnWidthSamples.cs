using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace XamlVisualEditor.SourceControl.UI.Behaviors;

/// <summary>
/// Sizes the data grid column of a cell text block so that the widest of the sample texts fits, measured with the
/// font of the text block. The column gets that width and keeps it as its minimum, so it does not depend on the
/// loaded rows and a star column next to it cannot squeeze it; the user can still make it wider.
/// </summary>
/// <remarks>
/// The text block is the content of a <see cref="DataGridTemplateColumn"/> cell and stretches across the cell; the
/// space around it, the cell padding, its margin and the grid line, is taken from the arranged cell. With
/// <see cref="WidestDigitsProperty"/> every digit of a sample is replaced by the widest digit of the font, so a
/// sample is at least as wide as every text of its form with other digits, in fonts without kerning between digits,
/// such as Inter.
/// </remarks>
public static class ColumnWidthSamples
{
    /// <summary>The sample texts the column of the cell must fit.</summary>
    public static readonly AttachedProperty<IReadOnlyList<string>?> TextsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<string>?>("Texts", typeof(ColumnWidthSamples));

    /// <summary>Whether every digit of the sample texts is measured as the widest digit of the font.</summary>
    public static readonly AttachedProperty<bool> WidestDigitsProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, bool>("WidestDigits", typeof(ColumnWidthSamples));

    private static readonly Dictionary<SampleKey, double> MeasuredWidths = new();

    static ColumnWidthSamples()
    {
        TextsProperty.Changed.AddClassHandler<TextBlock>(OnTextsChanged);
    }

    /// <summary>Gets the sample texts of a cell text block.</summary>
    public static IReadOnlyList<string>? GetTexts(TextBlock text) => text.GetValue(TextsProperty);

    /// <summary>Sets the sample texts of a cell text block.</summary>
    public static void SetTexts(TextBlock text, IReadOnlyList<string>? value) => text.SetValue(TextsProperty, value);

    /// <summary>Gets whether every digit of the samples of a cell text block is measured as the widest digit.</summary>
    public static bool GetWidestDigits(TextBlock text) => text.GetValue(WidestDigitsProperty);

    /// <summary>Sets whether every digit of the samples of a cell text block is measured as the widest digit.</summary>
    public static void SetWidestDigits(TextBlock text, bool value) => text.SetValue(WidestDigitsProperty, value);

    /// <summary>
    /// Returns the width of the widest of <paramref name="samples"/> in the font of <paramref name="text"/>; with
    /// <paramref name="widestDigits"/>, of the widest of the samples with all their digits set to the same digit, which
    /// for the widest digit of a font without kerning between digits is at least as wide as the sample with any digits.
    /// </summary>
    public static double MeasureWidest(TextBlock text, IReadOnlyList<string> samples, bool widestDigits = false)
    {
        // Keyed by the resolved font, not only by its family name, so a font collection added later is measured anew.
        Typeface typeface = new(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch);
        SampleKey key = new(string.Join('\n', samples), widestDigits, typeface, typeface.GlyphTypeface, text.FontSize);
        if (MeasuredWidths.TryGetValue(key, out double width))
        {
            return width;
        }

        // Each sample is measured once per digit, with all its digits set to that digit: whole texts, because layout
        // rounds the width of a single digit to whole pixels, which can hide the widest one.
        IEnumerable<string> measured = widestDigits
            ? "0123456789".SelectMany(digit => samples.Select(sample => new string(sample.Select(c => char.IsAsciiDigit(c) ? digit : c).ToArray())))
            : samples;
        width = measured.Max(sample => Measure(text, sample));
        MeasuredWidths[key] = width;
        return width;
    }

    /// <summary>Returns the width of <paramref name="value"/> in the font of <paramref name="text"/>.</summary>
    private static double Measure(TextBlock text, string value)
    {
        TextBlock probe = new()
        {
            Text = value,
            FontFamily = text.FontFamily,
            FontSize = text.FontSize,
            FontStyle = text.FontStyle,
            FontWeight = text.FontWeight,
            FontStretch = text.FontStretch
        };
        probe.Measure(Size.Infinity);
        return probe.DesiredSize.Width;
    }

    private static void OnTextsChanged(TextBlock text, AvaloniaPropertyChangedEventArgs e)
    {
        text.PropertyChanged -= OnTextPropertyChanged;
        if (e.NewValue is IReadOnlyList<string>)
        {
            text.PropertyChanged += OnTextPropertyChanged;
        }
    }

    private static void OnTextPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // A new size comes with the first layout and a resized column.
        if (sender is TextBlock text && e.Property == Visual.BoundsProperty)
        {
            // The cell gets its new bounds only after its content was arranged.
            Dispatcher.UIThread.Post(() => FitColumn(text), DispatcherPriority.Loaded);
        }
    }

    private static void FitColumn(TextBlock text)
    {
        if (GetTexts(text) is not { Count: > 0 } samples
            || text.FindAncestorOfType<DataGridCell>() is not { OwningColumn: { } column } cell)
        {
            return;
        }

        double width = System.Math.Ceiling(MeasureWidest(text, samples, GetWidestDigits(text)) + cell.Bounds.Width - text.Bounds.Width);
        if (column.MinWidth < width)
        {
            column.MinWidth = width;
        }

        if (column.ActualWidth < width)
        {
            column.Width = new DataGridLength(width);
        }
    }

    private readonly record struct SampleKey(
        string Samples,
        bool WidestDigits,
        Typeface Typeface,
        GlyphTypeface GlyphTypeface,
        double FontSize);
}
