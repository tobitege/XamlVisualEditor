using System.Diagnostics;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
using FakeEditorServices = XamlVisualEditor.Tests.Unit.Tfvc.FakeEditorServices;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>The merge and conflict commands against real repositories whose merge stopped with every conflict kind.</summary>
public sealed class GitConflictCommandsTests
{
    [Fact]
    public async Task Stage_all_stages_the_other_changes_and_leaves_the_conflicts_unmerged()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync(GitCommands.STAGE_ALL);
        await fixture.ExecuteAsync(GitCommands.STAGE, fixture.Repository.Path);

        Assert.Contains("local.txt", fixture.Repository.RunGit("diff", "--cached", "--name-only"));
        Assert.Contains("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
        Assert.Contains("<<<<<<< HEAD", fixture.Repository.ReadFile("content.txt"));
        Assert.Empty(fixture.Window.Errors);
    }

    [Fact]
    public async Task Stage_of_a_conflicted_file_reports_it_and_leaves_it_unmerged()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync(GitCommands.STAGE, "content.txt");

        Assert.Single(fixture.Window.Infos);
        Assert.Contains("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
    }

    [Fact]
    public async Task Accept_ours_asks_first_and_takes_the_version_of_the_current_branch()
    {
        bool confirm = false;
        using Fixture fixture = await Fixture.CreateAsync((_, _) => confirm);

        await fixture.ExecuteAsync(GitCommands.ACCEPT_OURS, "content.txt");
        Assert.Contains("<<<<<<< HEAD", fixture.Repository.ReadFile("content.txt"));

        confirm = true;
        await fixture.ExecuteAsync(GitCommands.ACCEPT_THEIRS, "content.txt");

        Assert.Equal(2, fixture.Dialogs.Confirmations.Count());
        Assert.Contains("content.txt", fixture.Dialogs.Confirmations.Last().Message);
        Assert.Equal("a\nFEATURE\nc\n", fixture.Repository.ReadFile("content.txt"));
        Assert.DoesNotContain("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
        Assert.False(fixture.Session.Current.Changes.Single(change => change.Path == "content.txt").IsConflicted);
    }

    [Fact]
    public async Task Mark_resolved_asks_while_markers_remain_and_stages_a_clean_file_without_asking()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => false);

        await fixture.ExecuteAsync(GitCommands.MARK_RESOLVED, "content.txt");
        ConfirmationDialogRequest question = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Contains("content.txt", question.Message);
        Assert.Contains("content.txt", fixture.Repository.RunGit("ls-files", "-u"));

        fixture.Repository.WriteFile("content.txt", "a\nMAIN and FEATURE\nc\n");
        await fixture.ExecuteAsync(GitCommands.MARK_RESOLVED, "content.txt");

        Assert.Single(fixture.Dialogs.Confirmations);
        Assert.DoesNotContain("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
        Assert.Equal("a\nMAIN and FEATURE\nc\n", fixture.Repository.RunGit("show", ":content.txt"));
    }

