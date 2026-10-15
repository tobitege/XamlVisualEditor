using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class InstalledExtensionPackagesTests
{
    [Fact]
    public void ResolveMainAssemblyPath_ResolvesInsideTheInstallationDirectoryOnly()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string assemblyPath = Path.Combine(root, "extension.dll");
            File.WriteAllBytes(assemblyPath, [0x4D, 0x5A]);

            Assert.Equal(assemblyPath, InstalledExtensionPackages.ResolveMainAssemblyPath(Package(root, "extension.dll")));
            Assert.Null(InstalledExtensionPackages.ResolveMainAssemblyPath(Package(root, "missing.dll")));
            Assert.Null(InstalledExtensionPackages.ResolveMainAssemblyPath(Package(root, null)));
            Assert.Null(InstalledExtensionPackages.ResolveMainAssemblyPath(Package(null, "extension.dll")));
            Assert.Throws<InvalidDataException>(() =>
                InstalledExtensionPackages.ResolveMainAssemblyPath(Package(root, "..\\extension.dll")));
            Assert.Throws<InvalidDataException>(() =>
                InstalledExtensionPackages.ResolveMainAssemblyPath(Package(root, "extension.json")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FindExtensionTypes_FindsPublicParameterlessImplementations()
    {
        IReadOnlyList<Type> types = InstalledExtensionPackages.FindExtensionTypes(typeof(InstalledExtensionPackagesTests).Assembly);

        Assert.Contains(typeof(SampleInstalledExtension), types);
        Assert.DoesNotContain(typeof(SampleExtensionWithoutDefaultConstructor), types);
    }

    private static ExtensionPackageInfo Package(string? installationDirectory, string? main) =>
        new(Path.Combine(installationDirectory ?? Path.GetTempPath(), "sample.nupkg"),
            new ExtensionManifest { Name = "sample", Publisher = "tests", Version = "1.0.0", Main = main },
            installationDirectory);
}

public sealed class SampleInstalledExtension : IXveExtension
{
    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class SampleExtensionWithoutDefaultConstructor(string name) : IXveExtension
{
    public string Name { get; } = name;

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken) => Task.CompletedTask;
}
