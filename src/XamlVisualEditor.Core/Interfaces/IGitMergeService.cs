using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Merges, cherry-picks and reverts commits, resolves conflicts by taking one side, and continues, skips a step of or
/// aborts the merge, rebase, cherry-pick, revert or patch application in progress. The methods throw
/// <see cref="System.InvalidOperationException"/> with git's output when git fails.
/// </summary>
public interface IGitMergeService
{
    /// <summary>
    /// Merges <paramref name="revision"/>, a branch, remote-tracking branch or commit, into the current branch with
    /// git's prepared merge message. Throws when git refuses the merge before changing anything, for example because
    /// a local change is in the way.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a merge that started runs to its end.</remarks>
    Task<GitMergeResult> MergeAsync(string repoPath, string revision, CancellationToken ct = default);

    /// <summary>
    /// Aborts the operation in progress when it is <paramref name="operation"/>, the one the user confirmed, and
    /// restores the state before it; returns false and changes nothing when another or no operation is in progress.
    /// Edits in conflicted files are lost; git refuses, and changes nothing, when a file the operation changed was
    /// edited afterwards.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; an abort that started runs to its end.</remarks>
    Task<bool> AbortAsync(string repoPath, GitRepositoryOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Continues the operation in progress with git's prepared message; for a merge, this commits it. git refuses
    /// while conflicts are unresolved.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a continue that started runs to its end.</remarks>
    Task ContinueAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Skips the current step of the operation in progress when it is <paramref name="operation"/>, the rebase,
    /// cherry-pick, revert or patch application the user confirmed, and continues with the next step; returns false and
    /// changes nothing when another or no such operation is in progress. The changes of the skipped step are lost,
    /// including resolved conflicts; a rebase also discards every other uncommitted change of tracked files.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a skip that started runs to its end.</remarks>
    Task<bool> SkipAsync(string repoPath, GitRepositoryOperation operation, CancellationToken ct = default);

    /// <summary>
    /// Applies the changes of <paramref name="commit"/> to the current branch as a new commit with its message. Returns
    /// <see cref="GitMergeResult.Stopped"/> when conflicts, an empty result or a hook stopped it, and the cherry-pick is
    /// in progress; throws when git refuses before changing anything, for example because a local change is in the way.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a cherry-pick that started runs to its end.</remarks>
    Task<GitMergeResult> CherryPickAsync(string repoPath, string commit, CancellationToken ct = default);

    /// <summary>
    /// Undoes the changes of <paramref name="commit"/> on the current branch with a new commit with git's revert
    /// message. Returns and throws as <see cref="CherryPickAsync"/>.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a revert that started runs to its end.</remarks>
    Task<GitMergeResult> RevertAsync(string repoPath, string commit, CancellationToken ct = default);

    /// <summary>
    /// Replays the commits of the current branch that <paramref name="upstream"/>, a branch, tag or commit, does not
    /// have on top of it. Returns <see cref="GitMergeResult.AlreadyUpToDate"/> when the branch already contains it,
    /// <see cref="GitMergeResult.Stopped"/> when a conflict stopped a step and the rebase is in progress, and throws when
    /// git refuses before changing anything, for example because of uncommitted changes.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a rebase that started runs to its end.</remarks>
    Task<GitMergeResult> RebaseAsync(string repoPath, string upstream, CancellationToken ct = default);

    /// <summary>
    /// Replaces each conflicted file of <paramref name="paths"/> by the version of <paramref name="side"/> and marks it
    /// resolved; a file that side deleted is deleted. Returns false and changes nothing when one of the paths is no
    /// longer conflicted, because taking a side would then overwrite its resolution.
    /// </summary>
    Task<bool> TakeSideAsync(string repoPath, IReadOnlyList<string> paths, GitConflictSide side, CancellationToken ct = default);

    /// <summary>
    /// Returns the commit message git prepared for the operation in progress without its comment lines, or null when
    /// git prepared none.
    /// </summary>
    Task<string?> GetPreparedMessageAsync(string repoPath, CancellationToken ct = default);
}
