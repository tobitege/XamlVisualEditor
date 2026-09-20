using Microsoft.Extensions.DependencyInjection;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class ExtensionManagerTests
{
    [Fact]
    public async Task EnabledThemePackagesRegisterWithoutLoadingCodeAndReleaseBeforeRemoval()
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-manager-theme-package");
        ExtensionManifest manifest = new()
        {
            Name = "sample", Publisher = "example", Version = "1.0.0",
            Contributes = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                designerThemes = new[] { new { id = "palette", name = "Sample", assembly = "lib/net10.0/Provider.dll", type = "Missing.Provider" } }
            })
        };
        FakeStore store = new() { Packages = new[] { new ExtensionPackageInfo(Path.Combine(directory, "package.nupkg"), manifest, directory) } };
        FakeStateStore states = new();
        ExtensionContributionRegistry contributions = new();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ExtensionManager manager = new(store, states, new FakeUpdateService(), services, contributions);
        List<string> lifecycle = new();
        manager.ThemeProvidersChanging += (id, ct) => { lifecycle.Add("stop:" + id); return Task.CompletedTask; };
        manager.ThemeProvidersChanged += (id, ct) => { lifecycle.Add("resume:" + id); return Task.CompletedTask; };
        await manager.GetInstalledAsync(CancellationToken.None);
        Assert.Empty(contributions.DesignerThemeProviders);
        await manager.SetEnabledAsync(manifest.ExtensionId, true, CancellationToken.None);
        Assert.Equal("example.sample/palette", Assert.Single(contributions.DesignerThemeProviders).Id);
        int changeCount = lifecycle.Count;
        await manager.GetInstalledAsync(CancellationToken.None);
        Assert.Equal(changeCount, lifecycle.Count);
        await manager.SetEnabledAsync(manifest.ExtensionId, false, CancellationToken.None);
        Assert.Empty(contributions.DesignerThemeProviders);
        await manager.SetEnabledAsync(manifest.ExtensionId, true, CancellationToken.None);
        lifecycle.Clear();
        store.BeforeUninstall = () => lifecycle.Add("remove files");
        await manager.UninstallAsync(manifest.ExtensionId, CancellationToken.None);
        Assert.Equal(new[] { "stop:example.sample", "remove files", "resume:example.sample" }, lifecycle);
        Assert.Empty(contributions.DesignerThemeProviders);
    }

    [Fact]
    public async Task ManagerDelegatesToStores()
    {
        FakeStore store = new();
        FakeStateStore stateStore = new();
        FakeUpdateService updates = new();
        ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ExtensionManager manager = new(store, stateStore, updates, services);

        await manager.InstallAsync("path.nupkg", CancellationToken.None);
        await manager.SetEnabledAsync("example.sample", true, CancellationToken.None);
        bool enabled = await manager.GetEnabledAsync("example.sample", CancellationToken.None);

        Assert.True(store.InstallCalled);
        Assert.True(enabled);
    }

    private sealed class FakeStore : IExtensionPackageStore
    {
        public bool InstallCalled { get; private set; }
        public IReadOnlyList<ExtensionPackageInfo> Packages { get; set; } = Array.Empty<ExtensionPackageInfo>();
        public Action? BeforeUninstall { get; set; }

        public Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct)
        {
            return Task.FromResult(Packages);
        }

        public Task<ExtensionPackageInfo> InstallAsync(string packagePath, CancellationToken ct)
        {
            InstallCalled = true;
            ExtensionManifest manifest = new()
            {
                Name = "sample",
                Publisher = "example",
                Version = "1.0.0"
            };
            return Task.FromResult(new ExtensionPackageInfo(packagePath, manifest));
        }

        public Task UninstallAsync(string extensionId, CancellationToken ct)
        {
            BeforeUninstall?.Invoke();
            Packages = Packages.Where(package => package.Manifest.ExtensionId != extensionId).ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeStateStore : IExtensionStateStore
    {
        private readonly Dictionary<string, bool> _states = new(StringComparer.OrdinalIgnoreCase);

        public Task<bool> GetEnabledAsync(string extensionId, CancellationToken ct)
        {
            _states.TryGetValue(extensionId, out bool enabled);
            return Task.FromResult(enabled);
        }

        public Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken ct)
        {
            _states[extensionId] = enabled;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ExtensionStateEntry>> GetAllAsync(CancellationToken ct)
        {
            List<ExtensionStateEntry> items = new();
            foreach (KeyValuePair<string, bool> entry in _states)
            {
                items.Add(new ExtensionStateEntry(entry.Key, entry.Value));
            }

            return Task.FromResult<IReadOnlyList<ExtensionStateEntry>>(items);
        }
    }

    private sealed class FakeUpdateService : IExtensionUpdateService
    {
        public Task<IReadOnlyList<ExtensionUpdateInfo>> CheckForUpdatesAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<ExtensionUpdateInfo>>(Array.Empty<ExtensionUpdateInfo>());
        }
    }
}
