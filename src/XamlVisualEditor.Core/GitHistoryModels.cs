using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Core;

/// <summary>
/// A local branch or a remote-tracking branch of a repository. Two branches with the same values are equal.
/// </summary>
public sealed record GitBranch
{
    /// <summary>Gets the short name, for example <c>main</c> or <c>origin/main</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the full reference name, for example <c>refs/heads/main</c> or <c>refs/remotes/origin/main</c>.</summary>
    public required string FullName { get; init; }

    /// <summary>Gets whether the branch is a remote-tracking branch below <c>refs/remotes/</c>.</summary>
    public bool IsRemote { get; init; }

    /// <summary>Gets the remote of a remote-tracking branch, for example <c>origin</c>; null for local branches.</summary>
    public string? RemoteName { get; init; }

    /// <summary>Gets whether HEAD points to this branch.</summary>
    public bool IsCurrent { get; init; }

    /// <summary>Gets the subject line of the commit the branch points to.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Gets the short name of the upstream branch of a local branch, for example <c>origin/main</c>, or null.</summary>
    public string? UpstreamName { get; init; }

    /// <summary>Gets whether the upstream branch is configured but no longer exists.</summary>
    public bool IsUpstreamGone { get; init; }

    /// <summary>Gets the number of commits the branch has that its upstream does not have.</summary>
    public int AheadBy { get; init; }

    /// <summary>Gets the number of commits the upstream has that the branch does not have.</summary>
    public int BehindBy { get; init; }

    /// <summary>
    /// Gets the name of the local branch a remote-tracking branch corresponds to, for example <c>feature/x</c> for
    /// <c>origin/feature/x</c>; the name itself for local branches.
    /// </summary>
    public string LocalName =>
        IsRemote && RemoteName is { Length: > 0 } remote && Name.StartsWith(remote + "/", StringComparison.Ordinal)
            ? Name[(remote.Length + 1)..]
            : Name;
}

/// <summary>
/// A configured remote with its fetch and push URL.
/// </summary>
/// <param name="Name">The name of the remote, for example <c>origin</c>.</param>
/// <param name="FetchUrl">The URL git fetches from; may contain user information.</param>
/// <param name="PushUrl">The URL git pushes to; may contain user information.</param>
public sealed record GitRemote(string Name, string FetchUrl, string PushUrl)
{
    /// <summary>Gets the name git gives the remote of a clone.</summary>
    public const string DEFAULT_NAME = "origin";

    /// <summary>Returns the remote <c>origin</c> when it exists, otherwise the first remote, or null when there is none.</summary>
    public static GitRemote? SelectDefault(IReadOnlyList<GitRemote> remotes)
    {
        foreach (GitRemote remote in remotes)
        {
            if (string.Equals(remote.Name, DEFAULT_NAME, StringComparison.Ordinal))
            {
                return remote;
            }
        }

        return remotes.Count == 0 ? null : remotes[0];
    }
}

/// <summary>
/// Result of <see cref="Interfaces.IGitBranchService.DeleteAsync"/>.
/// </summary>
public enum GitBranchDeleteResult
{
    /// <summary>The branch was deleted.</summary>
    Deleted,

    /// <summary>
    /// The branch was kept because it has commits that are neither in its upstream nor in HEAD; deleting it with
    /// force loses them.
    /// </summary>
    NotFullyMerged
}

/// <summary>
/// Why a fetch, pull or push failed.
/// </summary>
public enum GitRemoteFailureKind
{
    /// <summary>The command succeeded.</summary>
    None,

    /// <summary>The server refused the credentials, or git could not ask for them because prompts are disabled.</summary>
    Authentication,

    /// <summary>The server accepted the credentials but denies access (HTTP 403).</summary>
    Forbidden,

    /// <summary>The server could not be reached: unknown host, refused or timed out connection.</summary>
    Network,

    /// <summary>SSH does not know the host key of the server.</summary>
    HostKeyVerification,

    /// <summary>The push is not a fast-forward: the remote branch has commits the local branch does not have.</summary>
    Rejected,

    /// <summary>The remote declined the push itself, for example a protected branch or a server hook.</summary>
    RemoteRejected,

