using Avalonia.Headless.XUnit;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentOpenRedirectTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-OpenRedirect-" + Guid.NewGuid().ToString("N"));

    public DocumentOpenRedirectTests()
    {
        Directory.CreateDirectory(_root);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenFileAsync_Opens_The_Redirected_File()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = CreateFile("Source.cs");
        string targetPath = CreateFile("Generated.txt");
        DocumentOpenInterceptorRegistry registry = new();
        RedirectingInterceptor interceptor = new(sourcePath, targetPath);
        using IDisposable registration = registry.Register(interceptor);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"), documentOpenResolver: registry);

        await main.OpenFileAsync(sourcePath).WaitAsync(cancellationToken);

        IEditorDocumentViewModel document = Assert.Single(main.Documents);
        Assert.Equal(targetPath, document.FilePath);
        Assert.Equal(1, interceptor.Calls);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenFileAsync_Does_Not_Ask_Again_For_An_Open_File()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = CreateFile("Source.cs");
        DocumentOpenInterceptorRegistry registry = new();
        RedirectingInterceptor interceptor = new(sourcePath, null);
        using IDisposable registration = registry.Register(interceptor);
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"), documentOpenResolver: registry);

        await main.OpenFileAsync(sourcePath).WaitAsync(cancellationToken);
        await main.OpenFileAsync(sourcePath).WaitAsync(cancellationToken);

        Assert.Equal(sourcePath, Assert.Single(main.Documents).FilePath);
        Assert.Equal(1, interceptor.Calls);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task OpenFileAsync_Opens_Nothing_When_An_Interceptor_Cancels()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        string sourcePath = CreateFile("Source.cs");
        DocumentOpenInterceptorRegistry registry = new();
        using IDisposable registration = registry.Register(new CancellingInterceptor());
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"), documentOpenResolver: registry);

        await main.OpenFileAsync(sourcePath).WaitAsync(cancellationToken);

        Assert.Empty(main.Documents);
        Assert.Empty(main.RecentFiles);
        Assert.Equal(string.Format(UiStrings.OpenCanceled, "Source.cs"), main.StatusText);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "class Example { }");
        return path;
    }

    private sealed class CancellingInterceptor : IDocumentOpenInterceptor
    {
        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class RedirectingInterceptor(string sourcePath, string? targetPath) : IDocumentOpenInterceptor
    {
        public int Calls { get; private set; }

        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(filePath == sourcePath ? targetPath : null);
        }
    }
}
