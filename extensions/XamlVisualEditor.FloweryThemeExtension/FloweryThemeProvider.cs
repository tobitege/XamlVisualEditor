using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Flowery;
using Flowery.Controls;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.FloweryThemeExtension;

/// <summary>Applies registered Flowery palettes only to the isolated preview application.</summary>
/// <remarks>
/// The provider offers themes only when an assembly loaded by the target application references Flowery.NET 3.2.0 or
/// later. The methods that run before that check do not use Flowery.NET types, so a project without Flowery.NET does
/// not load the copy shipped in the package.
/// </remarks>
public sealed class FloweryThemeProvider : IDesignerThemeProvider, IDisposable
{
    /// <summary>The first Flowery.NET version that offers <c>DaisyThemeManager.GetPaletteFactory</c>.</summary>
    internal static readonly Version MinimumFloweryVersion = new(3, 2, 0);

    private const string FloweryAssemblyName = "Flowery.NET";
    private readonly Application _application;
    private readonly Func<IEnumerable<Assembly>> _loadedAssemblies;
    private readonly Func<Version?> _floweryVersion;
    private IResourceDictionary? _originalResources;
    private ThemeVariant? _originalVariant;
    private string? _originalThemeName;
    private ResourceDictionary? _overlay;
    private ResourceDictionary? _palette;
    private IStyle? _addedStyle;
    private bool _targetReferencesFlowery;
    private bool _disposed;

    /// <summary>Binds the provider to the initialized target application.</summary>
    public FloweryThemeProvider() : this(() => AssemblyLoadContext.Default.Assemblies, ReadFloweryVersion)
    {
    }

    /// <summary>Binds the provider to the initialized target application.</summary>
    /// <param name="loadedAssemblies">The assemblies of the target application that are checked for a Flowery.NET reference.</param>
    /// <param name="floweryVersion">The version of the bound Flowery.NET assembly.</param>
    internal FloweryThemeProvider(Func<IEnumerable<Assembly>> loadedAssemblies, Func<Version?> floweryVersion)
    {
        _application = Application.Current ?? throw new InvalidOperationException("No preview application has been initialized.");
        _loadedAssemblies = loadedAssemblies;
        _floweryVersion = floweryVersion;
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No loaded assembly of the target application references Flowery.NET.</exception>
    /// <exception cref="NotSupportedException">The bound Flowery.NET is older than <see cref="MinimumFloweryVersion"/>.</exception>
    public IReadOnlyList<DesignerTheme> GetThemes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureSupportedTarget();
        return ReadThemes();
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
        EnsureSupportedTarget();
        Apply(themeId);
    }

    /// <summary>Returns whether an assembly other than this extension and Flowery.NET itself references Flowery.NET.</summary>
    internal static bool ReferencesFlowery(IEnumerable<Assembly> assemblies)
    {
        string? extension = typeof(FloweryThemeProvider).Assembly.GetName().Name;
        foreach (Assembly assembly in assemblies)
        {
            if (assembly.IsDynamic) continue;
            string? name = assembly.GetName().Name;
            if (name == extension || name == FloweryAssemblyName) continue;
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                if (reference.Name == FloweryAssemblyName) return true;
        }
        return false;
    }

    private void EnsureSupportedTarget()
    {
        // Assemblies stay loaded in the target's context, so a found reference remains valid.
        _targetReferencesFlowery = _targetReferencesFlowery || ReferencesFlowery(_loadedAssemblies());
        if (!_targetReferencesFlowery)
            throw new InvalidOperationException("The project does not reference Flowery.NET.");
        Version? version = _floweryVersion();
        if (version is null || version < MinimumFloweryVersion)
            throw new NotSupportedException($"Flowery.NET {version} is not supported; the preview themes require version {MinimumFloweryVersion} or later.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Version? ReadFloweryVersion() => typeof(DaisyThemeManager).Assembly.GetName().Version;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static IReadOnlyList<DesignerTheme> ReadThemes() => FloweryPalettes.ListThemes()
        .Select(theme => new DesignerTheme(theme.Id, theme.Name, theme.IsDark)).ToArray();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Apply(string themeId)
    {
        (DaisyThemeInfo info, ResourceDictionary palette) = FloweryPalettes.CreatePalette(themeId);
        DaisyUITheme? style = ContainsDaisyTheme(_application.Styles) ? null : new DaisyUITheme();

        if (_overlay is null)
        {
            _originalResources = _application.Resources;
            _originalVariant = _application.RequestedThemeVariant;
            _originalThemeName = DaisyThemeManager.CurrentThemeName;
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
        // Raises DaisyThemeManager.ThemeChanged, whose subscribers update the theme controls of the rendered document.
        DaisyThemeManager.SetCurrentTheme(info.Name);
    }

    private static bool ContainsDaisyTheme(IStyle style) => style is DaisyUITheme || style.Children.Any(ContainsDaisyTheme);

    private void Restore()
    {
        if (_overlay is not null && _originalResources is { } resources && ReferenceEquals(_application.Resources, _overlay))
        {
            _application.Resources = resources;
            _application.RequestedThemeVariant = _originalVariant;
            // DaisyThemeManager offers no way to clear the current theme name, so an application that started
            // without one keeps the name of the last applied preview theme.
            if (_originalThemeName is { } themeName) RestoreThemeName(themeName);
        }
        if (_addedStyle is not null) _application.Styles.Remove(_addedStyle);
        _addedStyle = null;
        _palette = null;
        _overlay = null;
        _originalResources = null;
        _originalVariant = null;
        _originalThemeName = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RestoreThemeName(string themeName) => DaisyThemeManager.SetCurrentTheme(themeName);

    /// <summary>Removes provider-owned resources without resetting another provider's active state.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        Restore();
        _disposed = true;
    }
}
