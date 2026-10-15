namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Allows preview hosts to release package assemblies before a provider changes.</summary>
public interface IExtensionThemeProviderLifecycle
{
    event Func<string, CancellationToken, Task>? ThemeProvidersChanging;
    event Func<string, CancellationToken, Task>? ThemeProvidersChanged;
}

/// <summary>Coordinates extension install and enablement.</summary>
public interface IExtensionManager
{
    /// <summary>Gets installed packages.</summary>
    Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct);

    /// <summary>Installs a NuGet package.</summary>
    Task<ExtensionPackageInfo> InstallAsync(string packagePath, CancellationToken ct);

    /// <summary>Uninstalls an extension.</summary>
    Task UninstallAsync(string extensionId, CancellationToken ct);

    /// <summary>Gets whether an extension is enabled.</summary>
    Task<bool> GetEnabledAsync(string extensionId, CancellationToken ct);

    /// <summary>Sets whether an extension is enabled.</summary>
    Task SetEnabledAsync(string extensionId, bool enabled, CancellationToken ct);

    /// <summary>Checks for updates.</summary>
    Task<IReadOnlyList<ExtensionUpdateInfo>> CheckForUpdatesAsync(CancellationToken ct);
}
