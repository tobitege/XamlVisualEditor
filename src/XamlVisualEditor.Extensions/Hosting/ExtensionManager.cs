using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Default extension manager implementation.</summary>
public sealed class ExtensionManager : IExtensionManager, IExtensionThemeProviderLifecycle
{
    private readonly IExtensionPackageStore _store;
    private readonly IExtensionStateStore _stateStore;
    private readonly IExtensionUpdateService _updateService;
    private readonly Lazy<IReadOnlyList<ExtensionPackageInfo>> _builtInPackages;
    private readonly Lazy<HashSet<string>> _builtInIds;
    private readonly IExtensionContributionRegistry? _contributions;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private sealed record ThemePackage(IReadOnlyList<DesignerThemeProviderRegistration> Providers,
        IReadOnlyList<EditorThemeProviderRegistration> EditorProviders, IDisposable DesignerRegistration, IDisposable EditorRegistration) : IDisposable
    {
        public void Dispose()
        {
            DesignerRegistration.Dispose();
            EditorRegistration.Dispose();
        }
    }

    private readonly Dictionary<string, ThemePackage> _themePackages = new(StringComparer.OrdinalIgnoreCase);

    public event Func<string, CancellationToken, Task>? ThemeProvidersChanging;
    public event Func<string, CancellationToken, Task>? ThemeProvidersChanged;

