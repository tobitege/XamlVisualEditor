using System.IO;
using XamlVisualEditor.GitExtension.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitRepositoryWatcherTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "xve-watcher", "repo");
    private static readonly string GitDirectory = Path.Combine(Root, ".git");
    private static readonly string LinkedGitDirectory = Path.Combine(GitDirectory, "worktrees", "linked");

    [Theory]
    [InlineData("HEAD", true)]
    [InlineData("index", true)]
    [InlineData("MERGE_HEAD", true)]
    [InlineData("CHERRY_PICK_HEAD", true)]
    [InlineData("REVERT_HEAD", true)]
    [InlineData("FETCH_HEAD", true)]
    [InlineData("rebase-merge", true)]
    [InlineData("rebase-merge/done", true)]
    [InlineData("rebase-apply/next", true)]
    [InlineData("refs/heads/x", true)]
    [InlineData("objects/../index", true)]
    [InlineData("index.lock", false)]
    [InlineData("refs/heads/x.lock", false)]
    [InlineData("fsmonitor--daemon/cookies/x", false)]
    [InlineData("fsmonitor--daemon.ipc", false)]
    [InlineData("objects/..", false)]
    [InlineData("objects/ab/cdef", false)]
    [InlineData("logs/HEAD", false)]
    [InlineData("hooks/pre-commit", false)]
    [InlineData("COMMIT_EDITMSG", false)]
    [InlineData("ORIG_HEAD", false)]
    [InlineData("worktrees/x/HEAD", false)]
    [InlineData("rebase-merge-x", false)]
    [InlineData("HEAD/x", false)]
    [InlineData("../outside.txt", false)]
    public void Git_directory_changes_count_only_for_the_state_files_of_the_working_tree(string relativePath, bool relevant)
    {
        Assert.Equal(relevant, GitRepositoryWatcher.IsRelevantGitDirectoryPath(GitDirectory, Combine(GitDirectory, relativePath)));
    }

    [Theory]
    [InlineData("refs/heads/x", true)]
    [InlineData("refs/remotes/origin/main", true)]
    [InlineData("packed-refs", true)]
    [InlineData("config", true)]
    [InlineData("packed-refs.lock", false)]
    [InlineData("HEAD", false)]
    [InlineData("index", false)]
    [InlineData("worktrees/x/HEAD", false)]
    [InlineData("worktrees/x/index", false)]
    [InlineData("fsmonitor--daemon/cookies/x", false)]
    [InlineData("objects/..", false)]
    [InlineData("objects/pack/pack-1.idx", false)]
    [InlineData("config/x", false)]
    public void Common_directory_changes_count_only_for_refs_packed_refs_and_config(string relativePath, bool relevant)
    {
        Assert.Equal(relevant, GitRepositoryWatcher.IsRelevantCommonDirectoryPath(GitDirectory, Combine(GitDirectory, relativePath)));
    }

    [Theory]
    [InlineData("refs/heads/x", true)]
    [InlineData("refs/remotes/origin/main", true)]
    [InlineData("refs/tags/v1", true)]
    [InlineData("packed-refs", true)]
    [InlineData("packed-refs.lock", false)]
    [InlineData("refs/heads/x.lock", false)]
    [InlineData("HEAD", false)]
    [InlineData("index", false)]
    [InlineData("FETCH_HEAD", false)]
    [InlineData("config", false)]
    [InlineData("logs/refs/heads/x", false)]
    public void Only_changes_of_refs_and_packed_refs_change_the_branches(string relativePath, bool refs)
    {
        Assert.Equal(refs, GitRepositoryWatcher.IsRefsPath(GitDirectory, Combine(GitDirectory, relativePath)));
    }

    [Fact]
    public void The_git_directory_of_a_linked_worktree_has_its_own_state_files()
    {
        Assert.True(GitRepositoryWatcher.IsRelevantGitDirectoryPath(LinkedGitDirectory, Combine(LinkedGitDirectory, "HEAD")));
        Assert.True(GitRepositoryWatcher.IsRelevantGitDirectoryPath(LinkedGitDirectory, Combine(LinkedGitDirectory, "index")));
        Assert.False(GitRepositoryWatcher.IsRelevantGitDirectoryPath(LinkedGitDirectory, Combine(LinkedGitDirectory, "commondir")));
        Assert.False(GitRepositoryWatcher.IsRelevantGitDirectoryPath(LinkedGitDirectory, Combine(GitDirectory, "HEAD")));
    }

    [Theory]
    [InlineData("src/a.cs", true)]
    [InlineData("new.txt", true)]
    [InlineData(".gitignore", true)]
    [InlineData(".git/index", false)]
    [InlineData("src/bin/Debug/a.dll", false)]
    [InlineData("obj/project.assets.json", false)]
    [InlineData(".vs/settings.json", false)]
    [InlineData("TestResults/run.trx", false)]
    [InlineData("../outside.txt", false)]
    public void Working_tree_changes_skip_the_git_directory_and_build_output(string relativePath, bool relevant)
    {
        Assert.Equal(relevant, GitRepositoryWatcher.IsRelevantWorkingTreePath(Root, GitDirectory, Combine(Root, relativePath)));
    }

    private static string Combine(string folder, string relativePath) =>
        Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar));
}
