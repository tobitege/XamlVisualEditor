using XamlVisualEditor.Core;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>What the Git session knows about the workspace.</summary>
public enum GitSessionState
{
    /// <summary>No refresh has finished yet.</summary>
    Unknown,

    /// <summary>No workspace is open.</summary>
    NoWorkspace,

    /// <summary>The git executable could not be started.</summary>
    GitUnavailable,

    /// <summary>The workspace is not inside a git repository.</summary>
    NotRepository,

    /// <summary>The workspace is inside a git repository and <see cref="GitSnapshot.Status"/> holds its status.</summary>
    Repository
}

/// <summary>
/// An immutable state of the Git session: the repository of the workspace, its status, branch, upstream and the
/// switch of the working folder watcher.
/// </summary>
public sealed record GitSnapshot
{
    /// <summary>Gets the state before the first refresh.</summary>
    public static GitSnapshot Initial { get; } = new();

    /// <summary>Gets what the session knows about the workspace.</summary>
    public GitSessionState State { get; init; }

    /// <summary>Gets the workspace path the snapshot was taken for.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>Gets the root of the repository; null unless <see cref="State"/> is <see cref="GitSessionState.Repository"/>.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Gets the absolute git directory, or null when it is unknown.</summary>
    public string? GitDirectory { get; init; }

    /// <summary>Gets the repository status; null unless <see cref="State"/> is <see cref="GitSessionState.Repository"/>.</summary>
    public GitRepositoryStatus? Status { get; init; }

    /// <summary>Gets whether the repository has at least one remote.</summary>
    public bool HasRemote { get; init; }

    /// <summary>Gets the message that explains why there is no status, or git's error output.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets whether the working folder watcher is switched on.</summary>
    public bool IsWorkingTreeWatcherEnabled { get; init; }

    /// <summary>Gets whether git could be started; true until a refresh found it missing.</summary>
    public bool IsGitAvailable => State != GitSessionState.GitUnavailable;

    /// <summary>Gets whether the workspace is inside a git repository.</summary>
    public bool IsRepository => State == GitSessionState.Repository;

    /// <summary>Gets the branch name, or the abbreviated commit id when HEAD is detached.</summary>
    public string BranchName => Status?.BranchName ?? string.Empty;

    /// <summary>Gets the upstream branch, or null.</summary>
    public string? UpstreamName => Status?.UpstreamName;

    /// <summary>Gets whether the branch has an upstream branch.</summary>
    public bool HasUpstream => !string.IsNullOrEmpty(Status?.UpstreamName);

    /// <summary>Gets the number of commits the branch is ahead of its upstream.</summary>
    public int AheadBy => Status?.AheadBy ?? 0;

    /// <summary>Gets the number of commits the branch is behind its upstream.</summary>
    public int BehindBy => Status?.BehindBy ?? 0;

    /// <summary>Gets whether HEAD points to a commit instead of a branch.</summary>
    public bool IsDetached => Status?.IsDetached ?? false;

    /// <summary>Gets the merge, rebase, cherry-pick or revert in progress.</summary>
    public GitRepositoryOperation Operation => Status?.Operation ?? GitRepositoryOperation.None;

    /// <summary>Gets the changed, staged, untracked and conflicted files.</summary>
    public IReadOnlyList<GitFileChange> Changes => Status?.Changes ?? Array.Empty<GitFileChange>();

    /// <summary>Gets the number of entries in <see cref="Changes"/>.</summary>
    public int ChangeCount => Changes.Count;

    /// <summary>Gets whether a file has an unresolved merge conflict.</summary>
    public bool HasConflicts
    {
        get
        {
            IReadOnlyList<GitFileChange> changes = Changes;
            for (int i = 0; i < changes.Count; i++)
            {
                if (changes[i].IsConflicted)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