    public ExtensionManager(
        IExtensionPackageStore store,
        IExtensionStateStore stateStore,
        IExtensionUpdateService updateService,
        IServiceProvider services,
        IExtensionContributionRegistry? contributions = null)
    {
        _store = store;
        _stateStore = stateStore;
        _updateService = updateService;
        _contributions = contributions;
        _builtInPackages = new Lazy<IReadOnlyList<ExtensionPackageInfo>>(() =>
        {
            IEnumerable<IXveExtension> extensions = services.GetServices<IXveExtension>();
            return BuildBuiltInPackages(extensions ?? Array.Empty<IXveExtension>());
        });
        _builtInIds = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(
                _builtInPackages.Value.Select(package => package.Manifest.ExtensionId),
                StringComparer.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
        IReadOnlyList<ExtensionPackageInfo> installed = await _store.GetInstalledAsync(ct).ConfigureAwait(false);
        await SynchronizeThemeProvidersAsync(installed, ct).ConfigureAwait(false);
        if (_builtInPackages.Value.Count == 0)
        {
            return installed;
        }

        Dictionary<string, ExtensionPackageInfo> merged = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionPackageInfo package in installed)
        {
            merged[package.Manifest.ExtensionId] = package;
        }

        foreach (ExtensionPackageInfo package in _builtInPackages.Value)
        {
            if (!merged.ContainsKey(package.Manifest.ExtensionId))
            {
                merged.Add(package.Manifest.ExtensionId, package);
            }
        }

        return merged.Values.ToArray();
        }
        finally { _operations.Release(); }
    }

    /// <inheritdoc />
    public async Task<ExtensionPackageInfo> InstallAsync(string packagePath, CancellationToken ct)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ExtensionPackageInfo installed = await _store.InstallAsync(packagePath, ct).ConfigureAwait(false);
            await SynchronizeThemeProvidersAsync(await _store.GetInstalledAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            return installed;
        }
        finally { _operations.Release(); }
    }

    /// <inheritdoc />
    public async Task UninstallAsync(string extensionId, CancellationToken ct)
    {
        if (IsBuiltIn(extensionId))
        {
            return;
        }

        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try
            {
                await NotifyAsync(ThemeProvidersChanging, extensionId, ct).ConfigureAwait(false);
                await _store.UninstallAsync(extensionId, ct).ConfigureAwait(false);
                if (_themePackages.Remove(extensionId, out var previous)) previous.Dispose();
            }
            finally { await NotifyAsync(ThemeProvidersChanged, extensionId, CancellationToken.None).ConfigureAwait(false); }
        }
        finally { _operations.Release(); }
    }

    /// <inheritdoc />
    public Task<bool> GetEnabledAsync(string extensionId, CancellationToken ct)
    {
        if (IsBuiltIn(extensionId))
        {
            return Task.FromResult(true);
        }

        return _stateStore.GetEnabledAsync(extensionId, ct);
    }

    /// <inheritdoc />
    public async Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken ct)
    {
        if (IsBuiltIn(extensionId))
        {
            return;
        }

        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stateStore.SetEnabledAsync(extensionId, enabled, ct).ConfigureAwait(false);
            await SynchronizeThemeProvidersAsync(await _store.GetInstalledAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ExtensionUpdateInfo>> CheckForUpdatesAsync(CancellationToken ct)
    {
        return _updateService.CheckForUpdatesAsync(ct);
    }

    private async Task SynchronizeThemeProvidersAsync(IReadOnlyList<ExtensionPackageInfo> packages, CancellationToken ct)
    {
        if (_contributions is null) return;
        Dictionary<string, (IReadOnlyList<DesignerThemeProviderRegistration> Preview, IReadOnlyList<EditorThemeProviderRegistration> Editor)> desired = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionPackageInfo package in packages)
        {
            if (!await _stateStore.GetEnabledAsync(package.Manifest.ExtensionId, ct).ConfigureAwait(false)) continue;
            var providers = DesignerThemePackageContributions.Read(package);
            var editorProviders = DesignerThemePackageContributions.ReadEditor(package);
            if (providers.Count > 0 || editorProviders.Count > 0) desired[package.Manifest.ExtensionId] = (providers, editorProviders);
        }
        foreach (string id in _themePackages.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase).ToArray())
        {
            bool hasProviders = desired.TryGetValue(id, out var providers);
            if (_themePackages.TryGetValue(id, out var previous) && hasProviders
                && previous.Providers.SequenceEqual(providers.Preview) && previous.EditorProviders.SequenceEqual(providers.Editor)) continue;
            try
            {
                await NotifyAsync(ThemeProvidersChanging, id, ct).ConfigureAwait(false);
                if (_themePackages.Remove(id, out previous)) previous.Dispose();
                if (hasProviders)
                    _themePackages[id] = new(providers.Preview, providers.Editor,
                        _contributions.RegisterDesignerThemeProviders(id, providers.Preview),
                        _contributions.RegisterEditorThemeProviders(id, providers.Editor));
            }
            finally { await NotifyAsync(ThemeProvidersChanged, id, CancellationToken.None).ConfigureAwait(false); }
        }
    }

    private static async Task NotifyAsync(Func<string, CancellationToken, Task>? handlers, string id, CancellationToken ct)
    {
        if (handlers is null) return;
        foreach (Func<string, CancellationToken, Task> handler in handlers.GetInvocationList())
            await handler(id, ct).ConfigureAwait(false);
    }

    private bool IsBuiltIn(string extensionId)
    {
        return !string.IsNullOrWhiteSpace(extensionId) && _builtInIds.Value.Contains(extensionId);
    }

    private static IReadOnlyList<ExtensionPackageInfo> BuildBuiltInPackages(IEnumerable<IXveExtension> extensions)
    {
        List<ExtensionPackageInfo> results = new();
        foreach (IXveExtension extension in extensions)
        {
            Type type = extension.GetType();
            Assembly assembly = type.Assembly;
            string name = assembly.GetName().Name ?? type.Name;
            string version = assembly.GetName().Version?.ToString() ?? "0.0.0";
            ExtensionManifest manifest = new()
            {
                Name = name,
                Publisher = "builtin",
                Version = version,
                DisplayName = name,
                Capabilities = ExtensionCapabilitiesAttribute.GetDeclared(type)
            };

            string packagePath = "builtin:" + manifest.ExtensionId;
            results.Add(new ExtensionPackageInfo(packagePath, manifest));
        }

        return results;
    }
}
