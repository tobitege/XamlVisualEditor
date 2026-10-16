using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    private readonly ILogger<ExtensionManager> _logger;
    private readonly SemaphoreSlim _operations = new(1, 1);
    private sealed record ThemeProviders(IReadOnlyList<DesignerThemeProviderRegistration> Preview,
        IReadOnlyList<EditorThemeProviderRegistration> Editor);

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
        IExtensionContributionRegistry? contributions = null,
        ILogger<ExtensionManager>? logger = null)
    {
        _store = store;
        _stateStore = stateStore;
        _updateService = updateService;
        _contributions = contributions;
        _logger = logger ?? NullLogger<ExtensionManager>.Instance;
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

    /// <summary>
    /// Brings the theme provider registrations in line with the enabled packages. Every outdated registration is
    /// removed before any package registers, so no registration collides with an ID that the same synchronization
    /// releases. A package whose theme contributions cannot be read, or whose provider IDs another registration holds,
    /// is logged as a package problem and stays unregistered; the next synchronization tries it again.
    /// </summary>
    private async Task SynchronizeThemeProvidersAsync(IReadOnlyList<ExtensionPackageInfo> packages, CancellationToken ct)
    {
        if (_contributions is null) return;
        Dictionary<string, ThemeProviders> desired = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExtensionPackageInfo package in packages)
        {
            string extensionId = package.Manifest.ExtensionId;
            if (!await _stateStore.GetEnabledAsync(extensionId, ct).ConfigureAwait(false)) continue;
            ThemeProviders providers;
            try
            {
                providers = new(DesignerThemePackageContributions.Read(package), DesignerThemePackageContributions.ReadEditor(package));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Extension package {ExtensionId}: theme providers could not be read", extensionId);
                continue;
            }

            if (providers.Preview.Count > 0 || providers.Editor.Count > 0) desired[extensionId] = providers;
        }

        string[] changed = _themePackages.Keys.Union(desired.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(id => !IsRegistered(id, desired))
            .ToArray();
        List<string> released = new(changed.Length);
        try
        {
            foreach (string id in changed)
            {
                await NotifyAsync(ThemeProvidersChanging, id, ct).ConfigureAwait(false);
                released.Add(id);
                if (_themePackages.Remove(id, out ThemePackage? previous)) previous.Dispose();
            }
        }
        finally
        {
            foreach (string id in released)
            {
                if (desired.TryGetValue(id, out ThemeProviders? providers)) RegisterThemePackage(id, providers);
            }

            foreach (string id in released)
            {
                await NotifyAsync(ThemeProvidersChanged, id, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private bool IsRegistered(string id, Dictionary<string, ThemeProviders> desired) =>
        _themePackages.TryGetValue(id, out ThemePackage? current) && desired.TryGetValue(id, out ThemeProviders? providers)
        && current.Providers.SequenceEqual(providers.Preview) && current.EditorProviders.SequenceEqual(providers.Editor);

    /// <summary>Registers both provider kinds of one package, or neither of them.</summary>
    private void RegisterThemePackage(string id, ThemeProviders providers)
    {
        IDisposable? previewRegistration = null;
        try
        {
            previewRegistration = _contributions!.RegisterDesignerThemeProviders(id, providers.Preview);
            IDisposable editorRegistration = _contributions.RegisterEditorThemeProviders(id, providers.Editor);
            _themePackages[id] = new(providers.Preview, providers.Editor, previewRegistration, editorRegistration);
        }
        catch (ArgumentException exception)
        {
            previewRegistration?.Dispose();
            _logger.LogWarning(exception, "Extension package {ExtensionId}: theme providers were not registered", id);
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
