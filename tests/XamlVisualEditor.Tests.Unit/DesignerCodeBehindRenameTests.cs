using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.CSharp.Language;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerCodeBehindRenameTests
{
    [Fact]
    public async Task RenamePlanTracksControlSymbolsAndTypedLookupsAcrossPartialFilesWithoutMutatingWorkspace()
    {
        const string declaration = """
            using Avalonia.Controls;
            namespace Example;
            public partial class SampleView : UserControl
            {
                public TextBox Source = new();
                public string Inspect() => nameof(Source) + Source.Text;
                public string Literal() => "Source";
                public int Shadow() { int Source = 7; return Source; }
            }
            """;
        const string partial = """
            using Avalonia.Controls;
            namespace Example;
            public partial class SampleView
            {
                public void Update()
                {
                    this.Source.Text = "Source";
                    this.FindControl<TextBox>("Source")!.Text = "current";
                    new OtherView().FindControl<TextBox>("Source");
                }
            }
            public class OtherView : UserControl
            {
                public TextBox Source = new();
                public string Inspect() => Source.Text;
            }
            """;
        using AdhocWorkspace workspace = new();
        ProjectId projectId = ProjectId.CreateNewId();
        MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Concat(new[] { typeof(Control).Assembly.Location, typeof(AvaloniaObject).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        ProjectInfo info = ProjectInfo.Create(projectId, VersionStamp.Create(), "Example", "Example", LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references);
        Project project = workspace.AddProject(info);
        string firstPath = Path.Combine(Path.GetTempPath(), "SampleView.cs");
        string secondPath = Path.Combine(Path.GetTempPath(), "SampleView.Logic.cs");
        Document first = project.AddDocument("SampleView.cs", SourceText.From(declaration), filePath: firstPath);
        Document second = first.Project.AddDocument("SampleView.Logic.cs", SourceText.From(partial), filePath: secondPath);
        Assert.True(workspace.TryApplyChanges(second.Project.Solution));
        Solution before = workspace.CurrentSolution;
        Project originalProject = before.GetProject(projectId)!;
        LanguageWorkspaceEdit plan = Assert.IsType<LanguageWorkspaceEdit>(await DesignerCodeBehindRenamePlanner.CreateAsync(
            originalProject, "Example.SampleView", "Source", "RenamedSource"));
        Assert.Same(before, workspace.CurrentSolution);
        Assert.Equal(2, plan.DocumentEdits.Count);
        Solution changed = before;
        foreach (LanguageDocumentEdit edit in plan.DocumentEdits)
        {
            DocumentId id = Assert.Single(before.GetDocumentIdsWithFilePath(edit.FilePath));
            SourceText text = await before.GetDocument(id)!.GetTextAsync();
            Assert.Equal(text.ToString(), edit.ExpectedText);
            SourceText updated = text.WithChanges(edit.Edits.Select(change =>
                new TextChange(new TextSpan(change.Offset, change.Length), change.NewText)));
            changed = changed.WithDocumentText(id, updated);
        }
        string renamedDeclaration = (await changed.GetDocument(first.Id)!.GetTextAsync()).ToString();
        string renamedPartial = (await changed.GetDocument(second.Id)!.GetTextAsync()).ToString();
        Assert.Contains("TextBox RenamedSource = new()", renamedDeclaration);
        Assert.Contains("nameof(RenamedSource) + RenamedSource.Text", renamedDeclaration);
        Assert.Contains("int Source = 7; return Source;", renamedDeclaration);
        Assert.Contains("Literal() => \"Source\"", renamedDeclaration);
        Assert.Contains("this.RenamedSource.Text = \"Source\"", renamedPartial);
        Assert.Contains("this.FindControl<TextBox>(\"RenamedSource\")", renamedPartial);
        Assert.Contains("new OtherView().FindControl<TextBox>(\"Source\")", renamedPartial);
        Assert.Contains("Inspect() => Source.Text", renamedPartial);
        Compilation compilation = (await changed.GetProject(projectId)!.GetCompilationAsync())!;
        Assert.DoesNotContain(compilation.GetDiagnostics(), diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesignerCodeBehindRenamePlanner.CreateAsync(
            originalProject, "Example.SampleView", "Source", "Inspect"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => DesignerCodeBehindRenamePlanner.CreateAsync(
            originalProject, "Example.SampleView", "Source", null));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DesignerCodeBehindRenamePlanner.CreateAsync(
            originalProject, "Example.SampleView", "Source", "RenamedSource", canceled.Token));
        Assert.Same(before, workspace.CurrentSolution);
    }
}
