using System.Globalization;
using System.IO;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Stores installed extension packages on disk: each version directory holds the package
/// archive and its extracted content. Listing, installing and uninstalling run one at a time.
/// </summary>
public sealed class ExtensionPackageStore : IExtensionPackageStore
{
    private const string InstallingMarker = ".installing-";
    private const string RetiredMarker = ".retired-";
    private static readonly TimeSpan AbandonedWorkDirectoryAge = TimeSpan.FromHours(1);
    private readonly string _installRoot;
    private readonly ExtensionPackageLoader _loader;
    private readonly ILogger<ExtensionPackageStore> _logger;
    private readonly Func<string, string, CancellationToken, Task> _moveDirectory;
    private readonly SemaphoreSlim _operations = new(1, 1);

    public ExtensionPackageStore(string installRoot, ExtensionPackageLoader loader, ILogger<ExtensionPackageStore>? logger = null)
        : this(installRoot, loader, logger, MoveDirectoryAsync)
    {
    }

    // moveDirectory renames a directory within the install root.
    internal ExtensionPackageStore(string installRoot, ExtensionPackageLoader loader, ILogger<ExtensionPackageStore>? logger,
        Func<string, string, CancellationToken, Task> moveDirectory)
    {
        if (!Path.IsPathFullyQualified(installRoot)) throw new ArgumentException("An absolute install root is required.", nameof(installRoot));
        _installRoot = Path.GetFullPath(installRoot);
        _loader = loader;
        _logger = logger ?? NullLogger<ExtensionPackageStore>.Instance;
        _moveDirectory = moveDirectory;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExtensionPackageInfo>> GetInstalledAsync(CancellationToken ct)
    {
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ListInstalledAsync(ct).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<IReadOnlyList<ExtensionPackageInfo>> ListInstalledAsync(CancellationToken ct)
    {
        if (!Directory.Exists(_installRoot))
        {
            return Array.Empty<ExtensionPackageInfo>();
        }

        Dictionary<string, ExtensionPackageInfo> results = new(StringComparer.OrdinalIgnoreCase);
        foreach (string extensionDirectory in Directory.EnumerateDirectories(_installRoot))
        {
            string id = Path.GetFileName(extensionDirectory);
            string[] versionDirectories;
            try
            {
                DesignerThemePackageContributions.ResolvePackagePath(_installRoot, id);
                versionDirectories = Directory.GetDirectories(extensionDirectory);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(exception, "Skipping the extension directory {Directory}.", extensionDirectory);
                continue;
            }

            foreach (string directory in versionDirectories)
            {
                ct.ThrowIfCancellationRequested();
                string version = Path.GetFileName(directory);
                if (IsAbandonedWorkDirectory(directory))
                {
                    TryDeleteDirectory(directory);
                    continue;
                }
                ExtensionPackageInfo info;
                try
                {
                    string path = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
                        Path.Combine(id, version, id + "." + version + ".nupkg"));
                    if (!File.Exists(path)) continue;
                    ExtensionPackageInfo archive = await _loader.LoadAsync(path, ct).ConfigureAwait(false);
                    if (!string.Equals(archive.Manifest.ExtensionId, id, StringComparison.OrdinalIgnoreCase) || archive.Manifest.Version != version)
                        throw new InvalidDataException("Installed package identity does not match its directory.");
                    info = new(path, archive.Manifest, directory);
                    if (IsArchiveOnlyLayout(directory, path))
                        await ExtractInPlaceAsync(path, info, ct).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Skipping the installed extension package in {Directory}.", directory);
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
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await InstallPackageAsync(packagePath, ct).ConfigureAwait(false);
        }
        finally { _operations.Release(); }
    }

    private async Task<ExtensionPackageInfo> InstallPackageAsync(string packagePath, CancellationToken ct)
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
        await _moveDirectory(stagingDirectory, extensionDir, ct).ConfigureAwait(false);
        return installed;
    }

    // Staging and retired directories are renamed away when an installation completes; one that stays
    // behind for longer than any installation takes belongs to an interrupted run. A rename keeps the
    // last write time of the renamed directory, so the creation time is taken from the name.
    private static bool IsAbandonedWorkDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        string? suffix = WorkDirectorySuffix(name, InstallingMarker) ?? WorkDirectorySuffix(name, RetiredMarker);
        if (suffix is null) return false;
        int separator = suffix.IndexOf('-');
        DateTime created = separator > 0
            && long.TryParse(suffix.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks)
            && ticks <= DateTime.MaxValue.Ticks
                ? new DateTime(ticks, DateTimeKind.Utc)
                : Directory.GetLastWriteTimeUtc(directory);
        return DateTime.UtcNow - created > AbandonedWorkDirectoryAge;
    }

    private static string? WorkDirectorySuffix(string name, string marker)
    {
        int index = name.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? null : name[(index + marker.Length)..];
    }

    // marker followed by the UTC creation ticks and a unique id.
    private static string NewWorkDirectorySuffix(string marker) =>
        marker + DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

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
        string retiredDirectory = versionDirectory + NewWorkDirectorySuffix(RetiredMarker);
        try
        {
            await _moveDirectory(versionDirectory, retiredDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }

        try
        {
            await _moveDirectory(stagingDirectory, versionDirectory, ct).ConfigureAwait(false);
        }
        catch
        {
            await RestoreRetiredDirectoryAsync(retiredDirectory, versionDirectory, stagingDirectory).ConfigureAwait(false);
            throw;
        }

        TryDeleteDirectory(retiredDirectory);
        _logger.LogInformation("Extracted installed extension package {PackagePath} into its version directory.", archivePath);
    }

    // The retired directory holds the only archive of the installation; it is moved back before the
    // extracted copy is discarded.
    private async Task RestoreRetiredDirectoryAsync(string retiredDirectory, string versionDirectory, string stagingDirectory)
    {
        try
        {
            await _moveDirectory(retiredDirectory, versionDirectory, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Could not restore the installation {Directory} from {RetiredDirectory}.", versionDirectory, retiredDirectory);
            return;
        }

        TryDeleteDirectory(stagingDirectory);
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
            Path.Combine(installed.Manifest.ExtensionId, installed.Manifest.Version + NewWorkDirectorySuffix(InstallingMarker)));
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
    public async Task UninstallAsync(string extensionId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            throw new ArgumentException("Extension id is required.", nameof(extensionId));
        }

        DesignerThemePackageContributions.ValidateSegment(extensionId, "extension ID");
        await _operations.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string extensionDir = DesignerThemePackageContributions.ResolvePackagePath(_installRoot, extensionId);
            if (Directory.Exists(extensionDir))
            {
                Directory.Delete(extensionDir, recursive: true);
            }
        }
        finally { _operations.Release(); }
    }
}
