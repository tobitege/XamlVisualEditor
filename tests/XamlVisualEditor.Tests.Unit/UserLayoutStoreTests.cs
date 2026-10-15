using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class UserLayoutStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-layout-store-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ListNames_ReturnsEmptyWhenDirectoryDoesNotExist()
    {
        UserLayoutStore store = new(_directory);

        Assert.Empty(store.ListNames());
        Assert.False(store.Exists("Designer"));
    }

    [Fact]
    public void GetPath_CreatesDirectoryAndListNamesReturnsSortedNames()
    {
        UserLayoutStore store = new(_directory);

        File.WriteAllText(store.GetPath("zebra"), "{}");
        File.WriteAllText(store.GetPath("Alpha"), "{}");
        File.WriteAllText(Path.Combine(_directory, "ignored.txt"), "x");

        Assert.Equal(new[] { "Alpha", "zebra" }, store.ListNames());
        Assert.True(store.Exists("Alpha"));
        Assert.False(store.Exists("ignored"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" padded")]
    [InlineData("bad/name")]
    [InlineData("bad:name")]
    [InlineData("trailing.")]
    public void IsValidName_RejectsEmptyPaddedOrUnsafeNames(string? name)
    {
        UserLayoutStore store = new(_directory);

        Assert.False(store.IsValidName(name));
        Assert.Throws<ArgumentException>(() => store.GetPath(name!));
    }

    [Fact]
    public void IsValidName_AcceptsPlainNames()
    {
        UserLayoutStore store = new(_directory);

        Assert.True(store.IsValidName("My Layout 2"));
        Assert.EndsWith(Path.Combine(_directory, "My Layout 2.json"), store.GetPath("My Layout 2"));
    }

    [Fact]
    public void Delete_RemovesTheFileAndReportsMissingLayouts()
    {
        UserLayoutStore store = new(_directory);
        File.WriteAllText(store.GetPath("Debug"), "{}");

        Assert.True(store.Delete("Debug"));
        Assert.False(store.Exists("Debug"));
        Assert.Empty(store.ListNames());
        Assert.False(store.Delete("Debug"));
        Assert.False(store.Delete("bad/name"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
