using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Flowery;
using Flowery.Controls;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.FloweryThemeExtension;

/// <summary>Applies registered Flowery palettes only to the isolated preview application.</summary>
public sealed class FloweryThemeProvider : IDesignerThemeProvider, IDisposable
{
    private readonly Application _application;
    private readonly IResourceDictionary _originalResources;
    private readonly ThemeVariant? _originalVariant;
    private readonly string? _originalThemeName;
    private ResourceDictionary? _overlay;
    private ResourceDictionary? _palette;
    private DaisyUITheme? _addedStyle;
    private bool _disposed;

    /// <summary>Captures the initialized target application's theme state.</summary>
    public FloweryThemeProvider()
    {
        _application = Application.Current ?? throw new InvalidOperationException("No preview application has been initialized.");
        _originalResources = _application.Resources;
        _originalVariant = _application.RequestedThemeVariant;
        _originalThemeName = DaisyThemeManager.CurrentThemeName;
    }

    /// <inheritdoc />
    public IReadOnlyList<DesignerTheme> GetThemes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return DaisyThemeManager.AvailableThemes
            .Select(theme => new DesignerTheme(theme.Name.ToLowerInvariant(), theme.Name, theme.IsDark)).ToArray();
    }

    /// <inheritdoc />
    public void ApplyTheme(string? themeId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (themeId is null)
        {
            Restore();
            return;
        }
        DaisyThemeInfo info = DaisyThemeManager.GetThemeInfo(themeId)
            ?? throw new ArgumentException("The requested Flowery theme is not registered.", nameof(themeId));
        Func<ResourceDictionary> factory = DaisyThemeManager.GetPaletteFactory(info.Name)
            ?? throw new InvalidOperationException("The requested Flowery theme has no palette factory.");
        ResourceDictionary palette = factory()
            ?? throw new InvalidOperationException("The Flowery palette factory returned no resources.");
        DaisyUITheme? style = ContainsDaisyTheme(_application.Styles) ? null : new DaisyUITheme();

        if (_overlay is null)
        {
            _overlay = new ResourceDictionary();
            _overlay.MergedDictionaries.Add(_originalResources);
        }
        if (_palette is not null) _overlay.MergedDictionaries.Remove(_palette);
        _overlay.MergedDictionaries.Add(palette);
        _palette = palette;
        _application.Resources = _overlay;
        if (style is not null)
        {
            _addedStyle = style;
            _application.Styles.Add(style);
        }
        _application.RequestedThemeVariant = info.IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        DaisyThemeManager.SetCurrentTheme(info.Name);
    }

    private static bool ContainsDaisyTheme(IStyle style) => style is DaisyUITheme || style.Children.Any(ContainsDaisyTheme);

    private void Restore()
    {
        if (_overlay is not null && ReferenceEquals(_application.Resources, _overlay))
        {
            _application.Resources = _originalResources;
            _application.RequestedThemeVariant = _originalVariant;
            // The manager stores a nullable name; preserve an initially unset theme.
            DaisyThemeManager.SetCurrentTheme(_originalThemeName!);
        }
        if (_addedStyle is not null) _application.Styles.Remove(_addedStyle);
        _addedStyle = null;
        _palette = null;
        _overlay = null;
    }

    /// <summary>Removes provider-owned resources without resetting another provider's active state.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        _disposed = true;
    }
}
