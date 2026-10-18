using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Reads the commit history, the details and file diffs of commits and the tags of a repository. Every method throws
/// <see cref="System.InvalidOperationException"/> with git's error output when git fails.
/// </summary>
public interface IGitHistoryService
{
    /// <summary>
    /// Returns the commits of <paramref name="query"/>, newest first. A repository without commits has an empty
    /// history.
    /// </summary>
    Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken ct = default);

    /// <summary>
    /// Returns <paramref name="commit"/>, as <see cref="GetLogAsync"/> listed it, with the files it changed compared
    /// with its first parent.
    /// </summary>
    Task<GitCommitDetails> GetCommitAsync(string repoPath, GitCommitInfo commit, CancellationToken ct = default);

    /// <summary>
    /// Returns the commit that <paramref name="revision"/>, a commit id, branch, tag or other expression of one commit,
    /// names now, with its full and abbreviated id, so later reads can start at that commit even when the branch moves.
    /// A revision that does not name one commit, such as a range or a tag of a tree or a blob, fails with an
    /// <see cref="System.InvalidOperationException"/>.
    /// </summary>
    Task<GitCommitInfo> ResolveCommitAsync(string repoPath, string revision, CancellationToken ct = default);

    /// <summary>
    /// Returns the diff of one file of <paramref name="commit"/> against its first parent; for a renamed or copied
    /// file, against its old path. At most <paramref name="maxLineCount"/> lines and <paramref name="maxByteCount"/>
    /// bytes of git's output are read; when git writes more, it is stopped, the diff ends with the last complete line
    /// within both limits and has <see cref="GitDiff.IsTruncated"/>.
    /// </summary>
    Task<GitDiff> GetCommitFileDiffAsync(
        string repoPath,
        GitCommitInfo commit,
        GitCommitFileChange file,
        int maxLineCount = int.MaxValue,
        int maxByteCount = int.MaxValue,
        CancellationToken ct = default);

    /// <summary>Returns the tags, newest first.</summary>
    Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken ct = default);

    /// <summary>
    /// Returns the full id of the best common ancestor of the revisions <paramref name="first"/> and
    /// <paramref name="second"/>, from which both went their own way, or null when they have none.
    /// </summary>
    Task<string?> GetMergeBaseAsync(string repoPath, string first, string second, CancellationToken ct = default);
}
