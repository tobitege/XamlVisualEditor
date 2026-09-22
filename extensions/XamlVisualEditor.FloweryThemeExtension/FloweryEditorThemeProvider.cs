using Avalonia.Controls;
using Avalonia.Media;
using Flowery.Controls;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.FloweryThemeExtension;

/// <summary>Liefert Flowery-Paletten für XVE, ohne DaisyThemeManager oder Application.Current umzustellen.</summary>
public sealed class FloweryEditorThemeProvider : IEditorThemeProvider
{
    public IReadOnlyList<EditorTheme> GetThemes() => DaisyThemeManager.AvailableThemes
        .Select(theme => new EditorTheme(theme.Name.ToLowerInvariant(), theme.Name, theme.IsDark)).ToArray();

    public EditorThemePalette GetPalette(string themeId)
    {
        DaisyThemeInfo info = DaisyThemeManager.GetThemeInfo(themeId)
            ?? throw new ArgumentException("The requested Flowery theme is not registered.", nameof(themeId));
        ResourceDictionary palette = DaisyThemeManager.GetPaletteFactory(info.Name)?.Invoke()
            ?? throw new InvalidOperationException("The requested Flowery theme has no palette.");
        Color Read(string name) => palette[name] is Color color ? color
            : throw new InvalidOperationException("Missing Flowery palette color: " + name);
        string ColorValue(string name) => Read(name).ToString();
        Color foreground = Read("DaisyBaseContentColor");
        // Forest erhält auf den grünen Editor-Akzentflächen eine helle Beschriftung.
        Color primaryContent = string.Equals(info.Name, "Forest", StringComparison.OrdinalIgnoreCase)
            ? Colors.White : Read("DaisyPrimaryContentColor");
        return new EditorThemePalette(info.IsDark, new Dictionary<EditorThemeColor, string>
        {
            [EditorThemeColor.Background] = ColorValue("DaisyBase100Color"),
            [EditorThemeColor.Surface] = ColorValue("DaisyBase200Color"),
            [EditorThemeColor.SurfaceAlt] = ColorValue("DaisyBase300Color"),
            [EditorThemeColor.Foreground] = foreground.ToString(),
            [EditorThemeColor.MutedForeground] = Color.FromArgb(160, foreground.R, foreground.G, foreground.B).ToString(),
            [EditorThemeColor.Border] = ColorValue("DaisyBase300Color"),
            [EditorThemeColor.Accent] = ColorValue("DaisyPrimaryColor"),
            [EditorThemeColor.AccentForeground] = primaryContent.ToString(),
            [EditorThemeColor.Selection] = ColorValue("DaisyPrimaryColor"),
            [EditorThemeColor.SelectionForeground] = primaryContent.ToString(),
            [EditorThemeColor.Error] = ColorValue("DaisyErrorColor"),
            [EditorThemeColor.ErrorForeground] = ColorValue("DaisyErrorContentColor"),
            [EditorThemeColor.Warning] = ColorValue("DaisyWarningColor"),
            [EditorThemeColor.WarningForeground] = ColorValue("DaisyWarningContentColor"),
            [EditorThemeColor.Success] = ColorValue("DaisySuccessColor"),
            [EditorThemeColor.SuccessForeground] = ColorValue("DaisySuccessContentColor")
        });
    }
}
