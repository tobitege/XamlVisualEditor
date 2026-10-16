using System;
using System.Collections.Generic;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

public sealed class ExtensionContributionRegistry : IExtensionContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<ExtensionMenuContribution>> _menuByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionToolbarContribution>> _toolbarByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionCommandPaletteContribution>> _paletteByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionPropertyEditorContribution>> _propertyEditorsByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ExtensionViewContribution>> _viewsByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DesignerThemeProviderRegistration>> _themesByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<EditorThemeProviderRegistration>> _editorThemesByExtension = new(StringComparer.Ordinal);
    private List<ExtensionMenuContribution> _menuItems = new();
    private List<ExtensionToolbarContribution> _toolbarItems = new();
    private List<ExtensionCommandPaletteContribution> _paletteItems = new();
    private List<ExtensionPropertyEditorContribution> _propertyEditorItems = new();
    private List<ExtensionViewContribution> _viewItems = new();
    private List<DesignerThemeProviderRegistration> _themeItems = new();
    private List<EditorThemeProviderRegistration> _editorThemeItems = new();

    public event EventHandler? Changed;

    public IReadOnlyList<ExtensionMenuContribution> MenuItems => _menuItems;

    public IReadOnlyList<ExtensionToolbarContribution> ToolbarItems => _toolbarItems;

    public IReadOnlyList<ExtensionCommandPaletteContribution> CommandPaletteItems => _paletteItems;

    public IReadOnlyList<ExtensionPropertyEditorContribution> PropertyEditors => _propertyEditorItems;

    public IReadOnlyList<ExtensionViewContribution> ViewContributions => _viewItems;

    public IReadOnlyList<DesignerThemeProviderRegistration> DesignerThemeProviders => _themeItems;

    public IReadOnlyList<EditorThemeProviderRegistration> EditorThemeProviders => _editorThemeItems;

    public IDisposable RegisterEditorThemeProviders(string extensionId, IReadOnlyList<EditorThemeProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (EditorThemeProviderRegistration provider in providers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.TypeName);
            if (!ids.Add(provider.Id) || !System.IO.Path.IsPathFullyQualified(provider.AssemblyPath))
                throw new ArgumentException("Theme providers require unique IDs and absolute assembly paths.", nameof(providers));
        }
        return RegisterItems(extensionId, providers, _editorThemesByExtension, () => _editorThemeItems = BuildList(_editorThemesByExtension));
    }

    public IDisposable RegisterDesignerThemeProviders(string extensionId, IReadOnlyList<DesignerThemeProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (DesignerThemeProviderRegistration provider in providers)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(provider.TypeName);
            if (!ids.Add(provider.Id) || !System.IO.Path.IsPathFullyQualified(provider.AssemblyPath))
                throw new ArgumentException("Theme providers require unique IDs and absolute assembly paths.", nameof(providers));
        }
        return RegisterItems(extensionId, providers, _themesByExtension, () => _themeItems = BuildList(_themesByExtension));
    }

    public IDisposable RegisterMenuItems(string extensionId, IReadOnlyList<ExtensionMenuContribution> items)
    {
        return RegisterItems(extensionId, items, _menuByExtension, RebuildMenus);
    }

    public IDisposable RegisterToolbarItems(string extensionId, IReadOnlyList<ExtensionToolbarContribution> items)
    {
        return RegisterItems(extensionId, items, _toolbarByExtension, RebuildToolbars);
    }

    public IDisposable RegisterCommandPaletteItems(string extensionId, IReadOnlyList<ExtensionCommandPaletteContribution> items)
    {
        return RegisterItems(extensionId, items, _paletteByExtension, RebuildPalette);
    }

    public IDisposable RegisterPropertyEditors(string extensionId, IReadOnlyList<ExtensionPropertyEditorContribution> editors)
    {
        return RegisterItems(extensionId, editors, _propertyEditorsByExtension, RebuildPropertyEditors);
    }

    public IDisposable RegisterViews(string extensionId, IReadOnlyList<ExtensionViewContribution> views)
    {
        return RegisterItems(extensionId, views, _viewsByExtension, RebuildViews);
    }

    private IDisposable RegisterItems<T>(
        string extensionId,
        IReadOnlyList<T> items,
        Dictionary<string, List<T>> target,
        Action rebuild)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("Extension id is required.", nameof(extensionId));
        }

        items ??= Array.Empty<T>();

        List<T> registration = new(items);
        lock (_gate)
        {
            target[extensionId] = registration;
            rebuild();
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return new Registration(() => Unregister(extensionId, target, rebuild, registration));
    }

    private void Unregister<T>(string extensionId, Dictionary<string, List<T>> target, Action rebuild, List<T> expected)
    {
        lock (_gate)
        {
            if (!target.TryGetValue(extensionId, out List<T>? current) || !ReferenceEquals(current, expected))
            {
                return;
            }
            target.Remove(extensionId);
            rebuild();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildMenus()
    {
        _menuItems = BuildList(_menuByExtension);
    }

    private void RebuildToolbars()
    {
        _toolbarItems = BuildList(_toolbarByExtension);
    }

    private void RebuildPalette()
    {
        _paletteItems = BuildList(_paletteByExtension);
    }

    private void RebuildPropertyEditors()
    {
        _propertyEditorItems = BuildList(_propertyEditorsByExtension);
    }

    private void RebuildViews()
    {
        _viewItems = BuildList(_viewsByExtension);
    }

    private static List<T> BuildList<T>(Dictionary<string, List<T>> map)
    {
        List<T> results = new();
        foreach (List<T> items in map.Values)
        {
            results.AddRange(items);
        }

        return results;
    }

    private sealed class Registration : IDisposable
    {
        private readonly Action _dispose;
        private bool _isDisposed;

        public Registration(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _dispose();
            _isDisposed = true;
        }
    }
}