    /// <summary>A pull cannot fast-forward because the branch and its upstream have diverged.</summary>
    Diverged,

    /// <summary>The current branch has no upstream branch.</summary>
    NoUpstream,

    /// <summary>The upstream branch of the current branch no longer exists, for example because it was deleted on the remote.</summary>
    UpstreamGone,

    /// <summary>Git did not finish within the network timeout and was stopped.</summary>
    Timeout,

    /// <summary>Any other failure; <see cref="GitRemoteResult.Message"/> holds git's error output.</summary>
    Other
}

/// <summary>
/// Outcome of a fetch, pull or push.
/// </summary>
public sealed class GitRemoteResult
{
    /// <summary>Gets why the command failed; <see cref="GitRemoteFailureKind.None"/> on success.</summary>
    public GitRemoteFailureKind FailureKind { get; init; }

    /// <summary>Gets whether the command succeeded.</summary>
    public bool Success => FailureKind == GitRemoteFailureKind.None;

    /// <summary>Gets git's trimmed output: the error output on failure, otherwise what git reported.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the remotes a fetch fetched; on a failed fetch, the remotes that were fetched although others failed.
    /// Empty for pull and push.
    /// </summary>
    public IReadOnlyList<string> FetchedRemotes { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the number of commits a successful pull brought into the branch by fast-forwarding: 0 when the branch was
    /// already up to date, null when git could not count them, and null for fetch and push.
    /// </summary>
    public int? PulledCommitCount { get; init; }

    /// <summary>Creates a successful result.</summary>
    public static GitRemoteResult Succeeded(string message) => new() { Message = message };

    /// <summary>Creates a failed result; <paramref name="kind"/> must not be <see cref="GitRemoteFailureKind.None"/>.</summary>
    public static GitRemoteResult Failed(GitRemoteFailureKind kind, string message)
    {
        if (kind == GitRemoteFailureKind.None)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "A failed result needs a failure kind.");
        }

        return new GitRemoteResult { FailureKind = kind, Message = message };
    }
}

/// <summary>
/// A commit as <c>git log</c> lists it.
/// </summary>
public sealed record GitCommitInfo
{
    /// <summary>Gets the full commit id.</summary>
    public required string Sha { get; init; }

    /// <summary>Gets the abbreviated commit id as git abbreviates it in this repository.</summary>
    public string ShortSha { get; init; } = string.Empty;

    /// <summary>Gets the full ids of the parents; empty for a root commit, two or more for a merge commit.</summary>
    public IReadOnlyList<string> ParentShas { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets the abbreviated ids of the parents as git abbreviates them in this repository, in the order of
    /// <see cref="ParentShas"/>.
    /// </summary>
    public IReadOnlyList<string> ParentShortShas { get; init; } = Array.Empty<string>();

    /// <summary>Gets the author name.</summary>
    public string AuthorName { get; init; } = string.Empty;

    /// <summary>Gets the author email address.</summary>
    public string AuthorEmail { get; init; } = string.Empty;

    /// <summary>Gets the author date.</summary>
    public DateTimeOffset AuthorDate { get; init; }

    /// <summary>Gets the committer name.</summary>
    public string CommitterName { get; init; } = string.Empty;

    /// <summary>Gets the committer date.</summary>
    public DateTimeOffset CommitterDate { get; init; }

    /// <summary>Gets the subject: the first paragraph of the message in one line, as git's <c>%s</c> writes it.</summary>
    public string Subject { get; init; } = string.Empty;

    /// <summary>Gets the complete message without trailing line breaks.</summary>
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Gets the references that point to the commit as git decorates it, for example <c>HEAD -&gt; main</c>,
    /// <c>tag: v1.0</c> or <c>origin/main</c>.
    /// </summary>
    public IReadOnlyList<string> Refs { get; init; } = Array.Empty<string>();

    /// <summary>Gets whether the commit has no parent.</summary>
    public bool IsRoot => ParentShas.Count == 0;

    /// <summary>Gets whether the commit has more than one parent.</summary>
    public bool IsMerge => ParentShas.Count > 1;
}

/// <summary>
/// A file a commit changed, compared with its first parent.
/// </summary>
public sealed record GitCommitFileChange
{
    /// <summary>Gets the repository-relative path after the commit, with <c>/</c> as separator.</summary>
    public required string Path { get; init; }

