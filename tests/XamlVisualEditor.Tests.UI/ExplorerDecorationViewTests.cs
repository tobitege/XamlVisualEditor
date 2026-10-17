using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Dock.Avalonia.Themes.Fluent;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

/// <summary>An explorer tree over a real repository shows the Git status of its rows and follows a commit.</summary>
public sealed class ExplorerDecorationViewTests : IDisposable
{
    private static readonly string[] DecorationClasses =
        ["decoration-modified", "decoration-added", "decoration-deleted", "decoration-conflicted"];

    private readonly IStyle[] _appStyles;

    public ExplorerDecorationViewTests()
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        _appStyles =
        [
            new DockFluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") }
        ];
        Application.Current!.Styles.AddRange(_appStyles);
    }

    [AvaloniaFact]
    public async Task Rows_show_the_git_status_and_lose_it_after_a_commit()
    {
        using GitTestRepository repository = GitTestRepository.Create("src/a.txt", "src/b.txt", "top.txt");
        repository.WriteFile("src/a.txt", "changed\n");
        repository.WriteFile("new/x.txt", "untracked\n");
        ExplorerDecorationRegistry registry = new();
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FixedWorkspace(repository.Path));
        using GitExplorerDecorations decorations = new(session, registry);
        await session.RefreshAsync();
        using ExtensionTreeViewModel tree = new(
            new ExtensionViewContribution("probe.files", "Files", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new DirectoryTreeProvider(repository.Path),
            decorations: registry);
        await tree.LoadAsync(CancellationToken.None);
        foreach (ExtensionTreeNodeViewModel folder in tree.RootItems.Where(node => node.HasChildren))
        {
            await folder.EnsureChildrenAsync(CancellationToken.None);
            folder.IsExpanded = true;
        }

        tree.Model.Refresh();
        ExtensionTreeView view = new() { DataContext = tree };
        using ThemedWindow window = ThemedWindow.Show(view, 500, 400);

        AssertRow(view, "a.txt", "decoration-modified", "M");
        AssertRow(view, "src", "decoration-modified", null);
        AssertRow(view, "new", "decoration-added", "U");
        AssertRow(view, "x.txt", null, null);
        AssertRow(view, "b.txt", null, null);
        AssertRow(view, "top.txt", null, null);
        TextBlock label = Label(Row(view, "a.txt"));
        Assert.True(window.Window.TryFindResource("ExplorerDecorationModifiedBrush", window.Window.ActualThemeVariant, out object? brush));
        Assert.Equal(Assert.IsType<SolidColorBrush>(brush).Color, Assert.IsAssignableFrom<ISolidColorBrush>(label.Foreground).Color);

        repository.RunGit("add", "-A");
        repository.RunGit("commit", "-q", "-m", "all");
        await session.RefreshAsync();
        window.Pump();

        foreach (string name in new[] { "a.txt", "src", "new", "x.txt", "b.txt", "top.txt" })
        {
            AssertRow(view, name, null, null);
        }
    }

    public void Dispose()
    {
        foreach (IStyle style in _appStyles)
        {
            Application.Current!.Styles.Remove(style);
        }
    }

    private static void AssertRow(Control view, string name, string? decorationClass, string? badge)
    {
        StackPanel row = Row(view, name);
        Assert.Equal(decorationClass is null ? Array.Empty<string>() : new[] { decorationClass },
            DecorationClasses.Where(row.Classes.Contains));
        TextBlock badgeText = row.Children.OfType<TextBlock>().Single(text => text.Classes.Contains("explorer-badge"));
        Assert.Equal(badge is not null, badgeText.IsVisible);
        Assert.Equal(badge, badgeText.Text);
    }

    private static StackPanel Row(Control view, string name) =>
        view.GetVisualDescendants().OfType<DataGridRow>()
            .Where(row => row.DataContext is HierarchicalNode { Item: ExtensionTreeNodeViewModel node } && node.Label == name)
            .SelectMany(row => row.GetVisualDescendants().OfType<StackPanel>())
            .Single(panel => panel.Classes.Contains("explorer-row"));

    private static TextBlock Label(StackPanel row) =>
        row.Children.OfType<TextBlock>().First(text => text.Classes.Contains("decorated"));

    private sealed class FixedWorkspace(string path) : IWorkspaceInfo
    {
        public string? WorkspacePath => path;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed record Entry(string FullPath, bool IsDirectory) : IExtensionTreeItemPathProvider, IExtensionTreeItemChildrenProvider
    {
        public bool HasChildren => IsDirectory;
    }

    /// <summary>Lists the folders and files below a root, without the git folder.</summary>
    private sealed class DirectoryTreeProvider(string root) : IExtensionTreeDataProvider
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken)
        {
            string folder = element is Entry entry ? entry.FullPath : root;
            IEnumerable<object> folders = Directory.GetDirectories(folder)
                .Where(path => Path.GetFileName(path) != ".git")
                .Select(path => new Entry(path, true));
            IEnumerable<object> files = Directory.GetFiles(folder).Select(path => new Entry(path, false));
            return Task.FromResult<IReadOnlyList<object>>(folders.Concat(files).ToList());
        }

        public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken) =>
            Task.FromResult(new TreeItem(Path.GetFileName(((Entry)element).FullPath), null, null));
    }
}
