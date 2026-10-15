using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class ExtensionPackageStoreTests
{
    [Fact]
    public async Task InstallAsync_CopiesPackage()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        string installRoot = Path.Combine(tempRoot, "installed");
        Directory.CreateDirectory(tempRoot);

        try
        {
            string packagePath = CreatePackage(tempRoot, "sample-extension", "example", "1.0.0");

            ExtensionPackageLoader loader = new();
            ExtensionPackageStore store = new(installRoot, loader);

            ExtensionPackageInfo installed = await store.InstallAsync(packagePath, CancellationToken.None);

            Assert.Equal("example.sample-extension", installed.Manifest.ExtensionId);
            Assert.True(Directory.Exists(installRoot));
            Assert.NotNull(installed.InstallationDirectory);
            string payload = Path.Combine(installed.InstallationDirectory, "lib", "net10.0", "Sample.dll");
            Assert.Equal("sample payload", File.ReadAllText(payload));
            var provider = Assert.Single(DesignerThemePackageContributions.Read(installed));
            Assert.Equal("example.sample-extension/palette", provider.Id);
            Assert.Equal(payload, provider.AssemblyPath);
            Assert.Equal("Example.Library", provider.RequiredAssembly);
            Assert.Equal(installed.InstallationDirectory, Assert.Single(await store.GetInstalledAsync(CancellationToken.None)).InstallationDirectory);
            string newer = CreatePackage(tempRoot, "sample-extension", "example", "1.1.0");
            await store.InstallAsync(newer, CancellationToken.None);
            Assert.Equal("1.1.0", Assert.Single(await store.GetInstalledAsync(CancellationToken.None)).Manifest.Version);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.InstallAsync(newer, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("lib/../../outside.dll")]
    [InlineData("/absolute.dll")]
    public async Task InstallRejectsEscapingArchiveEntriesBeforeCreatingAnInstallation(string entryName)
    {
        string root = Path.Combine(Path.GetTempPath(), "XamlVisualEditor-preview-artifacts", "rejected-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string package = CreatePackage(root, "sample", "example", "1.0.0", entryName);
        string installed = Path.Combine(root, "installed");
        ExtensionPackageStore store = new(installed, new ExtensionPackageLoader());
        await Assert.ThrowsAsync<InvalidDataException>(() => store.InstallAsync(package, CancellationToken.None));
        Assert.False(Directory.Exists(installed));
    }

    [Fact]
    public async Task GetInstalledExtractsAnArchiveOnlyInstallation()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string versionDirectory = Path.Combine(root, "installed", "example.sample-extension", "1.0.0");
            Directory.CreateDirectory(Path.Combine(versionDirectory, "content", "lib"));
            File.Move(CreatePackage(root, "sample-extension", "example", "1.0.0"),
                Path.Combine(versionDirectory, "example.sample-extension.1.0.0.nupkg"));
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());

            ExtensionPackageInfo installed = Assert.Single(await store.GetInstalledAsync(CancellationToken.None));

            Assert.Equal(versionDirectory, installed.InstallationDirectory);
            Assert.Equal("sample payload", File.ReadAllText(Path.Combine(versionDirectory, "lib", "net10.0", "Sample.dll")));
            Assert.False(Directory.Exists(Path.Combine(versionDirectory, "content")));
            Assert.Equal(["1.0.0"], Directory.GetDirectories(Path.GetDirectoryName(versionDirectory)!).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledSkipsAPackageWhoseIdentityDoesNotMatchItsDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "1.0.0"), CancellationToken.None);
            string foreignDirectory = Path.Combine(root, "installed", "example.other", "2.0.0");
            Directory.CreateDirectory(foreignDirectory);
            File.Move(CreatePackage(root, "sample-extension", "example", "2.0.0"),
                Path.Combine(foreignDirectory, "example.other.2.0.0.nupkg"));

            ExtensionPackageInfo installed = Assert.Single(await store.GetInstalledAsync(CancellationToken.None));

            Assert.Equal("example.sample-extension", installed.Manifest.ExtensionId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledRemovesAbandonedStagingDirectoriesOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string extensionDirectory = Path.Combine(root, "installed", "example.sample-extension");
            string abandoned = Path.Combine(extensionDirectory, "1.0.0.installing-" + Guid.NewGuid().ToString("N"));
            string running = Path.Combine(extensionDirectory, "1.0.0.installing-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(abandoned);
            Directory.CreateDirectory(running);
            Directory.SetLastWriteTimeUtc(abandoned, DateTime.UtcNow.AddHours(-2));
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());

            Assert.Empty(await store.GetInstalledAsync(CancellationToken.None));

            Assert.False(Directory.Exists(abandoned));
            Assert.True(Directory.Exists(running));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledRestoresTheInstallationWhenMovingTheExtractedContentFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string versionDirectory = CreateArchiveOnlyInstallation(root);
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader(), null,
                (source, destination, _) =>
                {
                    if (Path.GetFileName(source).Contains(".installing-", StringComparison.Ordinal))
                        throw new IOException("Simulated rename failure.");
                    Directory.Move(source, destination);
                    return Task.CompletedTask;
                });

            Assert.Empty(await store.GetInstalledAsync(CancellationToken.None));

            Assert.True(File.Exists(Path.Combine(versionDirectory, "example.sample-extension.1.0.0.nupkg")));
            Assert.Equal(["1.0.0"], Directory.GetDirectories(Path.GetDirectoryName(versionDirectory)!).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledKeepsAFreshlyRetiredDirectoryWhoseTimestampIsOld()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string versionDirectory = CreateArchiveOnlyInstallation(root);
            Directory.SetLastWriteTimeUtc(versionDirectory, DateTime.UtcNow.AddHours(-2));
            ExtensionPackageStore failingStore = new(Path.Combine(root, "installed"), new ExtensionPackageLoader(), null,
                (source, destination, _) =>
                {
                    if (string.Equals(destination, versionDirectory, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Simulated rename failure.");
                    Directory.Move(source, destination);
                    return Task.CompletedTask;
                });
            Assert.Empty(await failingStore.GetInstalledAsync(CancellationToken.None));
            string retired = Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(versionDirectory)!, "1.0.0.retired-*"));
            Assert.True(Directory.GetLastWriteTimeUtc(retired) < DateTime.UtcNow.AddHours(-1));

            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            await store.GetInstalledAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(retired, "example.sample-extension.1.0.0.nupkg")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledSkipsAnExtensionDirectoryThatIsALink()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "1.0.0"), CancellationToken.None);
            string target = Path.Combine(root, "elsewhere");
            Directory.CreateDirectory(target);
            CreateDirectoryLink(Path.Combine(root, "installed", "example.linked"), target);

            ExtensionPackageInfo installed = Assert.Single(await store.GetInstalledAsync(CancellationToken.None));

            Assert.Equal("example.sample-extension", installed.Manifest.ExtensionId);
        }
        finally
        {
            // A recursive delete does not remove a junction; the link itself goes first.
            string link = Path.Combine(root, "installed", "example.linked");
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ConcurrentGetInstalledMigratesAnArchiveOnlyInstallationOnce()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string versionDirectory = CreateArchiveOnlyInstallation(root);
            int migrations = 0;
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader(), null,
                async (source, destination, ct) =>
                {
                    await Task.Delay(50, ct);
                    if (Path.GetFileName(source).Contains(".installing-", StringComparison.Ordinal))
                        Interlocked.Increment(ref migrations);
                    Directory.Move(source, destination);
                });

            IReadOnlyList<ExtensionPackageInfo>[] results = await Task.WhenAll(
                Task.Run(() => store.GetInstalledAsync(CancellationToken.None)),
                Task.Run(() => store.GetInstalledAsync(CancellationToken.None)));

            Assert.All(results, result => Assert.Equal(versionDirectory, Assert.Single(result).InstallationDirectory));
            Assert.Equal(1, migrations);
            Assert.Equal("sample payload", File.ReadAllText(Path.Combine(versionDirectory, "lib", "net10.0", "Sample.dll")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledRenamesAnAbandonedRetiredDirectoryBackWhenItsVersionDirectoryIsMissing()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            string versionDirectory = (await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "1.0.0"), CancellationToken.None))
                .InstallationDirectory!;
            await MoveFreshDirectoryAsync(versionDirectory, versionDirectory + AbandonedWorkDirectorySuffix(".retired-"));
            string otherVersion = (await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "0.9.0"), CancellationToken.None))
                .InstallationDirectory!;
            string staleCopy = otherVersion + AbandonedWorkDirectorySuffix(".retired-");
            Directory.CreateDirectory(staleCopy);

            ExtensionPackageInfo installed = Assert.Single(await store.GetInstalledAsync(CancellationToken.None));

            Assert.Equal(versionDirectory, installed.InstallationDirectory);
            Assert.True(File.Exists(Path.Combine(versionDirectory, "example.sample-extension.1.0.0.nupkg")));
            Assert.Equal(["0.9.0", "1.0.0"],
                Directory.GetDirectories(Path.GetDirectoryName(versionDirectory)!).Select(Path.GetFileName).Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("sample-extension", "1.0.0.installing-1")]
    [InlineData("sample-extension", "1.0.0.retired-1")]
    [InlineData("sample.retired-1", "1.0.0")]
    public async Task InstallRejectsNamesThatContainAWorkDirectoryMarker(string name, string version)
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string installRoot = Path.Combine(root, "installed");
            ExtensionPackageStore store = new(installRoot, new ExtensionPackageLoader());

            await Assert.ThrowsAsync<InvalidDataException>(
                () => store.InstallAsync(CreatePackage(root, name, "example", version), CancellationToken.None));

            Assert.False(Directory.Exists(installRoot));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetInstalledRemovesTheRetiredDirectoryOfAnUninstalledExtension()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            string retired = Path.Combine(root, "installed", "example.sample-extension.retired-1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(retired, "1.0.0"));
            File.WriteAllText(Path.Combine(retired, "1.0.0", "Left.dll"), "left over");
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());

            Assert.Empty(await store.GetInstalledAsync(CancellationToken.None));

            Assert.False(Directory.Exists(retired));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UninstallRemovesTheExtensionFromTheListingWhileOneOfItsAssembliesIsLoaded()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string installRoot = Path.Combine(root, "installed");
        LoadedAssemblyHolder holder = new();
        try
        {
            ExtensionPackageStore store = new(installRoot, new ExtensionPackageLoader());
            ExtensionPackageInfo installed = await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "1.0.0"), CancellationToken.None);
            string assemblyPath = Path.Combine(installed.InstallationDirectory!, "lib", "net10.0", "Loaded.dll");
            File.Copy(typeof(ExtensionPackageLoader).Assembly.Location, assemblyPath);
            holder.Load(assemblyPath);

            await store.UninstallAsync("example.sample-extension", CancellationToken.None);

            Assert.False(Directory.Exists(Path.Combine(installRoot, "example.sample-extension")));
            Assert.Empty(await store.GetInstalledAsync(CancellationToken.None));
            ExtensionPackageInfo reinstalled = await store.InstallAsync(CreatePackage(root, "sample-extension", "example", "1.0.0"), CancellationToken.None);
            Assert.Equal(installed.InstallationDirectory, Assert.Single(await store.GetInstalledAsync(CancellationToken.None)).InstallationDirectory);
            Assert.Equal(installed.InstallationDirectory, reinstalled.InstallationDirectory);
        }
        finally
        {
            holder.Unload();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    // The marker followed by creation ticks two hours in the past and a unique id.
    private static string AbandonedWorkDirectorySuffix(string marker) =>
        marker + DateTime.UtcNow.AddHours(-2).Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");

    // Keeps an assembly file mapped like a loaded extension assembly until Unload is called.
    private sealed class LoadedAssemblyHolder
    {
        private AssemblyLoadContext? _context;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Load(string path)
        {
            _context = new AssemblyLoadContext("extension-package-store-test", isCollectible: true);
            _context.LoadFromAssemblyPath(path);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public void Unload()
        {
            if (_context is null) return;
            WeakReference reference = new(_context);
            _context.Unload();
            _context = null;
            for (int attempt = 0; attempt < 10 && reference.IsAlive; attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    private static string CreateArchiveOnlyInstallation(string root)
    {
        string versionDirectory = Path.Combine(root, "installed", "example.sample-extension", "1.0.0");
        Directory.CreateDirectory(versionDirectory);
        File.Move(CreatePackage(root, "sample-extension", "example", "1.0.0"),
            Path.Combine(versionDirectory, "example.sample-extension.1.0.0.nupkg"));
        return versionDirectory;
    }

    // A junction needs no elevation on Windows; other systems create a symbolic link.
    private static void CreateDirectoryLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using Process process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, target },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    // A virus scanner may still hold freshly extracted files open, which denies moving their directory for a moment.
    private static async Task MoveFreshDirectoryAsync(string source, string destination)
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
                await Task.Delay(100);
            }
        }
    }

    private static string CreatePackage(string root, string name, string publisher, string version, string? extraEntry = null)
    {
        string packagePath = Path.Combine(root, Guid.NewGuid().ToString("N") + ".nupkg");
        using ZipArchive zip = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        ZipArchiveEntry entry = zip.CreateEntry("xve.extension.json");
        using (StreamWriter writer = new(entry.Open(), Encoding.UTF8))
            writer.Write("{" +
            "\"name\":\"" + name + "\"," +
            "\"publisher\":\"" + publisher + "\"," +
            "\"version\":\"" + version + "\"," +
            "\"contributes\":{\"designerThemes\":[{\"id\":\"palette\",\"name\":\"Sample\",\"assembly\":\"lib/net10.0/Sample.dll\",\"type\":\"Example.Provider\",\"requiresAssembly\":\"Example.Library\"}]}" +
            "}");

        using (StreamWriter writer = new(zip.CreateEntry("lib/net10.0/Sample.dll").Open(), Encoding.UTF8)) writer.Write("sample payload");
        if (extraEntry is not null)
            using (StreamWriter writer = new(zip.CreateEntry(extraEntry).Open(), Encoding.UTF8)) writer.Write("rejected payload");
        return packagePath;
    }
}
