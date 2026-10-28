using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Formats.Tar;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions.Debugging;

namespace XamlVisualEditor.App.Services;

public sealed class DebugToolInstaller : IDebugToolInstaller
{
    private const string ToolId = "netcoredbg";
    private const string ToolVersion = "3.1.3-1062";
    private const string ToolDownloadRoot = "https://github.com/Samsung/netcoredbg/releases/download/" + ToolVersion + "/";
    internal static readonly DebugToolPackage? Package = SelectPackage(
        OperatingSystem.IsWindows(), OperatingSystem.IsLinux(), OperatingSystem.IsMacOS(), RuntimeInformation.OSArchitecture);

    private readonly HttpClient _httpClient;
    private readonly string _toolsRoot;
    private readonly string _consentPath;
    private readonly HashSet<string> _consents = new(StringComparer.OrdinalIgnoreCase);
    private bool _consentLoaded;

    public DebugToolInstaller()
        : this(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), new HttpClientHandler())
    {
    }

    internal DebugToolInstaller(string appData, HttpMessageHandler httpHandler)
    {
        _httpClient = new HttpClient(httpHandler);
        _toolsRoot = Path.Combine(appData, "XamlVisualEditor", "tools");
        _consentPath = Path.Combine(appData, "XamlVisualEditor", "debug-tools-consent.json");
    }

    internal sealed record DebugToolPackage(string ArchiveName, string BinaryName)
    {
        // Each archive installs into its own folder, so a build of another platform that an earlier version
        // installed is never taken for this one.
        public string FolderName => ArchiveName[..ArchiveName.IndexOf('.', StringComparison.Ordinal)];
    }

    // netcoredbg publishes one archive per platform below. Other platforms have none, also macOS on arm64, where the
    // x64 build cannot debug arm64 processes; the debugger there needs the adapter path set in the debug settings.
    internal static DebugToolPackage? SelectPackage(bool isWindows, bool isLinux, bool isMacOS, Architecture architecture) =>
        (isWindows, isLinux, isMacOS, architecture) switch
        {
            (true, _, _, Architecture.X64) => new DebugToolPackage("netcoredbg-win64.zip", "netcoredbg.exe"),
            (_, true, _, Architecture.X64) => new DebugToolPackage("netcoredbg-linux-amd64.tar.gz", "netcoredbg"),
            (_, true, _, Architecture.Arm64) => new DebugToolPackage("netcoredbg-linux-arm64.tar.gz", "netcoredbg"),
            (_, _, true, Architecture.X64) => new DebugToolPackage("netcoredbg-osx-amd64.tar.gz", "netcoredbg"),
            _ => null
        };

    public string? GetNetcoredbgPath()
    {
        if (Package is null)
        {
            return null;
        }

        string binary = Path.Combine(GetInstallDir(Package), "netcoredbg", Package.BinaryName);
        return File.Exists(binary) ? binary : null;
    }

    public async Task<string?> EnsureNetcoredbgAsync(Func<DebugToolConsentRequest, Task<bool>> confirmAsync, CancellationToken ct = default)
    {
        string? existing = GetNetcoredbgPath();
        if (!string.IsNullOrWhiteSpace(existing))
        {
            return existing;
        }

        if (Package is null)
        {
            throw new PlatformNotSupportedException(string.Format(
                UiStrings.NetcoredbgNoBuild, $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"));
        }

        string downloadUrl = ToolDownloadRoot + Package.ArchiveName;

        LoadConsent();
        if (!_consents.Contains(ToolId))
        {
            DebugToolConsentRequest request = new(
                ToolId,
                ToolVersion,
                downloadUrl,
                GetInstallDir(Package),
                "The debugger requires netcoredbg to be downloaded (~3.5 MB). Allow download?");
            bool allowed = await confirmAsync(request).ConfigureAwait(false);
            if (!allowed)
            {
                return null;
            }

            _consents.Add(ToolId);
            SaveConsent();
        }

        Directory.CreateDirectory(_toolsRoot);
        // A name of its own inside the user's tools folder, which no other user can prepare beforehand.
        string tempArchive = Path.Combine(_toolsRoot, Path.GetRandomFileName() + "-" + Package.ArchiveName);
        string installDir = GetInstallDir(Package);
        // The archive is extracted beside the install folder and takes its place only when it is complete, so a
        // failed extraction never leaves a binary that looks installed.
        string extractDir = installDir + "-" + Path.GetRandomFileName();
        try
        {
            using (HttpResponseMessage response = await _httpClient.GetAsync(downloadUrl, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using FileStream target = new(tempArchive, FileMode.CreateNew);
                await response.Content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            Directory.CreateDirectory(extractDir);
            await ExtractAsync(tempArchive, extractDir, ct).ConfigureAwait(false);
            if (Directory.Exists(installDir))
            {
                Directory.Delete(installDir, recursive: true);
            }

            Directory.Move(extractDir, installDir);
        }
        finally
        {
            TryDelete(tempArchive);
            TryDelete(extractDir);
        }

        string? binary = GetNetcoredbgPath();
        if (binary is not null)
        {
            TryMakeExecutable(binary);
        }

        return binary;
    }

    // A leftover archive or extraction folder must not hide the outcome of the download or the installation.
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string GetInstallDir(DebugToolPackage package)
    {
        return Path.Combine(_toolsRoot, ToolId, ToolVersion, package.FolderName);
    }

    private void LoadConsent()
    {
        if (_consentLoaded)
        {
            return;
        }

        _consentLoaded = true;
        if (!File.Exists(_consentPath))
        {
            return;
        }

        try
        {
            string json = SharedFileReader.ReadAllText(_consentPath);
            string[]? tools = JsonSerializer.Deserialize<string[]>(json);
            if (tools is null)
            {
                return;
            }

            foreach (string tool in tools)
            {
                _consents.Add(tool);
            }
        }
        catch
        {
        }
    }

    private void SaveConsent()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_consentPath) ?? _toolsRoot);
            string json = JsonSerializer.Serialize(_consents, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_consentPath, json);
        }
        catch
        {
        }
    }

    // Both extractors refuse entries that would land outside the output directory.
    internal static async Task ExtractAsync(string archivePath, string outputDir, CancellationToken ct)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            await ZipFile.ExtractToDirectoryAsync(archivePath, outputDir, overwriteFiles: true, ct).ConfigureAwait(false);
            return;
        }

        await using FileStream fileStream = File.OpenRead(archivePath);
        await using GZipStream gzip = new(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzip, outputDir, overwriteFiles: true, ct).ConfigureAwait(false);
    }

    private static void TryMakeExecutable(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return;
            }

            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }
}
