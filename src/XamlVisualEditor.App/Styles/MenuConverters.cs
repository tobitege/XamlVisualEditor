using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace XamlVisualEditor.App.Styles;

/// <summary>Converters for the extension menu styles.</summary>
public static class MenuConverters
{
    /// <summary>Converts whether a menu item is a toggle into its check box presentation.</summary>
    public static readonly IValueConverter ToggleType =
        new FuncValueConverter<bool, MenuItemToggleType>(isToggle => isToggle ? MenuItemToggleType.CheckBox : MenuItemToggleType.None);
}
