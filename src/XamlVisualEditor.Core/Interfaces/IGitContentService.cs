using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>Reads file contents from the index or HEAD and the line authorship of working files.</summary>
public interface IGitContentService
{
    /// <summary>
    /// Returns the bytes of the file at the repository-relative <paramref name="path"/> in <paramref name="revision"/>,
    /// at most <paramref name="maxByteCount"/> of them; null when the revision has no such file, for example for an
    /// untracked file, a file with an unresolved conflict in the index, or a branch without commits.
    /// </summary>
    Task<GitFileContent?> GetFileContentAsync(
        string repoPath,
        string path,
        GitFileRevision revision,
        int maxByteCount,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the commit that last changed each line of the saved working file at the repository-relative
    /// <paramref name="path"/>; lines changed since the last commit belong to a commit with an id of zeros. At most
    /// <paramref name="maxByteCount"/> bytes of git's output are read. Throws <see cref="System.InvalidOperationException"/>
    /// with git's error output when git fails, for example for an untracked file.
    /// </summary>
    Task<GitBlame> GetBlameAsync(string repoPath, string path, int maxByteCount, CancellationToken ct = default);
}
