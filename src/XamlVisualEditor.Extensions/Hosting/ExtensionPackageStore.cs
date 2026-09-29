using System.IO;
using System.IO.Compression;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Stores installed extension packages on disk.</summary>
public sealed class ExtensionPackageStore : IExtensionPackageStore
{
    private readonly string _installRoot;
    private readonly ExtensionPackageLoader _loader;

    public ExtensionPackageStore(string installRoot, ExtensionPackageLoader loader)
    {
        if (!Path.IsPathFullyQualified(installRoot)) throw new ArgumentException("An absolute install root is required.", nameof(installRoot));
        _installRoot = Path.GetFullPath(installRoot);
        _loader = loader;
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
            foreach (string directory in Directory.EnumerateDirectories(extensionDirectory))
            {
                ct.ThrowIfCancellationRequested();
                string version = Path.GetFileName(directory);
                string path = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
                    Path.Combine(id, version, id + "." + version + ".nupkg"));
                if (!File.Exists(path)) continue;
                ExtensionPackageInfo archive = await _loader.LoadAsync(path, ct).ConfigureAwait(false);
                if (!string.Equals(archive.Manifest.ExtensionId, id, StringComparison.OrdinalIgnoreCase) || archive.Manifest.Version != version)
                    throw new InvalidDataException("Installed package identity does not match its directory.");
                ExtensionPackageInfo info = new(path, archive.Manifest, directory);
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
        string fileName = info.Manifest.ExtensionId + "." + info.Manifest.Version + ".nupkg";
        string targetPath = Path.Combine(extensionDir, fileName);
        ExtensionPackageInfo installed = new(targetPath, info.Manifest, extensionDir);
        var providerAssemblies = DesignerThemePackageContributions.Read(installed).Select(provider => provider.AssemblyPath)
            .Concat(DesignerThemePackageContributions.ReadEditor(installed).Select(provider => provider.AssemblyPath))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
        string stagingDirectory = DesignerThemePackageContributions.ResolvePackagePath(_installRoot,
            Path.Combine(info.Manifest.ExtensionId, info.Manifest.Version + ".installing-" + Guid.NewGuid().ToString("N")));
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
        }
        ct.ThrowIfCancellationRequested();
        File.Copy(packagePath, Path.Combine(stagingDirectory, fileName));
        Directory.Move(stagingDirectory, extensionDir);
        return installed;
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
