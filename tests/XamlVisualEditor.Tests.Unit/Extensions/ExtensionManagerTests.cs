using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.Core;
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
                designerThemes = new[] { new { id = "palette", name = "Sample", assembly = "lib/net10.0/Provider.dll", type = "Missing.Provider" } },
                editorThemes = new[] { new { id = "editor", name = "Sample Editor", assembly = "lib/net10.0/Provider.dll", type = "Missing.EditorProvider" } }
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
        Assert.Empty(contributions.EditorThemeProviders);
        await manager.SetEnabledAsync(manifest.ExtensionId, true, CancellationToken.None);
        Assert.Equal("example.sample/palette", Assert.Single(contributions.DesignerThemeProviders).Id);
        Assert.Equal("example.sample/editor", Assert.Single(contributions.EditorThemeProviders).Id);
        int changeCount = lifecycle.Count;
        await manager.GetInstalledAsync(CancellationToken.None);
        Assert.Equal(changeCount, lifecycle.Count);
        await manager.SetEnabledAsync(manifest.ExtensionId, false, CancellationToken.None);
        Assert.Empty(contributions.DesignerThemeProviders);
        Assert.Empty(contributions.EditorThemeProviders);
        await manager.SetEnabledAsync(manifest.ExtensionId, true, CancellationToken.None);
        lifecycle.Clear();
        store.BeforeUninstall = () => lifecycle.Add("remove files");
        await manager.UninstallAsync(manifest.ExtensionId, CancellationToken.None);
        Assert.Equal(new[] { "stop:example.sample", "remove files", "resume:example.sample" }, lifecycle);
        Assert.Empty(contributions.DesignerThemeProviders);
        Assert.Empty(contributions.EditorThemeProviders);
    }

    [Fact]
    public async Task AThemePackageWhoseProviderIdIsTakenIsReportedRegistersNothingAndRegistersOnceTheIdIsFree()
    {
        ExtensionPackageInfo package = ThemePackage("sample", new[] { "palette" }, new[] { "editor" });
        FakeStore store = new() { Packages = new[] { package } };
        FakeStateStore states = new();
        await states.SetEnabledAsync(package.Manifest.ExtensionId, true, CancellationToken.None);
        ExtensionContributionRegistry contributions = new();
        EditorThemeProviderRegistration taken = new("example.sample/editor", "Other", typeof(ExtensionManagerTests).Assembly.Location,
            "Other.EditorProvider");
        // Another extension holds the editor provider ID, so the second of the package's two registrations fails.
        IDisposable otherExtension = contributions.RegisterEditorThemeProviders("other.extension", new[] { taken });
        RecordingLogger logger = new();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ExtensionManager manager = new(store, states, new FakeUpdateService(), services, contributions, logger);

        IReadOnlyList<ExtensionPackageInfo> installed = await manager.GetInstalledAsync(CancellationToken.None);

        Assert.Same(package, Assert.Single(installed));
        (string message, Exception? exception) = Assert.Single(logger.Warnings);
        Assert.Contains("example.sample", message);
        Assert.Contains("example.sample/editor", Assert.IsAssignableFrom<ArgumentException>(exception).Message);
        Assert.Empty(contributions.DesignerThemeProviders);
        Assert.Equal(taken, Assert.Single(contributions.EditorThemeProviders));

        otherExtension.Dispose();
        await manager.GetInstalledAsync(CancellationToken.None);

        Assert.Equal("example.sample/palette", Assert.Single(contributions.DesignerThemeProviders).Id);
        Assert.Equal("example.sample/editor", Assert.Single(contributions.EditorThemeProviders).Id);
        Assert.Single(logger.Warnings);
    }

    [Fact]
    public async Task SynchronizationRemovesEveryOutdatedRegistrationBeforeAnyPackageRegisters()
    {
        ExtensionPackageInfo beta = ThemePackage("beta", new[] { "palette" }, Array.Empty<string>());
        ExtensionPackageInfo alpha = ThemePackage("alpha", new[] { "palette" }, Array.Empty<string>());
        FakeStore store = new() { Packages = new[] { beta, alpha } };
        FakeStateStore states = new();
        await states.SetEnabledAsync(beta.Manifest.ExtensionId, true, CancellationToken.None);
        await states.SetEnabledAsync(alpha.Manifest.ExtensionId, true, CancellationToken.None);
        ExtensionContributionRegistry contributions = new();
        using ServiceProvider services = new ServiceCollection().BuildServiceProvider();
        ExtensionManager manager = new(store, states, new FakeUpdateService(), services, contributions);
        await manager.GetInstalledAsync(CancellationToken.None);
        List<string[]> snapshots = new();
        contributions.Changed += (_, _) => snapshots.Add(contributions.DesignerThemeProviders.Select(provider => provider.Id).ToArray());

        // One synchronization updates the package that registered first and removes the one that registered second.
        store.Packages = new[] { ThemePackage("beta", new[] { "palette", "second" }, Array.Empty<string>()), alpha };
        await states.SetEnabledAsync(alpha.Manifest.ExtensionId, false, CancellationToken.None);
        await manager.GetInstalledAsync(CancellationToken.None);

        string[] firstWithUpdate = snapshots.First(ids => ids.Contains("example.beta/second"));
        Assert.DoesNotContain("example.alpha/palette", firstWithUpdate);
        Assert.Equal(new[] { "example.beta/palette", "example.beta/second" },
            contributions.DesignerThemeProviders.Select(provider => provider.Id));
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

    [Fact]
    public async Task BuiltInPackagesReportTheirDeclaredCapabilities()
    {
        using ServiceProvider services = new ServiceCollection()
            .AddSingleton<IXveExtension, CapabilityDeclaringExtension>()
            .BuildServiceProvider();
        ExtensionManager manager = new(new FakeStore(), new FakeStateStore(), new FakeUpdateService(), services);

        ExtensionPackageInfo package = Assert.Single(await manager.GetInstalledAsync(CancellationToken.None));

        Assert.Equal(new[] { ExtensionHostCapabilities.DocumentOpenInterceptors }, package.Manifest.Capabilities);
    }

    private static ExtensionPackageInfo ThemePackage(string name, string[] previewIds, string[] editorIds)
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-manager-theme-" + name);
        ExtensionManifest manifest = new()
        {
            Name = name, Publisher = "example", Version = "1.0.0",
            Contributes = System.Text.Json.JsonSerializer.SerializeToElement(new
            {
                designerThemes = previewIds.Select(id => new { id, name = id, assembly = "lib/net10.0/Provider.dll", type = "Missing.Provider" }),
                editorThemes = editorIds.Select(id => new { id, name = id, assembly = "lib/net10.0/Provider.dll", type = "Missing.EditorProvider" })
            })
        };
        return new ExtensionPackageInfo(Path.Combine(directory, "package.nupkg"), manifest, directory);
    }

    private sealed class RecordingLogger : ILogger<ExtensionManager>
    {
        public List<(string Message, Exception? Exception)> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add((formatter(state, exception), exception));
        }
    }

    [ExtensionCapabilities(ExtensionHostCapabilities.DocumentOpenInterceptors)]
    private sealed class CapabilityDeclaringExtension : IXveExtension
    {
        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
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
