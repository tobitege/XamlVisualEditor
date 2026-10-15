using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class FileExtensionStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-extension-storage-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ValuesSurviveANewInstanceAndStaySeparatedByExtensionAndKey()
    {
        await new FileExtensionStorage("tests.first", _root).SetAsync("layout", new StoredLayout("Left", 3), CancellationToken.None);
        await new FileExtensionStorage("tests.first", _root).SetAsync("theme", "Dark", CancellationToken.None);

        FileExtensionStorage reopened = new("tests.first", _root);
        Assert.Equal(new StoredLayout("Left", 3), await reopened.GetAsync<StoredLayout>("layout", CancellationToken.None));
        Assert.Equal("Dark", await reopened.GetAsync<string>("theme", CancellationToken.None));
        Assert.Null(await new FileExtensionStorage("tests.second", _root).GetAsync<string>("theme", CancellationToken.None));
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task OverwrittenAndRemovedValuesReadBackAccordingly()
    {
        FileExtensionStorage storage = new("tests.overwrite", _root);
        Assert.Null(await storage.GetAsync<string>("missing", CancellationToken.None));

        await storage.SetAsync("count", 1, CancellationToken.None);
        await storage.SetAsync("count", 2, CancellationToken.None);
        Assert.Equal(2, await storage.GetAsync<int>("count", CancellationToken.None));

        await storage.RemoveAsync("count", CancellationToken.None);
        Assert.Equal(0, await storage.GetAsync<int>("count", CancellationToken.None));
        Assert.Null(await new FileExtensionStorage("tests.overwrite", _root).GetAsync<int?>("count", CancellationToken.None));
    }

    [Fact]
    public async Task InvalidArgumentsAndCancelledWritesLeaveNoFiles()
    {
        Assert.Throws<ArgumentException>(() => new FileExtensionStorage(" ", _root));
        Assert.Throws<ArgumentException>(() => new FileExtensionStorage("tests.relative", "relative-root"));
        FileExtensionStorage storage = new("tests.cancel", _root);
        await Assert.ThrowsAsync<ArgumentException>(() => storage.SetAsync(" ", "value", CancellationToken.None));

        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => storage.SetAsync("key", "value", cancelled.Token));
        Assert.False(Directory.Exists(_root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public sealed record StoredLayout(string Dock, int Width);
}
