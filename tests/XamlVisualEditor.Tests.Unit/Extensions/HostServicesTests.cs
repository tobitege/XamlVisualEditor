using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class HostServicesTests
{
    [Fact]
    public async Task CommandRegistryRegistersAndExecutes()
    {
        var registry = new CommandRegistry();
        bool invoked = false;

        using IDisposable reg = registry.Register("test.command", _ =>
        {
            invoked = true;
            return Task.CompletedTask;
        });

        await registry.ExecuteAsync("test.command", null, CancellationToken.None);

        Assert.True(invoked);
    }

    [Fact]
    public async Task CommandRegistryUnregisters()
    {
        var registry = new CommandRegistry();

        IDisposable reg = registry.Register("test.command", _ => Task.CompletedTask);
        reg.Dispose();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ExecuteAsync("test.command", null, CancellationToken.None));
    }

    [Fact]
    public async Task CommandRegistryKeepsTheReplacementWhenTheReplacedRegistrationIsDisposed()
    {
        var registry = new CommandRegistry();
        Func<CommandContext, Task> handler = _ => Task.CompletedTask;

        IDisposable replaced = registry.Register("test.command", handler);
        using IDisposable current = registry.Register("test.command", handler);
        replaced.Dispose();
        replaced.Dispose();

        await registry.ExecuteAsync("test.command", null, CancellationToken.None);
    }

    [Fact]
    public void CommandMetadataRegistryKeepsTheReplacementWhenTheReplacedRegistrationIsDisposed()
    {
        var registry = new CommandMetadataRegistry();
        CommandMetadata metadata = new(Title: "Title");
        int changedCount = 0;

        IDisposable replaced = registry.Register("test.command", metadata);
        using IDisposable current = registry.Register("test.command", metadata);
        registry.Changed += (_, _) => changedCount++;
        replaced.Dispose();
        replaced.Dispose();

        Assert.True(registry.TryGet("test.command", out CommandMetadata stored));
        Assert.Same(metadata, stored);
        Assert.Equal(0, changedCount);
        current.Dispose();
        Assert.False(registry.TryGet("test.command", out _));
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void ExtensionViewRegistryKeepsTheReplacementWhenTheReplacedRegistrationIsDisposed()
    {
        var registry = new ExtensionViewRegistry();
        ICustomViewProvider provider = new NullCustomViewProvider();

        IDisposable replaced = registry.RegisterCustomViewProvider("test.view", provider);
        using IDisposable current = registry.RegisterCustomViewProvider("test.view", provider);
        replaced.Dispose();
        replaced.Dispose();

        Assert.True(registry.TryGetCustomViewProvider("test.view", out ICustomViewProvider stored));
        Assert.Same(provider, stored);
        current.Dispose();
        Assert.False(registry.TryGetCustomViewProvider("test.view", out _));
    }

    private sealed class NullCustomViewProvider : ICustomViewProvider
    {
        public object? CreateViewModel() => null;
    }

    [Fact]
    public async Task SettingsStoreUsesWorkspaceOverride()
    {
        var settings = new InMemorySettingsStore();

        await settings.UpdateAsync("setting", "user", SettingsTarget.User, CancellationToken.None);
        Assert.Equal("user", settings.Get<string>("setting"));

        await settings.UpdateAsync("setting", "workspace", SettingsTarget.Workspace, CancellationToken.None);
        Assert.Equal("workspace", settings.Get<string>("setting"));
    }

    [Fact]
    public async Task ExtensionStorageStoresValues()
    {
        var storage = new InMemoryExtensionStorage();

        await storage.SetAsync("key", 42, CancellationToken.None);
        int? value = await storage.GetAsync<int?>("key", CancellationToken.None);

        Assert.Equal(42, value);

        await storage.RemoveAsync("key", CancellationToken.None);
        value = await storage.GetAsync<int?>("key", CancellationToken.None);

        Assert.Null(value);
    }

    [Fact]
    public async Task WorkspaceFindsAndReadsFiles()
    {
        var workspace = new InMemoryWorkspace();
        byte[] content = Encoding.UTF8.GetBytes("hello");

        await workspace.WriteFileAsync("views/main.xaml", content, CancellationToken.None);
        await workspace.WriteFileAsync("readme.md", content, CancellationToken.None);

        var matches = await workspace.FindFilesAsync("**/*.xaml", null, CancellationToken.None);

        Assert.Single(matches);
        Assert.Equal("views/main.xaml", matches[0]);

        byte[] read = await workspace.ReadFileAsync("views/main.xaml", CancellationToken.None);
        Assert.Equal(content, read);
    }
}
