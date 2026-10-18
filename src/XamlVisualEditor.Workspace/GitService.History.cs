using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService
{
    /// <inheritdoc />
    /// <remarks>
    /// The author and message filters are fixed strings, not patterns, and ignore case; both must match. Signatures
    /// are never verified, and <c>--no-follow</c> keeps <c>log.follow</c> from following a folder. Git applies
    /// <c>--skip</c> before it follows renames, so the skip would count other commits; a followed history is
    /// therefore read from its start, and the skipped commits are dropped here.
    /// </remarks>
    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfNegative(query.Skip);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(query.MaxCount);

        bool hasPath = !string.IsNullOrEmpty(query.Path);
        bool follow = hasPath && query.Follow;
        List<string> args = new() { "log", "--format=" + GitLogParser.LOG_FORMAT, "-z", "--encoding=UTF-8", "--no-show-signature" };
        int maxCount = query.MaxCount;
        if (follow)
        {
            maxCount = (int)Math.Min((long)query.Skip + query.MaxCount, int.MaxValue);
        }
        else if (query.Skip > 0)
        {
            args.Add("--skip=" + query.Skip.ToString(CultureInfo.InvariantCulture));
        }

        args.Add("--max-count=" + maxCount.ToString(CultureInfo.InvariantCulture));
        bool hasAuthor = !string.IsNullOrWhiteSpace(query.Author);
        bool hasMessage = !string.IsNullOrWhiteSpace(query.MessageText);
        if (hasAuthor || hasMessage)
        {
            args.Add("--fixed-strings");
            args.Add("--regexp-ignore-case");
        }

        if (hasAuthor)
        {
            args.Add("--author=" + query.Author!.Trim());
        }

        if (hasMessage)
        {
            args.Add("--grep=" + query.MessageText!.Trim());
        }

        args.Add(follow ? "--follow" : "--no-follow");
        args.Add("--end-of-options");
        if (!string.IsNullOrWhiteSpace(query.Revision))
        {
            args.Add(query.Revision);
        }

        args.Add("--");
        if (hasPath)
        {
            args.Add(query.Path!);
        }

        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        if (result.ExitCode != 0)
        {
            // git log fails on a branch without commits; that history is empty.
            if (string.IsNullOrWhiteSpace(query.Revision) && !await HasHeadCommitAsync(repoPath, ct))
            {
                return Array.Empty<GitCommitInfo>();
            }

            ThrowIfFailed(result, "log");
        }

        IReadOnlyList<GitCommitInfo> commits = GitLogParser.ParseLog(result.StandardOutput);
        return follow && query.Skip > 0
            ? commits.Skip(query.Skip).ToArray()
            : commits;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>git rev-parse --verify</c> turns the revision into one object id, fails for a range or an unknown name and
    /// prints an excluded revision such as <c>^HEAD</c> with a leading <c>^</c>; <c>&lt;id&gt;^{commit}</c>, appended to
    /// the id so that <c>:/&lt;text&gt;</c> keeps its meaning, peels a tag and fails for a tree or a blob; <c>git log</c>
    /// reads the commit. Exit code 1 of <c>--verify --quiet</c> means no single object; any other failure, such as a
    /// folder that is not a repository, keeps git's message. A revision that starts with <c>-</c> is rejected before
    /// git runs, because <c>rev-parse</c> takes <c>--end-of-options</c> only from git 2.30 on.
    /// </remarks>
    public async Task<GitCommitInfo> ResolveCommitAsync(string repoPath, string revision, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision.StartsWith('-'))
        {
            throw NotOneCommit();
        }

        string commitId = await VerifyAsync(await VerifyAsync(revision) + "^{commit}");
        return (await GetLogAsync(repoPath, new GitLogQuery { Revision = commitId, MaxCount = 1 }, ct)).Single();

        async Task<string> VerifyAsync(string name)
        {
            GitProcessResult result = await RunGitAsync(
                repoPath,
                new[] { "rev-parse", "--verify", "--quiet", "--end-of-options", name },
                GitCommandAccess.Read,
                ct,
                allowNonZeroExitCode: true);
            if (result.ExitCode != 1)
            {
                ThrowIfFailed(result, "rev-parse");
            }

            string id = result.StandardOutput.Trim();
            return result.ExitCode == 0 && id.Length > 0 && id.All(char.IsAsciiHexDigit) ? id : throw NotOneCommit();
        }

        InvalidOperationException NotOneCommit() =>
            new(string.Format(CultureInfo.CurrentCulture, UiStrings.GitRevisionNotOneCommitFormat, revision));
    }

    /// <inheritdoc />
    /// <remarks>A merge commit is compared with its first parent, a root commit with the empty tree.</remarks>
    public async Task<GitCommitDetails> GetCommitAsync(string repoPath, GitCommitInfo commit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);

        List<string> args = new() { "diff-tree", "-r", "-z", "--name-status", "-M" };
        AddCommitTrees(args, commit);
        GitProcessResult treeResult = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        ThrowIfFailed(treeResult, "diff-tree");
        return new GitCommitDetails
        {
            Commit = commit,
            Files = GitLogParser.ParseNameStatus(treeResult.StandardOutput)
        };
    }

    /// <inheritdoc />
    public async Task<GitDiff> GetCommitFileDiffAsync(
        string repoPath,
        GitCommitInfo commit,
        GitCommitFileChange file,
        int maxLineCount = int.MaxValue,
        int maxByteCount = int.MaxValue,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(commit);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLineCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxByteCount);

        List<string> args = new() { "diff-tree", "-p", "-M", "--no-color", "--unified=3" };
        AddCommitTrees(args, commit);
        args.Add("--");
        args.Add(file.Path);

        // Rename detection needs both paths of a renamed file.
        if (!string.IsNullOrEmpty(file.OldPath) && !string.Equals(file.OldPath, file.Path, StringComparison.Ordinal))
        {
            args.Add(file.OldPath);
        }

        GitProcessResult result = await RunGitAsync(
            repoPath,
            args,
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true,
            maxOutputLines: maxLineCount == int.MaxValue ? null : maxLineCount,
            maxOutputBytes: maxByteCount == int.MaxValue ? null : maxByteCount);
        if (!result.IsOutputTruncated)
        {
            ThrowIfFailed(result, "diff-tree");
            return GitDiffParser.ParseUnifiedDiff(result.StandardOutput);
        }

        GitDiff diff = GitDiffParser.ParseUnifiedDiff(result.StandardOutput);
        return new GitDiff { Files = diff.Files, RawText = diff.RawText, IsTruncated = true };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken ct = default)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "for-each-ref", "--sort=-creatordate", "--format=" + GitRefParser.TAG_FORMAT, "refs/tags" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        ThrowIfFailed(result, "for-each-ref");
        return GitRefParser.ParseTags(result.StandardOutput);
    }

    /// <summary>
    /// Adds the trees diff-tree compares for <paramref name="commit"/>: its first parent and the commit, or the commit
    /// alone with <c>--root</c> for a root commit.
    /// </summary>
    private static void AddCommitTrees(List<string> args, GitCommitInfo commit)
    {
        if (commit.ParentShas.Count == 0)
        {
            args.Add("--root");
            args.Add("--no-commit-id");
            args.Add("--end-of-options");
            args.Add(commit.Sha);
            return;
        }

        args.Add("--end-of-options");
        args.Add(commit.ParentShas[0]);
        args.Add(commit.Sha);
    }

    private async Task<bool> HasHeadCommitAsync(string repoPath, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "rev-parse", "--verify", "--quiet", "HEAD^{commit}" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        return result.ExitCode == 0;
    }
}
