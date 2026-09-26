using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SolutionExplorerFilterTests
{
    private static readonly string Directory = Path.Combine(Path.GetTempPath(), "xve-filter");

    [AvaloniaFact(Timeout = 30000)]
    public async Task Filter_Opens_The_Folders_Above_Partial_Matches_And_Clearing_Restores_Them()
    {
        SolutionExplorerViewModel explorer = new();
        explorer.LoadWorkspace(new WorkspaceModel
        {
            Projects =
            [
                new ProjectModel
                {
                    Name = "App",
                    ProjectPath = Path.Combine(Directory, "App.csproj"),
                    Files =
                    [
                        File(Path.Combine("Controls", "BufferedTreeView.cs")),
                        File(Path.Combine("Controls", "AppIcon.cs")),
                        File("Program.cs")
                    ],
                    References = Array.Empty<AssemblyReference>(),
                    XamlFiles = Array.Empty<XamlFileModel>()
                }
            ],
            ProjectFolders = new Dictionary<string, string>()
        }, "Solution");
        Assert.DoesNotContain("BufferedTreeView.cs", VisibleNames(explorer));

        explorer.ApplyFilterAndSearch("buff");
        Assert.Contains("BufferedTreeView.cs", VisibleNames(explorer));
        explorer.FilterText = "buff";
        await WaitAsync(() => VisibleNames(explorer).Contains("BufferedTreeView.cs"));

        // The grid filters its rows with the predicate of the filter.
        Func<object, bool> predicate = Assert.Single(explorer.FilteringModel.Descriptors).Predicate;
        Dictionary<string, HierarchicalNode> rows = explorer.Model.Flattened.OfType<HierarchicalNode>()
            .ToDictionary(row => ((SolutionExplorerNodeViewModel)row.Item!).Name);
        Assert.True(predicate(rows["BufferedTreeView.cs"]));
        Assert.True(predicate(rows["Controls"]));
        Assert.False(predicate(rows["AppIcon.cs"]));
        Assert.False(predicate(rows["Program.cs"]));

        explorer.FilterText = string.Empty;
        await WaitAsync(() => !VisibleNames(explorer).Contains("BufferedTreeView.cs"));
    }

    private static ProjectFileModel File(string relativePath) =>
        new() { FilePath = Path.Combine(Directory, relativePath), RelativePath = relativePath };

    // The rows of expanded folders; the grid filter works on these.
    private static List<string> VisibleNames(SolutionExplorerViewModel explorer) =>
        explorer.Model.Flattened.OfType<HierarchicalNode>()
            .Select(row => (row.Item as SolutionExplorerNodeViewModel)?.Name ?? string.Empty)
            .ToList();

    // The filter follows the text after a short throttle.
    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(condition());
    }
}
