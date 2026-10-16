using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Designer.PreviewerHost;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerHostAssemblyFilterTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-previewer-filter-" + Guid.NewGuid().ToString("N"));

    public PreviewerHostAssemblyFilterTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void HasLoadableAssemblyAttributes_AcceptsAttributesFromTheRuntime()
    {
        string path = Emit("RuntimeAttributes", "[assembly: System.Reflection.AssemblyTitle(\"Runtime\")]", Array.Empty<MetadataReference>());

        Assert.True(RuntimeXamlLoaderRegistrar.HasLoadableAssemblyAttributes(path, AssemblyLoadContext.Default));
    }

    [Fact]
    public void HasLoadableAssemblyAttributes_RejectsAttributesFromAnUnavailableAssembly()
    {
        string missingName = "XveMissingAttributes" + Guid.NewGuid().ToString("N");
        MetadataReference missing = CompileInMemory(missingName,
            "namespace Missing { public sealed class MarkerAttribute : System.Attribute { } }");
        string path = Emit("ForeignAttributes", "[assembly: Missing.Marker]", new[] { missing });

        Assert.False(RuntimeXamlLoaderRegistrar.HasLoadableAssemblyAttributes(path, AssemblyLoadContext.Default));
    }

    [Fact]
    public void HasLoadableAssemblyAttributes_RejectsFilesWithoutMetadata()
    {
        string path = Path.Combine(_directory, "NotAnAssembly.dll");
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4 });

        Assert.False(RuntimeXamlLoaderRegistrar.HasLoadableAssemblyAttributes(path, AssemblyLoadContext.Default));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Emit(string name, string source, IEnumerable<MetadataReference> extraReferences)
    {
        string path = Path.Combine(_directory, name + ".dll");
        using FileStream output = File.Create(path);
        var result = CreateCompilation(name, source, extraReferences).Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }

    private static MetadataReference CompileInMemory(string name, string source)
    {
        using MemoryStream image = new();
        var result = CreateCompilation(name, source, Array.Empty<MetadataReference>()).Emit(image);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return MetadataReference.CreateFromImage(image.ToArray());
    }

    private static CSharpCompilation CreateCompilation(string name, string source, IEnumerable<MetadataReference> extraReferences)
    {
        IEnumerable<MetadataReference> runtime = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(file => Path.GetFileName(file).StartsWith("System.", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(file) is "netstandard.dll" or "mscorlib.dll")
            .Select(file => MetadataReference.CreateFromFile(file));
        return CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) },
            runtime.Concat(extraReferences), new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
