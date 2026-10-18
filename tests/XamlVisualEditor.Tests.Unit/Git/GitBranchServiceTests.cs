using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitBranchServiceTests
{
    [Fact]
    public async Task Lists_local_and_remote_branches_with_current_upstream_and_ahead()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("branch", "feature/x");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Zweiter Commit mit Ä");
        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitBranch> branches = await service.GetBranchesAsync(repository.Path, ct);

        Assert.Equal(new[] { "feature/x", "main", "origin/main" }, branches.Select(branch => branch.Name));
        GitBranch main = Assert.Single(branches, branch => branch.IsCurrent);
        Assert.Equal("main", main.Name);
        Assert.Equal("origin/main", main.UpstreamName);
        Assert.Equal(1, main.AheadBy);
        Assert.Equal(0, main.BehindBy);
        Assert.Equal("Zweiter Commit mit Ä", main.Subject);
        GitBranch remote = Assert.Single(branches, branch => branch.IsRemote);
        Assert.Equal("origin", remote.RemoteName);
        Assert.False(remote.IsCurrent);
    }

    [Fact]
    public async Task A_repository_without_commits_has_no_branches()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        GitService service = new(options: repository.ProcessOptions);

        Assert.Empty(await service.GetBranchesAsync(repository.Path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Create_without_switch_keeps_the_branch_and_create_with_switch_moves_head()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string firstCommit = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        GitService service = new(options: repository.ProcessOptions);

        await service.CreateAsync(repository.Path, "kept", startPoint: null, switchTo: false, ct);
        await service.CreateAsync(repository.Path, "from-first", firstCommit, switchTo: true, ct);

        Assert.Equal("from-first", CurrentBranch(repository));
        Assert.Equal(firstCommit, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(repository.RunGit("rev-parse", "main").Trim(), repository.RunGit("rev-parse", "kept").Trim());
    }

    [Fact]
    public async Task Create_with_an_invalid_name_throws_with_the_git_error()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(repository.Path, "bad..name", null, switchTo: false, TestContext.Current.CancellationToken));

        Assert.Contains("bad..name", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-m", "refs/heads/x1", false)]
    [InlineData("-D", "x1", false)]
    [InlineData("-D", "x1", true)]
    [InlineData("-m", null, true)]
    public async Task Create_with_an_option_like_name_throws_and_changes_no_branch(string name, string? startPoint, bool switchTo)
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "x1");
        GitService service = new(options: repository.ProcessOptions);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.CreateAsync(repository.Path, name, startPoint, switchTo, TestContext.Current.CancellationToken));

        Assert.Contains(name, error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "main", "x1" }, Branches(repository));
        Assert.Equal("main", CurrentBranch(repository));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_created_from_a_remote_branch_under_another_name_has_no_upstream(bool switchTo)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        GitService service = new(options: repository.ProcessOptions);

        await service.CreateAsync(repository.Path, "my-main", "refs/remotes/origin/main", switchTo, ct);

        GitBranch created = Assert.Single(await service.GetBranchesAsync(repository.Path, ct), branch => branch.Name == "my-main");
        Assert.Null(created.UpstreamName);
        Assert.Equal(switchTo, created.IsCurrent);
    }

    [Theory]
    [InlineData("refs/remotes/origin/feature", false)]
    [InlineData("origin/feature", true)]
    [InlineData("refs/remotes/team/fork/feature", true)]
    public async Task A_branch_created_from_a_remote_branch_of_the_same_name_tracks_it(string startPoint, bool switchTo)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("remote", "add", "team/fork", remote);
        repository.RunGit("fetch", "-q", "--all");
        GitService service = new(options: repository.ProcessOptions);

        await service.CreateAsync(repository.Path, "feature", startPoint, switchTo, ct);

        GitBranch created = Assert.Single(await service.GetBranchesAsync(repository.Path, ct), branch => branch.Name == "feature");
        Assert.Equal(GitRefParser.ShortenBranchReference(startPoint), created.UpstreamName);
        Assert.Equal(switchTo, created.IsCurrent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_branch_created_from_a_remote_branch_of_the_same_name_follows_auto_setup_merge(bool switchTo)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("fetch", "-q", "origin");
        repository.RunGit("config", "branch.autoSetupMerge", "false");
        GitService service = new(options: repository.ProcessOptions);

        await service.CreateAsync(repository.Path, "feature", "origin/feature", switchTo, ct);

        GitBranch created = Assert.Single(await service.GetBranchesAsync(repository.Path, ct), branch => branch.Name == "feature");
        Assert.Null(created.UpstreamName);
        Assert.Equal(switchTo, created.IsCurrent);
    }

    [Theory]
    [InlineData("refs/remotes/team/fork/feature", "feature", true)]
    [InlineData("team/fork/feature", "feature", true)]
    [InlineData("refs/remotes/team/fork/feature", "fork/feature", false)]
    [InlineData("team/fork/feature", "fork/feature", false)]
    [InlineData("refs/remotes/team/other", "other", true)]
    public async Task A_remote_branch_belongs_to_the_longest_remote_name_that_matches(string startPoint, string name, bool sameName)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        repository.RunGit("remote", "add", "team", remote);
        // git remote add refuses a name below an existing one; a configuration written by hand can still hold both.
        repository.RunGit("config", "remote.team/fork.url", remote);
        repository.RunGit("update-ref", "refs/remotes/team/fork/feature", "HEAD");
        repository.RunGit("update-ref", "refs/remotes/team/other", "HEAD");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(sameName, await service.IsRemoteBranchNamedAsync(repository.Path, startPoint, name, ct));
    }

    [Theory]
    [InlineData("feature/x", true)]
    [InlineData("-m", false)]
    [InlineData("two words", false)]
    [InlineData("a..b", false)]
    [InlineData("HEAD", false)]
    [InlineData("@{-1}", false)]
    [InlineData("", false)]
    public async Task Branch_names_are_checked_as_git_checks_them(string name, bool valid)
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "other");
        repository.RunGit("switch", "-q", "main");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(valid, await service.IsValidBranchNameAsync(repository.Path, name, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Detach_checks_out_the_commit_without_a_branch()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string first = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        GitService service = new(options: repository.ProcessOptions);

        await service.DetachAsync(repository.Path, first, TestContext.Current.CancellationToken);

        Assert.Equal("HEAD", CurrentBranch(repository));
        Assert.Equal(first, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Switch_accepts_short_and_full_local_names()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "feature");
        GitService service = new(options: repository.ProcessOptions);

        await service.SwitchAsync(repository.Path, "feature", ct);
        Assert.Equal("feature", CurrentBranch(repository));

        await service.SwitchAsync(repository.Path, "refs/heads/main", ct);
        Assert.Equal("main", CurrentBranch(repository));
    }

    [Fact]
    public async Task Switch_to_a_remote_branch_creates_a_local_branch_that_tracks_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.RunGitIn(clone, "switch", "-q", "-c", "topic");
        repository.CommitFileIn(clone, "topic.txt", "t\n", "topic");
        repository.RunGitIn(clone, "push", "-q", "origin", "topic");
        repository.RunGit("fetch", "-q", "origin");
        GitService service = new(options: repository.ProcessOptions);

        await service.SwitchAsync(repository.Path, "origin/topic", ct);

        Assert.Equal("topic", CurrentBranch(repository));
        GitBranch topic = Assert.Single(await service.GetBranchesAsync(repository.Path, ct), branch => branch.IsCurrent);
        Assert.Equal("origin/topic", topic.UpstreamName);
    }

    [Fact]
    public async Task Switch_with_conflicting_working_changes_throws_and_stays()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "other");
        repository.CommitFileIn(repository.Path, "a.txt", "other\n", "other");
        repository.RunGit("switch", "-q", "main");
        repository.WriteFile("a.txt", "local edit\n");
        GitService service = new(options: repository.ProcessOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SwitchAsync(repository.Path, "other", TestContext.Current.CancellationToken));

        Assert.Equal("main", CurrentBranch(repository));
    }

    [Fact]
    public async Task Delete_removes_a_merged_branch_and_refuses_an_unmerged_one_until_forced()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "merged");
        repository.RunGit("switch", "-q", "-c", "unmerged");
        repository.CommitFileIn(repository.Path, "u.txt", "u\n", "only here");
        repository.RunGit("switch", "-q", "main");
        // A translated git must not change the result: the service reads git's messages untranslated.
        Dictionary<string, string?> environment = new(repository.Environment)
        {
            ["LC_ALL"] = "de_DE.UTF-8",
            ["LANGUAGE"] = "de"
        };
        GitService service = new(options: new GitProcessOptions { Environment = environment });

        Assert.Equal(GitBranchDeleteResult.Deleted, await service.DeleteAsync(repository.Path, "merged", force: false, ct));
        Assert.Equal(GitBranchDeleteResult.NotFullyMerged, await service.DeleteAsync(repository.Path, "unmerged", force: false, ct));
        Assert.Contains("unmerged", Branches(repository));

        Assert.Equal(GitBranchDeleteResult.Deleted, await service.DeleteAsync(repository.Path, "refs/heads/unmerged", force: true, ct));
        Assert.Equal(new[] { "main" }, Branches(repository));
    }

    [Fact]
    public async Task Rename_moves_the_branch_with_its_upstream_and_refuses_the_name_of_another_branch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("branch", "other");
        GitService service = new(options: repository.ProcessOptions);

        await service.RenameAsync(repository.Path, "refs/heads/main", "renamed", ct);

        Assert.Equal("renamed", CurrentBranch(repository));
        Assert.Equal("origin/main", repository.RunGit("rev-parse", "--abbrev-ref", "renamed@{upstream}").Trim());
        Assert.Equal(new[] { "other", "renamed" }, Branches(repository));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RenameAsync(repository.Path, "renamed", "other", ct));
        Assert.Equal(new[] { "other", "renamed" }, Branches(repository));
    }

    [Fact]
    public async Task A_switch_cancelled_while_git_runs_runs_to_its_end()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "other");
        // The hook keeps git running after the checkout, long enough to cancel it.
        string hook = Path.Combine(repository.Path, ".git", "hooks", "post-checkout");
        File.WriteAllText(hook, "#!/bin/sh\nsleep 2\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        GitService service = new(options: repository.ProcessOptions);
        using CancellationTokenSource cancel = new();
        Task switching = service.SwitchAsync(repository.Path, "other", cancel.Token);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        cancel.Cancel();

        await switching;

        Assert.Equal("other", CurrentBranch(repository));
        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "index.lock")));
    }

    [Fact]
    public async Task Deleting_the_current_branch_throws()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DeleteAsync(repository.Path, "main", force: true, TestContext.Current.CancellationToken));
    }

    private static string CurrentBranch(GitTestRepository repository) =>
        repository.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim();

    private static string[] Branches(GitTestRepository repository) =>
        repository.RunGit("for-each-ref", "--format=%(refname:short)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
