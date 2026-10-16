using System;
using System.Collections.Generic;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Keeps every registration as its own list per extension. The published lists keep the registrations of one
/// extension in call order; the order of different extensions is unspecified.
/// </summary>
public sealed class ExtensionContributionRegistry : IExtensionContributionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<List<ExtensionMenuContribution>>> _menuByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<ExtensionToolbarContribution>>> _toolbarByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<ExtensionCommandPaletteContribution>>> _paletteByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<ExtensionPropertyEditorContribution>>> _propertyEditorsByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<ExtensionViewContribution>>> _viewsByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<DesignerThemeProviderRegistration>>> _themesByExtension = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<List<EditorThemeProviderRegistration>>> _editorThemesByExtension = new(StringComparer.Ordinal);
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
        foreach (EditorThemeProviderRegistration provider in providers)
        {
            ValidateThemeProvider(provider.Id, provider.Name, provider.TypeName, provider.AssemblyPath, nameof(providers));
        }
        return RegisterItems(extensionId, providers, _editorThemesByExtension,
            () => _editorThemeItems = BuildList(_editorThemesByExtension),
            new UniqueIds<EditorThemeProviderRegistration>("Editor theme provider", nameof(providers), provider => provider.Id));
    }

    public IDisposable RegisterDesignerThemeProviders(string extensionId, IReadOnlyList<DesignerThemeProviderRegistration> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        foreach (DesignerThemeProviderRegistration provider in providers)
        {
            ValidateThemeProvider(provider.Id, provider.Name, provider.TypeName, provider.AssemblyPath, nameof(providers));
        }
        return RegisterItems(extensionId, providers, _themesByExtension,
            () => _themeItems = BuildList(_themesByExtension),
            new UniqueIds<DesignerThemeProviderRegistration>("Preview theme provider", nameof(providers), provider => provider.Id));
    }

    private static void ValidateThemeProvider(string id, string name, string typeName, string assemblyPath, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        if (!System.IO.Path.IsPathFullyQualified(assemblyPath))
            throw new ArgumentException("Theme providers require absolute assembly paths.", parameterName);
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

    /// <summary>
    /// Adds one registration; <paramref name="uniqueIds"/> rejects an ID that appears twice in the registration or
    /// that an active registration already holds.
    /// </summary>
    private IDisposable RegisterItems<T>(
        string extensionId,
        IReadOnlyList<T> items,
        Dictionary<string, List<List<T>>> target,
        Action rebuild,
        UniqueIds<T>? uniqueIds = null)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("Extension id is required.", nameof(extensionId));
        }

        items ??= Array.Empty<T>();

        List<T> registration = new(items);
        lock (_gate)
        {
            if (uniqueIds is not null)
            {
                EnsureIdsAreFree(target, registration, uniqueIds);
            }

            if (!target.TryGetValue(extensionId, out List<List<T>>? registrations))
            {
                registrations = new();
                target.Add(extensionId, registrations);
            }

            registrations.Add(registration);
            rebuild();
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return new Registration(() => Unregister(extensionId, target, rebuild, registration));
    }

    private static void EnsureIdsAreFree<T>(Dictionary<string, List<List<T>>> target, List<T> registration, UniqueIds<T> uniqueIds)
    {
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (T item in registration)
        {
            string id = uniqueIds.GetId(item);
            if (!ids.Add(id))
            {
                throw uniqueIds.Duplicate(id);
            }
        }

        foreach (List<List<T>> registrations in target.Values)
        {
            foreach (List<T> existing in registrations)
            {
                foreach (T item in existing)
                {
                    string id = uniqueIds.GetId(item);
                    if (ids.Contains(id))
                    {
                        throw uniqueIds.Duplicate(id);
                    }
                }
            }
        }
    }

    /// <summary>Names the items whose IDs must be unique, for the message of a rejected registration.</summary>
    private sealed record UniqueIds<T>(string Subject, string ParameterName, Func<T, string> GetId)
    {
        public ArgumentException Duplicate(string id) =>
            new(Subject + " ID is not unique: " + id, ParameterName);
    }

    private void Unregister<T>(string extensionId, Dictionary<string, List<List<T>>> target, Action rebuild, List<T> registration)
    {
        lock (_gate)
        {
            if (!target.TryGetValue(extensionId, out List<List<T>>? registrations))
            {
                return;
            }

            int index = registrations.FindIndex(current => ReferenceEquals(current, registration));
            if (index < 0)
            {
                return;
            }

            registrations.RemoveAt(index);
            if (registrations.Count == 0)
            {
                target.Remove(extensionId);
            }

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

    private static List<T> BuildList<T>(Dictionary<string, List<List<T>>> map)
    {
        List<T> results = new();
        foreach (List<List<T>> registrations in map.Values)
        {
            foreach (List<T> items in registrations)
            {
                results.AddRange(items);
            }
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
