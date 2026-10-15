using System.IO.Compression;
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
