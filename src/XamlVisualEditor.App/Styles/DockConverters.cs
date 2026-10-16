using Avalonia.Data.Converters;

namespace XamlVisualEditor.App.Styles;

/// <summary>Converters for the Dock styles of the editor chrome.</summary>
public static class DockConverters
{
    /// <summary>Converts the number of tools of a dock to whether the dock is shown.</summary>
    public static readonly IValueConverter HasDockables = new FuncValueConverter<int, bool>(count => count > 0);
}
