using System;
using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using Avalonia.Media;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Checks literal values using their declared CLR conversion rules.</summary>
internal static class PropertyValueValidator
{
    public static void Validate(PropertyMetadata property, string? value)
    {
        if (value is null || property.ClrType is not { } declaredType) return;
        Type type = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
        if (type == typeof(string) || type == typeof(object)) return;
        string expression = value.TrimStart();
        if (expression.StartsWith('{') && !expression.StartsWith("{}", StringComparison.Ordinal)) return;
        if (value.StartsWith("{}", StringComparison.Ordinal)) value = value[2..];
        if (type == typeof(IBrush)) type = typeof(Brush);

        try
        {
            if (type.IsEnum)
            {
                object parsed = Enum.Parse(type, value, ignoreCase: true);
                if (!type.IsDefined(typeof(FlagsAttribute), inherit: false) && !Enum.IsDefined(type, parsed))
                    throw new FormatException(UiStrings.PropertyValueEnumUndefined);
                return;
            }
            TypeConverter converter = TypeDescriptor.GetConverter(type);
            if (converter.CanConvertFrom(typeof(string)))
            {
                converter.ConvertFrom(null, CultureInfo.InvariantCulture, value);
                return;
            }

            MethodInfo? parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { typeof(string), typeof(IFormatProvider) }, modifiers: null);
            if (parse is not null && type.IsAssignableFrom(parse.ReturnType))
            {
                parse.Invoke(null, new object[] { value, CultureInfo.InvariantCulture });
                return;
            }
            parse = type.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { typeof(string) }, modifiers: null);
            if (parse is not null && type.IsAssignableFrom(parse.ReturnType)) parse.Invoke(null, new object[] { value });
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException
            or NotSupportedException or TargetInvocationException)
        {
            throw new ArgumentException(
                string.Format(UiStrings.PropertyValueTypeInvalid, property.Name, type.Name, exception.GetBaseException().Message), exception);
        }
    }
}
