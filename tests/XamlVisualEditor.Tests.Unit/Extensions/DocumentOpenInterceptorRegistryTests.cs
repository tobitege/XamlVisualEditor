using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class DocumentOpenInterceptorRegistryTests
{
    [Fact]
    public async Task ResolveAsync_Returns_The_Path_When_No_Interceptor_Redirects()
    {
        DocumentOpenInterceptorRegistry registry = new();
        using IDisposable registration = registry.Register(new FixedInterceptor(null));

        Assert.Equal("a.cs", await registry.ResolveAsync("a.cs", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_Uses_The_First_Interceptor_That_Redirects()
    {
        DocumentOpenInterceptorRegistry registry = new();
        FixedInterceptor last = new("last.axaml");
        using IDisposable declining = registry.Register(new FixedInterceptor(null));
        using IDisposable first = registry.Register(new FixedInterceptor("first.axaml"));
        using IDisposable second = registry.Register(last);

        Assert.Equal("first.axaml", await registry.ResolveAsync("a.cs", CancellationToken.None));
        Assert.Equal(0, last.Calls);
    }

    [Fact]
    public async Task ResolveAsync_Skips_A_Failing_Interceptor()
    {
        DocumentOpenInterceptorRegistry registry = new();
        using IDisposable failing = registry.Register(new FailingInterceptor());
        using IDisposable redirecting = registry.Register(new FixedInterceptor("b.axaml"));

        Assert.Equal("b.axaml", await registry.ResolveAsync("a.cs", CancellationToken.None));
    }

    [Fact]
    public async Task ResolveAsync_Passes_A_Cancellation_On()
    {
        DocumentOpenInterceptorRegistry registry = new();
        using IDisposable cancelling = registry.Register(new CancellingInterceptor());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.ResolveAsync("a.cs", CancellationToken.None));
    }

    [Fact]
    public async Task Disposed_Registration_Is_No_Longer_Consulted()
    {
        DocumentOpenInterceptorRegistry registry = new();
        IDisposable registration = registry.Register(new FixedInterceptor("b.axaml"));

        registration.Dispose();
        registration.Dispose();

        Assert.Equal("a.cs", await registry.ResolveAsync("a.cs", CancellationToken.None));
    }

    private sealed class FixedInterceptor(string? redirect) : IDocumentOpenInterceptor
    {
        public int Calls { get; private set; }

        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(redirect);
        }
    }

    private sealed class CancellingInterceptor : IDocumentOpenInterceptor
    {
        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    private sealed class FailingInterceptor : IDocumentOpenInterceptor
    {
        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Interceptor failure");
    }
}
