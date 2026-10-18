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
    Repository,

    /// <summary>
    /// The workspace is inside a git repository, but reading its status failed, for example because of a damaged
    /// index; <see cref="GitSnapshot.ErrorMessage"/> holds git's error output and <see cref="GitSnapshot.Status"/>
    /// is null. The repository stays watched, so the next change refreshes the status again.
    /// </summary>
    StatusFailed
}

/// <summary>
/// An immutable state of the Git session: the repository of the workspace, its status, branch, upstream and the
/// switch of the working folder watcher. Two snapshots with the same values and changes are equal.
/// </summary>
public sealed record GitSnapshot
{
    /// <summary>Gets the state before the first refresh.</summary>
    public static GitSnapshot Initial { get; } = new();

    /// <summary>Gets what the session knows about the workspace.</summary>
    public GitSessionState State { get; init; }

    /// <summary>Gets the workspace path the snapshot was taken for.</summary>
    public string? WorkspacePath { get; init; }

    /// <summary>Gets the root of the repository; null unless <see cref="IsRepository"/> is true.</summary>
    public string? RepositoryRoot { get; init; }

    /// <summary>Gets the absolute git directory, or null when it is unknown.</summary>
    public string? GitDirectory { get; init; }

    /// <summary>
    /// Gets the absolute common git directory with the branches, which differs from <see cref="GitDirectory"/> only
    /// in linked worktrees, or null when it is unknown.
    /// </summary>
    public string? CommonDirectory { get; init; }

    /// <summary>Gets the repository status; null unless <see cref="State"/> is <see cref="GitSessionState.Repository"/>.</summary>
    public GitRepositoryStatus? Status { get; init; }

    /// <summary>Gets whether the repository has at least one remote.</summary>
    public bool HasRemote { get; init; }

    /// <summary>Gets the message that explains why there is no status, or git's error output.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>Gets whether the working folder watcher is switched on.</summary>
    public bool IsWorkingTreeWatcherEnabled { get; init; }

    /// <summary>
    /// Gets a number that grows when branches may have been created, deleted or moved without a change of HEAD: a
    /// change below <c>refs</c> or of <c>packed-refs</c> in the watched git directories, a command run through
    /// <see cref="GitSession.RunWithServicesAsync{T}"/>, or <see cref="GitSession.RefreshAllAsync"/>.
    /// </summary>
    public int RefsVersion { get; init; }

    /// <summary>Gets whether git could be started; true until a refresh found it missing.</summary>
    public bool IsGitAvailable => State != GitSessionState.GitUnavailable;

    /// <summary>Gets whether the workspace is inside a git repository, also when reading its status failed.</summary>
    public bool IsRepository => State is GitSessionState.Repository or GitSessionState.StatusFailed;

    /// <summary>Gets the branch name, or the abbreviated commit id when HEAD is detached.</summary>
    public string BranchName => Status?.BranchName ?? string.Empty;

    /// <summary>Gets the upstream branch, or null.</summary>
    public string? UpstreamName => Status?.UpstreamName;

    /// <summary>Gets whether the branch has an upstream branch.</summary>
    public bool HasUpstream => !string.IsNullOrEmpty(Status?.UpstreamName);

    /// <summary>Gets whether the upstream branch is configured but no longer exists, for example after it was deleted on the remote.</summary>
    public bool IsUpstreamGone => Status?.IsUpstreamGone ?? false;

    /// <summary>Gets the number of commits the branch is ahead of its upstream.</summary>
    public int AheadBy => Status?.AheadBy ?? 0;

    /// <summary>Gets the number of commits the branch is behind its upstream.</summary>
    public int BehindBy => Status?.BehindBy ?? 0;

    /// <summary>Gets whether HEAD points to a commit instead of a branch.</summary>
    public bool IsDetached => Status?.IsDetached ?? false;

    /// <summary>Gets the merge, rebase, cherry-pick, revert or patch application in progress.</summary>
    public GitRepositoryOperation Operation => Status?.Operation ?? GitRepositoryOperation.None;

    /// <summary>
    /// Gets whether a commit fits the operation in progress: none runs, a merge waits for its commit, or a rebase
    /// stopped at an edit step. The other operations commit each step themselves when they continue.
    /// </summary>
    public bool AllowsCommit =>
        Operation is GitRepositoryOperation.None or GitRepositoryOperation.Merging || Status?.IsStoppedForEdit == true;

    /// <summary>Gets whether amending HEAD fits the operation in progress: none runs, or a rebase stopped at an edit step.</summary>
    public bool AllowsAmend => Operation == GitRepositoryOperation.None || Status?.IsStoppedForEdit == true;

    /// <summary>Gets the changed, staged, untracked and conflicted files.</summary>
    public IReadOnlyList<GitFileChange> Changes => Status?.Changes ?? Array.Empty<GitFileChange>();

    /// <summary>Gets the number of entries in <see cref="Changes"/>.</summary>
    public int ChangeCount => Changes.Count;

    /// <summary>Gets the number of files with an unresolved conflict.</summary>
    public int ConflictCount
    {
        get
        {
            IReadOnlyList<GitFileChange> changes = Changes;
            int count = 0;
            for (int i = 0; i < changes.Count; i++)
            {
                if (changes[i].IsConflicted)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Gets whether a file has an unresolved conflict.</summary>
    public bool HasConflicts => ConflictCount > 0;
}
