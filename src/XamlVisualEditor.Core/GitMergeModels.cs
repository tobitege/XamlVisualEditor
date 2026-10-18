namespace XamlVisualEditor.Core;

/// <summary>
/// How <see cref="Interfaces.IGitMergeService.MergeAsync"/>, <see cref="Interfaces.IGitMergeService.CherryPickAsync"/>,
/// <see cref="Interfaces.IGitMergeService.RevertAsync"/> or <see cref="Interfaces.IGitMergeService.RebaseAsync"/> ended.
/// </summary>
public enum GitMergeResult
{
    /// <summary>
    /// The merge created a merge commit or fast-forwarded the branch; a cherry-pick or revert created its commit; a
    /// rebase replayed the commits.
    /// </summary>
    Merged,

    /// <summary>The revision was already merged into, or for a rebase contained in, the current branch; nothing changed.</summary>
    AlreadyUpToDate,

    /// <summary>
    /// The merge stopped before its commit, because of conflicts or a failing <c>pre-merge-commit</c> hook; the merge
    /// is in progress and waits to be committed or aborted. A cherry-pick, revert or rebase stopped the same way, or
    /// because its result is empty, and waits to be continued, skipped or aborted.
    /// </summary>
    Stopped
}

/// <summary>The side of a conflict whose version of a file is taken.</summary>
public enum GitConflictSide
{
    /// <summary>The version in HEAD ("ours"): the current branch of a merge, the branch a rebase replays onto.</summary>
    Ours,

    /// <summary>The incoming version ("theirs"): the merged, picked, reverted or replayed commit.</summary>
    Theirs
}
