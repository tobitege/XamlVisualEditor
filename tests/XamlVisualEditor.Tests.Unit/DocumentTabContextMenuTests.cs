using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
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

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Solution_Explorer_View_Selects_The_Revealed_File_Row(bool revealBeforeShow, bool selectOtherFirst)
    {
        string file = Path.Combine(_directory, "Controls", "Main.axaml");
        List<ProjectFileModel> files = Enumerable.Range(0, 200)
            .Select(index => new ProjectFileModel
            {
                FilePath = Path.Combine(_directory, "Aaa", $"File{index:000}.cs"),
                RelativePath = Path.Combine("Aaa", $"File{index:000}.cs")
            })
            .ToList();
        files.Add(new ProjectFileModel { FilePath = Path.Combine(_directory, "App.cs"), RelativePath = "App.cs" });
        files.Add(new ProjectFileModel { FilePath = file, RelativePath = Path.Combine("Controls", "Main.axaml") });
        SolutionExplorerViewModel explorer = new();
        explorer.LoadWorkspace(new WorkspaceModel
        {
            Projects =
            [
                new ProjectModel
                {
                    Name = "App",
                    ProjectPath = Path.Combine(_directory, "App.csproj"),
                    Files = files,
                    References = Array.Empty<AssemblyReference>(),
                    XamlFiles = Array.Empty<XamlFileModel>()
                }
            ],
            ProjectFolders = new Dictionary<string, string>()
        }, "Solution");
        SolutionExplorerView view = new() { DataContext = explorer };
        Window window = new() { Content = view, Width = 300, Height = 400 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Styles.Add(new StyleInclude(new Uri("avares://XamlVisualEditor.App/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        });
        if (revealBeforeShow) Assert.True(explorer.RevealFile(file));
        window.Show();
        try
        {
            Settle(window);
            if (selectOtherFirst)
            {
                Assert.True(explorer.RevealFile(Path.Combine(_directory, "Aaa", "File150.cs")));
                Settle(window);
            }

            if (!revealBeforeShow) Assert.True(explorer.RevealFile(file));
            Settle(window);

            DataGrid grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.NotNull(explorer.SelectedRow);
            Assert.Same(explorer.SelectedRow, grid.SelectedItem);
            Assert.Equal(file, explorer.SelectedNode?.FullPath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task File_Explorer_View_Selects_A_Revealed_File_After_Another()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        // The tree is shown in a window: with the inline scheduler of the other unit tests, its throttled filter
        // would refresh the model on a timer thread while the window lays out the same rows.
        ReactiveUI.Primitives.Concurrency.ISequencer previousScheduler = ReactiveUI.RxSchedulers.MainThreadScheduler;
        using IDisposable scheduler = System.Reactive.Disposables.Disposable.Create(() => ReactiveUI.RxSchedulers.MainThreadScheduler = previousScheduler);
        ReactiveUI.RxSchedulers.MainThreadScheduler = ReactiveUI.Primitives.Concurrency.AvaloniaScheduler.For(Avalonia.Threading.Dispatcher.UIThread);
        string first = Path.Combine(_directory, "Aaa", "First.axaml");
        string second = Path.Combine(_directory, "Zzz", "Second.axaml");
        // The files of the first folder push the second folder below the visible rows.
        IEnumerable<string> fillers = Enumerable.Range(0, 100).Select(index => Path.Combine(_directory, "Aaa", $"File{index:000}.cs"));
        foreach (string path in fillers.Append(first).Append(second))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "<UserControl />");
        }

        using ExtensionTreeViewModel explorer = new(
            new ExtensionViewContribution("files.panel", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new PathTreeProvider(_directory));
        ExtensionTreeView view = new() { DataContext = explorer };
        Window window = new() { Content = view, Width = 300, Height = 400 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        window.Styles.Add(new StyleInclude(new Uri("avares://XamlVisualEditor.App/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        });
        window.Show();
        try
        {
            Assert.True(await explorer.RevealPathAsync(first, cancellationToken));
            Settle(window);
            Assert.True(await explorer.RevealPathAsync(second, cancellationToken));
            Settle(window);

            DataGrid grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
            Assert.Same(explorer.SelectedRow, grid.SelectedItem);
            Assert.Equal(second, Assert.IsType<PathEntry>(explorer.SelectedNode?.Element).FullPath);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task File_Explorer_Expands_The_Folders_To_And_Selects_A_File()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string file = Path.Combine(_directory, "Views", "Forms", "Main.axaml");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "<UserControl />");
        File.WriteAllText(Path.Combine(_directory, "Other.txt"), string.Empty);
        using ExtensionTreeViewModel explorer = new(
            new ExtensionViewContribution("files.panel", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new PathTreeProvider(_directory));

        Assert.True(await explorer.RevealPathAsync(file, cancellationToken));
        Assert.Equal(file, Assert.IsType<PathEntry>(explorer.SelectedNode?.Element).FullPath);
        Assert.Contains(explorer.Model.Flattened, row => ReferenceEquals(row, explorer.SelectedRow));

        Assert.False(await explorer.RevealPathAsync(Path.Combine(_directory, "Views", "Missing.axaml"), cancellationToken));
        Assert.False(await explorer.RevealPathAsync(Path.Combine(Path.GetTempPath(), "Elsewhere.axaml"), cancellationToken));
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

    // Runs the posted scroll request and the layout it needs.
    private static void Settle(Window window)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private sealed record PathEntry(string FullPath) : IExtensionTreeItemPathProvider, IExtensionTreeItemChildrenProvider
    {
        public bool HasChildren => Directory.Exists(FullPath);

        public bool IsDirectory => Directory.Exists(FullPath);
    }

    [Theory]
    [InlineData(true, false, "Zeta|Alpha.txt|Beta.txt")]
    [InlineData(true, true, "Zeta|Beta.txt|Alpha.txt")]
    [InlineData(false, false, "Alpha.txt|Beta.txt|Zeta")]
    public void File_Explorer_Keeps_Folders_First_In_Both_Directions_When_The_Option_Is_On(
        bool foldersBeforeFiles, bool descending, string expected)
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Zeta"));
        File.WriteAllText(Path.Combine(_directory, "Alpha.txt"), string.Empty);
        File.WriteAllText(Path.Combine(_directory, "Beta.txt"), string.Empty);
        PathTreeProvider provider = new(_directory);
        using ExtensionTreeViewModel explorer = new(
            new ExtensionViewContribution("fileExplorer.panel", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            provider, foldersBeforeFiles);
        if (descending)
            explorer.SortingModel.SetOrUpdate(new Avalonia.Controls.DataGridSorting.SortingDescriptor(
                "Name", System.ComponentModel.ListSortDirection.Descending));
        ExtensionTreeNodeViewModel[] nodes = new[] { "Beta.txt", "Zeta", "Alpha.txt" }
            .Select(name => new PathEntry(Path.Combine(_directory, name)))
            .Select(entry => new ExtensionTreeNodeViewModel(entry, new TreeItem(Path.GetFileName(entry.FullPath), null, null), provider, () => { }))
            .ToArray();
        System.Collections.IComparer comparer = Assert.IsAssignableFrom<System.Collections.IComparer>(explorer.SortComparer);

        // The grid negates the result of a descending sort, as the test does here.
        Array.Sort(nodes, (left, right) => (descending ? -1 : 1) * comparer.Compare(left, right));

        Assert.Equal(expected, string.Join("|", nodes.Select(node => node.Label)));
    }

    [Fact]
    public void Other_Trees_Keep_The_Order_Of_Their_Provider()
    {
        using ExtensionTreeViewModel tree = new(
            new ExtensionViewContribution("files.panel", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new PathTreeProvider(_directory));

        Assert.Null(tree.SortComparer);
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
