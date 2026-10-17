using System;
using System.IO;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
using GitExtensionEntry = XamlVisualEditor.GitExtension.GitExtension;
using TestExtensionStorage = XamlVisualEditor.Tests.Unit.Tfvc.InMemoryExtensionStorage;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitSessionTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Refresh_publishes_the_repository_branch_and_changes()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("a.txt", "changed\n");
        repository.WriteFile("b.txt", "new\n");
        using GitSession session = CreateSession(repository, out _);

        await session.RefreshAsync().WaitAsync(WaitLimit);

        GitSnapshot snapshot = session.Current;
        Assert.Equal(GitSessionState.Repository, snapshot.State);
        Assert.True(snapshot.IsRepository);
        Assert.Equal("main", snapshot.BranchName);
        Assert.Equal(2, snapshot.ChangeCount);
        Assert.False(snapshot.HasRemote);
        Assert.False(snapshot.HasUpstream);
        Assert.NotNull(snapshot.GitDirectory);
        Assert.True(FileSystemPathComparison.Equals(
            Path.GetFullPath(Path.Combine(repository.Path, ".git")),
            Path.GetFullPath(snapshot.GitDirectory!)));

        repository.AddBareRemote();
        await session.RefreshAsync().WaitAsync(WaitLimit);

        Assert.True(session.Current.HasRemote);
    }

    [Fact]
    public async Task Refresh_requests_during_a_running_refresh_start_exactly_one_more_refresh()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GatedGitService git = new(new GitService(options: repository.ProcessOptions));
        using GitSession session = new(GitServiceSet.FromService(git), new FakeWorkspaceInfo { WorkspacePath = repository.Path });

        Task first = session.RefreshAsync();
        await git.FirstStatusStarted.Task.WaitAsync(WaitLimit);
        Task second = session.RefreshAsync();
        Task third = session.RefreshAsync();

        Assert.Same(second, third);
        Assert.Equal(1, git.StatusCalls);
        git.ReleaseFirstStatus.SetResult();
        await Task.WhenAll(first, second, third).WaitAsync(WaitLimit);

        Assert.Equal(2, git.StatusCalls);
        Assert.Equal(1, git.MaxConcurrentStatusCalls);
        Assert.Equal(1, git.RootCalls);
    }

    [Fact]
    public async Task Git_directory_watcher_sees_a_branch_switch_made_outside_the_editor()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using GitSession session = CreateSession(repository, out _);
        await session.StartAsync().WaitAsync(WaitLimit);
        Assert.Equal("main", session.Current.BranchName);
        Assert.False(session.Current.IsWorkingTreeWatcherEnabled);

        Task<GitSnapshot> switched = session.Snapshots
            .FirstAsync(snapshot => snapshot.BranchName == "feature")
            .Timeout(WaitLimit)
            .ToTask();
        repository.RunGit("switch", "-q", "-c", "feature");

        GitSnapshot snapshot = await switched;
        Assert.True(snapshot.IsRepository);
    }

    [Fact]
    public async Task Working_tree_watcher_refreshes_after_a_file_change_and_its_switch_is_saved()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using GitSession session = CreateSession(repository, out TestExtensionStorage storage);
        await session.StartAsync().WaitAsync(WaitLimit);

        await session.SetWorkingTreeWatcherEnabledAsync(true).WaitAsync(WaitLimit);
        Assert.True(session.Current.IsWorkingTreeWatcherEnabled);
        Assert.True(await storage.GetAsync<bool?>(GitSettingsStore.AUTO_REFRESH_KEY, TestContext.Current.CancellationToken));

        Task<GitSnapshot> changed = session.Snapshots
            .FirstAsync(snapshot => snapshot.Changes.Any(change => change.Path == "new.txt"))
            .Timeout(WaitLimit)
            .ToTask();
        repository.WriteFile("new.txt", "new\n");

        GitSnapshot snapshot = await changed;
        Assert.Equal(1, snapshot.ChangeCount);

        await session.SetWorkingTreeWatcherEnabledAsync(false).WaitAsync(WaitLimit);
        Assert.False(session.Current.IsWorkingTreeWatcherEnabled);
        Assert.False(await storage.GetAsync<bool?>(GitSettingsStore.AUTO_REFRESH_KEY, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Saved_working_tree_watcher_switch_is_loaded_on_start()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        TestExtensionStorage storage = new();
        await storage.SetAsync(GitSettingsStore.AUTO_REFRESH_KEY, true, TestContext.Current.CancellationToken);
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FakeWorkspaceInfo { WorkspacePath = repository.Path },
            new GitSettingsStore(storage));

        await session.StartAsync().WaitAsync(WaitLimit);

        Assert.True(session.Current.IsWorkingTreeWatcherEnabled);
    }

    [Fact]
    public async Task Refresh_does_not_rewrite_the_index()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string indexPath = Path.Combine(repository.Path, ".git", "index");
        // A newer modification time with the same content makes a plain "git status" refresh the index.
        File.SetLastWriteTimeUtc(Path.Combine(repository.Path, "a.txt"), DateTime.UtcNow.AddMinutes(1));
        byte[] indexBefore = File.ReadAllBytes(indexPath);
        DateTime writtenBefore = File.GetLastWriteTimeUtc(indexPath);
        using GitSession session = CreateSession(repository, out _);

        await session.RefreshAsync().WaitAsync(WaitLimit);
        await session.RefreshAsync().WaitAsync(WaitLimit);

        Assert.Equal(0, session.Current.ChangeCount);
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(indexPath));
        Assert.Equal(indexBefore, File.ReadAllBytes(indexPath));
    }

    [Fact]
    public async Task RunAsync_refreshes_after_the_command_also_when_git_fails()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("a.txt", "changed\n");
        using GitSession session = CreateSession(repository, out _);
        await session.RefreshAsync().WaitAsync(WaitLimit);
        GitFileChange unstaged = Assert.Single(session.Current.Changes);
        Assert.Equal(GitChangeKind.None, unstaged.IndexStatus);

        await session.RunAsync((git, root, ct) => git.StageAsync(root, ["a.txt"], ct)).WaitAsync(WaitLimit);

        GitFileChange staged = Assert.Single(session.Current.Changes);
        Assert.Equal(GitChangeKind.Modified, staged.IndexStatus);

        repository.WriteFile("a.txt", "changed again\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RunAsync((git, root, ct) => git.StageAsync(root, ["missing.txt"], ct)));
        Assert.Equal(GitChangeKind.Modified, Assert.Single(session.Current.Changes).WorkTreeStatus);
    }

    [Fact]
    public async Task Session_reports_no_workspace_and_a_folder_outside_a_repository_and_initializes_it()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        string folder = Path.Combine(Path.GetDirectoryName(repository.Path)!, "plain");
        Directory.CreateDirectory(folder);
        FakeWorkspaceInfo workspace = new();
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            workspace);
        await session.StartAsync().WaitAsync(WaitLimit);
        Assert.Equal(GitSessionState.NoWorkspace, session.Current.State);

        Task<GitSnapshot> notRepository = session.Snapshots
            .FirstAsync(snapshot => snapshot.State == GitSessionState.NotRepository)
            .Timeout(WaitLimit)
            .ToTask();
        workspace.Change(folder);
        GitSnapshot snapshot = await notRepository;
        Assert.True(snapshot.IsGitAvailable);
        Assert.Equal(UiStrings.WorkspaceNotGitRepository, snapshot.ErrorMessage);

        GitInitializeResult initialized = await session.InitializeRepositoryAsync().WaitAsync(WaitLimit);

        Assert.Equal(folder, initialized.Folder);
        Assert.True(initialized.Created);
        Assert.True(session.Current.IsRepository);
        Assert.True(Directory.Exists(Path.Combine(folder, ".git")));
    }

    [Fact]
    public async Task A_failing_status_keeps_the_repository_and_its_watchers_and_recovers()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using GitSession session = CreateSession(repository, out _);
        await session.StartAsync().WaitAsync(WaitLimit);
        Assert.Equal(GitSessionState.Repository, session.Current.State);
        string indexPath = Path.Combine(repository.Path, ".git", "index");
        byte[] index = File.ReadAllBytes(indexPath);

        Task<GitSnapshot> failed = session.Snapshots
            .FirstAsync(snapshot => snapshot.State == GitSessionState.StatusFailed)
            .Timeout(WaitLimit)
            .ToTask();
        File.WriteAllBytes(indexPath, index[..20]);
        GitSnapshot failure = await failed;

        Assert.True(failure.IsRepository);
        Assert.Null(failure.Status);
        Assert.False(string.IsNullOrWhiteSpace(failure.ErrorMessage));
        Assert.True(FileSystemPathComparison.Equals(Path.GetFullPath(repository.Path), Path.GetFullPath(failure.RepositoryRoot!)));
        ContextKeyRegistry contextKeys = new();
        GitExtensionEntry.UpdateContextKeys(contextKeys, failure);
        Assert.True(contextKeys.TryGet(GitCommands.IS_REPOSITORY_CONTEXT_KEY, out bool isRepository) && isRepository);

        // Only the git directory watcher can notice the repaired index.
        Task<GitSnapshot> recovered = session.Snapshots
            .FirstAsync(snapshot => snapshot.State == GitSessionState.Repository)
            .Timeout(WaitLimit)
            .ToTask();
        File.WriteAllBytes(indexPath, index);
        GitSnapshot snapshot = await recovered;

        Assert.Null(snapshot.ErrorMessage);
        Assert.Equal("main", snapshot.BranchName);
    }

    [Fact]
    public async Task Change_requests_that_keep_coming_still_refresh_once_per_interval()
    {
        HistoricalScheduler scheduler = new();
        FakeGitService git = new("repo-root");
        using GitSession session = new(
            GitServiceSet.FromService(git),
            new FakeWorkspaceInfo { WorkspacePath = "repo-root" },
            scheduler: scheduler);
        await session.RefreshAsync().WaitAsync(WaitLimit);
        int started = 0;
        using IDisposable subscription = session.IsRefreshing
            .Where(isRefreshing => isRefreshing)
            .Subscribe(_ => Interlocked.Increment(ref started));

        // A request every 100 ms, as from a file that is written all the time.
        TimeSpan step = TimeSpan.FromMilliseconds(100);
        for (TimeSpan elapsed = TimeSpan.Zero; elapsed < GitSession.RefreshDelay; elapsed += step)
        {
            Assert.Equal(0, Volatile.Read(ref started));
            session.RequestRefresh();
            scheduler.AdvanceBy(step);
        }

        Assert.Equal(1, Volatile.Read(ref started));
        await session.IsRefreshing.FirstAsync(isRefreshing => !isRefreshing).Timeout(WaitLimit).ToTask();
        Assert.Equal(2, git.StatusCalls);

        // Without requests no further refresh starts.
        scheduler.AdvanceBy(GitSession.RefreshDelay * 4);
        Assert.Equal(1, Volatile.Read(ref started));

        session.RequestRefresh();
        scheduler.AdvanceBy(GitSession.RefreshDelay);
        Assert.Equal(2, Volatile.Read(ref started));
    }

    [Fact]
    public async Task A_refresh_with_an_equal_status_publishes_no_snapshot()
    {
        FakeGitService git = new("repo-root", new GitFileChange { Path = "a.txt", WorkTreeStatus = GitChangeKind.Modified });
        using GitSession session = new(GitServiceSet.FromService(git), new FakeWorkspaceInfo { WorkspacePath = "repo-root" });
        await session.RefreshAsync().WaitAsync(WaitLimit);
        int published = 0;
        int completed = 0;
        using IDisposable subscription = session.Snapshots.Skip(1).Subscribe(_ => Interlocked.Increment(ref published));
        using IDisposable completedSubscription = session.RefreshCompleted.Subscribe(_ => Interlocked.Increment(ref completed));

        // New instances with the same values.
        git.Status = git.Status with { Changes = [new GitFileChange { Path = "a.txt", WorkTreeStatus = GitChangeKind.Modified }] };
        await session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(0, Volatile.Read(ref published));
        // Views of content, such as the diff of a.txt, still learn that the refresh ran.
        Assert.Equal(1, Volatile.Read(ref completed));

        git.Status = git.Status with { AheadBy = 1 };
        await session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(1, Volatile.Read(ref published));
        Assert.Equal(2, Volatile.Read(ref completed));
        Assert.Equal(1, session.Current.AheadBy);
    }

    [Fact]
    public async Task A_deleted_repository_is_no_repository_after_the_next_refresh()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using GitSession session = CreateSession(repository, out _);
        await session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(GitSessionState.Repository, session.Current.State);

        string gitDirectory = Path.Combine(repository.Path, ".git");
        foreach (string file in Directory.EnumerateFiles(gitDirectory, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(gitDirectory, recursive: true);
        await session.RefreshAsync().WaitAsync(WaitLimit);

        Assert.Equal(GitSessionState.NotRepository, session.Current.State);
        Assert.False(session.Current.IsRepository);
        Assert.Null(session.Current.RepositoryRoot);
        ContextKeyRegistry contextKeys = new();
        GitExtensionEntry.UpdateContextKeys(contextKeys, session.Current);
        Assert.False(contextKeys.TryGet(GitCommands.IS_REPOSITORY_CONTEXT_KEY, out bool isRepository) && isRepository);
    }

    [Fact]
    public async Task A_failing_status_of_a_fake_service_is_an_error_state_until_the_status_works_again()
    {
        FakeGitService git = new("repo-root") { StatusError = "fatal: index file corrupt" };
        using GitSession session = new(GitServiceSet.FromService(git), new FakeWorkspaceInfo { WorkspacePath = "repo-root" });

        await session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(GitSessionState.StatusFailed, session.Current.State);
        Assert.Equal("fatal: index file corrupt", session.Current.ErrorMessage);
        Assert.Equal("repo-root", session.Current.RepositoryRoot);

        git.StatusError = null;
        await session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Equal(GitSessionState.Repository, session.Current.State);
        Assert.Null(session.Current.ErrorMessage);
    }

    [Fact]
    public async Task A_failing_git_directory_watcher_is_reported_once_and_commands_still_refresh()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-git-fake-" + Guid.NewGuid().ToString("N"));
        string missingGitDirectory = Path.Combine(root, "missing-git-directory");
        FakeGitService git = new(root);
        RecordingExtensionLogger logger = new();
        using GitSession session = new(
            new GitServiceSet(git) { Repository = new FixedRepositoryService(missingGitDirectory) },
            new FakeWorkspaceInfo { WorkspacePath = root },
            logger: logger);

        await session.StartAsync().WaitAsync(WaitLimit);
        await session.RefreshAsync().WaitAsync(WaitLimit);
        await session.RunAsync((service, repositoryRoot, ct) => service.StageAsync(repositoryRoot, ["a.txt"], ct)).WaitAsync(WaitLimit);

        string warning = Assert.Single(logger.Entries);
        Assert.Contains(missingGitDirectory, warning, StringComparison.Ordinal);
        Assert.Equal(GitSessionState.Repository, session.Current.State);
        Assert.Equal(3, git.StatusCalls);
    }

    [Fact]
    public async Task A_linked_worktree_refreshes_when_its_branch_moves_in_the_common_directory()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string worktree = Path.Combine(repository.RootPath, "linked");
        repository.RunGit("worktree", "add", "-q", "-b", "topic", worktree);
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FakeWorkspaceInfo { WorkspacePath = worktree });
        await session.StartAsync().WaitAsync(WaitLimit);

        GitSnapshot start = session.Current;
        Assert.Equal("topic", start.BranchName);
        Assert.True(FileSystemPathComparison.Equals(
            Path.GetFullPath(Path.Combine(repository.Path, ".git")),
            start.CommonDirectory));
        Assert.False(FileSystemPathComparison.Equals(start.GitDirectory, start.CommonDirectory));

        string mainHead = repository.RunGit("rev-parse", "main").Trim();
        Task<GitSnapshot> moved = session.Snapshots
            .FirstAsync(snapshot => snapshot.Status?.HeadSha == mainHead)
            .Timeout(WaitLimit)
            .ToTask();
        // Moves the branch of the linked worktree outside of it, as a push or a reset in another worktree does.
        repository.RunGit("update-ref", "refs/heads/topic", mainHead);

        await moved;
    }

    [Fact]
    public async Task Refs_version_grows_for_commands_and_full_refreshes_but_not_for_status_refreshes()
    {
        FakeGitService git = new("repo-root");
        using GitSession session = new(GitServiceSet.FromService(git), new FakeWorkspaceInfo { WorkspacePath = "repo-root" });
        await session.RefreshAsync().WaitAsync(WaitLimit);
        int start = session.Current.RefsVersion;

        await session.RefreshAsync().WaitAsync(WaitLimit);
        await session.RunAsync((service, root, ct) => Task.CompletedTask).WaitAsync(WaitLimit);
        Assert.Equal(start, session.Current.RefsVersion);

        await session.RunWithServicesAsync((services, root, ct) => Task.FromResult(true)).WaitAsync(WaitLimit);
        Assert.Equal(start + 1, session.Current.RefsVersion);

        await session.RefreshAllAsync().WaitAsync(WaitLimit);
        Assert.Equal(start + 2, session.Current.RefsVersion);
    }

    [Fact]
    public async Task A_branch_created_outside_the_editor_grows_the_refs_version()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using GitSession session = CreateSession(repository, out _);
        await session.StartAsync().WaitAsync(WaitLimit);
        GitSnapshot before = session.Current;

        Task<GitSnapshot> changed = session.Snapshots
            .FirstAsync(snapshot => snapshot.RefsVersion > before.RefsVersion)
            .Timeout(WaitLimit)
            .ToTask();
        repository.RunGit("branch", "topic");

        GitSnapshot snapshot = await changed;
        Assert.Equal(before.Status, snapshot.Status);
    }

    private static GitSession CreateSession(GitTestRepository repository, out TestExtensionStorage storage)
    {
        storage = new TestExtensionStorage();
        return new GitSession(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FakeWorkspaceInfo { WorkspacePath = repository.Path },
            new GitSettingsStore(storage));
    }
}
