using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Finds the file or folder a path command works on and the changes of the repository below it.
/// </summary>
internal static class GitCommandTarget
{
    /// <summary>
    /// Returns the path in <see cref="CommandContext.Arguments"/>[0], or the file of the active document when the
    /// command got no path argument; null when there is neither.
    /// </summary>
    public static string? GetPath(CommandContext? context, IEditorServices? editor)
    {
        if (context?.Arguments is { Count: > 0 } arguments
            && arguments[0] is string argument
            && !string.IsNullOrWhiteSpace(argument))
        {
            return argument;
        }

        string? activePath = editor?.ActiveDocument?.FilePath;
        return string.IsNullOrWhiteSpace(activePath) ? null : activePath;
    }

    /// <summary>
    /// Converts a path into the repository-relative form of git's status output, with <c>/</c> as separator and an
    /// empty string for the root itself. A relative path is taken as relative to the root. Returns null for a path
    /// outside the repository.
    /// </summary>
    public static string? ToRepositoryPath(string repositoryRoot, string path)
    {
        string fullRoot = Path.GetFullPath(repositoryRoot);
        string fullPath = Path.GetFullPath(path, fullRoot);
        if (!FileSystemPathComparison.IsSameOrDescendant(fullPath, fullRoot))
        {
            return null;
        }

        string relative = Path.GetRelativePath(fullRoot, fullPath);
        return relative == "." ? string.Empty : relative.Replace('\\', '/');
    }

    /// <summary>
    /// Returns the changes that are <paramref name="repositoryPath"/> itself or lie below it and match
    /// <paramref name="predicate"/>.
    /// </summary>
    public static IReadOnlyList<GitFileChange> FindChanges(
        IReadOnlyList<GitFileChange> changes,
        string repositoryPath,
        Func<GitFileChange, bool> predicate)
    {
        List<GitFileChange> matches = new();
        string folderPrefix = repositoryPath.Length == 0 ? string.Empty : repositoryPath.TrimEnd('/') + "/";
        foreach (GitFileChange change in changes)
        {
            bool isTarget = repositoryPath.Length == 0
                || string.Equals(change.Path, repositoryPath, FileSystemPathComparison.Comparison)
                || change.Path.StartsWith(folderPrefix, FileSystemPathComparison.Comparison);
            if (isTarget && predicate(change))
            {
                matches.Add(change);
            }
        }

        return matches;
    }

    /// <summary>Returns whether the file is not tracked by git.</summary>
    public static bool IsUntracked(GitFileChange change)
    {
        return change.IsUntracked
            || change.IndexStatus == GitChangeKind.Untracked
            || change.WorkTreeStatus == GitChangeKind.Untracked;
    }

    /// <summary>Returns whether the working file differs from the index, including untracked files.</summary>
    public static bool HasWorkingTreeChange(GitFileChange change)
    {
        return change.WorkTreeStatus != GitChangeKind.None || IsUntracked(change);
    }

    /// <summary>Returns whether the index differs from HEAD.</summary>
    public static bool HasStagedChange(GitFileChange change)
    {
        return change.IndexStatus != GitChangeKind.None && !IsUntracked(change);
    }

    /// <summary>Returns whether discarding can restore the working file from the index.</summary>
    public static bool CanDiscard(GitFileChange change)
    {
        return change.WorkTreeStatus != GitChangeKind.None && !IsUntracked(change) && !change.IsConflicted;
    }
}
