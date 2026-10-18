using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Tests.UI;

/// <summary>
/// A repository on branch main with a scripted history: the log starts at the commit whose id is the revision or that
/// the name of <see cref="Refs"/> points to, or at the first commit, filters <see cref="Commits"/> by author and message text and pages them, records every query and
/// can hold a query until a test releases it. The workspace <c>test-workspace</c> is the repository <c>test-repo</c>;
/// any other workspace path is a repository of that name.
/// </summary>
internal sealed class ScriptedHistoryGitService : IGitService, IGitHistoryService
{
    private readonly object _gate = new();
    private readonly List<(GitLogQuery Query, CancellationToken Token)> _queries = new();
    private int _commitReads;
    private int _diffReads;
    private int _resolves;

    /// <summary>Gets the commits of the repository, newest first.</summary>
    public List<GitCommitInfo> Commits { get; } = new();

    /// <summary>Gets the branches and tags by name with the id of the commit they point to.</summary>
    public Dictionary<string, string> Refs { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets the files of a commit by id; a commit without entry changed <c>a.txt</c>.</summary>
    public Dictionary<string, GitCommitFileChange[]> Files { get; } = new(StringComparer.Ordinal);

    /// <summary>Gets or sets the diff text of every file.</summary>
    public string DiffText { get; set; } =
        "diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -1,2 +1,2 @@\n-one\n+eins\n two\n@@ -9 +9 @@\n-nine\n+neun\n";

    /// <summary>Gets the tags.</summary>
    public List<GitTag> Tags { get; } = new();

    /// <summary>Gets or sets the HEAD commit id of the status.</summary>
    public string HeadSha { get; set; } = "head-1";

    /// <summary>Gets or sets a task the next log query waits for before it answers; it is used once.</summary>
    public Task? HoldNextQuery { get; set; }

    public IReadOnlyList<(GitLogQuery Query, CancellationToken Token)> Queries
    {
        get
        {
            lock (_gate)
            {
                return _queries.ToArray();
            }
        }
    }

    public int CommitReads => Volatile.Read(ref _commitReads);

    public int DiffReads => Volatile.Read(ref _diffReads);

    public int Resolves => Volatile.Read(ref _resolves);

    /// <summary>Gets the line and byte limits of the last diff read.</summary>
    public (int Lines, int Bytes) LastDiffLimits { get; private set; }

    /// <summary>Creates <paramref name="count"/> commits, newest first, alternately by Anna and Bob.</summary>
    public static IEnumerable<GitCommitInfo> CreateCommits(int count, int firstNumber = 1)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            int number = firstNumber + i;
            yield return CreateCommit(number, number % 2 == 0 ? "Bob" : "Anna");
        }
    }

    public static GitCommitInfo CreateCommit(int number, string author, params string[] parents)
    {
        string sha = number.ToString("x40", CultureInfo.InvariantCulture);
        return new GitCommitInfo
        {
            Sha = sha,
            ShortSha = sha[^7..],
            ParentShas = parents,
            ParentShortShas = parents.Select(parent => parent[^7..]).ToArray(),
            AuthorName = author,
            AuthorEmail = author.ToLowerInvariant() + "@example.invalid",
            CommitterName = author,
            AuthorDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(number),
            CommitterDate = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(number),
            Subject = "Commit " + number,
            Message = "Commit " + number + "\n\nBody " + number
        };
    }

    public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) =>
        Task.FromResult<string?>(path == "test-workspace" ? "test-repo" : path);

    public Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default) =>
        Task.FromResult(new GitRepositoryStatus
        {
            RepositoryRoot = repoPath,
            BranchName = "main",
            HeadSha = HeadSha,
            Changes = Array.Empty<GitFileChange>()
        });

    public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
        Task.FromResult(new GitDiff { Files = Array.Empty<GitDiffFile>() });

    public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

    public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) => throw new NotSupportedException();

    public async Task<IReadOnlyList<GitCommitInfo>> GetLogAsync(string repoPath, GitLogQuery query, CancellationToken ct = default)
    {
        Task? hold;
        lock (_gate)
        {
            _queries.Add((query, ct));
            hold = HoldNextQuery;
            HoldNextQuery = null;
        }

        if (hold is not null)
        {
            await hold;
        }

        string? revision = query.Revision is { } name && Refs.TryGetValue(name, out string? target) ? target : query.Revision;
        int start = revision is null ? -1 : Commits.FindIndex(commit => commit.Sha == revision);
        return Commits
            .Skip(Math.Max(0, start))
            .Where(commit => query.Author is null || commit.AuthorName.Contains(query.Author, StringComparison.OrdinalIgnoreCase))
            .Where(commit => query.MessageText is null || commit.Message.Contains(query.MessageText, StringComparison.OrdinalIgnoreCase))
            .Skip(query.Skip)
            .Take(query.MaxCount)
            .ToArray();
    }

    public Task<GitCommitDetails> GetCommitAsync(string repoPath, GitCommitInfo commit, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _commitReads);
        GitCommitFileChange[] files = Files.TryGetValue(commit.Sha, out GitCommitFileChange[]? scripted)
            ? scripted
            : [new GitCommitFileChange { Path = "a.txt", Kind = GitChangeKind.Modified }];
        return Task.FromResult(new GitCommitDetails { Commit = commit, Files = files });
    }

    /// <summary>
    /// Returns the commit a name of <see cref="Refs"/> points to, or the commit whose id starts with
    /// <paramref name="revision"/>; fails like git for any other revision.
    /// </summary>
    public Task<GitCommitInfo> ResolveCommitAsync(string repoPath, string revision, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _resolves);
        string sha = Refs.TryGetValue(revision, out string? target) ? target : revision;
        GitCommitInfo? commit = Commits.Find(candidate => candidate.Sha.StartsWith(sha, StringComparison.OrdinalIgnoreCase));
        return commit is not null
            ? Task.FromResult(commit)
            : Task.FromException<GitCommitInfo>(new InvalidOperationException($"fatal: bad revision '{revision}'"));
    }

    /// <summary>Returns <see cref="DiffText"/>, cut after <paramref name="maxLineCount"/> lines as git's output would be.</summary>
    public Task<GitDiff> GetCommitFileDiffAsync(
        string repoPath,
        GitCommitInfo commit,
        GitCommitFileChange file,
        int maxLineCount = int.MaxValue,
        int maxByteCount = int.MaxValue,
        CancellationToken ct = default)
    {
        Interlocked.Increment(ref _diffReads);
        LastDiffLimits = (maxLineCount, maxByteCount);
        string[] lines = DiffText.TrimEnd('\n').Split('\n');
        if (lines.Length <= maxLineCount)
        {
            return Task.FromResult(GitDiffParser.ParseUnifiedDiff(DiffText));
        }

        GitDiff cut = GitDiffParser.ParseUnifiedDiff(string.Join('\n', lines.Take(maxLineCount)) + "\n");
        return Task.FromResult(new GitDiff { Files = cut.Files, RawText = cut.RawText, IsTruncated = true });
    }

    public Task<IReadOnlyList<GitTag>> GetTagsAsync(string repoPath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GitTag>>(Tags.ToArray());

    public Task<string?> GetMergeBaseAsync(string repoPath, string first, string second, CancellationToken ct = default) =>
        throw new NotSupportedException();
}

/// <summary>A workspace that is always open at the same path.</summary>
internal sealed class FixedWorkspaceInfo : IWorkspaceInfo
{
    public string? WorkspacePath => "test-workspace";

    public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
    {
        add { }
        remove { }
    }
}

/// <summary>A workspace that opens at <c>test-workspace</c> and switches to another path on request.</summary>
internal sealed class SwitchableWorkspaceInfo : IWorkspaceInfo
{
    public string? WorkspacePath { get; private set; } = "test-workspace";

    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;

    public void Open(string path)
    {
        WorkspacePath = path;
        WorkspaceChanged?.Invoke(this, new WorkspaceChangedEventArgs(path));
    }
}
