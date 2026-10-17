using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

/// <summary>
/// Explorer decorations stay cheap for large trees: on the UI thread, a <see cref="IExplorerDecorationLayer.Set"/>
/// includes the pass over the loaded nodes.
/// </summary>
public sealed class ExplorerDecorationPerformanceTests
{
    private const int FOLDERS = 100;
    private const int FILES_PER_FOLDER = 100;
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xve-decoration-performance");

    [AvaloniaFact]
    public async Task Set_and_the_pass_over_10000_loaded_nodes_take_less_than_200_ms()
    {
        // The tree applies its empty filter 150 ms after it is created through the main-thread scheduler, which
        // refreshes the model. Delivered through the UI dispatcher, as in the application, that refresh cannot run
        // beside the refreshes of the loading below; delivered inline, it ran on a timer thread and cancelled them.
        ISequencer previousScheduler = RxSchedulers.MainThreadScheduler;
        RxSchedulers.MainThreadScheduler = AvaloniaScheduler.For(Dispatcher.UIThread);
        try
        {
            await MeasureSetAndPassAsync();
        }
        finally
        {
            RxSchedulers.MainThreadScheduler = previousScheduler;
        }
    }

    private static async Task MeasureSetAndPassAsync()
    {
        ExplorerDecorationRegistry registry = new();
        using ExtensionTreeViewModel tree = new(
            new ExtensionViewContribution("probe.large", "Large", ExtensionViewType.Tree, ExtensionViewLocation.Left, 0),
            new LargeTreeProvider(),
            decorations: registry);
        await tree.LoadAsync(CancellationToken.None);
        foreach (ExtensionTreeNodeViewModel folder in tree.RootItems)
        {
            await folder.EnsureChildrenAsync(CancellationToken.None);
        }

        List<ExtensionTreeNodeViewModel> files = tree.RootItems.SelectMany(folder => folder.Children).ToList();
        Assert.Equal(FOLDERS * FILES_PER_FOLDER, files.Count);
        using IExplorerDecorationLayer layer = registry.CreateLayer();
        KeyValuePair<string, ExplorerDecoration>[] added = Decorate(files, new ExplorerDecoration(ExplorerDecorationKind.Added, "A"));
        KeyValuePair<string, ExplorerDecoration>[] modified = Decorate(files, new ExplorerDecoration(ExplorerDecorationKind.Modified, "M"));
        // The pass over the nodes is a dispatcher job; running the jobs is part of the measured set.
        layer.Set(added);
        Dispatcher.UIThread.RunJobs();

        Stopwatch stopwatch = Stopwatch.StartNew();
        layer.Set(modified);
        Dispatcher.UIThread.RunJobs();
        stopwatch.Stop();

        Assert.All(files, file => Assert.Equal(ExplorerDecorationKind.Modified, file.Decoration?.Kind));
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Set and pass over {files.Count + FOLDERS} nodes: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(stopwatch.ElapsedMilliseconds < 200, $"Set and pass took {stopwatch.ElapsedMilliseconds} ms (threshold: 200 ms)");
    }

    [Fact]
    public void Build_for_10000_changes_at_depth_6_takes_less_than_100_ms()
    {
        GitFileChange[] changes = Enumerable.Range(0, 10_000)
            .Select(index => new GitFileChange
            {
                Path = $"a{index % 10}/b{index % 7}/c{index % 5}/d{index % 3}/e{index % 2}/file{index}.txt",
                WorkTreeStatus = GitChangeKind.Modified
            })
            .ToArray();
        GitExplorerDecorations.Build(Root, changes);

        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<KeyValuePair<string, ExplorerDecoration>> entries = GitExplorerDecorations.Build(Root, changes);
        stopwatch.Stop();

        Assert.True(entries.Count > changes.Length);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Build for {changes.Length} changes ({entries.Count} entries): {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Build took {stopwatch.ElapsedMilliseconds} ms (threshold: 100 ms)");
    }

    private static KeyValuePair<string, ExplorerDecoration>[] Decorate(
        IEnumerable<ExtensionTreeNodeViewModel> nodes,
        ExplorerDecoration decoration) =>
        nodes.Select(node => new KeyValuePair<string, ExplorerDecoration>(node.ExplorerPath!, decoration)).ToArray();

    private sealed record Entry(string FullPath, bool IsDirectory) : IExtensionTreeItemPathProvider, IExtensionTreeItemChildrenProvider
    {
        public bool HasChildren => IsDirectory;
    }

    private sealed class LargeTreeProvider : IExtensionTreeDataProvider
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<object>> GetChildrenAsync(object? element, CancellationToken cancellationToken)
        {
            IReadOnlyList<object> children = element is Entry folder
                ? Enumerable.Range(0, FILES_PER_FOLDER).Select(index => (object)new Entry(Path.Combine(folder.FullPath, $"file{index}.txt"), false)).ToList()
                : Enumerable.Range(0, FOLDERS).Select(index => (object)new Entry(Path.Combine(Root, $"folder{index}"), true)).ToList();
            return Task.FromResult(children);
        }

        public Task<TreeItem> GetTreeItemAsync(object element, CancellationToken cancellationToken) =>
            Task.FromResult(new TreeItem(Path.GetFileName(((Entry)element).FullPath), null, null));
    }
}
