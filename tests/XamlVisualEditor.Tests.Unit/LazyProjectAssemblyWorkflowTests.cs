using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LazyProjectAssemblyWorkflowTests
{
    [AvaloniaFact]
    public void UnusedLibraryWithMissingAttributeDependencyDoesNotPreventControlRendering()
    {
        string suffix = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "xve-lazy-assemblies-" + suffix);
        Directory.CreateDirectory(directory);
        MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Concat(new[] { typeof(Control).Assembly.Location, typeof(AvaloniaObject).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path)).ToArray();

        byte[] missingDependency = Compile("DesignerAnnotations" + suffix, """
            using System;
            [AttributeUsage(AttributeTargets.Assembly)]
            public sealed class UnavailableDesignAttribute : Attribute { }
            """, references);
        string unusedName = "UnusedDesignLibrary" + suffix;
        byte[] unusedLibrary = Compile(unusedName, """
            [assembly: UnavailableDesign]
            public sealed class UnusedDesignType { }
            """, references.Append(MetadataReference.CreateFromImage(missingDependency)).ToArray());
        string usedName = "ActualControlLibrary" + suffix;
        byte[] usedLibrary = Compile(usedName, """
            [assembly: Avalonia.Metadata.XmlnsDefinition("urn:lazy-designer-controls", "DesignerControls")]
            namespace DesignerControls;
            public sealed class ProbeControl : Avalonia.Controls.TextBlock { }
            """, references);
        string unusedPath = Path.Combine(directory, unusedName + ".dll");
        string usedPath = Path.Combine(directory, usedName + ".dll");
        File.WriteAllBytes(unusedPath, unusedLibrary);
        File.WriteAllBytes(usedPath, usedLibrary);

        TypeMetadataService metadata = new();
        metadata.LoadAssemblies(new[] { unusedPath, usedPath });
        Assert.Contains(metadata.GetAvailableTypes(), type => type.FullName == "UnusedDesignType");
        var controlType = metadata.GetType("urn:lazy-designer-controls", "ProbeControl");
        Assert.NotNull(controlType);
        Assert.True(controlType.IsControl);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly =>
            assembly.GetName().Name == unusedName || assembly.GetName().Name == usedName);

        Assembly localAssembly = Assert.IsAssignableFrom<Type>(metadata.ResolveClrType(controlType)).Assembly;
        string xaml = $$"""
            <Border xmlns="https://github.com/avaloniaui"
                    xmlns:controls="clr-namespace:DesignerControls;assembly={{usedName}}">
                <controls:ProbeControl Text="Actual project control" />
            </Border>
            """;
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(new XamlParsingService().Parse(xaml).Document);
        RuntimeDesignRenderResult rendered = new RuntimeDesignRenderer(metadata)
            .Render(document, new Uri(Path.Combine(directory, "Form.axaml")), localAssembly);
        TextBlock control = Assert.IsAssignableFrom<TextBlock>(Assert.IsType<Border>(rendered.Root).Child);
        Assert.Equal("Actual project control", control.Text);
        Assert.Equal(usedName, control.GetType().Assembly.GetName().Name);
        Assert.Empty(rendered.Diagnostics);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), assembly => assembly.GetName().Name == unusedName);
    }

    private static byte[] Compile(string name, string source, MetadataReference[] references)
    {
        CSharpCompilation compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText(source) }, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using MemoryStream output = new();
        var result = compilation.Emit(output);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        return output.ToArray();
    }
}
