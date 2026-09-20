using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.TestControls;

public sealed class SampleThemeProvider : IDesignerThemeProvider
{
    private readonly Application _application = Application.Current ?? throw new InvalidOperationException("No preview application has been initialized.");
    private readonly ThemeVariant? _originalVariant;
    private readonly bool _hadOriginal;
    private readonly object? _original;

    public SampleThemeProvider()
    {
        _originalVariant = _application.RequestedThemeVariant;
        _hadOriginal = _application.Resources.TryGetValue("SampleThemeAccent", out _original);
    }

    public IReadOnlyList<DesignerTheme> GetThemes() => new[]
    {
        new DesignerTheme("harbor", "Harbor", true), new DesignerTheme("clay", "Clay", false)
    };

    public void ApplyTheme(string? themeId)
    {
        if (themeId is null)
        {
            if (_hadOriginal) _application.Resources["SampleThemeAccent"] = _original;
            else _application.Resources.Remove("SampleThemeAccent");
            _application.RequestedThemeVariant = _originalVariant;
            return;
        }
        _application.Resources["SampleThemeAccent"] = themeId switch
        {
            "harbor" => new SolidColorBrush(Color.Parse("#123456")),
            "clay" => new SolidColorBrush(Color.Parse("#C08050")),
            _ => throw new ArgumentException("Unknown sample theme.", nameof(themeId))
        };
        _application.RequestedThemeVariant = themeId == "harbor" ? ThemeVariant.Dark : ThemeVariant.Light;
    }
}

/// <summary>Captures the palette at construction, proving providers run before form construction.</summary>
public sealed class ThemeSensitiveControl : Border
{
    public ThemeSensitiveControl() => Background = Application.Current!.Resources.TryGetValue("SampleThemeAccent", out object? value)
        ? (IBrush)value! : new SolidColorBrush(Color.Parse("#334455"));
}
