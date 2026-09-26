using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Dock.Avalonia.Themes.Fluent;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentTabContextMenuTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-tab-menu-" + Guid.NewGuid().ToString("N"));

    public DocumentTabContextMenuTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task Commands_Pass_The_Path_Or_File_Name_And_Ignore_A_Missing_Path()
    {
        List<string> revealedInFileExplorer = new();
        List<string> revealedInSolutionExplorer = new();
        List<string> copied = new();
        DocumentTabCommands commands = new(path =>
        {
            revealedInFileExplorer.Add(path);
            return Task.CompletedTask;
        }, revealedInSolutionExplorer.Add, text =>
        {
            copied.Add(text);
            return Task.CompletedTask;
        });
        string path = Path.Combine(_directory, "View.axaml");

        await commands.RevealInFileExplorerCommand.Execute(path).ToTask();
        await commands.RevealInSolutionExplorerCommand.Execute(path).ToTask();
        await commands.CopyPathCommand.Execute(path).ToTask();
        await commands.CopyFileNameCommand.Execute(path).ToTask();
        await commands.CopyPathCommand.Execute(null).ToTask();
        await commands.RevealInFileExplorerCommand.Execute(" ").ToTask();

        Assert.Equal([path], revealedInFileExplorer);
        Assert.Equal([path], revealedInSolutionExplorer);
        Assert.Equal([path, "View.axaml"], copied);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task File_Explorer_Expands_The_Folders_To_And_Selects_A_File()
    {
        string file = Path.Combine(_directory, "Views", "Forms", "Main.axaml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<UserControl />");
        File.WriteAllText(Path.Combine(_directory, "Other.txt"), string.Empty);
        using ExtensionTreeViewModel explorer = new(
            new ExtensionViewContribution("files.panel", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new PathTreeProvider(_directory));

        Assert.True(await explorer.RevealPathAsync(file, CancellationToken.None));
        Assert.Equal(file, Assert.IsType<PathEntry>(explorer.SelectedNode?.Element).FullPath);
        Assert.Contains(explorer.Model.Flattened, row => ReferenceEquals(row, explorer.SelectedRow));

        Assert.False(await explorer.RevealPathAsync(Path.Combine(_directory, "Views", "Missing.axaml"), CancellationToken.None));
        Assert.False(await explorer.RevealPathAsync(Path.Combine(Path.GetTempPath(), "Elsewhere.axaml"), CancellationToken.None));
    }

    [AvaloniaFact]
    public void Solution_Explorer_Expands_To_And_Selects_A_File()
    {
        string file = Path.Combine(_directory, "Views", "Main.axaml");
        SolutionExplorerViewModel explorer = new();
        explorer.LoadWorkspace(new WorkspaceModel
        {
            Projects =
            [
                new ProjectModel
                {
                    Name = "App",
                    ProjectPath = Path.Combine(_directory, "App.csproj"),
                    Files = [new ProjectFileModel { FilePath = file, RelativePath = Path.Combine("Views", "Main.axaml") }],
                    References = Array.Empty<AssemblyReference>(),
                    XamlFiles = Array.Empty<XamlFileModel>()
                }
            ],
            ProjectFolders = new Dictionary<string, string>()
        }, "Solution");

        Assert.True(explorer.RevealFile(file));
        Assert.Equal(file, explorer.SelectedNode?.FullPath);
        Assert.Contains(explorer.Model.Flattened, row => ReferenceEquals(row, explorer.SelectedRow));

        Assert.False(explorer.RevealFile(Path.Combine(_directory, "Other.axaml")));
    }

    [AvaloniaFact]
    public void Dock_Document_Menus_Get_The_File_Commands_Bound_To_The_Tab()
    {
        BindingErrorSink sink = new();
        HashSet<object?> fileItems = new();
        ILogSink? previousSink = Logger.Sink;
        Logger.Sink = sink;
        try
        {
            Border host = new();
            host.Styles.Add(new DockFluentTheme());
            host.Styles.Add(new StyleInclude(new Uri("avares://XamlVisualEditor.App/"))
            {
                Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml")
            });

            Assert.Equal(DocumentTabContextMenus.MenuKeys.Length, DocumentTabContextMenus.Extend(host));

            Assert.True(host.TryGetResource("DocumentTabStripItemContextMenu", null, out object? resource));
            ContextMenu menu = Assert.IsType<ContextMenu>(resource);
            fileItems.UnionWith(menu.Items.Cast<object?>().TakeLast(5));
            MenuItem copyPath = menu.Items.OfType<MenuItem>().Reverse().Skip(1).First();
            string path = Path.Combine(_directory, "Tab.xml");
            using TextDocumentViewModel document = new(path);
            DocumentTabCommands commands = new(_ => Task.CompletedTask, _ => { }, _ => Task.CompletedTask);

            // Before a tab opens the menu, it has no dockable.
            Assert.False(copyPath.IsVisible);
            Assert.Null(copyPath.Command);

            menu.DataContext = new TextDocument(document) { TabCommands = commands };
            Assert.Same(commands.CopyPathCommand, copyPath.Command);
            Assert.Equal(path, copyPath.CommandParameter);
            Assert.True(copyPath.IsVisible);

            menu.DataContext = new InfiniteCanvasDocument();
            Assert.False(copyPath.IsVisible);
            Assert.Null(copyPath.Command);
        }
        finally
        {
            Logger.Sink = previousSink;
        }

        // Dock's own items report a dockable without owner; the appended items stay silent without and with a dockable.
        string[] fileItemErrors = sink.Messages.Where(message => fileItems.Contains(message.Source)).Select(message => message.Text).ToArray();
        Assert.True(fileItemErrors.Length == 0, string.Join(Environment.NewLine, fileItemErrors));
    }

    private sealed record PathEntry(string FullPath) : IExtensionTreeItemPathProvider, IExtensionTreeItemChildrenProvider
    {
        public bool HasChildren => Directory.Exists(FullPath);
    }

    // Lists the real file system below one root folder.
    private sealed class PathTreeProvider(string root) : IExtensionTreeDataProvider
    {
        public event EventHandler? Changed { add { } remove { } }

        public Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken)
        {
            IReadOnlyList<object> children = element is PathEntry { HasChildren: true } folder
                ? Directory.EnumerateFileSystemEntries(folder.FullPath).Order().Select(path => (object)new PathEntry(path)).ToArray()
                : element is null ? [new PathEntry(root)] : Array.Empty<object>();
            return Task.FromResult(children);
        }

        public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken) =>
            Task.FromResult(new TreeItem(Path.GetFileName(((PathEntry)element).FullPath), null, ((PathEntry)element).FullPath));
    }

    private sealed class BindingErrorSink : ILogSink
    {
        public List<(object? Source, string Text)> Messages { get; } = new();

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
        {
            if (IsEnabled(level, area)) Messages.Add((source, messageTemplate));
        }

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area)) Messages.Add((source, messageTemplate + " " + string.Join(", ", propertyValues)));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