    [Fact]
    public async Task A_deleted_side_is_marked_resolved_without_a_marker_question()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => false);
        File.Delete(Path.Combine(fixture.Repository.Path, "deleted-by-them.txt"));

        await fixture.ExecuteAsync(GitCommands.MARK_RESOLVED, "deleted-by-them.txt");

        Assert.Empty(fixture.Dialogs.Confirmations);
        Assert.DoesNotContain("deleted-by-them.txt", fixture.Repository.RunGit("ls-files"));
    }

    [Fact]
    public async Task Abort_asks_names_the_conflicts_and_restores_the_branch()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);
        string head = fixture.Repository.RunGit("rev-parse", "HEAD");

        await fixture.ExecuteAsync(GitCommands.ABORT);

        ConfirmationDialogRequest question = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Contains(GitStrings.OperationMerging, question.Message);
        Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);
        Assert.Equal(head, fixture.Repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal("local\nlocal edit\n", fixture.Repository.ReadFile("local.txt"));
    }

    [Fact]
    public async Task Continue_of_a_merge_points_to_commit_and_commit_refuses_while_conflicts_remain()
    {
        using Fixture fixture = await Fixture.CreateAsync();

        await fixture.ExecuteAsync(GitCommands.CONTINUE);
        await fixture.ExecuteAsync(GitCommands.COMMIT);

        Assert.Equal([GitStrings.ContinueMergeWithCommit, GitStrings.CommitWithConflicts], fixture.Window.Infos);
        Assert.Equal(GitRepositoryOperation.Merging, fixture.Session.Current.Operation);
    }

    [Fact]
    public async Task Commit_completes_a_merge_with_nothing_staged()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);
        string[] conflicted = fixture.Session.Current.Changes.Where(change => change.IsConflicted).Select(change => change.Path).ToArray();
        GitService git = new(options: fixture.Repository.ProcessOptions);
        Assert.True(await git.TakeSideAsync(fixture.Repository.Path, conflicted, GitConflictSide.Ours, CancellationToken.None));
        // Taking the current branch's side everywhere leaves only the clean merge result staged; unstaged, nothing is.
        fixture.Repository.RunGit("reset", "-q", "--", "clean.txt");
        fixture.Repository.RunGit("checkout", "--", "clean.txt");
        await fixture.Session.RefreshAsync();
        fixture.Window.InputBoxResult = "Merge feature";

        await fixture.ExecuteAsync(GitCommands.COMMIT);

        Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);
        Assert.Equal(3, fixture.Repository.RunGit("rev-list", "--parents", "-n", "1", "HEAD").Trim().Split(' ').Length);
    }

    [Fact]
    public async Task Stash_and_merge_run_directly_during_a_merge_are_refused_and_keep_the_merge()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);
        fixture.Window.InputBoxResult = "stash";

        await fixture.ExecuteAsync(GitCommands.STASH);
        await fixture.ExecuteAsync(GitCommands.MERGE, "feature");

        string refused = string.Format(GitStrings.RefusedDuringOperationFormat, GitStrings.OperationMerging);
        Assert.Equal([refused, refused], fixture.Window.Infos);
        Assert.Equal(string.Empty, fixture.Repository.RunGit("stash", "list"));
        Assert.True(File.Exists(Path.Combine(fixture.Repository.Path, ".git", "MERGE_HEAD")));
    }

    [Fact]
    public async Task Merge_that_stops_shows_the_changes_panel()
    {
        using Fixture fixture = await Fixture.CreateAsync(startMerge: false);

        await fixture.ExecuteAsync(GitCommands.MERGE, "feature");

        Assert.Equal(GitRepositoryOperation.Merging, fixture.Session.Current.Operation);
        Assert.Contains(GitCommands.CHANGES_VIEW_ID, fixture.ViewHost.Shown);
        Assert.Equal([string.Format(GitStrings.MergeStoppedFormat, "feature")], fixture.Window.Infos);
    }

    [Theory]
    [InlineData(0, "a\nMAIN\nc\n")]
    [InlineData(1, "a\nFEATURE\nc\n")]
    [InlineData(2, "a\nMAIN\nFEATURE\nc\n")]
    public async Task A_picked_side_replaces_the_block_in_the_buffer_in_one_edit(int pick, string expected)
    {
        using Fixture fixture = await Fixture.CreateAsync();
        FakeEditorDocument document = fixture.Open("content.txt");
        fixture.Window.QuickPick = items => items[pick];

        await fixture.Commands.ExecuteAsync(GitCommands.RESOLVE_CONFLICT, [document.FilePath, 3], CancellationToken.None);

        Assert.Equal(expected, document.Text);
        Assert.Single(document.AppliedEdits);
        Assert.Equal(["HEAD", "feature"], fixture.Window.QuickPicks.Single().Items.Take(2).Select(item => item.Description));
        // Only the buffer changed; the file keeps its markers until it is saved.
        Assert.Contains("<<<<<<< HEAD", fixture.Repository.ReadFile("content.txt"));
    }

    [Fact]
    public async Task The_block_at_the_caret_is_resolved_and_a_line_outside_a_block_is_reported()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        FakeEditorDocument document = fixture.Open("content.txt");
        fixture.Window.QuickPick = items => items[1];

        document.CaretOffset = 0;
        await fixture.ExecuteAsync(GitCommands.RESOLVE_CONFLICT);
        Assert.Equal([GitStrings.NoConflictAtLine], fixture.Window.Infos);

        document.CaretOffset = document.Text.IndexOf("MAIN", StringComparison.Ordinal);
        await fixture.ExecuteAsync(GitCommands.RESOLVE_CONFLICT);
        Assert.Equal("a\nFEATURE\nc\n", document.Text);
    }

    [Fact]
    public async Task A_block_that_changed_while_the_list_was_open_is_not_replaced()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        FakeEditorDocument document = fixture.Open("content.txt");
        fixture.Window.QuickPick = items =>
        {
            document.Edit(document.Text.Replace("FEATURE", "FEATURE typed", StringComparison.Ordinal));
            return items[0];
        };

        await fixture.Commands.ExecuteAsync(GitCommands.RESOLVE_CONFLICT, [document.FilePath, 2], CancellationToken.None);

        Assert.Empty(document.AppliedEdits);
        Assert.Equal([GitStrings.ConflictBlockChanged], fixture.Window.Infos);
    }

    [Fact]
    public async Task A_block_moved_by_lines_typed_above_it_while_the_list_was_open_is_not_replaced()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        FakeEditorDocument document = fixture.Open("content.txt");
        fixture.Window.QuickPick = items =>
        {
            document.Edit("typed\n" + document.Text);
            return items[0];
        };

        await fixture.Commands.ExecuteAsync(GitCommands.RESOLVE_CONFLICT, [document.FilePath, 2], CancellationToken.None);

        Assert.Empty(document.AppliedEdits);
        Assert.Equal([GitStrings.ConflictBlockChanged], fixture.Window.Infos);
    }

    [Fact]
    public async Task Mark_resolved_refuses_a_file_resolved_only_in_the_editor_and_stages_it_after_saving()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);
        FakeEditorDocument document = fixture.Open("content.txt");
        document.Edit("a\nMAIN and FEATURE\nc\n");

        await fixture.ExecuteAsync(GitCommands.MARK_RESOLVED, "content.txt");

        Assert.Empty(fixture.Dialogs.Confirmations);
        Assert.Equal(
            [string.Format(GitStrings.MarkResolvedUnsavedFormat, "content.txt")],
            fixture.Window.Infos);
        Assert.Contains("content.txt", fixture.Repository.RunGit("ls-files", "-u"));

        fixture.Repository.WriteFile("content.txt", document.Text.ReplaceLineEndings("\r\n"));
        await fixture.ExecuteAsync(GitCommands.MARK_RESOLVED, "content.txt");

        Assert.Empty(fixture.Dialogs.Confirmations);
        Assert.DoesNotContain("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
    }

    [Fact]
    public async Task Commit_during_a_rebase_is_refused_and_continue_commits_the_step_with_its_author()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("commit", "-q", "--amend", "--no-edit", "--author=Orig <orig@example.com>");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        repository.RunGit("switch", "-q", "feature");
        Assert.False(repository.TryRunGit(out _, "rebase", "main"));
        using Fixture fixture = await Fixture.ForAsync(repository);
        GitService git = new(options: repository.ProcessOptions);
        Assert.True(await git.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, CancellationToken.None));
        await fixture.Session.RefreshAsync();
        string head = repository.RunGit("rev-parse", "HEAD");
        fixture.Window.InputBoxResult = "Typed";

        await fixture.ExecuteAsync(GitCommands.COMMIT);

        Assert.Equal([string.Format(GitStrings.CommitDuringOperationFormat, GitStrings.OperationRebasing)], fixture.Window.Infos);
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD"));

        await fixture.ExecuteAsync(GitCommands.CONTINUE);

        Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);
        Assert.Equal("Orig|Feature", repository.RunGit("log", "-1", "--format=%an|%s").Trim());
        // git continues to the end once it started, so the progress offers to stop it only after a wait.
        ProgressDialogRequest progress = Assert.Single(fixture.Dialogs.Progress);
        Assert.True(progress.CancelDelay > TimeSpan.Zero);
        Assert.Equal(GitStrings.StopGitButton, progress.CancelText);
    }

    [Fact]
    public async Task A_checkout_that_hangs_in_a_hook_is_stopped_only_after_a_confirmation()
    {
        GitTestRepository repository = CreateWithHookedBranch("touch .git/hook-started; sleep 3; touch .git/hook-finished");
        // The first stop is declined, the progress shows again, and the second stop is confirmed.
        int questions = 0;
        using Fixture fixture = await Fixture.ForAsync(repository, (_, _) => ++questions == 2);
        WatchAtOnce(fixture);
        fixture.Dialogs.ProgressAsync = async (_, _) =>
        {
            await WaitForFileAsync(Path.Combine(repository.Path, ".git", "hook-started"));
            return true;
        };

        await fixture.ExecuteAsync(GitCommands.CHECKOUT, "other");

        Assert.Equal(2, questions);
        Assert.All(fixture.Dialogs.Confirmations, question => Assert.True(question.IsDestructive));
        Assert.Equal(2, fixture.Dialogs.Progress.Count());
        Assert.Contains(GitStrings.GitStopped, Assert.Single(fixture.Window.Errors));
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hook-finished")));
    }

    [Fact]
    public async Task A_stop_question_that_is_open_when_git_ends_closes_and_stops_nothing()
    {
        GitTestRepository repository = CreateWithHookedBranch("touch .git/hook-started; sleep 2; touch .git/hook-finished");
        using Fixture fixture = await Fixture.ForAsync(repository);
        WatchAtOnce(fixture);
        fixture.Dialogs.ProgressAsync = async (_, _) =>
        {
            await WaitForFileAsync(Path.Combine(repository.Path, ".git", "hook-started"));
            return true;
        };
        // The question would confirm, but only once something closes it.
        fixture.Dialogs.RespondAsync = async (_, ct) =>
        {
            // A question that no longer closes fails the test instead of hanging the run.
            using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct, TestContext.Current.CancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            await Task.Delay(Timeout.Infinite, limit.Token).ContinueWith(_ => { }, TaskScheduler.Default);
            Assert.True(ct.IsCancellationRequested, "The stop question did not close when git ended.");
            return true;
        };

        await fixture.ExecuteAsync(GitCommands.CHECKOUT, "other");

        Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Empty(fixture.Window.Errors);
        Assert.True(File.Exists(Path.Combine(repository.Path, ".git", "hook-finished")));
        Assert.Equal("other", fixture.Session.Current.BranchName);
    }

    [Fact]
    public async Task A_watch_that_fails_still_waits_for_the_command()
    {
        GitTestRepository repository = CreateWithHookedBranch("sleep 1; touch .git/hook-finished");
        using Fixture fixture = await Fixture.ForAsync(repository);
        fixture.Session.Watch = (_, _) => throw new InvalidOperationException("The watch failed.");

        await fixture.ExecuteAsync(GitCommands.CHECKOUT, "other");

        Assert.True(File.Exists(Path.Combine(repository.Path, ".git", "hook-finished")));
        Assert.Equal("other", fixture.Session.Current.BranchName);
        Assert.Empty(fixture.Window.Errors);
    }

    [Fact]
    public async Task A_pull_whose_fast_forward_hangs_after_its_cancel_can_be_stopped()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string peer = repository.CloneRemote("peer");
        repository.CommitFileIn(peer, "b.txt", "b\n", "Peer");
        repository.RunGitIn(peer, "push", "-q");
        WriteHook(repository, "post-merge", "touch .git/hook-started; sleep 3; touch .git/hook-finished");
        using Fixture fixture = await Fixture.ForAsync(repository, (_, _) => true);
        fixture.Handler.ProgressDelay = TimeSpan.Zero;
        fixture.Handler.StopDelay = TimeSpan.Zero;
        // The cancel comes while the hook after the fast-forward runs, which a cancel does not stop.
        fixture.Dialogs.ProgressAsync = async (request, _) =>
        {
            if (request.CancelText is null)
            {
                await WaitForFileAsync(Path.Combine(repository.Path, ".git", "hook-started"));
            }

            return true;
        };

        await fixture.ExecuteAsync(GitCommands.PULL);

        Assert.Equal([null, GitStrings.StopGitButton], fixture.Dialogs.Progress.Select(progress => progress.CancelText));
        Assert.True(Assert.Single(fixture.Dialogs.Confirmations).IsDestructive);
        Assert.Contains(GitStrings.GitStopped, Assert.Single(fixture.Window.Errors));
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(Path.Combine(repository.Path, ".git", "hook-finished")));
    }

    /// <summary>Creates a repository with the branch other and a post-checkout hook that runs <paramref name="command"/>.</summary>
    private static GitTestRepository CreateWithHookedBranch(string command)
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "other");
        WriteHook(repository, "post-checkout", command);
        return repository;
    }

    private static void WriteHook(GitTestRepository repository, string name, string command)
    {
        string hook = Path.Combine(repository.Path, ".git", "hooks", name);
        File.WriteAllText(hook, "#!/bin/sh\n" + command + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // The progress shows and offers the stop at once.
    private static void WatchAtOnce(Fixture fixture)
    {
        fixture.Handler.ProgressDelay = TimeSpan.Zero;
        fixture.Handler.StopDelay = TimeSpan.Zero;
        fixture.Session.Watch = fixture.Handler.WatchAsync;
    }

    private static async Task WaitForFileAsync(string path)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!File.Exists(path))
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), "The hook did not start: " + path);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_merge_picked_from_the_list_asks_first_and_a_declined_question_merges_nothing()
    {
        bool confirm = false;
        using Fixture fixture = await Fixture.CreateAsync((_, _) => confirm, startMerge: false);
        fixture.Window.QuickPick = items => items[0];
        string head = fixture.Repository.RunGit("rev-parse", "HEAD");

        await fixture.ExecuteAsync(GitCommands.MERGE);

        ConfirmationDialogRequest question = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Equal(string.Format(GitStrings.MergePromptFormat, "feature", "main"), question.Message);
        Assert.True(question.IsDestructive);
        Assert.Equal(head, fixture.Repository.RunGit("rev-parse", "HEAD"));
        Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);

        confirm = true;
        await fixture.ExecuteAsync(GitCommands.MERGE);

        Assert.Equal(GitRepositoryOperation.Merging, fixture.Session.Current.Operation);
    }

    [Fact]
    public async Task Abort_of_an_operation_that_changed_while_the_question_was_open_aborts_nothing()
    {
        Fixture? fixture = null;
        string? head = null;
        // The merge ends elsewhere while the question is open.
        fixture = await Fixture.CreateAsync((_, _) =>
        {
            fixture!.Repository.RunGit("merge", "--abort");
            head = fixture.Repository.RunGit("rev-parse", "HEAD");
            return true;
        });
        using (fixture)
        {
            await fixture.ExecuteAsync(GitCommands.ABORT);

            Assert.Equal([GitStrings.OperationChanged], fixture.Window.Infos);
            Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);
            Assert.Equal(head, fixture.Repository.RunGit("rev-parse", "HEAD"));
        }
    }

    [Fact]
    public async Task Taking_a_side_refuses_a_file_with_unsaved_changes_in_the_editor()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);
        FakeEditorDocument document = fixture.Open("content.txt");
        document.Edit("a\nedited in the editor\nc\n");

        await fixture.ExecuteAsync(GitCommands.ACCEPT_THEIRS, "content.txt");

        Assert.Empty(fixture.Dialogs.Confirmations);
        Assert.Equal([string.Format(GitStrings.TakeSideUnsavedFormat, "content.txt")], fixture.Window.Infos);
        Assert.Contains("content.txt", fixture.Repository.RunGit("ls-files", "-u"));
    }

    [Fact]
    public async Task Commit_and_amend_at_an_edit_step_of_a_rebase_are_part_of_the_rebase()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "a.txt", "two\n", "Two");
        repository.RunGit("-c", "sequence.editor=sed -i.bak -e s/^pick/edit/", "rebase", "-q", "-i", "HEAD~1");
        using Fixture fixture = await Fixture.ForAsync(repository);
        Assert.True(fixture.Session.Current.AllowsCommit);
        Assert.True(fixture.Session.Current.AllowsAmend);
        repository.WriteFile("b.txt", "b\n");
        repository.RunGit("add", "b.txt");
        await fixture.Session.RefreshAsync();
        fixture.Window.InputBoxResult = "Split";

        await fixture.ExecuteAsync(GitCommands.COMMIT);

        Assert.Empty(fixture.Window.Infos);
        Assert.Equal("Split", repository.RunGit("log", "-1", "--format=%s").Trim());
        Assert.Equal(GitRepositoryOperation.Rebasing, fixture.Session.Current.Operation);
    }

    [Fact]
    public async Task Skip_of_a_rebase_step_warns_about_the_other_changes_and_only_a_confirmation_skips()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        repository.RunGit("switch", "-q", "feature");
        Assert.False(repository.TryRunGit(out _, "rebase", "main"));
        bool confirm = false;
        using Fixture fixture = await Fixture.ForAsync(repository, (_, _) => confirm);

        await fixture.ExecuteAsync(GitCommands.SKIP);

        ConfirmationDialogRequest question = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.True(question.IsDestructive);
        Assert.Equal(string.Format(GitStrings.SkipRebasePromptFormat, GitStrings.OperationRebasing), question.Message);
        Assert.Equal(GitRepositoryOperation.Rebasing, fixture.Session.Current.Operation);

        confirm = true;
        await fixture.ExecuteAsync(GitCommands.SKIP);

        Assert.Equal(GitRepositoryOperation.None, fixture.Session.Current.Operation);
        Assert.Equal(repository.RunGit("rev-parse", "main"), repository.RunGit("rev-parse", "HEAD"));
        Assert.Empty(fixture.Window.Infos);
    }

    [Fact]
    public async Task Skip_during_a_merge_is_refused_without_a_question()
    {
        using Fixture fixture = await Fixture.CreateAsync((_, _) => true);

        await fixture.ExecuteAsync(GitCommands.SKIP);

        Assert.Empty(fixture.Dialogs.Confirmations);
        Assert.Equal([GitStrings.NoStepToSkip], fixture.Window.Infos);
        Assert.Equal(GitRepositoryOperation.Merging, fixture.Session.Current.Operation);
    }

    [Fact]
    public async Task Cherry_pick_without_a_commit_shows_the_history_and_one_that_stops_shows_the_changes()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        using Fixture fixture = await Fixture.ForAsync(repository);

        await fixture.ExecuteAsync(GitCommands.CHERRY_PICK);
        await fixture.ExecuteAsync(GitCommands.CHERRY_PICK, repository.RunGit("rev-parse", "feature").Trim());

        Assert.Equal([GitCommands.HISTORY_VIEW_ID, GitCommands.CHANGES_VIEW_ID], fixture.ViewHost.Shown);
        Assert.Equal([GitStrings.PickCommitInHistory, GitStrings.CherryPickStopped], fixture.Window.Infos);
        Assert.Equal(GitRepositoryOperation.CherryPicking, fixture.Session.Current.Operation);

        await fixture.ExecuteAsync(GitCommands.REVERT, "HEAD");

        Assert.Equal(
            string.Format(GitStrings.RefusedDuringOperationFormat, GitStrings.OperationCherryPicking),
            fixture.Window.Infos.Last());
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(GitTestRepository repository, Func<string, object?, object?> respond)
        {
            Repository = repository;
            Dialogs = new GitDialogHost(respond);
            Session = new GitSession(
                GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
                new FakeWorkspaceInfo { WorkspacePath = repository.Path });
            Handler = new(Session, ViewHost, Window, Dialogs, Editor, null, () => null, null);
            GitCommandHandler handler = Handler;
            GitCommands.RegisterCommands(Commands, new CommandMetadataRegistry(), handler, new List<IDisposable>());
        }

        public GitTestRepository Repository { get; }

        public GitSession Session { get; }

        public GitCommandHandler Handler { get; }

        public GitDialogHost Dialogs { get; }

        public RecordingExtensionViewHost ViewHost { get; } = new();

        public RecordingWindow Window { get; } = new();

        public FakeEditorServices Editor { get; } = new();

        public CommandRegistry Commands { get; } = new();

        public static Task<Fixture> CreateAsync(Func<string, object?, object?>? respond = null, bool startMerge = true) =>
            ForAsync(GitTestRepository.CreateConflict(startMerge), respond);

        /// <summary>Creates the fixture for <paramref name="repository"/>, which it disposes.</summary>
        public static async Task<Fixture> ForAsync(GitTestRepository repository, Func<string, object?, object?>? respond = null)
        {
            try
            {
                Fixture fixture = new(repository, respond ?? ((_, _) => false));
                await fixture.Session.RefreshAsync();
                return fixture;
            }
            catch
            {
                repository.Dispose();
                throw;
            }
        }

        public FakeEditorDocument Open(string relativePath)
        {
            FakeEditorDocument document = new(Path.Combine(Repository.Path, relativePath), Repository.ReadFile(relativePath)!);
            Editor.Activate(document);
            return document;
        }

        public Task ExecuteAsync(string commandId, string? argument = null) =>
            Commands.ExecuteAsync(commandId, argument is null ? null : [argument], CancellationToken.None);

        public void Dispose()
        {
            Session.Dispose();
            Repository.Dispose();
        }
    }
}
