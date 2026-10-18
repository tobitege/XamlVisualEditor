using System;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService : IGitContentService
{
    /// <inheritdoc />
    /// <remarks>
    /// The index is read as stage 0, <c>:0:&lt;path&gt;</c>, so a path that starts with a digit and a colon is not taken
    /// for a stage number. Git fails for a missing file; that failure, which also appears in the command log, yields null.
    /// </remarks>
    public async Task<GitFileContent?> GetFileContentAsync(
        string repoPath,
        string path,
        GitFileRevision revision,
        int maxByteCount,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxByteCount);
        string objectName = (revision == GitFileRevision.Head ? "HEAD:" : ":0:") + path;
        GitProcessResult result = await RunGitAsync(
            repoPath,
            ["cat-file", "blob", objectName],
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true,
            maxOutputBytes: maxByteCount);
        if (result.IsOutputTruncated)
        {
            return new GitFileContent(result.StandardOutputBytes, IsTruncated: true);
        }

        return result.ExitCode == 0 ? new GitFileContent(result.StandardOutputBytes, IsTruncated: false) : null;
    }

    /// <inheritdoc />
    public async Task<GitBlame> GetBlameAsync(string repoPath, string path, int maxByteCount, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentOutOfRangeException.ThrowIfNegative(maxByteCount);
        string[] args = ["blame", "--porcelain", "--", path];
        GitProcessResult result = await RunGitAsync(
            repoPath,
            args,
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true,
            maxOutputBytes: maxByteCount);
        if (!result.IsOutputTruncated)
        {
            ThrowIfFailed(result, args[0]);
        }

        return GitBlameParser.Parse(result.StandardOutput, result.IsOutputTruncated);
    }
}
