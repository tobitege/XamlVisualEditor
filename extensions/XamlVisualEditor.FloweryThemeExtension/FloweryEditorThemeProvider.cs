using Avalonia.Controls;
using Avalonia.Media;
using Flowery.Controls;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.FloweryThemeExtension;

/// <summary>Provides Flowery palettes for XVE without switching DaisyThemeManager or Application.Current.</summary>
public sealed class FloweryEditorThemeProvider : IEditorThemeProvider
{
    /// <summary>Alpha of the base content color for secondary text.</summary>
    private const byte MutedForegroundAlpha = 160;

    /// <summary>Share of the base content color mixed into the base-300 color for borders, so they stand out on every surface.</summary>
    private const double BorderContentShare = 0.25;

    public IReadOnlyList<EditorTheme> GetThemes() => FloweryPalettes.ListThemes()
        .Select(theme => new EditorTheme(theme.Id, theme.Name, theme.IsDark)).ToArray();

    public EditorThemePalette GetPalette(string themeId)
    {
        (DaisyThemeInfo info, ResourceDictionary palette) = FloweryPalettes.CreatePalette(themeId);
        Color Read(string name) => palette[name] is Color color ? color
            : throw new InvalidOperationException("Missing Flowery palette color: " + name);
        string ColorValue(string name) => Read(name).ToString();
        Color foreground = Read("DaisyBaseContentColor");
        Color primaryContent = Read("DaisyPrimaryContentColor");
        Color surfaceAlt = Read("DaisyBase300Color");
        return new EditorThemePalette(info.IsDark, new Dictionary<EditorThemeColor, string>
        {
            [EditorThemeColor.Background] = ColorValue("DaisyBase100Color"),
            [EditorThemeColor.Surface] = ColorValue("DaisyBase200Color"),
            [EditorThemeColor.SurfaceAlt] = surfaceAlt.ToString(),
            [EditorThemeColor.Foreground] = foreground.ToString(),
            [EditorThemeColor.MutedForeground] = Color.FromArgb(MutedForegroundAlpha, foreground.R, foreground.G, foreground.B).ToString(),
            [EditorThemeColor.Border] = Mix(surfaceAlt, foreground, BorderContentShare).ToString(),
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

    private static Color Mix(Color color, Color overlay, double share) => Color.FromRgb(
        (byte)Math.Round(color.R + (overlay.R - color.R) * share),
        (byte)Math.Round(color.G + (overlay.G - color.G) * share),
        (byte)Math.Round(color.B + (overlay.B - color.B) * share));
}
