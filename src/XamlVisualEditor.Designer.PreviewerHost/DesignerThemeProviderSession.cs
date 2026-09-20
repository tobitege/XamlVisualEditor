using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.PreviewerHost;

/// <summary>Keeps provider code and application-wide theme state inside the preview process.</summary>
internal sealed class DesignerThemeProviderSession : IDisposable
{
    private sealed record Provider(DesignerThemeProviderRegistration Registration, IDesignerThemeProvider Instance);
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AssemblyDependencyResolver> _resolvers = new(StringComparer.Ordinal);
    private Provider? _active;
    private string? _lastCatalog;

    public DesignerThemeProviderSession() => AssemblyLoadContext.Default.Resolving += ResolveProviderDependency;

    public void Apply(DesignerThemeRequest request)
    {
        Dictionary<string, DesignerThemeProviderRegistration> requested = request.Providers.ToDictionary(item => item.Id, StringComparer.Ordinal);
        foreach (var entry in _providers.ToArray())
        {
            if (requested.TryGetValue(entry.Key, out var registration) && registration == entry.Value.Registration) continue;
            if (ReferenceEquals(_active, entry.Value)) ResetActive();
            if (entry.Value.Instance is IDisposable disposable) disposable.Dispose();
            _providers.Remove(entry.Key);
        }

        List<DesignerThemeProviderCatalog> catalogs = new();
        foreach (DesignerThemeProviderRegistration registration in request.Providers)
        {
            try
            {
                if (!_providers.TryGetValue(registration.Id, out Provider? provider))
                {
                    if (!Path.IsPathFullyQualified(registration.AssemblyPath) || !File.Exists(registration.AssemblyPath))
                        throw new InvalidOperationException($"Theme provider '{registration.Name}' has no accessible assembly.");
                    _resolvers.TryAdd(registration.AssemblyPath, new AssemblyDependencyResolver(registration.AssemblyPath));
                    Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(registration.AssemblyPath);
                    Type type = assembly.GetType(registration.TypeName, throwOnError: true)!;
                    if (!typeof(IDesignerThemeProvider).IsAssignableFrom(type) || type.IsAbstract)
                        throw new InvalidOperationException($"'{registration.TypeName}' is not a designer theme provider.");
                    provider = new Provider(registration, (IDesignerThemeProvider)Activator.CreateInstance(type)!);
                    _providers.Add(registration.Id, provider);
                }
                IReadOnlyList<DesignerTheme> themes = provider.Instance.GetThemes();
                if (themes.Any(theme => string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name))
                    || themes.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count() != themes.Count)
                    throw new InvalidOperationException($"Theme provider '{registration.Name}' returned invalid or duplicate theme IDs.");
                catalogs.Add(new(registration.Id, registration.Name, themes));
            }
            catch (Exception error)
            {
                catalogs.Add(new(registration.Id, registration.Name, Array.Empty<DesignerTheme>(), error.GetBaseException().Message));
            }
        }

        string catalog = JsonSerializer.Serialize(new DesignerThemeCatalog(request.RequestId, catalogs));
        if (catalog != _lastCatalog)
        {
            Console.WriteLine(DesignerThemeProtocol.CatalogPrefix + catalog);
            _lastCatalog = catalog;
        }

        if (request.Selection is not { } selection)
        {
            ResetActive();
            return;
        }
        DesignerThemeProviderCatalog? selectedCatalog = catalogs.FirstOrDefault(item => item.Id == selection.ProviderId);
        if (selectedCatalog?.Error is { } providerError)
            throw new InvalidOperationException($"Theme provider '{selectedCatalog.Name}' failed: {providerError}");
        if (!_providers.TryGetValue(selection.ProviderId, out Provider? selected)
            || selectedCatalog?.Themes.Any(theme => theme.Id == selection.ThemeId) != true)
            throw new InvalidOperationException($"The selected preview theme '{selection.ProviderId}/{selection.ThemeId}' is unavailable.");
        if (!ReferenceEquals(selected, _active)) ResetActive();
        _active = selected;
        selected.Instance.ApplyTheme(selection.ThemeId);
    }

    private void ResetActive()
    {
        if (_active is null) return;
        _active.Instance.ApplyTheme(null);
        _active = null;
    }

    private Assembly? ResolveProviderDependency(AssemblyLoadContext context, AssemblyName name)
    {
        foreach (AssemblyDependencyResolver resolver in _resolvers.Values)
            if (resolver.ResolveAssemblyToPath(name) is { } path) return context.LoadFromAssemblyPath(path);
        return null;
    }

    public void Dispose()
    {
        ResetActive();
        foreach (Provider provider in _providers.Values)
            if (provider.Instance is IDisposable disposable) disposable.Dispose();
        _providers.Clear();
        _resolvers.Clear();
        AssemblyLoadContext.Default.Resolving -= ResolveProviderDependency;
    }
}
