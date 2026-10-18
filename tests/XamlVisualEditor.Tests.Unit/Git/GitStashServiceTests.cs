using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitStashServiceTests
{
    [Fact]
    public async Task Push_with_message_and_untracked_files_lists_the_stash_with_its_fields()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("a.txt", "changed\n");
        repository.WriteFile("neu.txt", "new\n");
        GitService service = new(options: repository.ProcessOptions);

        await service.PushAsync(repository.Path, "Grüße", includeUntracked: true, ct);
        IReadOnlyList<GitStashEntry> stashes = await service.ListAsync(repository.Path, ct);

        GitStashEntry stash = Assert.Single(stashes);
        Assert.Equal(0, stash.Index);
        Assert.Equal("stash@{0}", stash.Name);
        Assert.Equal(repository.RunGit("rev-parse", "stash@{0}").Trim(), stash.ObjectId);
        Assert.Equal("On main: Grüße", stash.Message);
        Assert.InRange(stash.Created, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(5));
        Assert.Equal(string.Empty, repository.RunGit("status", "--porcelain"));
    }

    [Fact]
    public async Task Push_without_untracked_files_keeps_them_and_push_without_changes_creates_no_stash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("neu.txt", "new\n");
        GitService service = new(options: repository.ProcessOptions);

        await service.PushAsync(repository.Path, null, includeUntracked: false, ct);

        Assert.Empty(await service.ListAsync(repository.Path, ct));
        Assert.True(File.Exists(Path.Combine(repository.Path, "neu.txt")));
    }

    [Fact]
    public async Task Push_of_paths_stashes_only_them_with_their_untracked_files_and_takes_a_name_with_brackets_literally()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "b.txt", "a1.txt");
        string committed = repository.RunGit("show", "HEAD:a.txt");
        repository.WriteFile("a.txt", "changed\n");
        repository.WriteFile("b.txt", "changed\n");
        repository.WriteFile("a1.txt", "changed\n");
        repository.WriteFile("a[1].txt", "new\n");
        repository.WriteFile("dir/new.txt", "new\n");
        GitService service = new(options: repository.ProcessOptions);

        await service.PushPathsAsync(repository.Path, "part", ["a.txt", "a[1].txt", "dir"], ct);

        Assert.Equal(committed, repository.ReadFile("a.txt"));
        Assert.Null(repository.ReadFile("a[1].txt"));
        Assert.Null(repository.ReadFile("dir/new.txt"));
        // A pattern "a[1].txt" would have matched a1.txt.
        Assert.Equal("changed\n", repository.ReadFile("a1.txt"));
        Assert.Equal("changed\n", repository.ReadFile("b.txt"));
        Assert.Equal("On main: part", Assert.Single(await service.ListAsync(repository.Path, ct)).Message);
        Assert.Equal("new\n", repository.RunGit("show", "stash@{0}^3:a[1].txt"));
    }

    [Fact]
    public async Task Push_of_the_staged_changes_keeps_the_unstaged_and_untracked_ones()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "b.txt");
        string committed = repository.RunGit("show", "HEAD:a.txt");
        repository.WriteFile("a.txt", "staged\n");
        repository.RunGit("add", "a.txt");
        repository.WriteFile("b.txt", "unstaged\n");
        repository.WriteFile("new.txt", "new\n");
        GitService service = new(options: repository.ProcessOptions);

        await service.PushStagedAsync(repository.Path, null, ct);

        Assert.Equal(committed, repository.ReadFile("a.txt"));
        Assert.Equal("unstaged\n", repository.ReadFile("b.txt"));
        Assert.Equal("new\n", repository.ReadFile("new.txt"));
        Assert.Equal("a.txt", repository.RunGit("stash", "show", "--name-only").Trim());
    }

    [Fact]
    public async Task Apply_by_commit_id_keeps_the_stash_after_the_list_changed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "b.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("a.txt", "first\n");
        await service.PushAsync(repository.Path, "first", includeUntracked: false, ct);
        GitStashEntry first = Assert.Single(await service.ListAsync(repository.Path, ct));
        repository.WriteFile("b.txt", "second\n");
        await service.PushAsync(repository.Path, "second", includeUntracked: false, ct);

        await service.ApplyAsync(repository.Path, first, ct);

        Assert.Equal("first\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
        Assert.Equal("b.txt\n", File.ReadAllText(Path.Combine(repository.Path, "b.txt")));
        Assert.Equal(2, (await service.ListAsync(repository.Path, ct)).Count);
    }

    [Fact]
    public async Task Pop_with_a_conflict_fails_and_keeps_the_stash_and_the_conflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("a.txt", "stashed\n");
        await service.PushAsync(repository.Path, null, includeUntracked: false, ct);
        repository.CommitFileIn(repository.Path, "a.txt", "committed\n", "conflicting");
        GitStashEntry stash = Assert.Single(await service.ListAsync(repository.Path, ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.PopAsync(repository.Path, stash, ct));

        Assert.Equal(stash, Assert.Single(await service.ListAsync(repository.Path, ct)));
        Assert.StartsWith("UU a.txt", repository.RunGit("status", "--porcelain"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Branch_applies_the_stash_on_a_new_branch_at_its_commit_and_removes_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        string stashedOn = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.WriteFile("a.txt", "stashed\n");
        await service.PushAsync(repository.Path, "work", includeUntracked: false, ct);
        repository.CommitFileIn(repository.Path, "a.txt", "later\n", "Later");
        GitStashEntry stash = Assert.Single(await service.ListAsync(repository.Path, ct));

        Assert.True(await service.BranchAsync(repository.Path, stash, "from-stash", ct));

        Assert.Equal("from-stash", repository.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim());
        Assert.Equal(stashedOn, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal("stashed\n", repository.ReadFile("a.txt"));
        Assert.Empty(await service.ListAsync(repository.Path, ct));
    }

    [Fact]
    public async Task Branch_of_a_stash_that_moved_changes_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "b.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("a.txt", "first\n");
        await service.PushAsync(repository.Path, "first", includeUntracked: false, ct);
        GitStashEntry first = Assert.Single(await service.ListAsync(repository.Path, ct));
        repository.WriteFile("b.txt", "second\n");
        repository.RunGit("stash", "push", "-q", "-m", "pushed elsewhere");

        Assert.False(await service.BranchAsync(repository.Path, first, "from-stash", ct));
        Assert.False(repository.TryRunGit(out _, "rev-parse", "--verify", "--quiet", "refs/heads/from-stash"));
        Assert.Equal(2, (await service.ListAsync(repository.Path, ct)).Count);
    }

    [Fact]
    public async Task Drop_and_pop_of_a_stash_that_moved_in_the_list_change_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "b.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("a.txt", "first\n");
        await service.PushAsync(repository.Path, "first", includeUntracked: false, ct);
        GitStashEntry first = Assert.Single(await service.ListAsync(repository.Path, ct));
        repository.WriteFile("b.txt", "second\n");
        repository.RunGit("stash", "push", "-q", "-m", "pushed elsewhere");

        Assert.False(await service.DropAsync(repository.Path, first, ct));
        Assert.False(await service.PopAsync(repository.Path, first, ct));

        IReadOnlyList<GitStashEntry> stashes = await service.ListAsync(repository.Path, ct);
        Assert.Equal(["On main: pushed elsewhere", "On main: first"], stashes.Select(stash => stash.Message));
        Assert.True(await service.DropAsync(repository.Path, stashes[1], ct));
        Assert.Equal("On main: pushed elsewhere", Assert.Single(await service.ListAsync(repository.Path, ct)).Message);
    }

    [Fact]
    public async Task Pop_applies_and_removes_the_stash()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("a.txt", "stashed\n");
        await service.PushAsync(repository.Path, null, includeUntracked: false, ct);
        GitStashEntry stash = Assert.Single(await service.ListAsync(repository.Path, ct));

        Assert.True(await service.PopAsync(repository.Path, stash, ct));

        Assert.Empty(await service.ListAsync(repository.Path, ct));
        Assert.Equal("stashed\n", File.ReadAllText(Path.Combine(repository.Path, "a.txt")));
    }
}
