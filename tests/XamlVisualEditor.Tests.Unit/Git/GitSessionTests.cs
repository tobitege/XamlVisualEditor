using System;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
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

        string initialized = await session.InitializeRepositoryAsync().WaitAsync(WaitLimit);

        Assert.Equal(folder, initialized);
        Assert.True(session.Current.IsRepository);
        Assert.True(Directory.Exists(Path.Combine(folder, ".git")));
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
