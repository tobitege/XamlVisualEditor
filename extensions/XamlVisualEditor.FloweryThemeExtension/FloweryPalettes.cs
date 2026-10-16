using Avalonia.Controls;
using Flowery.Controls;

namespace XamlVisualEditor.FloweryThemeExtension;

/// <summary>Reads the themes registered with DaisyThemeManager without applying one.</summary>
internal static class FloweryPalettes
{
    /// <summary>Returns the registered themes in the manager's order; IDs are the lowercase registered names.</summary>
    public static IEnumerable<(string Id, string Name, bool IsDark)> ListThemes() =>
        DaisyThemeManager.AvailableThemes.Select(theme => (theme.Name.ToLowerInvariant(), theme.Name, theme.IsDark));

    /// <summary>Returns the registered theme and a new instance of its palette.</summary>
    /// <exception cref="ArgumentException">The theme is not registered.</exception>
    /// <exception cref="InvalidOperationException">The theme has no palette.</exception>
    public static (DaisyThemeInfo Info, ResourceDictionary Palette) CreatePalette(string themeId)
    {
        DaisyThemeInfo info = DaisyThemeManager.GetThemeInfo(themeId)
            ?? throw new ArgumentException("The requested Flowery theme is not registered.", nameof(themeId));
        Func<ResourceDictionary> factory = DaisyThemeManager.GetPaletteFactory(info.Name)
            ?? throw new InvalidOperationException("The requested Flowery theme has no palette factory.");
        ResourceDictionary palette = factory()
            ?? throw new InvalidOperationException("The Flowery palette factory returned no resources.");
        return (info, palette);
    }
}
