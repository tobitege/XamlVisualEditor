using System;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;
using XamlVisualEditor.Designer.PreviewerHost;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerHostNativeLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-native-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ResolveUnmanagedPath_FindsRuntimeSpecificNativeAssetOfTargetOutput()
    {
        string targetDirectory = Path.Combine(_root, "target");
        string nativeDirectory = Path.Combine(targetDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native");
        Directory.CreateDirectory(nativeDirectory);
        string targetAssembly = CopyTargetAssembly(targetDirectory);
        string library = Path.Combine(nativeDirectory, "libXveTest" + NativeExtension());
        File.WriteAllBytes(library, Array.Empty<byte>());

        Program.TargetAssemblyResolver resolver = new(targetAssembly, Path.Combine(_root, "host"));

        Assert.Equal(library, resolver.ResolveUnmanagedPath("libXveTest"));
    }

    [Fact]
    public void ResolveUnmanagedPath_FallsBackToHostOutput()
    {
        string targetDirectory = Path.Combine(_root, "target");
        string hostDirectory = Path.Combine(_root, "host");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(hostDirectory);
        string targetAssembly = CopyTargetAssembly(targetDirectory);
        string library = Path.Combine(hostDirectory, "libXveTest" + NativeExtension());
        File.WriteAllBytes(library, Array.Empty<byte>());

        Program.TargetAssemblyResolver resolver = new(targetAssembly, hostDirectory);

        Assert.Equal(library, resolver.ResolveUnmanagedPath("libXveTest"));
        Assert.Null(resolver.ResolveUnmanagedPath("libXveMissing"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static string CopyTargetAssembly(string targetDirectory)
    {
        string source = typeof(XamlVisualEditor.TestControls.CaptionControl).Assembly.Location;
        string target = Path.Combine(targetDirectory, Path.GetFileName(source));
        File.Copy(source, target);
        return target;
    }

    private static string NativeExtension() =>
        OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";
}
