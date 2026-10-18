using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitRemoteServiceTests
{
    private const string SHA = "0123456789abcdef0123456789abcdef01234567";
    private const string TWO_BRANCHES =
        SHA + "\t\tbranch 'other' of ../remote\n" + SHA + "\t\tbranch 'main' of ../remote\n";

    [Fact]
    public async Task Push_without_upstream_fails_and_with_set_upstream_pushes_and_tracks()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult withoutUpstream = await service.PushAsync(repository.Path, ct: ct);
        Assert.False(withoutUpstream.Success);
        Assert.Equal(GitRemoteFailureKind.NoUpstream, withoutUpstream.FailureKind);

        GitRemoteResult pushed = await service.PushAsync(repository.Path, "origin", ct);

        Assert.True(pushed.Success, pushed.Message);
        Assert.Equal(GitRemoteFailureKind.None, pushed.FailureKind);
        Assert.Equal("origin/main", repository.RunGit("rev-parse", "--abbrev-ref", "main@{upstream}").Trim());
        Assert.Equal(
            repository.RunGit("rev-parse", "HEAD").Trim(),
            repository.RunGitIn(repository.RemotePath!, "rev-parse", "main").Trim());
        GitRemote remote = Assert.Single(await service.GetRemotesAsync(repository.Path, ct));
        Assert.Equal("origin", remote.Name);
        Assert.Equal(repository.RemotePath, remote.FetchUrl);
    }

    [Fact]
    public async Task Force_push_replaces_an_amended_commit_and_is_refused_when_the_remote_got_unfetched_commits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        GitService service = new(options: repository.ProcessOptions);
        Assert.True((await service.PushAsync(repository.Path, "origin", ct)).Success);
        repository.RunGit("commit", "-q", "--amend", "-m", "Amended");

        Assert.Equal(GitRemoteFailureKind.Rejected, (await service.PushAsync(repository.Path, ct: ct)).FailureKind);
        GitRemoteResult forced = await service.ForcePushAsync(repository.Path, "main", Sha(repository, "origin/main"), ct);

        Assert.True(forced.Success, forced.Message);
        Assert.Equal(Sha(repository, "HEAD"), repository.RunGitIn(remote, "rev-parse", "main").Trim());

        // Another user pushes a commit after the lease was read, and this repository even fetches it: the lease of
        // the commit the question was about keeps it.
        string lease = Sha(repository, "origin/main");
        string clone = repository.CloneRemote("other");
        repository.CommitFileIn(clone, "b.txt", "b\n", "Other");
        repository.RunGitIn(clone, "push", "-q");
        string theirs = repository.RunGitIn(remote, "rev-parse", "main").Trim();
        repository.RunGit("fetch", "-q");
        repository.RunGit("commit", "-q", "--amend", "-m", "Amended again");

        GitRemoteResult refused = await service.ForcePushAsync(repository.Path, "main", lease, ct);

        Assert.False(refused.Success);
        Assert.Equal(GitRemoteFailureKind.Rejected, refused.FailureKind);
        Assert.Equal(theirs, repository.RunGitIn(remote, "rev-parse", "main").Trim());
    }

    [Fact]
    public async Task Force_push_pushes_only_the_named_branch_to_its_upstream_of_another_name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        GitService service = new(options: repository.ProcessOptions);
        repository.RunGit("push", "-q", "origin", "main", "main:shared", "main:topic-remote");
        // The local topic tracks topic-remote, and shared is behind its remote-tracking branch after another push.
        repository.RunGit("switch", "-q", "-c", "topic", "--track", "origin/topic-remote");
        repository.RunGit("switch", "-q", "-c", "shared", "--track", "origin/shared");
        string clone = repository.CloneRemote("other");
        repository.RunGitIn(clone, "switch", "-q", "shared");
        repository.CommitFileIn(clone, "b.txt", "b\n", "Colleague");
        repository.RunGitIn(clone, "push", "-q", "origin", "shared");
        repository.RunGit("fetch", "-q");
        string colleague = repository.RunGitIn(remote, "rev-parse", "shared").Trim();
        repository.RunGit("config", "push.default", "matching");
        repository.RunGit("switch", "-q", "topic");
        repository.RunGit("commit", "-q", "--amend", "-m", "Rewritten");

        GitRemoteResult forced = await service.ForcePushAsync(repository.Path, "topic", Sha(repository, "origin/topic-remote"), ct);

        Assert.True(forced.Success, forced.Message);
        Assert.Equal(Sha(repository, "topic"), repository.RunGitIn(remote, "rev-parse", "topic-remote").Trim());
        Assert.Equal(colleague, repository.RunGitIn(remote, "rev-parse", "shared").Trim());
        Assert.False(repository.TryRunGitIn(remote, out _, "rev-parse", "--verify", "refs/heads/topic"));
        Assert.Equal(
            GitRemoteFailureKind.NoUpstream,
            (await service.ForcePushAsync(repository.Path, "nothing", Sha(repository, "HEAD"), ct)).FailureKind);
    }

    [Fact]
    public async Task A_force_push_lease_is_given_only_when_the_push_goes_to_the_fetched_upstream_on_a_remote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        GitService service = new(options: repository.ProcessOptions);
        repository.RunGit("push", "-q", "-u", "origin", "main");
        Assert.Equal(Sha(repository, "origin/main"), await service.GetForcePushLeaseAsync(repository.Path, "main", ct));

        // feature tracks origin/main, but push.default=current pushes it to origin/feature.
        repository.RunGit("switch", "-q", "-c", "feature", "--track", "origin/main");
        repository.RunGit("config", "push.default", "current");
        Assert.Null(await service.GetForcePushLeaseAsync(repository.Path, "feature", ct));

        // main pushes to another remote than its upstream.
        repository.RunGit("remote", "add", "fork", repository.RemotePath!);
        repository.RunGit("config", "branch.main.pushRemote", "fork");
        Assert.Null(await service.GetForcePushLeaseAsync(repository.Path, "main", ct));
        repository.RunGit("config", "--unset", "branch.main.pushRemote");

        // A push under matching pushes every branch of the same name, so another one may have rejected it.
        repository.RunGit("config", "push.default", "matching");
        Assert.Null(await service.GetForcePushLeaseAsync(repository.Path, "main", ct));
        repository.RunGit("config", "push.default", "current");

        // The upstream of local is the local branch main.
        repository.RunGit("switch", "-q", "-c", "local", "--track", "main");
        Assert.Null(await service.GetForcePushLeaseAsync(repository.Path, "local", ct));

        // The upstream of fresh was never fetched.
        repository.RunGit("switch", "-q", "-c", "fresh");
        repository.RunGit("config", "branch.fresh.remote", "origin");
        repository.RunGit("config", "branch.fresh.merge", "refs/heads/fresh");
        Assert.Null(await service.GetForcePushLeaseAsync(repository.Path, "fresh", ct));
        Assert.Equal(
            GitRemoteFailureKind.NoUpstream,
            (await service.ForcePushAsync(repository.Path, "local", Sha(repository, "main"), ct)).FailureKind);
    }

    private static string Sha(GitTestRepository repository, string revision) => repository.RunGit("rev-parse", revision).Trim();

    [Fact]
    public async Task A_tag_is_pushed_rejected_when_the_remote_tag_differs_and_deleted_on_the_remote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        GitService service = new(options: repository.ProcessOptions);
        Assert.True((await service.PushAsync(repository.Path, "origin", ct)).Success);
        // A branch of the tag's name does not take its place.
        repository.RunGit("tag", "v1");
        repository.RunGit("branch", "v1");

        GitRemoteResult pushed = await service.PushTagAsync(repository.Path, "origin", "v1", ct);

        Assert.True(pushed.Success, pushed.Message);
        Assert.Equal(repository.RunGit("rev-parse", "refs/tags/v1").Trim(), repository.RunGitIn(remote, "rev-parse", "refs/tags/v1").Trim());
        Assert.False(repository.TryRunGitIn(remote, out _, "rev-parse", "--verify", "refs/heads/v1"));

        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.RunGit("tag", "-f", "v1", "HEAD");
        GitRemoteResult rejected = await service.PushTagAsync(repository.Path, "origin", "v1", ct);

        Assert.False(rejected.Success);
        Assert.Equal(GitRemoteFailureKind.Rejected, rejected.FailureKind);

        GitRemoteResult deleted = await service.DeleteRemoteTagAsync(repository.Path, "origin", "v1", ct);

        Assert.True(deleted.Success, deleted.Message);
        Assert.False(repository.TryRunGitIn(remote, out _, "rev-parse", "--verify", "refs/tags/v1"));
        Assert.True(repository.TryRunGit(out _, "rev-parse", "--verify", "refs/tags/v1"));
    }

    [Fact]
    public async Task Remotes_are_added_renamed_with_their_upstreams_changed_and_removed_with_their_branches()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remotePath = repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        GitService service = new(options: repository.ProcessOptions);

        await service.AddRemoteAsync(repository.Path, "backup", remotePath, ct);
        await service.RenameRemoteAsync(repository.Path, "origin", "upstream", ct);
        await service.SetRemoteUrlAsync(repository.Path, "backup", "https://example.com/repo.git", ct);

        Assert.Equal("upstream/main", repository.RunGit("rev-parse", "--abbrev-ref", "main@{upstream}").Trim());
        Assert.Equal(
            [("backup", "https://example.com/repo.git"), ("upstream", remotePath)],
            (await service.GetRemotesAsync(repository.Path, ct)).Select(remote => (remote.Name, remote.FetchUrl)).OrderBy(remote => remote.Name));

        await service.RemoveRemoteAsync(repository.Path, "upstream", ct);

        Assert.Equal("backup", Assert.Single(await service.GetRemotesAsync(repository.Path, ct)).Name);
        Assert.Equal(string.Empty, repository.RunGit("branch", "-r").Trim());
        Assert.False(repository.TryRunGit(out _, "rev-parse", "--abbrev-ref", "main@{upstream}"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddRemoteAsync(repository.Path, "backup", remotePath, ct));
    }

    [Fact]
    public async Task An_option_like_remote_name_is_refused_and_one_made_elsewhere_is_fetched_as_a_name()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.RunGit("remote", "add", "origin", "../nowhere");

        await Assert.ThrowsAsync<ArgumentException>(() => service.AddRemoteAsync(repository.Path, "--upload-pack=echo", "../nowhere", ct));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RenameRemoteAsync(repository.Path, "origin", "-x", ct));
        Assert.Equal("origin", repository.RunGit("remote").Trim());

        // git itself accepts the name; the fetch must pass it as the remote, not as the option --upload-pack.
        repository.RunGit("remote", "add", "--", "--upload-pack=echo", "../nowhere");
        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);

        Assert.False(fetched.Success);
        Assert.DoesNotContain("bad line length", fetched.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_option_like_url_is_stored_as_the_url()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        await service.AddRemoteAsync(repository.Path, "odd", "--mirror=fetch", ct);

        GitRemote remote = Assert.Single(await service.GetRemotesAsync(repository.Path, ct));
        Assert.Equal(("odd", "--mirror=fetch"), (remote.Name, remote.FetchUrl));
        Assert.False(repository.TryRunGit(out _, "config", "remote.odd.mirror"));
    }

    [Fact]
    public async Task Fetch_reports_the_commits_behind_and_pull_fast_forwards()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "from-clone.txt", "c\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);
        Assert.True(fetched.Success, fetched.Message);
        Assert.Equal(["origin"], fetched.FetchedRemotes);
        GitBranch main = Assert.Single(await service.GetBranchesAsync(repository.Path, ct), branch => branch.IsCurrent);
        Assert.Equal(1, main.BehindBy);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal(repository.RunGitIn(clone, "rev-parse", "HEAD").Trim(), repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal("2", repository.RunGit("rev-list", "--count", "HEAD").Trim());

        GitRemoteResult again = await service.PullAsync(repository.Path, ct);
        Assert.True(again.Success, again.Message);
        Assert.Equal(0, again.PulledCommitCount);
    }

    [Fact]
    public async Task Fetch_fetches_configured_notes_and_tag_refspecs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("config", "--add", "remote.origin.fetch", "+refs/notes/*:refs/notes/*");
        repository.RunGit("config", "--add", "remote.origin.fetch", "+refs/tags/*:refs/tags/*");
        string clone = repository.CloneRemote("clone");
        repository.RunGitIn(clone, "notes", "add", "-m", "reviewed", "HEAD");
        // A tag on a commit that no branch contains is fetched only through the tag refspec.
        repository.RunGitIn(clone, "switch", "-q", "--orphan", "side");
        repository.CommitFileIn(clone, "side.txt", "s\n", "side");
        repository.RunGitIn(clone, "tag", "side-release");
        repository.RunGitIn(clone, "push", "-q", "origin", "refs/notes/commits", "side-release");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);

        Assert.True(fetched.Success, fetched.Message);
        Assert.Equal("reviewed", repository.RunGit("notes", "show", "HEAD").Trim());
        Assert.Equal(
            repository.RunGitIn(clone, "rev-parse", "side-release").Trim(),
            repository.RunGit("rev-parse", "side-release").Trim());
    }

    [Fact]
    public async Task Fetch_skips_a_remote_with_skip_fetch_all()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("remote", "add", "offline", Path.Combine(repository.RootPath, "missing.git"));
        repository.RunGit("config", "remote.offline.skipFetchAll", "true");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);

        Assert.True(fetched.Success, fetched.Message);
        Assert.Equal(["origin"], fetched.FetchedRemotes);
    }

    [Fact]
    public async Task Pull_of_a_branch_whose_upstream_was_deleted_reports_the_upstream_as_gone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("fetch", "-q", "origin");
        repository.RunGit("switch", "-q", "--track", "origin/feature");
        repository.RunGit("config", "fetch.prune", "true");
        repository.RunGitIn(repository.RemotePath!, "branch", "-D", "feature");
        GitService service = new(options: repository.ProcessOptions);
        Assert.True((await service.FetchAsync(repository.Path, ct)).Success);

        GitRepositoryStatus status = await service.GetStatusAsync(repository.Path, ct);
        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(status.IsUpstreamGone);
        Assert.Equal("origin/feature", status.UpstreamName);
        Assert.Equal(GitRemoteFailureKind.UpstreamGone, pulled.FailureKind);
    }

    [Fact]
    public async Task Pull_of_a_branch_whose_upstream_was_deleted_fails_as_gone_also_without_pruning()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("fetch", "-q", "origin");
        repository.RunGit("switch", "-q", "--track", "origin/feature");
        repository.RunGitIn(repository.RemotePath!, "branch", "-D", "feature");
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);
        Assert.True((await service.FetchAsync(repository.Path, ct)).Success);

        GitRepositoryStatus status = await service.GetStatusAsync(repository.Path, ct);
        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        // Without pruning, the remote-tracking branch stays and the status cannot tell that the upstream is gone.
        Assert.False(status.IsUpstreamGone);
        Assert.False(pulled.Success);
        Assert.Equal(GitRemoteFailureKind.UpstreamGone, pulled.FailureKind);
        Assert.Contains("'refs/heads/feature' from the remote, but no such ref was fetched", pulled.Message, StringComparison.Ordinal);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Pull_without_a_fetch_before_updates_the_remote_tracking_branch_and_counts_the_pulled_commits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "one.txt", "1\n", "one");
        repository.CommitFileIn(clone, "two.txt", "2\n", "two");
        repository.RunGitIn(clone, "push", "-q");
        string remoteHead = repository.RunGitIn(clone, "rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(2, pulled.PulledCommitCount);
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task Pull_fast_forwards_a_branch_whose_upstream_is_a_local_branch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "--track", "topic", "main");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "on main");
        string mainHead = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.RunGit("switch", "-q", "topic");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal(mainHead, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Pull_fetches_the_whole_remote_with_its_tags_and_other_branches_like_git_pull()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "b.txt", "b\n", "tagged");
        repository.RunGitIn(clone, "tag", "release");
        repository.RunGitIn(clone, "push", "-q", "origin", "main", "main:other", "release");
        string remoteHead = repository.RunGitIn(clone, "rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "release^{commit}").Trim());
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "origin/other").Trim());
    }

    [Theory]
    [InlineData("second")]
    [InlineData("team/fork")]
    public async Task Pull_fast_forwards_from_an_upstream_of_another_name_on_a_second_remote(string remoteName)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "origin", "main:topic");
        string second = Path.Combine(repository.RootPath, "second.git");
        repository.RunGitIn(repository.RootPath, "clone", "-q", "--bare", repository.RemotePath!, second);
        repository.RunGit("remote", "add", remoteName, second);
        repository.RunGit("fetch", "-q", remoteName);
        repository.RunGit("branch", "-q", "--set-upstream-to", remoteName + "/topic");
        string clone = repository.CloneRemote("clone");
        repository.RunGitIn(clone, "switch", "-q", "topic");
        repository.CommitFileIn(clone, "b.txt", "b\n", "on topic");
        repository.RunGitIn(clone, "push", "-q", second, "topic");
        string remoteHead = repository.RunGitIn(clone, "rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Pull_into_the_unborn_branch_of_a_clone_of_an_empty_repository_reports_success_without_a_number()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        repository.AddBareRemote();
        string clone = repository.CloneRemote("clone");
        // The name of the unborn branch of the clone depends on the git version; the test sets it and its upstream.
        repository.RunGitIn(clone, "symbolic-ref", "HEAD", "refs/heads/main");
        repository.RunGitIn(clone, "config", "branch.main.remote", "origin");
        repository.RunGitIn(clone, "config", "branch.main.merge", "refs/heads/main");
        repository.RunGit("push", "-q", "origin", "main");
        // A merge into an unborn branch leaves ORIG_HEAD as it is; counted from this one, the pull would bring 1.
        repository.RunGitIn(clone, "fetch", "-q", "origin");
        repository.RunGitIn(clone, "update-ref", "ORIG_HEAD", "origin/main~1");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(clone, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Null(pulled.PulledCommitCount);
        Assert.Equal(repository.RunGit("rev-parse", "HEAD").Trim(), repository.RunGitIn(clone, "rev-parse", "HEAD").Trim());
        Assert.Equal("1", repository.RunGitIn(clone, "rev-list", "--count", "ORIG_HEAD..@{upstream}").Trim());
    }

    [Fact]
    public async Task Pull_counts_only_the_pulled_commits_and_not_a_commit_made_right_after_the_merge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "b.txt", "b\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        string remoteHead = repository.RunGitIn(clone, "rev-parse", "HEAD").Trim();
        // Git runs post-merge after a fast-forward, so this commit lands after the merge and before the count.
        WriteHook(
            Path.Combine(repository.Path, ".git", "hooks", "post-merge"),
            "#!/bin/sh\ngit commit -q --allow-empty -m late\n");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal("late", repository.RunGit("log", "-1", "--format=%s").Trim());
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "HEAD~1").Trim());
    }

    [Fact]
    public async Task Pull_counts_only_the_pulled_commits_and_not_those_of_a_fetch_right_after_the_merge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "b.txt", "b\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        string remoteHead = repository.RunGitIn(clone, "rev-parse", "HEAD").Trim();
        // Git runs post-merge after a fast-forward, so this push and fetch move the upstream after the merge and
        // before the count.
        WriteHook(
            Path.Combine(repository.Path, ".git", "hooks", "post-merge"),
            "#!/bin/sh\ngit -C ../clone commit -q --allow-empty -m late\n"
                + "git -C ../clone push -q\ngit fetch -q origin\n");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(1, pulled.PulledCommitCount);
        Assert.Equal(remoteHead, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal("late", repository.RunGit("log", "-1", "--format=%s", "origin/main").Trim());
        Assert.Equal("2", repository.RunGit("rev-list", "--count", "ORIG_HEAD..@{upstream}").Trim());
        Assert.Equal("pull: Fast-forward", repository.RunGit("reflog", "-1", "--format=%gs", "main").Trim());
    }

    [Fact]
    public async Task Pull_with_two_merge_values_whose_first_was_deleted_on_the_remote_reports_the_upstream_as_gone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("fetch", "-q", "origin");
        repository.RunGit("switch", "-q", "--track", "origin/feature");
        repository.RunGit("config", "--add", "branch.feature.merge", "refs/heads/main");
        repository.RunGitIn(repository.RemotePath!, "branch", "-D", "feature");
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);

        // The fetch marks refs/heads/main for merge, but the upstream is the first value.
        Assert.Contains(
            "\t\tbranch 'main' of ",
            File.ReadAllText(Path.Combine(repository.Path, ".git", "FETCH_HEAD")),
            StringComparison.Ordinal);
        Assert.Equal(GitRemoteFailureKind.UpstreamGone, pulled.FailureKind);
        Assert.Contains(
            "'refs/heads/feature' from the remote, but no such ref was fetched",
            pulled.Message,
            StringComparison.Ordinal);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Theory]
    [InlineData(SHA + "\t\tbranch 'main' of ../remote\n", "refs/heads/main", true)]
    [InlineData(SHA + "\tnot-for-merge\tbranch 'main' of ../remote\n", "refs/heads/main", false)]
    [InlineData(SHA + "\t\tbranch 'other' of ../remote\n", "refs/heads/gone", false)]
    [InlineData(TWO_BRANCHES, "refs/heads/main", true)]
    [InlineData(SHA + "\t\tbranch 'main/x' of ../remote\n", "refs/heads/main", false)]
    [InlineData(SHA + "\t\t../nofetch\n", "refs/heads/main", false)]
    [InlineData(SHA + "\t\tbranch 'main' of .\n", "refs/heads/main", true)]
    [InlineData(SHA + "\t\tbranch 'it's' of ../remote\n", "refs/heads/it's", true)]
    [InlineData(SHA + "\t\tbranch 'main' of ../re mote ü\n", "refs/heads/main", true)]
    [InlineData(SHA + "\t\ttag 'v1' of ../remote\n", "refs/tags/v1", true)]
    [InlineData(SHA + "\t\tremote-tracking branch 'origin/main' of .\n", "refs/remotes/origin/main", true)]
    [InlineData(SHA + "\t\t'refs/pull/1/head' of ../remote\n", "refs/pull/1/head", true)]
    [InlineData(SHA + "\t\t../remote\n", "HEAD", true)]
    [InlineData(SHA + "\t\t'lead\n", "HEAD", true)]
    [InlineData(SHA + "\t\tbranch 'main' of ../remote\n", "HEAD", true)]
    [InlineData(SHA + "\tnot-for-merge\tbranch 'main' of ../remote\n", "HEAD", false)]
    [InlineData(SHA + "\t\tbranch 'main' of ../remote\n", "main", false)]
    [InlineData("", "refs/heads/main", false)]
    public void Only_the_fetch_head_entry_for_the_merge_reference_counts(string fetchHead, string merge, bool expected)
    {
        Assert.Equal(expected, GitService.HasMergeEntry(fetchHead, merge));
    }

    [Theory]
    [InlineData("refs/heads/main")]
    [InlineData("refs/tags/v1")]
    [InlineData("refs/remotes/origin/main")]
    [InlineData("refs/pull/1/head")]
    [InlineData("HEAD")]
    public void The_fetch_head_entry_of_git_fetch_counts_for_its_merge_reference_only(string merge)
    {
        string[] merges = { "refs/heads/main", "refs/tags/v1", "refs/remotes/origin/main", "refs/pull/1/head", "HEAD" };
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("tag", "v1");
        repository.RunGit("update-ref", "refs/remotes/origin/main", "HEAD");
        repository.RunGit("update-ref", "refs/pull/1/head", "HEAD");
        // With the remote '.' git fetch takes the merge value from the repository itself and marks it for merge.
        repository.RunGit("config", "branch.main.remote", ".");
        repository.RunGit("config", "branch.main.merge", merge);
        repository.RunGit("fetch", "-q", ".");
        string fetchHead = File.ReadAllText(Path.Combine(repository.Path, ".git", "FETCH_HEAD"));

        Assert.Single(fetchHead.Split('\n'), line => line.Split('\t') is [_, "", ..]);
        foreach (string other in merges)
        {
            // Any line marked for merge counts for HEAD.
            bool expected = other == merge || other == "HEAD";
            Assert.True(expected == GitService.HasMergeEntry(fetchHead, other), $"{other} in:\n{fetchHead}");
        }
    }

    [Fact]
    public async Task Pull_on_a_detached_head_says_that_no_branch_is_checked_out()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "--detach");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal(GitRemoteFailureKind.Other, pulled.FailureKind);
        Assert.Equal("You are not currently on a branch.", pulled.Message);
    }

    [Fact]
    public async Task Pull_of_a_branch_without_upstream_fails_and_runs_no_fetch()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        List<string> commands = new();
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            ProcessStarted = (arguments, _) =>
            {
                lock (commands)
                {
                    commands.Add(arguments[0]);
                }
            }
        });

        GitRemoteResult pulled = await service.PullAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.Equal(GitRemoteFailureKind.NoUpstream, pulled.FailureKind);
        Assert.DoesNotContain("fetch", commands);
        Assert.DoesNotContain("merge", commands);
    }

    [Fact]
    public async Task A_push_behind_the_remote_is_rejected_and_the_pull_reports_the_divergence()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "from-clone.txt", "c\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        repository.CommitFileIn(repository.Path, "local.txt", "l\n", "local");
        string localHead = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pushed = await service.PushAsync(repository.Path, ct: ct);
        Assert.Equal(GitRemoteFailureKind.Rejected, pushed.FailureKind);
        Assert.False(pushed.Success, pushed.Message);

        GitRemoteResult pulled = await service.PullAsync(repository.Path, ct);
        Assert.False(pulled.Success);
        Assert.Equal(GitRemoteFailureKind.Diverged, pulled.FailureKind);
        Assert.Equal(localHead, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal(string.Empty, repository.RunGit("status", "--porcelain").Trim());
    }

    [Fact]
    public async Task A_push_declined_by_a_server_hook_is_told_apart_from_a_push_behind_the_remote()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        WriteHook(
            Path.Combine(remote, "hooks", "pre-receive"),
            "#!/bin/sh\necho \"GH006: Protected branch update failed\" >&2\nexit 1\n");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult pushed = await service.PushAsync(repository.Path, "origin", TestContext.Current.CancellationToken);

        Assert.Equal(GitRemoteFailureKind.RemoteRejected, pushed.FailureKind);
        Assert.Contains("pre-receive hook declined", pushed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fetch_of_a_broken_remote_still_fetches_the_others_and_names_them()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("remote", "add", "broken", Path.Combine(repository.RootPath, "missing.git"));
        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "from-clone.txt", "c\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        GitService service = new(options: repository.ProcessOptions);

        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);

        Assert.False(fetched.Success);
        Assert.Equal(["origin"], fetched.FetchedRemotes);
        Assert.StartsWith("broken: ", fetched.Message, StringComparison.Ordinal);
        Assert.Equal(repository.RunGitIn(clone, "rev-parse", "HEAD").Trim(), repository.RunGit("rev-parse", "origin/main").Trim());
    }

    [Fact]
    public async Task Fetch_prunes_deleted_remote_branches_only_as_configured_and_their_local_branches_then_show_the_upstream_as_gone()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        repository.RunGit("push", "-q", "origin", "main:old");
        repository.RunGit("branch", "--track", "old", "origin/old");
        string clone = repository.CloneRemote("clone");
        repository.RunGitIn(clone, "push", "-q", "origin", "--delete", "old");
        GitService service = new(options: repository.ProcessOptions);

        Assert.True((await service.FetchAsync(repository.Path, ct)).Success);
        IReadOnlyList<GitBranch> unpruned = await service.GetBranchesAsync(repository.Path, ct);
        Assert.Equal(new[] { "main", "old", "origin/main", "origin/old" }, unpruned.Select(branch => branch.Name));
        Assert.False(Assert.Single(unpruned, branch => branch.Name == "old").IsUpstreamGone);

        repository.RunGit("config", "remote.origin.prune", "true");
        GitRemoteResult fetched = await service.FetchAsync(repository.Path, ct);

        Assert.True(fetched.Success, fetched.Message);
        IReadOnlyList<GitBranch> branches = await service.GetBranchesAsync(repository.Path, ct);
        Assert.Equal(new[] { "main", "old", "origin/main" }, branches.Select(branch => branch.Name));
        GitBranch old = Assert.Single(branches, branch => branch.Name == "old");
        Assert.True(old.IsUpstreamGone);
        Assert.Equal("origin/old", old.UpstreamName);
    }

    [Fact]
    public async Task Remote_branches_of_a_remote_with_a_slash_in_its_name_belong_to_that_remote()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string remote = repository.AddBareRemote();
        repository.RunGit("push", "-q", "origin", "main:feature");
        repository.RunGit("remote", "add", "team/fork", remote);
        GitService service = new(options: repository.ProcessOptions);

        Assert.True((await service.FetchAsync(repository.Path, ct)).Success);
        GitBranch feature = Assert.Single(
            await service.GetBranchesAsync(repository.Path, ct),
            branch => branch.FullName == "refs/remotes/team/fork/feature");

        Assert.Equal("team/fork", feature.RemoteName);
        Assert.Equal("feature", feature.LocalName);
    }

    [Fact]
    public async Task A_running_fetch_does_not_hold_up_writing_commands()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using BlockingRemote remote = new(repository);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            ProcessStarted = (arguments, _) =>
            {
                if (arguments[0] == "fetch")
                {
                    started.TrySetResult();
                }
            }
        });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<GitRemoteResult> fetch = service.FetchAsync(repository.Path, cancellation.Token);
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            repository.WriteFile("b.txt", "b\n");

            // Bounded, so a fetch that holds up the stage fails the test instead of hanging it.
            await service.StageAsync(repository.Path, ["b.txt"], TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            Assert.False(fetch.IsCompleted);
            Assert.Contains("A  b.txt", repository.RunGit("status", "--porcelain"), StringComparison.Ordinal);
        }
        finally
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task Cancelling_a_blocked_fetch_kills_git_with_its_child_processes_and_throws()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using BlockingRemote remote = new(repository);
        TaskCompletionSource<Process> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            ProcessStarted = (arguments, id) =>
            {
                if (arguments[0] == "fetch")
                {
                    started.TrySetResult(Process.GetProcessById(id));
                }
            }
        });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<GitRemoteResult> fetch = service.FetchAsync(repository.Path, cancellation.Token);
        using Process git = await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using Process helper = await remote.WaitForHelperAsync(fetch);
        try
        {
            Assert.False(fetch.IsCompleted);

            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
            Assert.True(git.HasExited);
            Assert.True(helper.WaitForExit(TimeSpan.FromSeconds(30)), "The child process of git survived the cancellation.");
        }
        finally
        {
            KillIfRunning(helper);
        }
    }

    [Fact]
    public async Task Cancelling_a_pull_during_its_fetch_throws_and_leaves_the_branch()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using BlockingRemote remote = new(repository);
        repository.RunGit("config", "branch.main.remote", "origin");
        repository.RunGit("config", "branch.main.merge", "refs/heads/main");
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        Task<GitRemoteResult> pull = service.PullAsync(repository.Path, cancellation.Token);
        using Process helper = await remote.WaitForHelperAsync(pull);
        try
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pull);
            Assert.Equal(head, repository.RunGit("rev-parse", "HEAD").Trim());
            Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "index.lock")));
        }
        finally
        {
            KillIfRunning(helper);
        }
    }

    [Fact]
    public async Task A_blocked_fetch_ends_with_a_timeout_result_after_the_network_timeout()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        using BlockingRemote remote = new(repository);
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            NetworkTimeout = TimeSpan.FromMilliseconds(500)
        });

        GitRemoteResult result = await service.FetchAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(GitRemoteFailureKind.Timeout, result.FailureKind);
    }

    [Fact]
    public async Task Network_commands_run_untranslated_and_use_batch_mode_ssh_only_without_own_ssh_settings()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        Dictionary<string, string?> environment = new(repository.Environment)
        {
            ["GIT_SSH"] = null,
            ["GIT_SSH_COMMAND"] = null
        };
        GitService service = new(options: new GitProcessOptions { Environment = environment });

        IReadOnlyDictionary<string, string?> defaults = await service.CreateNetworkEnvironmentAsync(repository.Path, ct);
        Assert.Equal("C", defaults["LC_ALL"]);
        Assert.Equal(GitService.BATCH_MODE_SSH_COMMAND, defaults["GIT_SSH_COMMAND"]);

        repository.RunGit("config", "core.sshCommand", "plink -batch");
        IReadOnlyDictionary<string, string?> configured = await service.CreateNetworkEnvironmentAsync(repository.Path, ct);
        Assert.False(configured.ContainsKey("GIT_SSH_COMMAND"));
    }

    [Theory]
    [InlineData(null, null, null, true)]
    [InlineData("plink.exe", null, null, false)]
    [InlineData(null, "ssh -i key", null, false)]
    [InlineData(null, null, "ssh -F config", false)]
    [InlineData("  ", "", "", true)]
    public void Batch_mode_ssh_is_used_only_when_no_ssh_program_is_configured(
        string? gitSsh,
        string? gitSshCommand,
        string? coreSshCommand,
        bool expected)
    {
        Dictionary<string, string?> process = new() { ["GIT_SSH"] = gitSsh, ["GIT_SSH_COMMAND"] = gitSshCommand };

        bool batchMode = GitService.UsesBatchModeSsh(
            name => process.GetValueOrDefault(name),
            new Dictionary<string, string?>(),
            coreSshCommand);

        Assert.Equal(expected, batchMode);
    }

    [Fact]
    public void Options_environment_overrides_the_process_environment_for_ssh_settings()
    {
        Assert.True(GitService.UsesBatchModeSsh(
            _ => "plink.exe",
            new Dictionary<string, string?> { ["GIT_SSH"] = null, ["GIT_SSH_COMMAND"] = null },
            null));
        Assert.False(GitService.UsesBatchModeSsh(
            _ => null,
            new Dictionary<string, string?> { ["GIT_SSH_COMMAND"] = "ssh -v" },
            null));
    }

    [Fact]
    public void Remotes_with_skip_fetch_all_are_read_from_the_configuration()
    {
        string output = "remote.offline.skipfetchall\0"
            + "remote.team.fork.skipfetchall\nno\0"
            + "remote.slow.skipfetchall\nYes\0"
            + "remote.changed.skipfetchall\ntrue\0"
            + "remote.changed.skipfetchall\n0\0";

        Assert.Equal(["offline", "slow"], GitService.ParseSkipFetchAllRemotes(output).Order(StringComparer.Ordinal));
    }

    private static void WriteHook(string path, string text)
    {
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The process is ending.
        }
    }

    /// <summary>
    /// The remote <c>origin</c> of a repository whose transport is a helper process that git starts through
    /// <c>ext::</c>: the helper reports its process id through a <see cref="LoopbackSignal"/> and then waits without
    /// answering, so a fetch blocks until it is stopped. On Windows the helper is Windows PowerShell, elsewhere
    /// <c>bash</c> that replaces itself with <c>git cat-file --batch</c>. The helper command holds no file path.
    /// </summary>
    private sealed class BlockingRemote : IDisposable
    {
        private readonly LoopbackSignal _signal = new();

        public BlockingRemote(GitTestRepository repository)
        {
            string port = _signal.Port.ToString(CultureInfo.InvariantCulture);
            string helper = OperatingSystem.IsWindows()
                ? Escape(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
                    + " -NoProfile -NonInteractive -Command "
                    + Escape("$c = [Net.Sockets.TcpClient]::new('127.0.0.1', " + port + "); $s = $c.GetStream(); "
                        + "$b = [Text.Encoding]::ASCII.GetBytes([string]$PID + [char]10); $s.Write($b, 0, $b.Length); "
                        + "[void]$s.ReadByte()")
                : "bash -c "
                    + Escape("exec 3<>/dev/tcp/127.0.0.1/" + port + " && echo $$ >&3 && exec git cat-file --batch");
            repository.RunGit("config", "protocol.ext.allow", "always");
            repository.RunGit("remote", "add", "origin", "ext::" + helper);
        }

        /// <summary>Returns the helper process once it reported; fails when <paramref name="run"/> ends first.</summary>
        public async Task<Process> WaitForHelperAsync(Task run)
        {
            string id = await _signal.WaitForProcessAsync(run);
            return Process.GetProcessById(int.Parse(id.Trim(), CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            _signal.Dispose();
        }

        // In an ext:: command, spaces separate the arguments and % escapes; "% " is a space within an argument.
        private static string Escape(string argument) => argument.Replace("%", "%%").Replace(" ", "% ");
    }
}
