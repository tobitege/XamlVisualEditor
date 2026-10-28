using System;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.App.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DebugToolInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-debug-tool-" + Guid.NewGuid().ToString("N"));

    public DebugToolInstallerTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Theory]
    [InlineData(true, false, false, Architecture.X64, "netcoredbg-win64.zip", "netcoredbg.exe")]
    [InlineData(false, true, false, Architecture.X64, "netcoredbg-linux-amd64.tar.gz", "netcoredbg")]
    [InlineData(false, true, false, Architecture.Arm64, "netcoredbg-linux-arm64.tar.gz", "netcoredbg")]
    [InlineData(false, false, true, Architecture.X64, "netcoredbg-osx-amd64.tar.gz", "netcoredbg")]
    public void Each_Platform_Gets_Its_Own_Build(bool isWindows, bool isLinux, bool isMacOS, Architecture architecture,
        string archiveName, string binaryName)
    {
        DebugToolInstaller.DebugToolPackage? package = DebugToolInstaller.SelectPackage(isWindows, isLinux, isMacOS, architecture);

        Assert.Equal(new DebugToolInstaller.DebugToolPackage(archiveName, binaryName), package);
    }

    [Theory]
    [InlineData(true, false, false, Architecture.Arm64)]
    [InlineData(false, true, false, Architecture.X86)]
    [InlineData(false, false, true, Architecture.Arm64)]
    [InlineData(false, false, false, Architecture.X64)]
    public void A_Platform_Without_A_Build_Gets_None(bool isWindows, bool isLinux, bool isMacOS, Architecture architecture)
    {
        Assert.Null(DebugToolInstaller.SelectPackage(isWindows, isLinux, isMacOS, architecture));
    }

    [Fact]
    public void A_Build_An_Earlier_Version_Installed_For_Every_Platform_Is_Not_Taken()
    {
        Assert.SkipWhen(DebugToolInstaller.Package is null, "netcoredbg has no build for this platform.");
        // Earlier versions put the macOS build into the version folder on every platform.
        string earlierBinary = Path.Combine(_root, "XamlVisualEditor", "tools", "netcoredbg", "3.1.3-1062", "netcoredbg",
            "netcoredbg");
        Directory.CreateDirectory(Path.GetDirectoryName(earlierBinary)!);
        File.WriteAllText(earlierBinary, "a build of another platform");

        Assert.Null(new DebugToolInstaller(_root, new ArchiveHandler(null)).GetNetcoredbgPath());
    }

    [Fact]
    public async Task The_Download_Installs_The_Build_Of_This_Platform_And_Removes_The_Archive()
    {
        DebugToolInstaller.DebugToolPackage? package = DebugToolInstaller.Package;
        Assert.SkipWhen(package is null, "netcoredbg has no build for this platform.");
        string archive = Path.Combine(_root, package!.ArchiveName);
        WriteArchive(archive, "netcoredbg/" + package.BinaryName);
        ArchiveHandler handler = new(archive);
        DebugToolInstaller installer = new(_root, handler);

        string? binary = await installer.EnsureNetcoredbgAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken);

        Assert.Equal(
            Path.Combine(_root, "XamlVisualEditor", "tools", "netcoredbg", "3.1.3-1062", package.FolderName, "netcoredbg",
                package.BinaryName),
            binary);
        Assert.EndsWith("/" + package.ArchiveName, handler.RequestedUrl);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "XamlVisualEditor", "tools")));
    }

    [Fact]
    public async Task A_Failed_Download_Reports_The_Failure()
    {
        Assert.SkipWhen(DebugToolInstaller.Package is null, "netcoredbg has no build for this platform.");
        DebugToolInstaller installer = new(_root, new ArchiveHandler(null));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => installer.EnsureNetcoredbgAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken));

        Assert.Null(installer.GetNetcoredbgPath());
    }

    [Fact]
    public async Task A_Failed_Extraction_Leaves_Neither_Archive_Nor_A_Partial_Installation()
    {
        DebugToolInstaller.DebugToolPackage? package = DebugToolInstaller.Package;
        Assert.SkipWhen(package is null, "netcoredbg has no build for this platform.");
        // The binary comes first, so the extraction has written it when the escaping entry stops it.
        string archive = Path.Combine(_root, package!.ArchiveName);
        WriteArchive(archive, "netcoredbg/" + package.BinaryName, "../escaped");
        DebugToolInstaller installer = new(_root, new ArchiveHandler(archive));

        await Assert.ThrowsAnyAsync<IOException>(
            () => installer.EnsureNetcoredbgAsync(_ => Task.FromResult(true), TestContext.Current.CancellationToken));

        Assert.Null(installer.GetNetcoredbgPath());
        string toolsRoot = Path.Combine(_root, "XamlVisualEditor", "tools");
        Assert.Empty(Directory.GetFiles(toolsRoot));
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(toolsRoot, "netcoredbg", "3.1.3-1062")));
    }

    [Fact]
    public async Task A_Zip_Archive_Is_Extracted()
    {
        string archive = Path.Combine(_root, "tool.zip");
        using (ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            WriteZipEntry(zip, "netcoredbg/netcoredbg.exe", "binary");
        }

        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        await DebugToolInstaller.ExtractAsync(archive, output, TestContext.Current.CancellationToken);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(output, "netcoredbg", "netcoredbg.exe")));
    }

    [Fact]
    public async Task A_Tar_Gz_Archive_Is_Extracted()
    {
        string archive = Path.Combine(_root, "tool.tar.gz");
        WriteTarGz(archive, "netcoredbg/netcoredbg");

        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        await DebugToolInstaller.ExtractAsync(archive, output, TestContext.Current.CancellationToken);

        Assert.Equal("binary", File.ReadAllText(Path.Combine(output, "netcoredbg", "netcoredbg")));
    }

    [Fact]
    public async Task An_Entry_Outside_The_Output_Directory_Is_Refused()
    {
        string archive = Path.Combine(_root, "tool.tar.gz");
        WriteTarGz(archive, "../escaped");

        string output = Directory.CreateDirectory(Path.Combine(_root, "out")).FullName;
        await Assert.ThrowsAsync<IOException>(
            () => DebugToolInstaller.ExtractAsync(archive, output, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Path.Combine(_root, "escaped")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static void WriteArchive(string archive, params string[] entryNames)
    {
        if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            WriteTarGz(archive, entryNames);
            return;
        }

        using ZipArchive zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (string entryName in entryNames)
        {
            WriteZipEntry(zip, entryName, "binary");
        }
    }

    // Serves the given archive for every request, or answers 404 without one.
    private sealed class ArchiveHandler(string? archive) : HttpMessageHandler
    {
        public string RequestedUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri?.ToString() ?? string.Empty;
            HttpResponseMessage response = archive is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(File.ReadAllBytes(archive)) };
            return Task.FromResult(response);
        }
    }

    private static void WriteZipEntry(ZipArchive zip, string name, string content)
    {
        using StreamWriter writer = new(zip.CreateEntry(name).Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private static void WriteTarGz(string archive, params string[] entryNames)
    {
        using FileStream file = File.Create(archive);
        using GZipStream gzip = new(file, CompressionLevel.Fastest);
        using TarWriter tar = new(gzip);
        foreach (string entryName in entryNames)
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(Encoding.UTF8.GetBytes("binary"))
            };
            tar.WriteEntry(entry);
        }
    }
}
