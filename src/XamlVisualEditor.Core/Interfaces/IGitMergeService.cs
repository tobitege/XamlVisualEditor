using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Merges commits, resolves conflicts by taking one side, and continues or aborts the merge, rebase, cherry-pick,
/// revert or patch application in progress. The methods throw <see cref="System.InvalidOperationException"/> with
/// git's output when git fails.
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
    /// Aborts the operation in progress and restores the state before it. Edits in conflicted files are lost;
    /// git refuses, and changes nothing, when a file the operation changed was edited afterwards.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; an abort that started runs to its end.</remarks>
    Task AbortAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Continues the operation in progress with git's prepared message; for a merge, this commits it. git refuses
    /// while conflicts are unresolved.
    /// </summary>
    /// <remarks>Cancelling stops only the wait for other git commands; a continue that started runs to its end.</remarks>
    Task ContinueAsync(string repoPath, CancellationToken ct = default);

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
