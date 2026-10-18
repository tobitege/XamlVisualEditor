using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Lists, creates, applies and removes stashes. The methods throw <see cref="System.InvalidOperationException"/> with
/// git's output when git fails, for example when applying a stash causes conflicts.
/// </summary>
public interface IGitStashService
{
    /// <summary>Returns the stashes, newest first.</summary>
    Task<IReadOnlyList<GitStashEntry>> ListAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Stashes the staged and unstaged changes of the tracked files and, with <paramref name="includeUntracked"/>, the
    /// untracked files; <paramref name="message"/> null or empty uses git's default message.
    /// </summary>
    Task PushAsync(string repoPath, string? message, bool includeUntracked, CancellationToken ct = default);

    /// <summary>
    /// Stashes the staged and unstaged changes of <paramref name="paths"/>, repository-relative files or folders,
    /// including the untracked files among them; the other changes stay.
    /// </summary>
    Task PushPathsAsync(string repoPath, string? message, IReadOnlyList<string> paths, CancellationToken ct = default);

    /// <summary>
    /// Stashes only the staged changes; the unstaged changes and the untracked files stay. Needs Git 2.35 or newer.
    /// </summary>
    Task PushStagedAsync(string repoPath, string? message, CancellationToken ct = default);

    /// <summary>Applies the changes of <paramref name="stash"/> to the working tree and keeps the stash.</summary>
    Task ApplyAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="stash"/> and removes it when it applied without conflict. Returns false and changes
    /// nothing when the stash is no longer at its position in the list.
    /// </summary>
    Task<bool> PopAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default);

    /// <summary>Removes <paramref name="stash"/>. Returns false and changes nothing when the stash is no longer at its position in the list.</summary>
    Task<bool> DropAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default);

    /// <summary>
    /// Creates the branch <paramref name="branchName"/> at the commit <paramref name="stash"/> was made on, switches
    /// to it, applies the stash there and removes it when it applied without conflict. Returns false and changes
    /// nothing when the stash is no longer at its position in the list.
    /// </summary>
    Task<bool> BranchAsync(string repoPath, GitStashEntry stash, string branchName, CancellationToken ct = default);
}
