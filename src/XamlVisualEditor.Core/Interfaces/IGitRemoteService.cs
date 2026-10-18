using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Exchanges commits with remotes. Fetch, pull and push never prompt on a terminal, stop their network part after
/// the network timeout and report failures in their <see cref="GitRemoteResult"/>; cancelling the token stops git
/// and throws <see cref="System.OperationCanceledException"/>. The network part does not wait for, and does not
/// hold up, the writing commands of the repository such as stage or commit.
/// </summary>
public interface IGitRemoteService
{
    /// <summary>Returns the configured remotes; empty when there is none.</summary>
    Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Adds the remote <paramref name="name"/> with <paramref name="url"/> for fetch and push, without fetching it.
    /// Throws <see cref="System.InvalidOperationException"/> with git's error output when git refuses the name, for
    /// example because a remote of that name exists, and <see cref="System.ArgumentException"/> for a name that starts
    /// with <c>-</c>, which git accepts but which other commands could read as an option.
    /// </summary>
    Task AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct = default);

    /// <summary>
    /// Renames the remote <paramref name="name"/> to <paramref name="newName"/> with its remote-tracking branches and
    /// the upstreams of the branches that track them. Throws <see cref="System.InvalidOperationException"/> with git's
    /// error output when git fails, and <see cref="System.ArgumentException"/> for a new name that starts with <c>-</c>.
    /// </summary>
    Task RenameRemoteAsync(string repoPath, string name, string newName, CancellationToken ct = default);

    /// <summary>
    /// Removes the remote <paramref name="name"/> with its remote-tracking branches; branches that tracked them lose
    /// their upstream. Throws <see cref="System.InvalidOperationException"/> with git's error output when git fails.
    /// </summary>
    Task RemoveRemoteAsync(string repoPath, string name, CancellationToken ct = default);

    /// <summary>
    /// Sets the URL of the remote <paramref name="name"/> to <paramref name="url"/>. A remote without its own push URL
    /// pushes to it as well. Throws <see cref="System.InvalidOperationException"/> with git's error output when git
    /// fails.
    /// </summary>
    Task SetRemoteUrlAsync(string repoPath, string name, string url, CancellationToken ct = default);

    /// <summary>
    /// Fetches every remote that a fetch of all remotes does not skip, one remote after the other, as configured for
    /// it: with its refspecs and with pruning only when <c>fetch.prune</c> or <c>remote.&lt;name&gt;.prune</c> asks
    /// for it. A failing remote does not stop the others: the result then fails with the failure of the first
    /// failing remote and lists the fetched ones in <see cref="GitRemoteResult.FetchedRemotes"/>.
    /// </summary>
    Task<GitRemoteResult> FetchAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Fetches the remote of the upstream branch of the current branch as <c>git pull</c> does, with the configured
    /// refspecs, tags and pruning of that remote, and fast-forwards the branch to its upstream. A branch that has
    /// diverged from its upstream is left unchanged and the result reports <see cref="GitRemoteFailureKind.Diverged"/>,
    /// one whose upstream no longer exists on the remote <see cref="GitRemoteFailureKind.UpstreamGone"/>, also when no
    /// fetch pruned its remote-tracking branch. <see cref="GitRemoteResult.PulledCommitCount"/> tells a fast-forward
    /// from a branch that was already up to date, or is null when git could not count the pulled commits, such as on
    /// an unborn branch. When a second pull of the same repository fast-forwards between this pull's fast-forward and
    /// its count, the count of this pull is 0. Cancellation stops the fetch and the wait for other writing commands;
    /// once the fast-forward has started, it is finished.
    /// </summary>
    Task<GitRemoteResult> PullAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Pushes the current branch to its upstream. With <paramref name="setUpstreamRemote"/>, it is pushed to the
    /// branch of the same name on that remote, which then becomes its upstream.
    /// </summary>
    Task<GitRemoteResult> PushAsync(string repoPath, string? setUpstreamRemote = null, CancellationToken ct = default);

    /// <summary>
    /// Pushes the local branch <paramref name="branch"/> to its upstream branch and replaces it, for example after an
    /// amend or a rebase, but only while it still points to <paramref name="expectedUpstreamSha"/>, the commit the user
    /// saw; otherwise the result reports <see cref="GitRemoteFailureKind.Rejected"/> and nothing changes. No other
    /// branch is pushed, whatever <c>push.default</c> says, and a branch without upstream reports
    /// <see cref="GitRemoteFailureKind.NoUpstream"/>.
    /// </summary>
    Task<GitRemoteResult> ForcePushAsync(string repoPath, string branch, string expectedUpstreamSha, CancellationToken ct = default);

    /// <summary>
    /// Pushes the tag <paramref name="name"/> to <paramref name="remote"/>. A tag of that name on the remote that
    /// points elsewhere is not replaced; the result reports <see cref="GitRemoteFailureKind.Rejected"/>.
    /// </summary>
    Task<GitRemoteResult> PushTagAsync(string repoPath, string remote, string name, CancellationToken ct = default);

    /// <summary>Deletes the tag <paramref name="name"/> on <paramref name="remote"/>; the local tag stays.</summary>
    Task<GitRemoteResult> DeleteRemoteTagAsync(string repoPath, string remote, string name, CancellationToken ct = default);
}
