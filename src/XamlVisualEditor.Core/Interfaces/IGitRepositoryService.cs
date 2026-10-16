using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Repository-level git operations: the git directory, the configured remotes and creating a repository.
/// </summary>
public interface IGitRepositoryService
{
    /// <summary>
    /// Returns the absolute git directory of the repository, which is a folder outside <c>.git</c> for linked
    /// worktrees, or null when <paramref name="repoPath"/> is not inside a repository.
    /// </summary>
    Task<string?> GetGitDirectoryAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Returns the names of the configured remotes; empty when there is none.
    /// </summary>
    Task<IReadOnlyList<string>> GetRemoteNamesAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Creates an empty repository in the existing folder <paramref name="path"/>. Throws
    /// <see cref="System.InvalidOperationException"/> with git's error output when git fails.
    /// </summary>
    Task InitAsync(string path, CancellationToken ct = default);
}
