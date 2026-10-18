using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// The user stopped a writing git command, which may have left its work half done. Derives from
/// <see cref="InvalidOperationException"/>, so it is reported like a failed git command, and callers that react to a
/// failure tell the two apart by its type.
/// </summary>
internal sealed class GitStoppedException : InvalidOperationException
{
    public GitStoppedException()
        : base(GitStrings.GitStopped)
    {
    }
}
