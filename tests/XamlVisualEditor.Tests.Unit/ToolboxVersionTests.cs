using System.Collections.Immutable;
using System.Reactive.Concurrency;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxVersionTests
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
        Assert.Contains(baseline.Controls, item => item.FullName == typeof(Button).FullName && item.IsStandardControl);
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

    [AvaloniaFact]
    public async Task OfficialLibrariesStayBeforeFavoritesAndAvaloniaNamedThirdPartyLibraries()
    {
        string thirdPartyPath = CompileLibrary("Avalonia.Aardvark", "1.2.3.0", """
            namespace ThirdParty;
            public sealed class ThirdPartyButton : Avalonia.Controls.Button { }
            """, officialKey: false, referenceAvalonia: true);
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        catalog.SetWorkspaceAssemblies(new[] { thirdPartyPath });
        SynchronizationContext context = Assert.IsAssignableFrom<SynchronizationContext>(SynchronizationContext.Current);
        using ToolboxPanelViewModel model = new(new CommandRegistry(), new InMemorySettingsStore(), catalog,
            new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(context));
        await model.ReloadAsync();

        ToolboxAssemblyGroup official = model.AssemblyGroups[0];
        Assert.Equal("Avalonia.Controls", official.AssemblyName);
        Assert.True(official.IsStandardAssembly);
        Assert.Equal(typeof(Control).Assembly.GetName().Version!.ToString(), official.AssemblyVersion);
        ToolboxAssemblyGroup thirdParty = Assert.Single(model.AssemblyGroups, group => group.AssemblyName == "Avalonia.Aardvark");
        Assert.False(thirdParty.IsStandardAssembly);
        Assert.All(thirdParty.Items, item => Assert.False(item.IsStandardControl));
        thirdParty.IsFavorite = true;
        Assert.True(thirdParty.IsFavorite);
        Assert.Same(official, model.AssemblyGroups[0]);
        Assert.Equal("Avalonia.Controls", model.Libraries[1].Value);
        Assert.Same(official, model.AssemblyChoices[0]);
        Assert.Contains("1.2.3.0", thirdParty.Description);
        model.SearchText = "ThirdPartyButton";
        Assert.Same(thirdParty, Assert.Single(model.AssemblyGroups));
        model.SearchText = string.Empty;
        Assert.Same(official, model.AssemblyGroups[0]);
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

    private static string CompileLibrary(string name, string version, string source, bool officialKey,
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
