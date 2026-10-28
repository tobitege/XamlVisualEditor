using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.CSharp.Language;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Language;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Xaml.Language;
using XamlVisualEditor.Xaml.Parsing;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class XamlRenameTests : IDisposable
{
    private const string ViewXaml = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                     x:Class="Example.SampleView">
          <UserControl.Styles>
            <Style Selector="TextBlock#Title">
              <Setter Property="FontSize" Value="20" />
            </Style>
          </UserControl.Styles>
          <StackPanel>
            <TextBlock x:Name="Title" Text="Hello" />
            <TextBlock Text="{Binding #Title.Text}" />
            <TextBlock Text="{Binding Text, ElementName=Title}" />
            <Button Content="OnGo" Click="OnGo" />
            <Button Content="Again" PointerPressed="OnGo" />
            <ListBox>
              <ListBox.ItemTemplate>
                <DataTemplate>
                  <TextBlock x:Name="Item" Text="Item" />
                </DataTemplate>
              </ListBox.ItemTemplate>
            </ListBox>
          </StackPanel>
        </UserControl>
        """;

    private const string ViewCode = """
        using Avalonia.Controls;
        using Avalonia.Interactivity;
        namespace Example;
        public partial class SampleView : UserControl
        {
            private TextBlock Title = new();
            private TextBlock Item = new();
            private void OnGo(object? sender, RoutedEventArgs e) => Title.Text = this.FindControl<TextBlock>("Title")!.Text;
            public void Repeat() => OnGo(null, new RoutedEventArgs());
        }
        """;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-xaml-rename-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Rename_Of_A_Name_Changes_Its_Selectors_And_Bindings()
    {
        string xaml = ViewXaml.Replace(" x:Class=\"Example.SampleView\"", string.Empty, StringComparison.Ordinal);
        XamlLanguageService service = CreateService(null);

        LanguageWorkspaceEdit edit = Assert.IsType<LanguageWorkspaceEdit>(
            await service.RenameSymbolAsync(Context("View.axaml", xaml, "x:Name=\"Ti", "Header")));

        Assert.Equal(xaml.Replace("Title", "Header", StringComparison.Ordinal), Apply(xaml, Assert.Single(edit.DocumentEdits)));
    }

    [Fact]
    public async Task Rename_Of_A_Handler_Changes_Only_Event_Attributes()
    {
        string xaml = ViewXaml.Replace(" x:Class=\"Example.SampleView\"", string.Empty, StringComparison.Ordinal);
        XamlLanguageService service = CreateService(null);

        LanguageWorkspaceEdit edit = Assert.IsType<LanguageWorkspaceEdit>(
            await service.RenameSymbolAsync(Context("View.axaml", xaml, "Click=\"On", "OnStart")));

        string expected = xaml.Replace("Click=\"OnGo\"", "Click=\"OnStart\"", StringComparison.Ordinal)
            .Replace("PointerPressed=\"OnGo\"", "PointerPressed=\"OnStart\"", StringComparison.Ordinal);
        Assert.Equal(expected, Apply(xaml, Assert.Single(edit.DocumentEdits)));
        Assert.Null(await service.PrepareRenameAsync(Context("View.axaml", xaml, "Content=\"On", "OnStart")));
        Assert.Null(await service.PrepareRenameAsync(Context("View.axaml", xaml, "Text=\"He", "World")));
    }

    [Fact]
    public async Task Rename_To_A_Name_That_Is_Taken_Is_Refused()
    {
        string xaml = ViewXaml.Replace(" x:Class=\"Example.SampleView\"", string.Empty, StringComparison.Ordinal)
            .Replace("<TextBlock Text=\"{Binding #Title.Text}\" />", "<TextBlock x:Name=\"Other\" />", StringComparison.Ordinal);
        XamlLanguageService service = CreateService(null);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameSymbolAsync(Context("View.axaml", xaml, "x:Name=\"Ti", "Other")));

        Assert.Equal(string.Format(UiStrings.DesignerNameInUse, "Other"), refused.Message);
    }

    [Fact]
    public async Task Without_CSharp_Refactoring_Names_And_Handlers_Of_A_Code_Behind_Class_Are_Not_Renamed()
    {
        XamlLanguageService service = CreateService(null);

        foreach (string caret in new[] { "x:Name=\"Ti", "Click=\"On" })
        {
            LanguageRenameContext context = Context("View.axaml", ViewXaml, caret, "Renamed");
            Assert.Null(await service.PrepareRenameAsync(context));
            Assert.Null(await service.RenameSymbolAsync(context));
        }
    }

    [Fact]
    public async Task Rename_Of_A_Name_Or_A_Handler_Changes_The_Code_Behind_Too()
    {
        string xamlPath = CreateFile("SampleView.axaml", ViewXaml);
        string codePath = CreateFile("SampleView.axaml.cs", ViewCode);
        using AdhocWorkspace workspace = new();
        XamlLanguageService service = CreateService(new PlannerRefactoring(CreateSolution(workspace, xamlPath, codePath)));

        LanguageWorkspaceEdit name = Assert.IsType<LanguageWorkspaceEdit>(
            await service.RenameSymbolAsync(Context(xamlPath, ViewXaml, "x:Name=\"Ti", "Header")));
        LanguageWorkspaceEdit handler = Assert.IsType<LanguageWorkspaceEdit>(
            await service.RenameSymbolAsync(Context(xamlPath, ViewXaml, "Click=\"On", "OnStart")));
        LanguageWorkspaceEdit templateName = Assert.IsType<LanguageWorkspaceEdit>(
            await service.RenameSymbolAsync(Context(xamlPath, ViewXaml, "x:Name=\"It", "Row")));

        Assert.Equal(ViewXaml.Replace("Title", "Header", StringComparison.Ordinal), Apply(ViewXaml, name.DocumentEdits[0]));
        Assert.Equal(ViewCode.Replace("Title", "Header", StringComparison.Ordinal),
            Apply(ViewCode, Assert.Single(name.DocumentEdits, edit => edit.FilePath == codePath)));
        Assert.Equal(ViewXaml.Replace("Click=\"OnGo\"", "Click=\"OnStart\"", StringComparison.Ordinal)
                .Replace("PointerPressed=\"OnGo\"", "PointerPressed=\"OnStart\"", StringComparison.Ordinal),
            Apply(ViewXaml, handler.DocumentEdits[0]));
        Assert.Equal(ViewCode.Replace("OnGo", "OnStart", StringComparison.Ordinal),
            Apply(ViewCode, Assert.Single(handler.DocumentEdits, edit => edit.FilePath == codePath)));
        // A name inside a template gets no field, so the code-behind keeps its member of that name.
        Assert.Equal(ViewXaml.Replace("x:Name=\"Item\"", "x:Name=\"Row\"", StringComparison.Ordinal),
            Apply(ViewXaml, Assert.Single(templateName.DocumentEdits)));
    }

    [Fact]
    public async Task Code_Behind_Rename_Needs_The_Project_That_Lists_The_Xaml_File()
    {
        string xamlPath = CreateFile("SampleView.axaml", ViewXaml);
        string codePath = CreateFile("SampleView.axaml.cs", ViewCode);
        using AdhocWorkspace workspace = new();
        XamlLanguageService service = CreateService(new PlannerRefactoring(CreateSolution(workspace, xamlPath, codePath)));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RenameSymbolAsync(Context(Path.Combine(_root, "Other.axaml"), ViewXaml, "x:Name=\"Ti", "Header")));

        Assert.Equal(UiStrings.CodeBehindRenameOwningProjectMissing, refused.Message);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Rename_Through_The_Extension_Api_Changes_Xaml_And_Code_Behind_And_Undoes_Both()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string xamlPath = CreateFile("SampleView.axaml", ViewXaml);
        string codePath = CreateFile("SampleView.axaml.cs", ViewCode);
        using AdhocWorkspace workspace = new();
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        XamlLanguageService service = new(CompletionProviderRegistry.CreateDefault(), new XamlParsingService(),
            new TypeMetadataService(), new PlannerRefactoring(CreateSolution(workspace, xamlPath, codePath)),
            main.GetOpenTextDocumentTexts);
        using EditorServicesAdapter editor = new(main);
        LanguageNavigationServiceAdapter navigation = new(new LanguageServiceRegistry(new[] { service }), editor);
        await main.OpenFileAsync(codePath).WaitAsync(cancellationToken);
        TextDocumentViewModel code = Assert.IsType<TextDocumentViewModel>(main.ActiveDocument);
        // The unsaved text of the code-behind is what the rename plans against.
        string edited = ViewCode.Replace("public void Repeat()", "internal void Repeat()", StringComparison.Ordinal);
        code.Document.Text = edited;
        await main.OpenFileAsync(xamlPath).WaitAsync(cancellationToken);
        DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);
        LanguageRenameContext context = Context(xamlPath, ViewXaml, "x:Name=\"Ti", "Header");

        // Extensions may call from any thread; the editor reads its documents and applies the edit on the UI thread.
        LanguageWorkspaceEdit edit = Assert.IsType<LanguageWorkspaceEdit>(
            await Task.Run(() => navigation.RenameAsync(context, cancellationToken), cancellationToken));
        Assert.Equal(edited, Assert.Single(edit.DocumentEdits, item => item.FilePath == codePath).ExpectedText);
        Assert.True(await Task.Run(() => editor.ApplyWorkspaceEditAsync(edit, cancellationToken), cancellationToken));

        Assert.Same(designer, main.ActiveDocument);
        Assert.Equal(ViewXaml.Replace("Title", "Header", StringComparison.Ordinal), designer.CodeEditor.Document.Text);
        Assert.Equal(edited.Replace("Title", "Header", StringComparison.Ordinal), code.Document.Text);

        await ((IWorkspaceCommands)main).UndoAsync(cancellationToken);

        Assert.Equal(ViewXaml, designer.CodeEditor.Document.Text);
        Assert.Equal(edited, code.Document.Text);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Designer_Rename_In_A_Loaded_Project_Changes_The_Closed_Code_Behind_Without_Switching_To_It()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string xamlPath = CreateFile("SampleView.axaml", ViewXaml);
        string codePath = CreateFile("SampleView.axaml.cs", ViewCode);
        using AdhocWorkspace workspace = new();
        Solution solution = CreateSolution(workspace, xamlPath, codePath);
        string projectPath = CreateFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        WorkspaceModel project = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Example", ProjectPath = projectPath,
                    XamlFiles = new[] { new XamlFileModel { FilePath = xamlPath, RelativePath = "SampleView.axaml" } },
                    Files = Array.Empty<ProjectFileModel>(), References = Array.Empty<AssemblyReference>()
                }
            }
        };
        using MainWindowViewModel main = new(
            workspaceService: new FixedWorkspaceService(project),
            metadataService: new TypeMetadataService(),
            languageRegistry: new LanguageServiceRegistry(new[] { new PlannerLanguageService(solution) }),
            recentFilesPath: Path.Combine(_root, "recent.json"));
        using IDisposable trust = main.PreviewerTrustInteraction.RegisterHandler(context =>
            context.SetOutput(PreviewerTrustDecision.Cancel));
        await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);
        await main.OpenFileAsync(xamlPath).WaitAsync(cancellationToken);
        DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);
        MutableAstObjectNode title = XamlNames.Objects(designer.SyncEngine.CurrentDocument!.Root!)
            .Single(node => node.GetPropertyValue("x:Name") == "Title");

        Assert.True(await main.RenameDesignerControlAsync(designer, title, "Header", cancellationToken));

        TextDocumentViewModel code = Assert.IsType<TextDocumentViewModel>(
            main.Documents.Single(document => document.FilePath == codePath));
        Assert.Same(designer, main.ActiveDocument);
        Assert.Equal(ViewCode.Replace("Title", "Header", StringComparison.Ordinal), code.Document.Text);
        Assert.Contains("TextBlock#Header", designer.SyncEngine.CurrentText);

        await ((IWorkspaceCommands)main).UndoAsync(cancellationToken);

        Assert.Equal(ViewCode, code.Document.Text);
        Assert.Contains("TextBlock#Title", designer.SyncEngine.CurrentText);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Designer_Event_Handler_Uses_The_Project_That_Lists_The_Xaml_File()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string xamlPath = CreateFile("SampleView.axaml", ViewXaml);
        string codePath = CreateFile("SampleView.axaml.cs", ViewCode);
        string projectPath = CreateFile("Example.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        using AdhocWorkspace workspace = new();
        PlannerLanguageService service = new(CreateSolution(workspace, xamlPath, codePath));

        foreach (int owners in new[] { 1, 2 })
        {
            WorkspaceModel project = new()
            {
                Projects = Enumerable.Range(0, owners).Select(index => new ProjectModel
                {
                    Name = "Example" + index, ProjectPath = index == 0 ? projectPath : Path.Combine(_root, "Other.csproj"),
                    XamlFiles = new[] { new XamlFileModel { FilePath = xamlPath, RelativePath = "SampleView.axaml" } },
                    Files = Array.Empty<ProjectFileModel>(), References = Array.Empty<AssemblyReference>()
                }).ToArray()
            };
            using MainWindowViewModel main = new(
                workspaceService: new FixedWorkspaceService(project),
                metadataService: new TypeMetadataService(),
                languageRegistry: new LanguageServiceRegistry(new[] { service }),
                recentFilesPath: Path.Combine(_root, "recent.json"));
            using IDisposable trust = main.PreviewerTrustInteraction.RegisterHandler(context =>
                context.SetOutput(PreviewerTrustDecision.Cancel));
            await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);
            await main.OpenFileAsync(xamlPath).WaitAsync(cancellationToken);
            DesignerDocumentViewModel designer = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);
            MutableAstObjectNode button = XamlNames.Objects(designer.SyncEngine.CurrentDocument!.Root!)
                .First(node => node.TypeName == "Button");
            Task<bool> assign = main.SetDesignerEventHandlerAsync(designer, button, "Click", "OnStart", cancellationToken);

            if (owners == 1)
            {
                Assert.True(await assign);
                Assert.Equal(projectPath, service.EventRequests.Last().ProjectPath);
            }
            else
            {
                InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => assign);
                Assert.Equal(UiStrings.EventHandlerMultipleProjects, refused.Message);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static XamlLanguageService CreateService(IDesignerCodeBehindRefactoringService? refactoring) =>
        new(CompletionProviderRegistry.CreateDefault(), new XamlParsingService(), new TypeMetadataService(), refactoring, null);

    private static LanguageRenameContext Context(string filePath, string text, string caretAfter, string newName) => new()
    {
        FilePath = filePath,
        Text = text,
        Offset = text.IndexOf(caretAfter, StringComparison.Ordinal) + caretAfter.Length,
        NewName = newName
    };

    private static string Apply(string text, LanguageDocumentEdit edit)
    {
        Assert.Equal(text, edit.ExpectedText);
        foreach (TextEdit change in edit.Edits.OrderByDescending(change => change.Offset))
        {
            text = text.Remove(change.Offset, change.Length).Insert(change.Offset, change.NewText);
        }

        return text;
    }

    private string CreateFile(string name, string text)
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, text);
        return path;
    }

    // Like an Avalonia project, which passes its AXAML files to the compiler as additional files.
    private static Solution CreateSolution(AdhocWorkspace workspace, string xamlPath, string codePath)
    {
        MetadataReference[] references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator).Concat(new[] { typeof(Control).Assembly.Location, typeof(AvaloniaObject).Assembly.Location })
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path)).ToArray();
        ProjectInfo info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Create(), "Example", "Example",
            LanguageNames.CSharp, filePath: Path.Combine(Path.GetDirectoryName(xamlPath)!, "Example.csproj"),
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references);
        Project project = workspace.AddProject(info);
        project = project.AddDocument("SampleView.axaml.cs", SourceText.From(File.ReadAllText(codePath)), filePath: codePath).Project;
        return project.AddAdditionalDocument("SampleView.axaml", SourceText.From(File.ReadAllText(xamlPath)), filePath: xamlPath)
            .Project.Solution;
    }

    // Plans with the C# planner of the editor on a solution that is not loaded from disk.
    private sealed class PlannerRefactoring(Solution solution) : IDesignerCodeBehindRefactoringService
    {
        public Task<LanguageWorkspaceEdit?> PrepareControlRenameAsync(DesignerCodeBehindRenameRequest request,
            CancellationToken cancellationToken = default) =>
            DesignerCodeBehindRenamePlanner.CreateAsync(solution, request, cancellationToken);
    }

    // The designer finds the C# refactoring among the language services; this one serves no documents and records
    // event handler requests, whose handler it reports as existing without a source location.
    private sealed class PlannerLanguageService(Solution solution)
        : ILanguageIntellisenseService, IDesignerCodeBehindRefactoringService, IDesignerEventBindingService
    {
        public List<DesignerEventBindingRequest> EventRequests { get; } = new();

        public Task<DesignerEventBindingResult> ResolveEventHandlerAsync(DesignerEventBindingRequest request,
            CancellationToken cancellationToken = default)
        {
            EventRequests.Add(request);
            return Task.FromResult(new DesignerEventBindingResult(null, null, 0, 0));
        }

        public string LanguageId => "csharp";

        public bool CanHandle(string filePath, string? languageId) => false;

        public Task<LanguageWorkspaceEdit?> PrepareControlRenameAsync(DesignerCodeBehindRenameRequest request,
            CancellationToken cancellationToken = default) =>
            DesignerCodeBehindRenamePlanner.CreateAsync(solution, request, cancellationToken);

        public Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct = default) => Task.CompletedTask;

        public Task ClearWorkspaceAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<IReadOnlyList<CompletionItem>> GetCompletionsAsync(CompletionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<CompletionItem>>(Array.Empty<CompletionItem>());

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<LanguageSemanticToken>> GetSemanticTokensAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSemanticToken>>(Array.Empty<LanguageSemanticToken>());

        public Task<IReadOnlyList<TextEdit>> GetFormattingEditsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TextEdit>>(Array.Empty<TextEdit>());

        public Task<LanguageHover?> GetHoverAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageHover?>(null);

        public Task<IReadOnlyList<LanguageLocation>> FindDefinitionsAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());

        public Task<IReadOnlyList<LanguageLocation>> FindReferencesAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageLocation>>(Array.Empty<LanguageLocation>());

        public Task<LanguageRenameInfo?> PrepareRenameAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageRenameInfo?>(null);

        public Task<LanguageWorkspaceEdit?> RenameSymbolAsync(LanguageRenameContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageWorkspaceEdit?>(null);

        public Task<LanguageSignatureHelp?> GetSignatureHelpAsync(LanguagePositionContext context, CancellationToken ct = default)
            => Task.FromResult<LanguageSignatureHelp?>(null);

        public Task<IReadOnlyList<LanguageCodeAction>> GetCodeActionsAsync(LanguageCodeActionContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageCodeAction>>(Array.Empty<LanguageCodeAction>());

        public Task<IReadOnlyList<LanguageSymbol>> GetDocumentSymbolsAsync(LanguageDocumentContext context, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());

        public Task<IReadOnlyList<LanguageSymbol>> GetWorkspaceSymbolsAsync(LanguageSymbolQuery query, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LanguageSymbol>>(Array.Empty<LanguageSymbol>());
    }

    private sealed class FixedWorkspaceService(WorkspaceModel workspace) : IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) => Task.FromResult(workspace);

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) => Task.FromResult(workspace);

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) => workspace;
    }
}
