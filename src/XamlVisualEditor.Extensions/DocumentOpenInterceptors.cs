namespace XamlVisualEditor.Extensions;

/// <summary>Decides which file a user-requested open shows, for example a generated file in place of its source.</summary>
public interface IDocumentOpenInterceptor
{
    /// <summary>Returns the path of the file to open instead of <paramref name="filePath"/>, or null to open it unchanged.</summary>
    /// <remarks>Throwing <see cref="OperationCanceledException"/> cancels the open, for example when the user cancelled a conversion.</remarks>
    /// <param name="filePath">The existing file the user asked to open; it is not open in the editor yet.</param>
    /// <param name="cancellationToken">Cancels the decision.</param>
    Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken);
}

/// <summary>Registers interceptors that user-requested opens consult before a file is shown.</summary>
public interface IDocumentOpenInterceptors
{
    /// <summary>Registers an interceptor; the first registered interceptor that redirects wins.</summary>
    /// <returns>A handle that removes the interceptor when disposed.</returns>
    IDisposable Register(IDocumentOpenInterceptor interceptor);
}