    /// <summary>Gets the path before the commit of a renamed or copied file, otherwise null.</summary>
    public string? OldPath { get; init; }

    /// <summary>Gets the kind of the change.</summary>
    public GitChangeKind Kind { get; init; }

    /// <summary>Gets the similarity in percent of a renamed or copied file, otherwise null.</summary>
    public int? Similarity { get; init; }
}

/// <summary>
/// A commit with the files it changed.
/// </summary>
public sealed record GitCommitDetails
{
    /// <summary>Gets the commit.</summary>
    public required GitCommitInfo Commit { get; init; }

    /// <summary>
    /// Gets the files the commit changed compared with its first parent; for a root commit, all its files. A merge
    /// commit therefore lists what the merge brought into the branch it was merged into.
    /// </summary>
    public required IReadOnlyList<GitCommitFileChange> Files { get; init; }
}

/// <summary>
/// What <see cref="Interfaces.IGitHistoryService.GetLogAsync"/> lists.
/// </summary>
public sealed record GitLogQuery
{
    /// <summary>Gets the number of commits <see cref="MaxCount"/> has unless set.</summary>
    public const int DEFAULT_MAX_COUNT = 100;

    /// <summary>
    /// Gets the repository-relative file or folder whose commits are listed, with <c>/</c> as separator; null or
    /// empty lists the commits of the whole repository.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>Gets whether the history of the file <see cref="Path"/> continues beyond renames; ignored without a path.</summary>
    public bool Follow { get; init; }

    /// <summary>Gets the commit, branch or tag the history starts at; null starts at HEAD.</summary>
    public string? Revision { get; init; }

    /// <summary>Gets the text the author name or email address must contain, ignoring case; null for every author.</summary>
    public string? Author { get; init; }

    /// <summary>Gets the text the commit message must contain, ignoring case; null for every message.</summary>
    public string? MessageText { get; init; }

    /// <summary>Gets the number of commits that are skipped before the first listed one.</summary>
    public int Skip { get; init; }

    /// <summary>Gets the maximum number of listed commits.</summary>
    public int MaxCount { get; init; } = DEFAULT_MAX_COUNT;
}

/// <summary>
/// A tag of a repository.
/// </summary>
public sealed record GitTag
{
    /// <summary>The <see cref="TargetType"/> of a tag that points to a commit.</summary>
    public const string COMMIT_TYPE = "commit";

    /// <summary>Gets the short name, for example <c>v1.0</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the full reference name, for example <c>refs/tags/v1.0</c>.</summary>
    public required string FullName { get; init; }

    /// <summary>
    /// Gets the id of the object the tag points to: for an annotated tag, the object its tag object points to, not
    /// the tag object itself.
    /// </summary>
    public string TargetSha { get; init; } = string.Empty;

    /// <summary>Gets <see cref="TargetSha"/> as git abbreviates it in this repository.</summary>
    public string TargetShortSha { get; init; } = string.Empty;

    /// <summary>
    /// Gets the type of the object the tag points to as git names it: <c>commit</c>, <c>tree</c>, <c>blob</c>, or
    /// <c>tag</c> for an annotated tag of a tag that git did not peel.
    /// </summary>
    public string TargetType { get; init; } = COMMIT_TYPE;

    /// <summary>Gets whether the tag points to a commit, so it has a history and a branch can start at it.</summary>
    public bool IsCommit => string.Equals(TargetType, COMMIT_TYPE, StringComparison.Ordinal);

    /// <summary>Gets whether the tag is an annotated tag object with tagger, date and message.</summary>
    public bool IsAnnotated { get; init; }

    /// <summary>Gets the date of an annotated tag, or the committer date of the commit a lightweight tag points to.</summary>
    public DateTimeOffset? Date { get; init; }

    /// <summary>Gets the tagger of an annotated tag; empty for a lightweight tag.</summary>
    public string TaggerName { get; init; } = string.Empty;

    /// <summary>Gets the subject of the tag message of an annotated tag, or of the commit a lightweight tag points to.</summary>
    public string Subject { get; init; } = string.Empty;
}
