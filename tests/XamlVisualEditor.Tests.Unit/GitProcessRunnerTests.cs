using System.Diagnostics;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class GitProcessRunnerTests
{
    private const string UMLAUT_FILE = "Größe ä.xaml";
    private const string SPACE_FILE = "a b.txt";

    [Fact]
    public async Task Stages_files_with_umlauts_and_spaces_by_the_paths_the_status_reports()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("tracked.txt");
        repository.WriteFile(UMLAUT_FILE, "<Grid />\n");
        repository.WriteFile(SPACE_FILE, "text\n");
        GitService service = new(options: repository.ProcessOptions);

        GitRepositoryStatus before = await service.GetStatusAsync(repository.Path, ct);
        GitFileChange umlaut = Assert.Single(before.Changes, change => change.Path == UMLAUT_FILE);
        GitFileChange space = Assert.Single(before.Changes, change => change.Path == SPACE_FILE);
        Assert.True(umlaut.IsUntracked);
        Assert.True(space.IsUntracked);

        await service.StageAsync(repository.Path, new[] { umlaut.Path, space.Path }, ct);

        GitRepositoryStatus after = await service.GetStatusAsync(repository.Path, ct);
        Assert.Equal(2, after.Changes.Count);
        foreach (string path in new[] { UMLAUT_FILE, SPACE_FILE })
        {
            GitFileChange staged = Assert.Single(after.Changes, change => change.Path == path);
            Assert.Equal(GitChangeKind.Added, staged.IndexStatus);
            Assert.Equal(GitChangeKind.None, staged.WorkTreeStatus);
            Assert.False(staged.IsUntracked);
        }
    }

    [Fact]
    public async Task Commit_passes_a_message_with_umlauts_through_standard_input()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("tracked.txt");
        repository.WriteFile(UMLAUT_FILE, "<Grid />\n");
        GitService service = new(options: repository.ProcessOptions);
        await service.StageAsync(repository.Path, new[] { UMLAUT_FILE }, ct);
        const string message = "Größe geändert: äöü ß €\n\n-Zweite Zeile mit Ä, Ö und Ü";

        await service.CommitAsync(repository.Path, message, ct);

        Assert.Equal(message, repository.RunGit("log", "-1", "--format=%B").TrimEnd('\n'));
        GitRepositoryStatus status = await service.GetStatusAsync(repository.Path, ct);
        Assert.Empty(status.Changes);
        Assert.Equal(repository.RunGit("rev-parse", "HEAD").Trim(), status.HeadSha);
    }

    [Fact]
    public async Task Diff_text_keeps_umlauts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create(UMLAUT_FILE);
        repository.WriteFile(UMLAUT_FILE, "Größe ä\n");
        GitService service = new(options: repository.ProcessOptions);

        GitDiff diff = await service.GetDiffAsync(repository.Path, new GitDiffRequest { Path = UMLAUT_FILE }, ct);

        GitDiffFile file = Assert.Single(diff.Files);
        Assert.Equal(UMLAUT_FILE, file.Path);
        Assert.Contains(file.Hunks.SelectMany(hunk => hunk.Lines), line => line.Kind == GitDiffLineKind.Added && line.Text == "Größe ä");
    }

    [Fact]
    public async Task Status_reports_an_unborn_branch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.WriteFile("new.txt", "new\n");

        GitRepositoryStatus status = await new GitService(options: repository.ProcessOptions).GetStatusAsync(repository.Path, ct);

        Assert.True(status.IsUnborn);
        Assert.Null(status.HeadSha);
        Assert.Equal("main", status.BranchName);
        GitFileChange untracked = Assert.Single(status.Changes);
        Assert.Equal("new.txt", untracked.Path);
    }

    [Fact]
    public async Task Status_reports_a_merge_conflict_and_the_merge_in_progress()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create(UMLAUT_FILE);
        repository.RunGit("checkout", "-q", "-b", "other");
        repository.WriteFile(UMLAUT_FILE, "other\n");
        repository.RunGit("commit", "-q", "-a", "-m", "other");
        repository.RunGit("checkout", "-q", "main");
        repository.WriteFile(UMLAUT_FILE, "main\n");
        repository.RunGit("commit", "-q", "-a", "-m", "main");
        Assert.False(repository.TryRunGit(out _, "merge", "other"));

        GitRepositoryStatus status = await new GitService(options: repository.ProcessOptions).GetStatusAsync(repository.Path, ct);

        Assert.Equal(GitRepositoryOperation.Merging, status.Operation);
        Assert.Equal("main", status.BranchName);
        GitFileChange conflict = Assert.Single(status.Changes);
        Assert.Equal(UMLAUT_FILE, conflict.Path);
        Assert.Equal(GitConflictKind.BothModified, conflict.ConflictKind);
        Assert.True(conflict.IsConflicted);
    }

    [Fact]
    public async Task Cancellation_kills_a_process_that_waits_for_input()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        GitProcessRunner runner = new(repository.ProcessOptions);
        TaskCompletionSource<Process> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using UnendingInputStream input = new();

        Task<GitProcessResult> run = runner.RunAsync(
            new GitProcessRequest
            {
                WorkingDirectory = repository.Path,
                Arguments = new[] { "cat-file", "--batch" },
                IsReadOnly = true,
                StandardInput = input,
                ProcessStarted = id => started.SetResult(Process.GetProcessById(id))
            },
            cancellation.Token);
        using Process process = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(process.HasExited);
    }

    /// <summary>
    /// A readable stream whose reads wait until they are cancelled, like a pipe whose writer stays open.
    /// </summary>
    private sealed class UnendingInputStream : Stream
    {
        private readonly TaskCompletionSource _never = new();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _never.Task.WaitAsync(cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
