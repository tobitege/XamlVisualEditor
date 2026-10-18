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

    /// <summary>
    /// Returns whether the working file differs from the index, including untracked files. A conflicted file is not a
    /// working tree change: staging it would mark it resolved, which <see cref="GitCommands.MARK_RESOLVED"/> does after
    /// checking for conflict markers.
    /// </summary>
    public static bool HasWorkingTreeChange(GitFileChange change)
    {
        return !change.IsConflicted && (change.WorkTreeStatus != GitChangeKind.None || IsUntracked(change));
    }

    /// <summary>Returns whether the index differs from HEAD. A conflicted file is not staged until it is resolved.</summary>
    public static bool HasStagedChange(GitFileChange change)
    {
        return change.IndexStatus != GitChangeKind.None && !IsUntracked(change) && !change.IsConflicted;
    }

    /// <summary>Returns the pathspec of a repository path: the path itself, or <c>.</c> for the repository root.</summary>
    public static string ToPathspec(string repositoryPath)
    {
        return repositoryPath.Length == 0 ? "." : repositoryPath;
    }

    /// <summary>Returns the paths of the changes.</summary>
    public static IReadOnlyList<string> PathsOf(IReadOnlyList<GitFileChange> changes)
    {
        string[] paths = new string[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            paths[i] = changes[i].Path;
        }

        return paths;
    }

    /// <summary>
    /// Returns the paths that stage the changes found below <paramref name="repositoryPath"/>: git's own paths of the
    /// changes, which match the index also when the path on disk differs in case, and for a folder the folder
    /// itself, so files that appeared below it after the last refresh are staged too; <c>.</c> for the repository
    /// root. With <paramref name="hasConflictsBelow"/>, only the paths of the changes.
    /// </summary>
    public static IReadOnlyList<string> StagePathsOf(
        IReadOnlyList<GitFileChange> changes,
        string repositoryPath,
        bool hasConflictsBelow = false)
    {
        // The folder pathspec would also stage the conflicted files below it, markers included.
        if (hasConflictsBelow)
        {
            return PathsOf(changes);
        }

        if (repositoryPath.Length == 0)
        {
            return [ToPathspec(repositoryPath)];
        }

        List<string> paths = new(changes.Count + 1);
        bool isChangedFile = false;
        foreach (GitFileChange change in changes)
        {
            paths.Add(change.Path);
            isChangedFile |= string.Equals(change.Path, repositoryPath, FileSystemPathComparison.Comparison);
        }

        if (!isChangedFile)
        {
            paths.Add(repositoryPath);
        }

        return paths;
    }

    /// <summary>
    /// Returns the paths that unstage the changes: a staged rename also needs its old path, otherwise the deletion
    /// of the old path stays staged.
    /// </summary>
    public static IReadOnlyList<string> UnstagePathsOf(IReadOnlyList<GitFileChange> changes)
    {
        List<string> paths = new(changes.Count);
        foreach (GitFileChange change in changes)
        {
            paths.Add(change.Path);
            if (change.IsRenamed && !string.IsNullOrEmpty(change.OldPath))
            {
                paths.Add(change.OldPath);
            }
        }

        return paths;
    }

    /// <summary>Returns whether discarding can restore the working file from the index.</summary>
    public static bool CanDiscard(GitFileChange change)
    {
        return change.WorkTreeStatus != GitChangeKind.None && !IsUntracked(change) && !change.IsConflicted;
    }
}
