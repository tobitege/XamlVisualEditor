using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Themes.Fluent;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using ExtensionMenuTestHost = XamlVisualEditor.Tests.UI.ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost;

namespace XamlVisualEditor.Tests.UI;

/// <summary>
/// Extension items of the explorer and document tab context menus: they sit in a submenu, receive the path of the
/// clicked node or tab and hide on nodes without a path.
/// </summary>
public sealed class ExplorerContextMenuTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-explorer-menu-" + Guid.NewGuid().ToString("N"));
    private readonly MenuProbeExtension _probe = new();
    private readonly IStyle[] _appStyles;

    public ExplorerContextMenuTests()
    {
        Directory.CreateDirectory(_directory);
        _appStyles = CreateAppStyles();
        Application.Current!.Styles.AddRange(_appStyles);
    }

    [AvaloniaFact]
    public async Task File_explorer_items_pass_the_path_of_the_right_clicked_row_from_the_submenu()
    {
        string first = Path.Combine(_directory, "a.txt");
        string second = Path.Combine(_directory, "b.txt");
        using ExtensionMenuTestHost host = new(additionalExtension: _probe);
        await host.ActivateAsync();
        using ExtensionTreeViewModel tree = new(
            new ExtensionViewContribution("probe.tree", "Tree", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new ListTreeProvider(new PathItem(first), new PathItem(second), "no path"))
        {
            ExtensionContextMenuItems = host.ViewModel.ExplorerContextMenuItems
        };
        await tree.LoadAsync(CancellationToken.None);
        tree.SelectedRow = tree.Model.Flattened[0];
        ExtensionTreeView view = new() { DataContext = tree };
        using ThemedWindow window = ThemedWindow.Show(view, 500, 400);

        // The right click selects the second row, so the command acts on it and not on the selected first row.
        ContextMenu menu = RightClick(window, view, row => row is ExtensionTreeNodeViewModel { Label: "b.txt" });
        MenuItem git = FindExtensionItem(menu, "Git");
        Assert.True(git.IsVisible);
        Assert.True(Assert.IsType<MenuItem>(menu.Items[menu.Items.IndexOf(git) - 1]).IsVisible);
        await ClickAsync(host, window, git, "Stage");
        Assert.Equal(new object?[] { second }, Assert.Single(_probe.Calls)!);

        // The menu closes with the submenu open and keeps its items for the next time.
        int itemCount = menu.Items.Count;
        menu.Close();
        Assert.Equal(itemCount, menu.Items.Count);

        menu = RightClick(window, view, row => row is ExtensionTreeNodeViewModel { Label: "no path" });
        git = FindExtensionItem(menu, "Git");
        Assert.False(git.IsVisible);
        Assert.False(Assert.IsType<MenuItem>(menu.Items[menu.Items.IndexOf(git) - 1]).IsVisible);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Solution_explorer_passes_the_project_folder_and_hides_the_items_on_references()
    {
        string projectFolder = Path.Combine(_directory, "App");
        string file = Path.Combine(projectFolder, "Main.axaml");
        using ExtensionMenuTestHost host = new(additionalExtension: _probe);
        await host.ActivateAsync();
        using SolutionExplorerViewModel explorer = new() { ExtensionContextMenuItems = host.ViewModel.ExplorerContextMenuItems };
        explorer.LoadWorkspace(new WorkspaceModel
        {
            Projects =
            [
                new ProjectModel
                {
                    Name = "App",
                    ProjectPath = Path.Combine(projectFolder, "App.csproj"),
                    Files = [new ProjectFileModel { FilePath = file, RelativePath = "Main.axaml" }],
                    References = [new AssemblyReference { Name = "Library", Path = Path.Combine(_directory, "Library.dll") }],
                    XamlFiles = Array.Empty<XamlFileModel>()
                }
            ],
            ProjectFolders = new Dictionary<string, string>()
        }, "Solution");
        SolutionExplorerNodeViewModel reference = explorer.Root!.Children.Single().Children
            .SelectMany(child => child.Children)
            .Single(node => node.Kind == SolutionExplorerNodeKind.Reference);
        Assert.True(explorer.Model.TryExpandToItem(reference, out _));
        SolutionExplorerView view = new() { DataContext = explorer };
        using ThemedWindow window = ThemedWindow.Show(view, 500, 400);

        ContextMenu menu = RightClick(window, view, row => row is SolutionExplorerNodeViewModel { Kind: SolutionExplorerNodeKind.Project });
        MenuItem git = FindExtensionItem(menu, "Git");
        Assert.True(git.IsVisible);
        await ClickAsync(host, window, git, "Stage");
        Assert.Equal(new object?[] { projectFolder }, Assert.Single(_probe.Calls)!);
        menu.Close();

        menu = RightClick(window, view, row => ReferenceEquals(row, reference));
        Assert.False(FindExtensionItem(menu, "Git").IsVisible);
        menu.Close();
    }

    [AvaloniaFact]
    public async Task Document_tab_items_pass_the_file_of_the_tab_and_other_menus_pass_no_arguments()
    {
        string path = Path.Combine(_directory, "Tab.xml");
        using ExtensionMenuTestHost host = new(additionalExtension: _probe);
        await host.ActivateAsync();
        Border target = new() { Width = 100, Height = 100 };
        using ThemedWindow window = ThemedWindow.Show(target, 400, 300);
        // Like the application, the host holds the Dock theme and the editor styles in its own styles.
        Border resources = new();
        resources.Styles.AddRange(CreateAppStyles());
        Assert.Equal(DocumentTabContextMenus.MenuKeys.Length, DocumentTabContextMenus.Extend(resources));
        Assert.True(resources.TryGetResource("DocumentTabStripItemContextMenu", null, out object? resource));
        ContextMenu menu = Assert.IsType<ContextMenu>(resource);
        using TextDocumentViewModel document = new(path);
        DocumentTabCommands commands = new(_ => Task.CompletedTask, _ => { }, _ => Task.CompletedTask)
        {
            ExtensionItems = host.ViewModel.DocumentTabContextMenuItems
        };

        menu.DataContext = new TextDocument(document) { TabCommands = commands };
        target.ContextMenu = menu;
        menu.Open(target);
        window.Pump();
        MenuItem git = FindExtensionItem(menu, "Git");
        Assert.True(git.IsVisible);
        await ClickAsync(host, window, git, "Stage");
        Assert.Equal(new object?[] { path }, Assert.Single(_probe.Calls)!);
        menu.Close();

        // A tab without a file offers no extension items.
        menu.DataContext = new InfiniteCanvasDocument();
        menu.Open(target);
        window.Pump();
        Assert.DoesNotContain(menu.Items.OfType<MenuItem>(), item => item.Classes.Contains("extension-item") && item.IsVisible);
        menu.Close();

        // Menus without arguments keep calling the command without arguments.
        _probe.Calls.Clear();
        host.ViewModel.EditorContextMenuItems.Single(item => item.Title == "Git").Children.Single().Command!.Execute(null);
        host.ViewModel.ExtensionMenuItems.Single(item => item.CommandId == MenuProbeExtension.STAGE).Command!.Execute(null);
        await host.FlushAsync();
        Assert.Equal(new IReadOnlyList<object?>?[] { null, null }, _probe.Calls);
    }

    public void Dispose()
    {
        foreach (IStyle style in _appStyles)
        {
            Application.Current!.Styles.Remove(style);
        }

        Directory.Delete(_directory, recursive: true);
    }

    private static IStyle[] CreateAppStyles()
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        return
        [
            new DockFluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") },
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorContextMenu.axaml") }
        ];
    }

    private static ContextMenu RightClick(ThemedWindow window, Control view, Func<object?, bool> isRow)
    {
        DataGrid grid = view.GetVisualDescendants().OfType<DataGrid>().Single();
        DataGridRow row = grid.GetVisualDescendants().OfType<DataGridRow>()
            .Single(candidate => candidate.DataContext is HierarchicalNode node && isRow(node.Item));
        Point point = row.TranslatePoint(new Point(40, row.Bounds.Height / 2), window.Window)!.Value;
        window.Window.MouseDown(point, MouseButton.Right);
        window.Window.MouseUp(point, MouseButton.Right);
        window.Pump();
        ContextMenu menu = grid.ContextMenu!;
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem FindExtensionItem(ContextMenu menu, string header) =>
        menu.Items.OfType<MenuItem>().Single(item => item.Classes.Contains("extension-item") && Equals(item.Header, header));

    private static async Task ClickAsync(ExtensionMenuTestHost host, ThemedWindow window, MenuItem submenu, string header)
    {
        submenu.Open();
        window.Pump();
        MenuItem item = submenu.GetLogicalChildren().OfType<MenuItem>().Single(child => Equals(child.Header, header));
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await host.FlushAsync();
    }

    private sealed class MenuProbeExtension : IXveExtension
    {
        public const string STAGE = "probe.stage";

        public List<IReadOnlyList<object?>?> Calls { get; } = new();

        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
        {
            context.Subscriptions.Add(context.Commands.Register(STAGE, command =>
            {
                Calls.Add(command.Arguments);
                return Task.CompletedTask;
            }));
            context.Subscriptions.Add(context.Contributions.RegisterMenuItems(context.ExtensionId,
            [
                Item(ExtensionMenuLocations.ExplorerContext),
                Item(ExtensionMenuLocations.DocumentTabContext),
                Item(ExtensionMenuLocations.EditorContext),
                new ExtensionMenuContribution(STAGE, "Stage", ExtensionMenuLocations.Tools, "probe")
            ]));
            return Task.CompletedTask;
        }

        private static ExtensionMenuContribution Item(string location) =>
            new(STAGE, "Stage", location, "probe") { Submenu = "Git" };
    }

    private sealed record PathItem(string FullPath) : IExtensionTreeItemPathProvider
    {
        public bool IsDirectory => false;
    }

    private sealed class ListTreeProvider(params object[] roots) : IExtensionTreeDataProvider
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<object>>(element is null ? roots : []);

        public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken) =>
            Task.FromResult(new TreeItem(element is PathItem item ? Path.GetFileName(item.FullPath) : element.ToString()!, null, null));
    }
}
