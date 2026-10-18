using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Lists, switches, creates and deletes branches. Every method throws
/// <see cref="System.InvalidOperationException"/> with git's error output when git fails.
/// </summary>
public interface IGitBranchService
{
    /// <summary>
    /// Returns the local branches followed by the remote-tracking branches, each group sorted by name. Symbolic
    /// references such as <c>origin/HEAD</c> are left out.
    /// </summary>
    Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Switches to a branch. <paramref name="name"/> is a local branch name, a remote-tracking branch such as
    /// <c>origin/feature</c> or a full reference name. For a remote-tracking branch without a local branch of the
    /// same name, a local branch is created that tracks it.
    /// </summary>
    Task SwitchAsync(string repoPath, string name, CancellationToken ct = default);

    /// <summary>
    /// Creates the local branch <paramref name="name"/> at <paramref name="startPoint"/>, or at HEAD when it is
    /// null, and switches to it when <paramref name="switchTo"/> is true. A branch that starts at a remote-tracking
    /// branch of the same name, such as <c>feature</c> at <c>origin/feature</c>, tracks it when
    /// <c>branch.autoSetupMerge</c> lets it, as by default; any other new branch has no upstream, and its first push
    /// sets one. A name that is no valid branch name, such as one that starts with <c>-</c>, fails.
    /// </summary>
    Task CreateAsync(string repoPath, string name, string? startPoint, bool switchTo, CancellationToken ct = default);

    /// <summary>
    /// Returns whether <paramref name="name"/> is a valid name for a new branch, as <c>git check-ref-format --branch</c>
    /// decides it.
    /// </summary>
    Task<bool> IsValidBranchNameAsync(string repoPath, string name, CancellationToken ct = default);

    /// <summary>Checks out the commit of <paramref name="commitish"/> without a branch (detached HEAD).</summary>
    Task DetachAsync(string repoPath, string commitish, CancellationToken ct = default);

    /// <summary>
    /// Deletes the local branch <paramref name="name"/>. Without <paramref name="force"/>, a branch that is not
    /// fully merged is kept and <see cref="GitBranchDeleteResult.NotFullyMerged"/> is returned.
    /// </summary>
    Task<GitBranchDeleteResult> DeleteAsync(string repoPath, string name, bool force, CancellationToken ct = default);
}
