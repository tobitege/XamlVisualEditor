using Microsoft.Extensions.Logging;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Resolves the file that a user-requested open shows.</summary>
public interface IDocumentOpenResolver
{
    /// <summary>Returns the redirected path of the first interceptor that redirects, or <paramref name="filePath"/>.</summary>
    Task<string> ResolveAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Thread-safe registry of document open interceptors.</summary>
public sealed class DocumentOpenInterceptorRegistry : IDocumentOpenInterceptors, IDocumentOpenResolver
{
    private readonly object _gate = new();
    private readonly List<IDocumentOpenInterceptor> _interceptors = new();
    private readonly ILogger<DocumentOpenInterceptorRegistry> _logger;

    /// <summary>Creates the registry.</summary>
    public DocumentOpenInterceptorRegistry(ILogger<DocumentOpenInterceptorRegistry>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<DocumentOpenInterceptorRegistry>.Instance;
    }

    /// <inheritdoc />
    public IDisposable Register(IDocumentOpenInterceptor interceptor)
    {
        ArgumentNullException.ThrowIfNull(interceptor);
        lock (_gate)
        {
            _interceptors.Add(interceptor);
        }

        return new Registration(this, interceptor);
    }

    /// <inheritdoc />
    public async Task<string> ResolveAsync(string filePath, CancellationToken cancellationToken)
    {
        IDocumentOpenInterceptor[] interceptors;
        lock (_gate)
        {
            interceptors = _interceptors.ToArray();
        }

        foreach (IDocumentOpenInterceptor interceptor in interceptors)
        {
            string? redirected;
            try
            {
                redirected = await interceptor.RedirectAsync(filePath, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A failing interceptor must not keep the user from opening the requested file.
                _logger.LogWarning(exception, "Document open interceptor {Interceptor} failed for {FilePath}",
                    interceptor.GetType().FullName, filePath);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(redirected))
                return redirected;
        }

        return filePath;
    }

    private void Unregister(IDocumentOpenInterceptor interceptor)
    {
        lock (_gate)
        {
            _interceptors.Remove(interceptor);
        }
    }

    private sealed class Registration(DocumentOpenInterceptorRegistry owner, IDocumentOpenInterceptor interceptor) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Unregister(interceptor);
        }
    }
}
