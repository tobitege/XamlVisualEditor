using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Stores installed extension packages on disk: each version directory holds the package
/// archive and its extracted content.
/// </summary>
public sealed class ExtensionPackageStore : IExtensionPackageStore
{
    private static readonly TimeSpan AbandonedWorkDirectoryAge = TimeSpan.FromHours(1);
    private readonly string _installRoot;
    private readonly ExtensionPackageLoader _loader;
    private readonly ILogger<ExtensionPackageStore> _logger;

    public ExtensionPackageStore(string installRoot, ExtensionPackageLoader loader, ILogger<ExtensionPackageStore>? logger = null)
    {
        if (!Path.IsPathFullyQualified(installRoot)) throw new ArgumentException("An absolute install root is required.", nameof(installRoot));
        _installRoot = Path.GetFullPath(installRoot);
        _loader = loader;
        _logger = logger ?? NullLogger<ExtensionPackageStore>.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_installRoot))
        {
            return Array.Empty<ExtensionPackageInfo>();
        }

        Dictionary<string, ExtensionPackageInfo> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (string extensionDirectory in Directory.EnumerateDirectories(_installRoot))
        {
            string id = Path.GetFileName(extensionDirectory);
            DesignerThemePackageContributions.ResolvePackagePath(_installRoot, id);
            foreach (string directory in Directory.GetDirectories(extensionDirectory))
            {
                ct.ThrowIfCancellationRequested();
                string version = Path.GetFileName(directory);
                if (IsAbandonedWorkDirectory(directory))
                {
                    TryDeleteDirectory(directory);
                    continue;
                }
                string path = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
                    Path.Combine(id, version, id + "." + version + ".nupkg"));
                if (!File.Exists(path)) continue;
                ExtensionPackageInfo info;
                try
                {
                    ExtensionPackageInfo archive = await _loader.LoadAsync(path, ct).ConfigureAwait(false);
                    if (!string.Equals(archive.Manifest.ExtensionId, id, StringComparison.OrdinalIgnoreCase) || archive.Manifest.Version != version)
                        throw new InvalidDataException("Installed package identity does not match its directory.");
                    info = new(path, archive.Manifest, directory);
                    if (IsArchiveOnlyLayout(directory, path))
                        await ExtractInPlaceAsync(path, info, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Skipping installed extension package {PackagePath}.", path);
                    continue;
                }
                if (!results.TryGetValue(id, out ExtensionPackageInfo? current)
                    || ExtensionUpdateService.ExtensionVersionComparer.IsNewer(version, current.Manifest.Version))
                    results[id] = info;
            }
        }

        return results.Values.ToArray();
    }

    /// <inheritdoc />
    public async Task<ExtensionPackageInfo> InstallAsync(string packagePath, CancellationToken ct)
    {
        ExtensionPackageInfo info = await _loader.LoadAsync(packagePath, ct).ConfigureAwait(false);
        DesignerThemePackageContributions.ValidateSegment(info.Manifest.Name, "extension name");
        DesignerThemePackageContributions.ValidateSegment(info.Manifest.Publisher, "extension publisher");
        DesignerThemePackageContributions.ValidateSegment(info.Manifest.Version, "extension version");
        string extensionDir = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
            Path.Combine(info.Manifest.ExtensionId, info.Manifest.Version));
        if (Directory.Exists(extensionDir)) throw new InvalidOperationException("This extension version is already installed. Uninstall it before reinstalling.");
        string targetPath = Path.Combine(extensionDir, info.Manifest.ExtensionId + "." + info.Manifest.Version + ".nupkg");
        ExtensionPackageInfo installed = new(targetPath, info.Manifest, extensionDir);
        string stagingDirectory = ExtractToStagingDirectory(packagePath, installed, ct);
        await MoveDirectoryAsync(stagingDirectory, extensionDir, ct).ConfigureAwait(false);
        return installed;
    }

    // Staging and retired directories are renamed away when an installation completes; one that stays
    // behind for longer than any installation takes belongs to an interrupted run.
    private static bool IsAbandonedWorkDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        return (name.Contains(".installing-", StringComparison.Ordinal) || name.Contains(".retired-", StringComparison.Ordinal))
            && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > AbandonedWorkDirectoryAge;
    }

    private void TryDeleteDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(exception, "Could not remove the abandoned directory {Directory}.", directory);
        }
    }

    // Earlier versions kept only the archive in the version directory and extracted it on activation;
    // such an installation is brought into the current layout before it is reported.
    private static bool IsArchiveOnlyLayout(string versionDirectory, string archivePath) =>
        Directory.EnumerateFileSystemEntries(versionDirectory).All(entry =>
            string.Equals(entry, archivePath, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(entry), "content", StringComparison.OrdinalIgnoreCase) && Directory.Exists(entry));

    private async Task ExtractInPlaceAsync(string archivePath, ExtensionPackageInfo installed, CancellationToken ct)
    {
        string versionDirectory = installed.InstallationDirectory!;
        string stagingDirectory = ExtractToStagingDirectory(archivePath, installed, ct);
        string retiredDirectory = versionDirectory + ".retired-" + Guid.NewGuid().ToString("N");
        await MoveDirectoryAsync(versionDirectory, retiredDirectory, ct).ConfigureAwait(false);
        await MoveDirectoryAsync(stagingDirectory, versionDirectory, ct).ConfigureAwait(false);
        Directory.Delete(retiredDirectory, recursive: true);
        _logger.LogInformation("Extracted installed extension package {PackagePath} into its version directory.", archivePath);
    }

    // Extracts the package and a copy of its archive into a new staging directory next to the version
    // directory of installed, after checking every entry against the version directory.
    private string ExtractToStagingDirectory(string packagePath, ExtensionPackageInfo installed, CancellationToken ct)
    {
        string extensionDir = installed.InstallationDirectory!;
        string targetPath = installed.PackagePath;
        string fileName = Path.GetFileName(targetPath);
        var providerAssemblies = DesignerThemePackageContributions.Read(installed).Select(provider => provider.AssemblyPath)
            .Concat(DesignerThemePackageContributions.ReadEditor(installed).Select(provider => provider.AssemblyPath))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        string stagingDirectory = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
            Path.Combine(installed.Manifest.ExtensionId, installed.Manifest.Version + ".installing-" + Guid.NewGuid().ToString("N")));
        using (ZipArchive archive = ZipFile.OpenRead(packagePath))
        {
            HashSet<string> files = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destination = DesignerThemePackageContributions.ResolvePackagePath(extensionDir, entry.FullName);
                if (!files.Add(destination) || string.Equals(destination, targetPath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new InvalidDataException("The package contains a duplicate or reserved destination.");
            }
            foreach (string providerAssembly in providerAssemblies)
                if (!archive.Entries.Any(entry => !entry.FullName.EndsWith('/') &&
                    string.Equals(DesignerThemePackageContributions.ResolvePackagePath(extensionDir, entry.FullName), providerAssembly,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)))
                    throw new InvalidDataException("The package does not contain its declared theme provider assembly.");
            Directory.CreateDirectory(stagingDirectory);
            try
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    ct.ThrowIfCancellationRequested();
                    string destination = DesignerThemePackageContributions.ResolvePackagePath(stagingDirectory, entry.FullName);
                    if (entry.FullName.EndsWith('/')) Directory.CreateDirectory(destination);
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                        entry.ExtractToFile(destination);
                    }
                }
                ct.ThrowIfCancellationRequested();
                File.Copy(packagePath, Path.Combine(stagingDirectory, fileName));
            }
            catch
            {
                Directory.Delete(stagingDirectory, recursive: true);
                throw;
            }
        }
        return stagingDirectory;
    }

    // A virus scanner or search indexer may still hold freshly extracted files open; Windows then denies
    // moving their directory for a moment.
    private static async Task MoveDirectoryAsync(string source, string destination, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(source, destination);
                return;
            }
            catch (Exception exception) when (attempt < 20 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public Task UninstallAsync(string extensionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("Extension id is required.", nameof(extensionId));
        }

        DesignerThemePackageContributions.ValidateSegment(extensionId, "extension ID");
        string extensionDir = DesignerThemePackageContributions.ResolvePackagePath(_installRoot, extensionId);
        if (Directory.Exists(extensionDir))
        {
            Directory.Delete(extensionDir, recursive: true);
        }

        return Task.CompletedTask;
    }
}
