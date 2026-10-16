using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.PropertyEditorExtension;

internal sealed class GridLengthUnitConverter(PropertyEntryViewModel entry) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? text = value as string ?? entry.DefaultValue;
        if (string.IsNullOrWhiteSpace(text) || entry.IsExpression) return null;
        try
        {
            return GridLength.Parse(text).GridUnitType;
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not GridUnitType unit || entry.IsExpression) return BindingOperations.DoNothing;
        if (unit == GridUnitType.Auto) return "Auto";
        try
        {
            string? text = entry.Value ?? entry.DefaultValue;
            GridLength current = string.IsNullOrWhiteSpace(text) ? new GridLength(1) : GridLength.Parse(text);
            double amount = current.IsAuto ? 1 : current.Value;
            return amount.ToString("R", CultureInfo.InvariantCulture) + (unit == GridUnitType.Star ? "*" : string.Empty);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException or OverflowException)
        {
            return new BindingNotification(new FormatException(
                UiStrings.GridLengthUnitNeedsValidSize, exception), BindingErrorType.DataValidationError);
        }
    }
}
