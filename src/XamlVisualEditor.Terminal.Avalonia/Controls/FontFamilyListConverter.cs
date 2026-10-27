using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace XamlVisualEditor.Terminal.Avalonia.Controls;

/// <summary>Converts a comma-separated list of font families into a <see cref="FontFamily"/>.</summary>
public sealed class FontFamilyListConverter : IValueConverter
{
    public static FontFamilyListConverter Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string families && !string.IsNullOrWhiteSpace(families)
            ? new FontFamily(families)
            : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
