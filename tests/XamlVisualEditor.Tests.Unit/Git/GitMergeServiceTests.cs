using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitMergeServiceTests
{
    private static readonly string[] ConflictedPaths =
    [
        "added.txt", "binary.dat", "content.txt", "deleted-by-them.txt", "deleted-by-us.txt",
        "rename-deleted-feature.txt", "renamed-feature.txt", "renamed-main.txt", "renamed.txt"
    ];

    [Fact]
    public async Task Merge_with_conflicts_stops_with_every_conflict_kind_and_keeps_the_local_edit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict(startMerge: false);
        GitService service = new(options: repository.ProcessOptions);

        GitMergeResult result = await service.MergeAsync(repository.Path, "feature", ct);
        GitRepositoryStatus status = await service.GetStatusAsync(repository.Path, ct);

        Assert.Equal(GitMergeResult.Stopped, result);
        Assert.Equal(GitRepositoryOperation.Merging, status.Operation);
        Dictionary<string, GitConflictKind> kinds = status.Changes
            .Where(change => change.IsConflicted)
            .ToDictionary(change => change.Path, change => change.ConflictKind);
        Assert.Equal(
            new Dictionary<string, GitConflictKind>
            {
                ["added.txt"] = GitConflictKind.BothAdded,
                ["binary.dat"] = GitConflictKind.BothModified,
                ["content.txt"] = GitConflictKind.BothModified,
                ["deleted-by-them.txt"] = GitConflictKind.DeletedByThem,
                ["deleted-by-us.txt"] = GitConflictKind.DeletedByUs,
                ["rename-deleted-feature.txt"] = GitConflictKind.DeletedByUs,
                ["renamed-feature.txt"] = GitConflictKind.AddedByThem,
                ["renamed-main.txt"] = GitConflictKind.AddedByUs,
                ["renamed.txt"] = GitConflictKind.BothDeleted
            },
            kinds);
        Assert.Equal("local\nlocal edit\n", repository.ReadFile("local.txt"));
        Assert.Contains("<<<<<<< HEAD\nMAIN\n=======\nFEATURE\n>>>>>>> feature\n", repository.ReadFile("content.txt"));
    }

    [Fact]
    public async Task Taking_ours_for_every_conflict_gives_head_with_the_clean_merge_results()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);

        Assert.True(await service.TakeSideAsync(repository.Path, ConflictedPaths, GitConflictSide.Ours, ct));

        Assert.Equal(string.Empty, repository.RunGit("ls-files", "-u"));
        Assert.Equal(
            ["added.txt", "binary.dat", "clean.txt", "content.txt", "deleted-by-them.txt", "local.txt", "renamed-main.txt"],
            TrackedFiles(repository));
        Assert.Equal("a\nMAIN\nc\n", repository.ReadFile("content.txt"));
        Assert.Equal("main add\n", repository.ReadFile("added.txt"));
        Assert.Equal("\0\u0001main", repository.ReadFile("binary.dat"));
        Assert.Equal("y2\n", repository.ReadFile("deleted-by-them.txt"));
        Assert.Null(repository.ReadFile("deleted-by-us.txt"));
        Assert.Null(repository.ReadFile("renamed-feature.txt"));
        Assert.Equal("k2\n", repository.ReadFile("clean.txt"));
        Assert.Equal("local\nlocal edit\n", repository.ReadFile("local.txt"));
        // Everything except the clean merge result is HEAD again; the local edit stays unstaged.
        Assert.Equal("clean.txt\n", repository.RunGit("diff", "--cached", "--name-only", "HEAD"));
    }

    [Fact]
    public async Task Taking_theirs_for_every_conflict_gives_the_files_of_the_merged_branch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);

        Assert.True(await service.TakeSideAsync(repository.Path, ConflictedPaths, GitConflictSide.Theirs, ct));

        Assert.Equal(string.Empty, repository.RunGit("ls-files", "-u"));
        Assert.Equal(
            ["added.txt", "binary.dat", "clean.txt", "content.txt", "deleted-by-us.txt", "local.txt", "rename-deleted-feature.txt", "renamed-feature.txt"],
            TrackedFiles(repository));
        Assert.Equal("a\nFEATURE\nc\n", repository.ReadFile("content.txt"));
        Assert.Equal("feature add\n", repository.ReadFile("added.txt"));
        Assert.Equal("\0\u0001feature", repository.ReadFile("binary.dat"));
        Assert.Equal("x2\n", repository.ReadFile("deleted-by-us.txt"));
        Assert.Null(repository.ReadFile("deleted-by-them.txt"));
        Assert.Null(repository.ReadFile("renamed-main.txt"));
        Assert.Equal("local\nlocal edit\n", repository.ReadFile("local.txt"));
        // Apart from the uncommitted local edit, the index is the merged branch.
        Assert.Equal(string.Empty, repository.RunGit("diff", "--cached", "--name-only", "feature"));
    }

    [Fact]
    public async Task Taking_a_side_refuses_and_changes_nothing_when_a_path_is_resolved_already()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("content.txt", "resolved\n");
        repository.RunGit("add", "content.txt");
        repository.WriteFile("content.txt", "resolved and edited\n");

        bool taken = await service.TakeSideAsync(repository.Path, ["added.txt", "content.txt"], GitConflictSide.Theirs, ct);

        Assert.False(taken);
        Assert.Equal("resolved and edited\n", repository.ReadFile("content.txt"));
        Assert.Equal("resolved\n", repository.RunGit("show", ":content.txt"));
        Assert.Contains("added.txt", repository.RunGit("ls-files", "-u"));
        Assert.Contains("<<<<<<< HEAD", repository.ReadFile("added.txt"));
    }

    [Fact]
    public async Task Merge_fast_forwards_and_reports_an_already_merged_branch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        GitService service = new(options: repository.ProcessOptions);

        GitMergeResult first = await service.MergeAsync(repository.Path, "feature", ct);
        GitMergeResult second = await service.MergeAsync(repository.Path, "feature", ct);

        Assert.Equal(GitMergeResult.Merged, first);
        Assert.Equal(GitMergeResult.AlreadyUpToDate, second);
        Assert.Equal(repository.RunGit("rev-parse", "feature"), repository.RunGit("rev-parse", "main"));
        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task Merge_of_an_unknown_branch_fails_with_the_message_of_git()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.MergeAsync(repository.Path, "missing", ct));

        Assert.Contains("missing", exception.Message, StringComparison.Ordinal);
        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task Merge_refused_because_of_a_local_change_throws_and_starts_no_merge()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict(startMerge: false);
        GitService service = new(options: repository.ProcessOptions);
        repository.WriteFile("content.txt", "uncommitted\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(repository.Path, "feature", ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal("uncommitted\n", repository.ReadFile("content.txt"));
    }

    [Fact]
    public async Task Merge_stopped_by_a_failing_pre_merge_commit_hook_is_stopped_without_conflicts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "c.txt", "c\n", "Main");
        WriteHook(Path.Combine(repository.Path, ".git", "hooks", "pre-merge-commit"), "#!/bin/sh\nexit 1\n");
        GitService service = new(options: repository.ProcessOptions);

        GitMergeResult result = await service.MergeAsync(repository.Path, "feature", ct);
        GitRepositoryStatus status = await service.GetStatusAsync(repository.Path, ct);

        Assert.Equal(GitMergeResult.Stopped, result);
        Assert.Equal(GitRepositoryOperation.Merging, status.Operation);
        Assert.DoesNotContain(status.Changes, change => change.IsConflicted);
    }

    [Fact]
    public async Task Abort_restores_head_and_keeps_an_edit_of_a_file_the_merge_did_not_touch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);
        string head = repository.RunGit("rev-parse", "HEAD");

        // The confirmed operation is checked first: a confirmed rebase aborts no merge.
        Assert.False(await service.AbortAsync(repository.Path, GitRepositoryOperation.Rebasing, ct));
        Assert.Equal(GitRepositoryOperation.Merging, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        Assert.True(await service.AbortAsync(repository.Path, GitRepositoryOperation.Merging, ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("a\nMAIN\nc\n", repository.ReadFile("content.txt"));
        Assert.Equal("local\nlocal edit\n", repository.ReadFile("local.txt"));
    }

    [Fact]
    public async Task Abort_refused_by_git_keeps_the_merge_and_the_edit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);
        // clean.txt was merged without conflict; git refuses to abort over an edit of it.
        repository.WriteFile("clean.txt", "edited after the merge started\n");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AbortAsync(repository.Path, GitRepositoryOperation.Merging, ct));

        Assert.Equal(GitRepositoryOperation.Merging, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal("edited after the merge started\n", repository.ReadFile("clean.txt"));
    }

    [Fact]
    public async Task Commit_after_taking_ours_everywhere_completes_the_merge_with_two_parents()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        GitService service = new(options: repository.ProcessOptions);
        await service.TakeSideAsync(repository.Path, ConflictedPaths, GitConflictSide.Ours, ct);

        string? message = await service.GetPreparedMessageAsync(repository.Path, ct);
        await service.CommitAsync(repository.Path, message!, ct);

        Assert.Equal("Merge branch 'feature'", message);
        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(3, repository.RunGit("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
        Assert.Equal("Merge branch 'feature'", repository.RunGit("log", "-1", "--format=%B").Trim());
    }

    [Fact]
    public async Task Prepared_message_is_null_without_an_operation()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Null(await service.GetPreparedMessageAsync(repository.Path, ct));
    }

    [Fact]
    public async Task A_cherry_pick_with_a_conflict_is_detected_and_continues_after_taking_theirs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "cherry-pick", "feature"));
        Assert.Equal(GitRepositoryOperation.CherryPicking, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        Assert.True(await service.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, ct));
        await service.ContinueAsync(repository.Path, ct);

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal("feature\n", repository.ReadFile("a.txt"));
        Assert.Equal("Feature", repository.RunGit("log", "-1", "--format=%s").Trim());
    }

    [Fact]
    public async Task A_rebase_with_a_conflict_is_detected_and_continues_after_taking_a_side()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        GitService service = new(options: repository.ProcessOptions);
        repository.RunGit("switch", "-q", "feature");
        Assert.False(repository.TryRunGit(out _, "rebase", "main"));
        GitRepositoryStatus stopped = await service.GetStatusAsync(repository.Path, ct);
        Assert.Equal(GitRepositoryOperation.Rebasing, stopped.Operation);
        Assert.False(stopped.IsStoppedForEdit);

        Assert.True(await service.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, ct));
        await service.ContinueAsync(repository.Path, ct);

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(repository.RunGit("rev-parse", "main"), repository.RunGit("rev-parse", "HEAD~1"));
    }

    [Fact]
    public async Task A_rebase_stopped_at_an_edit_step_is_stopped_for_edit_and_continues()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        GitService service = new(options: repository.ProcessOptions);
        // -i.bak is the in-place form that GNU and BSD sed both accept.
        repository.RunGit("-c", "sequence.editor=sed -i.bak -e s/^pick/edit/", "rebase", "-q", "-i", "HEAD~1");

        GitRepositoryStatus stopped = await service.GetStatusAsync(repository.Path, ct);
        Assert.Equal(GitRepositoryOperation.Rebasing, stopped.Operation);
        Assert.True(stopped.IsStoppedForEdit);

        await service.ContinueAsync(repository.Path, ct);

        GitRepositoryStatus done = await service.GetStatusAsync(repository.Path, ct);
        Assert.Equal(GitRepositoryOperation.None, done.Operation);
        Assert.False(done.IsStoppedForEdit);
    }

    [Theory]
    [InlineData("cherry-pick", (int)GitRepositoryOperation.CherryPicking)]
    [InlineData("revert", (int)GitRepositoryOperation.Reverting)]
    public async Task A_sequence_whose_stopped_step_was_committed_stays_detected_and_continues(string command, int operation)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string[] args;
        if (command == "cherry-pick")
        {
            // Both commits of feature are picked onto main; the first conflicts with main's change of a.txt.
            repository.RunGit("switch", "-q", "-c", "feature");
            repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
            repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Second");
            repository.RunGit("switch", "-q", "main");
            repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
            args = ["cherry-pick", "main..feature"];
        }
        else
        {
            // Reverting Two and then One; reverting Two conflicts with Three's change of a.txt.
            repository.CommitFileIn(repository.Path, "b.txt", "b\n", "One");
            repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
            repository.CommitFileIn(repository.Path, "a.txt", "three\n", "Three");
            args = ["revert", "--no-edit", "HEAD~1", "HEAD~2"];
        }

        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, args));
        Assert.True(await service.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, ct));
        repository.RunGit("commit", "-q", "--no-edit");

        Assert.Equal((GitRepositoryOperation)operation, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        await service.ContinueAsync(repository.Path, ct);

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(command == "cherry-pick" ? "b\n" : null, repository.ReadFile("b.txt"));
    }

    [Fact]
    public async Task A_revert_with_a_conflict_is_detected_and_aborted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.CommitFileIn(repository.Path, "a.txt", "three\n", "Three");
        string head = repository.RunGit("rev-parse", "HEAD");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "revert", "--no-edit", "HEAD~1"));
        Assert.Equal(GitRepositoryOperation.Reverting, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        Assert.True(await service.AbortAsync(repository.Path, GitRepositoryOperation.Reverting, ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("three\n", repository.ReadFile("a.txt"));
    }

    [Fact]
    public async Task A_patch_application_with_a_conflict_is_detected_and_aborted()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        string patch = Path.Combine(repository.RootPath, "feature.patch");
        File.WriteAllText(patch, repository.RunGit("format-patch", "-1", "--stdout", "feature"));
        string head = repository.RunGit("rev-parse", "HEAD");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "am", "-3", patch));
        Assert.Equal(GitRepositoryOperation.ApplyingPatches, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        Assert.True(await service.AbortAsync(repository.Path, GitRepositoryOperation.ApplyingPatches, ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));
    }

    [Fact]
    public async Task Abort_without_an_operation_changes_nothing_and_continue_fails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        Assert.False(await service.AbortAsync(repository.Path, GitRepositoryOperation.Merging, ct));
        Assert.False(await service.AbortAsync(repository.Path, GitRepositoryOperation.None, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ContinueAsync(repository.Path, ct));
    }

    [Fact]
    public async Task Cherry_pick_applies_a_commit_and_a_conflicting_one_stops_until_skip_keeps_head_and_other_edits()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        repository.RunGit("switch", "-q", "feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        string addB = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.RunGit("switch", "-q", "main");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(GitMergeResult.Merged, await service.CherryPickAsync(repository.Path, addB, ct));
        Assert.Equal("b\n", repository.ReadFile("b.txt"));
        Assert.Equal("Add b", repository.RunGit("log", "-1", "--format=%s").Trim());

        string head = repository.RunGit("rev-parse", "HEAD");
        repository.WriteFile("b.txt", "edited\n");
        Assert.Equal(GitMergeResult.Stopped, await service.CherryPickAsync(repository.Path, addB + "~1", ct));
        Assert.Equal(GitRepositoryOperation.CherryPicking, (await service.GetStatusAsync(repository.Path, ct)).Operation);

        Assert.True(await SkipCurrentStepAsync(service, repository, GitRepositoryOperation.CherryPicking));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("main\n", repository.ReadFile("a.txt"));
        Assert.Equal("edited\n", repository.ReadFile("b.txt"));
    }

    [Fact]
    public async Task Cherry_pick_of_a_commit_already_on_the_branch_stops_empty_and_skip_ends_it()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        string main = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(GitMergeResult.Stopped, await service.CherryPickAsync(repository.Path, main, ct));
        GitRepositoryStatus stopped = await service.GetStatusAsync(repository.Path, ct);
        Assert.Equal(GitRepositoryOperation.CherryPicking, stopped.Operation);
        Assert.Empty(stopped.Changes);

        Assert.True(await SkipCurrentStepAsync(service, repository, GitRepositoryOperation.CherryPicking));
        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(main, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task Revert_undoes_a_commit_and_a_conflicting_one_stops()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.CommitFileIn(repository.Path, "a.txt", "three\n", "Three");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(GitMergeResult.Merged, await service.RevertAsync(repository.Path, "HEAD~2", ct));
        Assert.Null(repository.ReadFile("b.txt"));
        Assert.StartsWith("Revert \"Add b\"", repository.RunGit("log", "-1", "--format=%s").Trim(), StringComparison.Ordinal);

        Assert.Equal(GitMergeResult.Stopped, await service.RevertAsync(repository.Path, "HEAD~2", ct));
        Assert.Equal(GitRepositoryOperation.Reverting, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task Cherry_pick_refused_because_of_a_local_change_throws_and_starts_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        repository.WriteFile("a.txt", "uncommitted\n");
        GitService service = new(options: repository.ProcessOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CherryPickAsync(repository.Path, "feature", ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal("uncommitted\n", repository.ReadFile("a.txt"));
    }

    [Fact]
    public async Task Skip_of_a_rebase_step_drops_the_step_and_discards_every_other_uncommitted_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        repository.RunGit("switch", "-q", "feature");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "rebase", "main"));
        repository.WriteFile("b.txt", "edited\n");

        Assert.True(await SkipCurrentStepAsync(service, repository, GitRepositoryOperation.Rebasing));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(repository.RunGit("rev-parse", "main"), repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("b\n", repository.ReadFile("b.txt"));
    }

    [Fact]
    public async Task Skip_refuses_a_merge_and_an_operation_other_than_the_confirmed_one()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        GitService service = new(options: repository.ProcessOptions);
        Assert.Equal(GitMergeResult.Stopped, await service.MergeAsync(repository.Path, "feature", ct));

        Assert.False(await service.SkipAsync(repository.Path, GitRepositoryOperation.Merging, null, ct));
        Assert.False(await service.SkipAsync(repository.Path, GitRepositoryOperation.CherryPicking, null, ct));
        Assert.False(await service.SkipAsync(repository.Path, GitRepositoryOperation.None, null, ct));

        Assert.Equal(GitRepositoryOperation.Merging, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task Rebase_replays_the_branch_reports_a_contained_upstream_and_stops_on_a_conflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "c.txt", "c\n", "Add c");
        repository.RunGit("switch", "-q", "feature");
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(GitMergeResult.Merged, await service.RebaseAsync(repository.Path, "refs/heads/main", "feature", ct));
        Assert.Equal(repository.RunGit("rev-parse", "main"), repository.RunGit("rev-parse", "HEAD~1"));
        Assert.Equal(GitMergeResult.AlreadyUpToDate, await service.RebaseAsync(repository.Path, "main", null, ct));

        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        repository.RunGit("switch", "-q", "feature");
        Assert.Equal(GitMergeResult.Stopped, await service.RebaseAsync(repository.Path, "main", null, ct));
        Assert.Equal(GitRepositoryOperation.Rebasing, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task Rebase_refused_because_of_an_uncommitted_change_throws_and_keeps_the_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        repository.RunGit("switch", "-q", "feature");
        string head = repository.RunGit("rev-parse", "HEAD");
        repository.WriteFile("a.txt", "uncommitted\n");
        GitService service = new(options: repository.ProcessOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RebaseAsync(repository.Path, "main", "feature", ct));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("uncommitted\n", repository.ReadFile("a.txt"));
    }

    [Fact]
    public async Task Skip_refuses_a_step_other_than_the_confirmed_one()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        // Picking Two and Three onto main conflicts at both, as main changed a.txt as well.
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.CommitFileIn(repository.Path, "a.txt", "three\n", "Three");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "cherry-pick", "main..feature"));
        string? confirmed = (await service.GetStatusAsync(repository.Path, ct)).OperationStep;
        Assert.NotNull(confirmed);

        // The pick goes on elsewhere while the question is open and stops at the next step.
        Assert.False(repository.TryRunGit(out _, "cherry-pick", "--skip"));
        string? current = (await service.GetStatusAsync(repository.Path, ct)).OperationStep;
        Assert.NotEqual(confirmed, current);

        Assert.False(await service.SkipAsync(repository.Path, GitRepositoryOperation.CherryPicking, confirmed, ct));
        Assert.Equal(current, (await service.GetStatusAsync(repository.Path, ct)).OperationStep);
    }

    [Theory]
    [InlineData("revert")]
    [InlineData("am")]
    public async Task Skip_of_a_revert_or_patch_step_keeps_the_other_uncommitted_changes(string command)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        GitService service = new(options: repository.ProcessOptions);
        GitRepositoryOperation operation;
        if (command == "revert")
        {
            // Reverting Main conflicts with nothing, so a.txt gets a change of its own first.
            repository.CommitFileIn(repository.Path, "a.txt", "later\n", "Later");
            Assert.False(repository.TryRunGit(out _, "revert", "--no-edit", "HEAD~2"));
            operation = GitRepositoryOperation.Reverting;
        }
        else
        {
            string patch = Path.Combine(repository.RootPath, "feature.patch");
            File.WriteAllText(patch, repository.RunGit("format-patch", "-1", "--stdout", "feature"));
            Assert.False(repository.TryRunGit(out _, "am", "-3", patch));
            operation = GitRepositoryOperation.ApplyingPatches;
        }

        repository.WriteFile("b.txt", "edited\n");

        Assert.True(await SkipCurrentStepAsync(service, repository, operation));

        Assert.Equal(GitRepositoryOperation.None, (await service.GetStatusAsync(repository.Path, ct)).Operation);
        Assert.Equal("edited\n", repository.ReadFile("b.txt"));
    }

    [Fact]
    public async Task Rebase_of_a_named_branch_rebases_it_although_another_one_is_checked_out()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Add b");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "c.txt", "c\n", "Add c");
        string main = repository.RunGit("rev-parse", "main").Trim();
        GitService service = new(options: repository.ProcessOptions);

        Assert.Equal(GitMergeResult.Merged, await service.RebaseAsync(repository.Path, "main", "feature", ct));

        Assert.Equal("feature", repository.RunGit("branch", "--show-current").Trim());
        Assert.Equal(main, repository.RunGit("rev-parse", "feature~1").Trim());
        Assert.Equal(main, repository.RunGit("rev-parse", "main").Trim());
    }

    [Fact]
    public async Task A_cherry_pick_git_refuses_during_a_stopped_merge_throws_instead_of_reporting_a_stop()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateDivergedBranches();
        GitService service = new(options: repository.ProcessOptions);
        Assert.Equal(GitMergeResult.Stopped, await service.MergeAsync(repository.Path, "feature", ct));

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CherryPickAsync(repository.Path, "feature", ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.MergeAsync(repository.Path, "feature", ct));

        Assert.Equal(GitRepositoryOperation.Merging, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    [Fact]
    public async Task A_cherry_pick_that_stops_beside_a_sequence_with_a_committed_step_reports_the_stop()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "Second");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        repository.CommitFileIn(repository.Path, "c.txt", "c\n", "Other");
        string conflicting = repository.RunGit("rev-parse", "feature~1").Trim();
        GitService service = new(options: repository.ProcessOptions);
        // The sequence stops at its first step, which is committed on the command line; its rest waits in the sequencer.
        Assert.False(repository.TryRunGit(out _, "cherry-pick", "main..feature"));
        Assert.True(await service.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, ct));
        repository.RunGit("commit", "-q", "--no-edit");
        repository.WriteFile("a.txt", "main again\n");
        repository.RunGit("commit", "-q", "-am", "Main again");

        Assert.Equal(GitMergeResult.Stopped, await service.CherryPickAsync(repository.Path, conflicting, ct));
        Assert.Equal(conflicting, repository.RunGit("rev-parse", "CHERRY_PICK_HEAD").Trim());
    }

    [Fact]
    public async Task Skip_refuses_a_rebase_step_other_than_the_confirmed_one()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        // Replaying Two and Three onto main conflicts at both, as main changed a.txt as well.
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.CommitFileIn(repository.Path, "a.txt", "three\n", "Three");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        repository.RunGit("switch", "-q", "feature");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGit(out _, "rebase", "main"));
        string? confirmed = (await service.GetStatusAsync(repository.Path, ct)).OperationStep;

        Assert.False(repository.TryRunGit(out _, "rebase", "--skip"));
        Assert.NotEqual(confirmed, (await service.GetStatusAsync(repository.Path, ct)).OperationStep);

        Assert.False(await service.SkipAsync(repository.Path, GitRepositoryOperation.Rebasing, confirmed, ct));
        Assert.Equal(GitRepositoryOperation.Rebasing, (await service.GetStatusAsync(repository.Path, ct)).Operation);
    }

    private static async Task<bool> SkipCurrentStepAsync(GitService service, GitTestRepository repository, GitRepositoryOperation operation)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string? step = (await service.GetStatusAsync(repository.Path, ct)).OperationStep;
        return await service.SkipAsync(repository.Path, operation, step, ct);
    }

    /// <summary>Creates <c>main</c> and <c>feature</c>, which both changed a.txt since they diverged.</summary>
    private static GitTestRepository CreateDivergedBranches()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        return repository;
    }

    private static void WriteHook(string path, string text)
    {
        File.WriteAllText(path, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static string[] TrackedFiles(GitTestRepository repository) =>
        repository.RunGit("ls-files").Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
