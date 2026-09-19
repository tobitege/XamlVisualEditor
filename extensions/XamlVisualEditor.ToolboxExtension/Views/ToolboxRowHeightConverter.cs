using System.Globalization;
using Avalonia.Data.Converters;

namespace XamlVisualEditor.ToolboxExtension.Views;

/// <summary>Keeps each assembly's control list bounded by the available toolbox viewport.</summary>
public sealed class ToolboxRowHeightConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is double height && double.IsFinite(height) && height > 0 ? height : 240.0;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
