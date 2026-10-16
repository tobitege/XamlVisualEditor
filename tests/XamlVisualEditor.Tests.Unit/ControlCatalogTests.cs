using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ControlCatalogTests
{
    [Fact]
    public async Task ProjectVersionsReplaceTheBaseCatalogWithoutLeakingNewerControls()
    {
        string oldPath = CompileLibrary("Avalonia.Controls", "11.9.0.0", """
            namespace Avalonia.Controls;
            public class Control { }
            public class OldOnly : Control { }
            """, officialKey: true);
        string newPath = CompileLibrary("Avalonia.Controls", "12.9.0.0", """
            namespace Avalonia.Controls;
            public class Control { }
            public class NewOnly : Control { }
            """, officialKey: true);
        int reads = 0;
        MetadataControlCatalog catalog = new((paths, error, progress, token, includeProjectControls) =>
        {
            reads++;
            return TypeMetadataService.ReadControlTypes(paths, error, progress, token, includeProjectControls);
        });

        ControlCatalogSnapshot baseline = await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false);
        Assert.Contains(baseline.Controls, item => item.FullName == typeof(Avalonia.Controls.Button).FullName && item.IsStandardControl);
        string basePath = typeof(AvaloniaObject).Assembly.Location;
        string hostControls = typeof(Control).Assembly.Location;
        // An outdated copy in the output directory must not override the resolved reference.
        catalog.SetWorkspaceAssemblies(new[] { oldPath, basePath, hostControls });
        ControlCatalogSnapshot old = await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false);
        Assert.Contains(old.Controls, item => item.TypeName == "OldOnly" && item.AssemblyVersion == "11.9.0.0");
        Assert.DoesNotContain(old.Controls, item => item.TypeName is "Button" or "NewOnly");

        catalog.SetWorkspaceAssemblies(new[] { newPath, basePath });
        ControlCatalogSnapshot newer = await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false);
        Assert.Contains(newer.Controls, item => item.TypeName == "NewOnly" && item.AssemblyVersion == "12.9.0.0");
        Assert.DoesNotContain(newer.Controls, item => item.TypeName is "Button" or "OldOnly");

        catalog.SetWorkspaceAssemblies(new[] { oldPath, basePath, hostControls });
        Assert.Same(old, await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false));
        Assert.Equal(3, reads);
        Assert.NotSame(old, await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false, forceRefresh: true));
        Assert.Equal(4, reads);

        File.Copy(newPath, oldPath, overwrite: true);
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddSeconds(2));
        ControlCatalogSnapshot rebuilt = await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false);
        Assert.Contains(rebuilt.Controls, item => item.TypeName == "NewOnly" && item.AssemblyVersion == "12.9.0.0");
        Assert.DoesNotContain(rebuilt.Controls, item => item.TypeName == "OldOnly");

        catalog.SetWorkspaceAssemblies(Array.Empty<string>());
        Assert.Same(baseline, await catalog.GetControlsAsync(CancellationToken.None, includeProjectControls: false));
    }

    [Fact]
    public void ResolvedReferencesPrecedeOutputCopiesInTheProjectCatalog()
    {
        string resolved = CompileLibrary("Versioned.Controls", "1.0.0.0", "public class Old { }", false);
        string output = CompileLibrary("Versioned.Controls", "2.0.0.0", "public class New { }", false);
        ProjectModel project = new()
        {
            Name = "Project",
            ProjectPath = Path.Combine(Path.GetDirectoryName(output)!, "Project.csproj"),
            OutputAssemblyPath = output,
            XamlFiles = Array.Empty<XamlFileModel>(),
            Files = Array.Empty<ProjectFileModel>(),
            References = new[] { new AssemblyReference { Name = "Versioned.Controls", Path = resolved } }
        };
        IReadOnlyList<string> paths = WorkspaceAssemblyDiscovery.FindCatalogAssemblies(project);
        Assert.Equal(resolved, paths[0]);
        IReadOnlyList<TypeMetadata> types = TypeMetadataService.ReadControlTypes(paths,
            message => throw new IOException(message), (_, _, _) => { }, CancellationToken.None);
        Assert.Contains(types, type => type.Name == "Old" && type.AssemblyVersion == "1.0.0.0");
        Assert.DoesNotContain(types, type => type.Name == "New");
    }

    [Fact]
    public async Task UnusableAssembliesAreSkippedWhileOtherControlsAreLoaded()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-unusable-" + Guid.NewGuid().ToString("N") + ".dll");
        await File.WriteAllBytesAsync(path, new byte[] { 0x4d, 0x5a, 0, 0 });
        try
        {
            MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
            List<string> assemblies = new() { path, typeof(CaptionControl).Assembly.Location };
            if (OperatingSystem.IsWindows())
            {
                string nativePath = Path.Combine(Environment.SystemDirectory, "kernel32.dll");
                using FileStream nativeStream = File.OpenRead(nativePath);
                using System.Reflection.PortableExecutable.PEReader reader = new(nativeStream);
                Assert.False(reader.HasMetadata);
                assemblies.Insert(0, nativePath);
            }
            catalog.SetWorkspaceAssemblies(assemblies);
            ControlCatalogSnapshot result = await catalog.GetControlsAsync(CancellationToken.None);
            Assert.Empty(result.Diagnostics);
            Assert.Contains(result.Controls, item => item.FullName == typeof(CaptionControl).FullName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadingControlTypesStopsWhenCancelledDuringAnAssembly()
    {
        string libraryPath = typeof(CaptionControl).Assembly.Location;
        using CancellationTokenSource cancellation = new();
        List<string> visited = new();
        Assert.Throws<OperationCanceledException>(() => TypeMetadataService.ReadControlTypes(
            new[] { libraryPath }, _ => { }, (_, _, assembly) =>
            {
                visited.Add(assembly);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Single(visited);
    }

    internal static string CompileLibrary(string name, string version, string source, bool officialKey,
        bool referenceAvalonia = false)
    {
        string directory = Path.Combine(Path.GetTempPath(), "xve-toolbox-versions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name + ".dll");
        IEnumerable<string> references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(file => !Path.GetFileName(file).StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase));
        if (referenceAvalonia)
            references = references.Concat(new[] { typeof(Control).Assembly.Location, typeof(AvaloniaObject).Assembly.Location });
        CSharpCompilationOptions options = new(OutputKind.DynamicallyLinkedLibrary);
        if (officialKey)
        {
            byte[] key = typeof(Control).Assembly.GetName().GetPublicKey()!;
            Assert.NotEmpty(key);
            options = options.WithCryptoPublicKey(ImmutableArray.CreateRange(key)).WithPublicSign(true);
        }
        CSharpCompilation compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]\n" + source) },
            references.Distinct(StringComparer.OrdinalIgnoreCase).Select(file => MetadataReference.CreateFromFile(file)), options);
        using FileStream output = File.Create(path);
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return path;
    }
}
