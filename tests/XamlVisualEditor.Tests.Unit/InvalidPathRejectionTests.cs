using System.Text.Json;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class InvalidPathRejectionTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-InvalidPath-" + Guid.NewGuid().ToString("N"));

    public InvalidPathRejectionTests()
    {
        Directory.CreateDirectory(_root);
    }

    [AvaloniaFact(Timeout = 30000)]
    public void Recent_Entries_With_Invalid_Paths_Are_Dropped_And_Removed_From_The_Store()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string validPath = Path.Combine(_root, "Valid.cs");
        string invalidPath = "relative" + Path.DirectorySeparatorChar + "Invalid.cs";
        string recentFilesPath = Path.Combine(_root, "recent.json");
        File.WriteAllText(recentFilesPath, JsonSerializer.Serialize(new[]
        {
            new { FilePath = validPath, Kind = "File" },
            new { FilePath = invalidPath, Kind = "File" }
        }));

        using MainWindowViewModel main = new(recentFilesPath: recentFilesPath);

        Assert.Equal(validPath, Assert.Single(main.RecentFiles).FilePath);
        Assert.DoesNotContain("Invalid.cs", File.ReadAllText(recentFilesPath), StringComparison.Ordinal);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenFileAsync_Rejects_An_Invalid_Path()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string invalidPath = "relative" + Path.DirectorySeparatorChar + "Invalid.cs";
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));

        await main.OpenFileAsync(invalidPath).WaitAsync(cancellationToken);

        Assert.Empty(main.Documents);
        Assert.Empty(main.RecentFiles);
        Assert.Equal(string.Format(UiStrings.InvalidPathRejected, invalidPath), main.StatusText);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenFolderAsync_Rejects_An_Invalid_Path()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string invalidPath = "relative" + Path.DirectorySeparatorChar + "Folder";
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));

        await main.OpenFolderAsync(invalidPath);

        Assert.Empty(main.RecentFiles);
        Assert.Equal(string.Format(UiStrings.InvalidPathRejected, invalidPath), main.StatusText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
