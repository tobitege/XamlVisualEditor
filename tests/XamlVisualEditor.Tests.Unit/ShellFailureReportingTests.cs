using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ShellFailureReportingTests : IDisposable
{
    private const string Xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Button /></UserControl>";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-FailureReporting-" + Guid.NewGuid().ToString("N"));

    public ShellFailureReportingTests()
    {
        Directory.CreateDirectory(_root);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_layout_that_cannot_be_written_is_reported_and_not_selected()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        UserLayoutStore store = new(Path.Combine(_root, "layouts"));
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"), userLayoutStore: store);
        using IDisposable nameHandler = main.LayoutNameInteraction.RegisterHandler(context => context.SetOutput("Blocked"));
        // A folder in place of the layout file makes the final move fail.
        string blockedPath = store.GetPath("Blocked");
        Directory.CreateDirectory(blockedPath);

        Assert.False(main.DockFactory.SaveLayout(main.DockLayout, blockedPath));
        await main.SaveLayoutAsCommand.Execute().FirstAsync().ToTask(cancellationToken);

        Assert.Equal(string.Format(UiStrings.LayoutSaveFailed, "Blocked"), main.StatusText);
        Assert.NotEqual("Blocked", main.CurrentLayoutName);
        Assert.False(File.Exists(blockedPath + ".tmp"));

        string writablePath = store.GetPath("Writable");
        Assert.True(main.DockFactory.SaveLayout(main.DockLayout, writablePath));
        Assert.True(File.Exists(writablePath));
        Assert.False(File.Exists(writablePath + ".tmp"));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Opening_a_folder_drops_the_design_themes_of_the_previous_solution()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string folder = Path.Combine(_root, "Folder");
        Directory.CreateDirectory(folder);
        IReadOnlyList<Func<IStyle?>> previousStyles = WorkspaceDesignThemeRegistry.StyleFactories;
        IReadOnlyList<Func<IResourceProvider?>> previousResources = WorkspaceDesignThemeRegistry.ResourceFactories;
        try
        {
            Func<IStyle?> staleStyle = () => null;
            Func<IResourceProvider?> staleResources = () => null;
            WorkspaceDesignThemeRegistry.Update(new[] { staleStyle }, new[] { staleResources });
            using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));

            await main.OpenFolderAsync(folder).WaitAsync(cancellationToken);

            Assert.DoesNotContain(staleStyle, WorkspaceDesignThemeRegistry.StyleFactories);
            Assert.DoesNotContain(staleResources, WorkspaceDesignThemeRegistry.ResourceFactories);
            Assert.False(main.HasWorkspace);
            Assert.Empty(main.WorkspaceProjects);
        }
        finally
        {
            WorkspaceDesignThemeRegistry.Update(previousStyles, previousResources);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Loading_a_workspace_without_build_outputs_drops_the_design_themes_of_the_previous_solution()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string projectPath = Path.Combine(_root, "Unbuilt.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />", cancellationToken);
        string output = Path.Combine(_root, "missing-output");
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Unbuilt", ProjectPath = projectPath,
                    OutputAssemblyPath = Path.Combine(output, "Unbuilt.dll"),
                    XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                    References = Array.Empty<AssemblyReference>()
                }
            }
        };
        IReadOnlyList<Func<IStyle?>> previousStyles = WorkspaceDesignThemeRegistry.StyleFactories;
        IReadOnlyList<Func<IResourceProvider?>> previousResources = WorkspaceDesignThemeRegistry.ResourceFactories;
        try
        {
            Func<IStyle?> staleStyle = () => null;
            Func<IResourceProvider?> staleResources = () => null;
            WorkspaceDesignThemeRegistry.Update(new[] { staleStyle }, new[] { staleResources });
            using MainWindowViewModel main = new(
                workspaceService: new FixedWorkspaceService(workspace),
                metadataService: new XamlVisualEditor.Workspace.TypeMetadataService(),
                recentFilesPath: Path.Combine(_root, "recent.json"),
                workspaceSessions: new WorkspaceSessionStore(Path.Combine(_root, "sessions.json")));

            await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);

            Assert.True(main.HasWorkspace);
            Assert.DoesNotContain(staleStyle, WorkspaceDesignThemeRegistry.StyleFactories);
            Assert.DoesNotContain(staleResources, WorkspaceDesignThemeRegistry.ResourceFactories);
        }
        finally
        {
            WorkspaceDesignThemeRegistry.Update(previousStyles, previousResources);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Selecting_the_same_node_from_another_source_notifies_the_source()
    {
        string path = Path.Combine(_root, "Selection.axaml");
        await File.WriteAllTextAsync(path, Xaml, TestContext.Current.CancellationToken);
        using DesignerDocumentViewModel document = new(path);
        Guid nodeId = Guid.NewGuid();
        document.SetSelectedNode(nodeId, SyncSource.DesignSurface);
        List<SyncSource> sources = new();
        using IDisposable subscription = document.WhenAnyValue(x => x.SelectionSource).Subscribe(sources.Add);

        document.SetSelectedNode(nodeId, SyncSource.CodeEditor);

        Assert.Equal(new[] { SyncSource.DesignSurface, SyncSource.CodeEditor }, sources);
        Assert.Equal(nodeId, document.SelectedNodeId);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_save_that_the_designer_refuses_is_reported_and_keeps_the_document_open()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = Path.Combine(_root, "Pending.axaml");
        await File.WriteAllTextAsync(path, Xaml, cancellationToken);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        using IDisposable saveHandler = main.SaveChangesInteraction.RegisterHandler(context => context.SetOutput(SaveChangesChoice.Save));
        await main.OpenFileAsync(path).WaitAsync(cancellationToken);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(Assert.Single(main.Documents));
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Pending edit");
        document.IsModified = true;

        Assert.False(await main.ConfirmCloseAsync([document]).WaitAsync(cancellationToken));
        await main.CloseDocumentCommand.Execute(document).FirstAsync().ToTask(cancellationToken);

        Assert.Contains(document, main.Documents);
        Assert.Equal(string.Format(UiStrings.DocumentSaveFailed, "Finish or cancel the current edit before saving."),
            main.StatusText);
        Assert.Equal(Xaml, await File.ReadAllTextAsync(path, cancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FixedWorkspaceService(WorkspaceModel workspace) : XamlVisualEditor.Core.Interfaces.IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) => workspace;
    }
}
