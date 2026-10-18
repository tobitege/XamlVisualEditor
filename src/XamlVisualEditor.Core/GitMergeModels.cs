namespace XamlVisualEditor.Core;

/// <summary>How <see cref="Interfaces.IGitMergeService.MergeAsync"/> ended.</summary>
public enum GitMergeResult
{
    /// <summary>The merge created a merge commit or fast-forwarded the branch.</summary>
    Merged,

    /// <summary>The revision was already merged into the current branch; nothing changed.</summary>
    AlreadyUpToDate,

    /// <summary>
    /// The merge stopped before its commit, because of conflicts or a failing <c>pre-merge-commit</c> hook; the merge
    /// is in progress and waits to be committed or aborted.
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
