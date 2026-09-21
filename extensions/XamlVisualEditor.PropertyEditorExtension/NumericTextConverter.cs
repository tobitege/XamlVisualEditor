using System.Globalization;
using Avalonia.Data.Converters;

namespace XamlVisualEditor.PropertyEditorExtension;

/// <summary>Übersetzt Zahlentexte zwischen Anzeige- und XAML-Format und erhält ungültige Eingaben zur Validierung.</summary>
public sealed class NumericTextConverter : IValueConverter
{
    /// <inheritdoc />
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConvertNumber(value, CultureInfo.InvariantCulture, culture);

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ConvertNumber(value, culture, CultureInfo.InvariantCulture);

    private static object? ConvertNumber(object? value, CultureInfo source, CultureInfo target) =>
        value is string text && double.TryParse(text, NumberStyles.Float, source, out double number)
            ? number.ToString(target)
            : value;
}
