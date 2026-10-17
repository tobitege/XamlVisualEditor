using System;
using System.Collections.Generic;
using System.Linq;

namespace XamlVisualEditor.Core;

/// <summary>
/// Represents a git repository status snapshot. Two statuses are equal when all values and the changes, in order,
/// are equal.
/// </summary>
public sealed record GitRepositoryStatus
{
    public required string RepositoryRoot { get; init; }

    public string BranchName { get; init; } = string.Empty;

    public string? UpstreamName { get; init; }

    public int AheadBy { get; init; }

    public int BehindBy { get; init; }

    /// <summary>
    /// Gets whether the branch has an upstream whose remote-tracking branch no longer exists, for example because the
    /// branch was deleted on the remote and a fetch pruned it. <see cref="UpstreamName"/> still holds its name.
    /// </summary>
    public bool IsUpstreamGone { get; init; }

    public bool IsRepository { get; init; } = true;

    public string? ErrorMessage { get; init; }

    public required IReadOnlyList<GitFileChange> Changes { get; init; }

    /// <summary>Gets the full commit id of HEAD, or null when the branch has no commit yet.</summary>
    public string? HeadSha { get; init; }

    /// <summary>
    /// Gets whether HEAD points to a commit instead of a branch. <see cref="BranchName"/> then holds the abbreviated
    /// commit id.
    /// </summary>
    public bool IsDetached { get; init; }

    /// <summary>Gets whether the current branch has no commit yet.</summary>
    public bool IsUnborn { get; init; }

    /// <summary>Gets the merge, rebase, cherry-pick or revert that is in progress.</summary>
    public GitRepositoryOperation Operation { get; init; }

    /// <inheritdoc />
    public bool Equals(GitRepositoryStatus? other)
    {
        return other is not null
            && RepositoryRoot == other.RepositoryRoot
            && BranchName == other.BranchName
            && UpstreamName == other.UpstreamName
            && AheadBy == other.AheadBy
            && BehindBy == other.BehindBy
            && IsUpstreamGone == other.IsUpstreamGone
            && IsRepository == other.IsRepository
            && ErrorMessage == other.ErrorMessage
            && HeadSha == other.HeadSha
            && IsDetached == other.IsDetached
            && IsUnborn == other.IsUnborn
            && Operation == other.Operation
            && Changes.SequenceEqual(other.Changes);
    }

    /// <inheritdoc />
    public override int GetHashCode()
    {
        return HashCode.Combine(RepositoryRoot, BranchName, HeadSha, AheadBy, BehindBy, Operation, Changes.Count);
    }
}

/// <summary>
/// An operation that stopped in the middle and waits to be continued or aborted.
/// </summary>
public enum GitRepositoryOperation
{
    None,
    Merging,
    Rebasing,
    CherryPicking,
    Reverting
}

/// <summary>
/// Describes a file change entry in git status; two entries with the same values are equal.
/// </summary>
public sealed record GitFileChange
{
    public required string Path { get; init; }

    public string? OldPath { get; init; }

    public GitChangeKind IndexStatus { get; init; }

    public GitChangeKind WorkTreeStatus { get; init; }

    public bool IsUntracked { get; init; }

    public bool IsIgnored { get; init; }

    public bool IsRenamed { get; init; }

    public bool IsCopied { get; init; }

    /// <summary>Gets how the two sides of a merge conflict changed the file; <see cref="GitConflictKind.None"/> without conflict.</summary>
    public GitConflictKind ConflictKind { get; init; }

    /// <summary>Gets whether the file has an unresolved merge conflict.</summary>
    public bool IsConflicted => ConflictKind != GitConflictKind.None;
}

/// <summary>
/// The kind of an unresolved merge conflict, named after git's status codes from the view of the current branch
/// ("us") and the merged commit ("them").
/// </summary>
public enum GitConflictKind
{
    /// <summary>The file has no conflict.</summary>
    None,

    /// <summary>Both sides deleted the file (DD).</summary>
    BothDeleted,

    /// <summary>The current branch added the file (AU).</summary>
    AddedByUs,

    /// <summary>The merged commit deleted the file (UD).</summary>
    DeletedByThem,

    /// <summary>The merged commit added the file (UA).</summary>
    AddedByThem,

    /// <summary>The current branch deleted the file (DU).</summary>
    DeletedByUs,

    /// <summary>Both sides added the file (AA).</summary>
    BothAdded,

    /// <summary>Both sides modified the file (UU).</summary>
    BothModified
}

/// <summary>
/// Known git status kinds for index or work tree.
/// </summary>
public enum GitChangeKind
{
    None,
    Added,
    Modified,
    Deleted,
    Renamed,
    Copied,
    TypeChanged,
    Unmerged,
    Untracked,
    Ignored,
    Unknown
}

/// <summary>
/// Request for a git diff operation.
/// </summary>
public sealed class GitDiffRequest
{
    public bool Staged { get; init; }

    public string? Path { get; init; }

    public bool IsUntracked { get; init; }
}

/// <summary>
/// Represents a parsed git diff.
/// </summary>
public sealed class GitDiff
{
    public required IReadOnlyList<GitDiffFile> Files { get; init; }

    public string? RawText { get; init; }

    /// <summary>
    /// Gets whether git wrote more lines or bytes than were read, so the diff ends early and its last hunk can be
    /// incomplete.
    /// </summary>
    public bool IsTruncated { get; init; }
}

/// <summary>
/// Represents a file diff entry.
/// </summary>
public sealed class GitDiffFile
{
    public required string Path { get; init; }

    public string? OldPath { get; init; }

    public bool IsBinary { get; init; }

    public required IReadOnlyList<string> HeaderLines { get; init; }

    public required IReadOnlyList<GitDiffHunk> Hunks { get; init; }
}

/// <summary>
/// Represents a diff hunk.
/// </summary>
public sealed class GitDiffHunk
{
    public required string Header { get; init; }

    public int OldStart { get; init; }

    public int OldCount { get; init; }

    public int NewStart { get; init; }

    public int NewCount { get; init; }

    public required IReadOnlyList<GitDiffLine> Lines { get; init; }
}

/// <summary>
/// Represents a diff line in a hunk.
/// </summary>
public sealed class GitDiffLine
{
    public required GitDiffLineKind Kind { get; init; }

    public required string Text { get; init; }

    public int? OldLine { get; init; }

    public int? NewLine { get; init; }
}

/// <summary>
/// Indicates the kind of diff line.
/// </summary>
public enum GitDiffLineKind
{
    Context,
    Added,
    Removed,
    NoNewline
}

/// <summary>
/// Describes a git command that finished, as reported by <see cref="Interfaces.IGitCommandLog"/>.
/// </summary>
/// <param name="WorkingDirectory">The folder git ran in.</param>
/// <param name="Arguments">The arguments of the command, without the options every git process gets.</param>
/// <param name="ExitCode">The exit code of git.</param>
/// <param name="Duration">The time from the start of the process to its end.</param>
/// <param name="ErrorOutput">The trimmed standard error output; empty when git wrote nothing there.</param>
public sealed record GitCommandLogEntry(
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    int ExitCode,
    TimeSpan Duration,
    string ErrorOutput);
