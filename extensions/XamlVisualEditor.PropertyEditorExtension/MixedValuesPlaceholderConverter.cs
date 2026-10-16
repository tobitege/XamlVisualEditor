using System;
using System.Globalization;
using Avalonia.Data.Converters;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.PropertyEditorExtension;

/// <summary>
/// Converts <see cref="PropertyEntryViewModel.IsMixed" /> to the placeholder of a value field: the mixed-values text
/// while the selected controls have different values, otherwise the converter parameter.
/// </summary>
public sealed class MixedValuesPlaceholderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? UiStrings.MixedValuesPlaceholder : parameter;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
