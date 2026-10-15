using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.CSharp.Language;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerEventBindingResolverTests
{
    [Fact]
    public async Task ProjectLoadedOncePerTargetFrameworkResolvesTheHandler()
    {
        const string source = """
            using Avalonia.Controls;
            using Avalonia.Interactivity;

            namespace Example;

            public partial class SampleView : UserControl
            {
                private void OnSaveClick(object? sender, RoutedEventArgs e) { }
            }
            """;
        string projectPath = Path.Combine(Path.GetTempPath(), "Example", "Example.csproj");
        string documentPath = Path.Combine(Path.GetTempPath(), "Example", "SampleView.axaml.cs");
        MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Concat(new[] { typeof(Control).Assembly.Location, typeof(AvaloniaObject).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        using AdhocWorkspace workspace = new();
        Solution solution = workspace.CurrentSolution;
        foreach (string framework in new[] { "net8.0", "net10.0" })
        {
            ProjectId projectId = ProjectId.CreateNewId();
            solution = solution
                .AddProject(ProjectInfo.Create(projectId, VersionStamp.Create(), $"Example({framework})", "Example",
                    LanguageNames.CSharp, filePath: projectPath,
                    compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
                    metadataReferences: references))
                .AddDocument(DocumentId.CreateNewId(projectId), "SampleView.axaml.cs", SourceText.From(source), filePath: documentPath);
        }

        DesignerEventBindingResult result = await DesignerEventBindingResolver.ResolveAsync(solution,
            new DesignerEventBindingRequest(projectPath, "Example.SampleView", "Avalonia.Controls.Button", "Avalonia.Controls",
                "Click", "OnSaveClick", new Dictionary<string, string>()));

        Assert.Equal(documentPath, result.FilePath);
        Assert.Equal("OnSaveClick", result.ExpectedText!.Substring(result.NameOffset, result.NameLength));
    }
}
