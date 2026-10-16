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
