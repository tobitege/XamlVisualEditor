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

    private sealed class Fixture : IDisposable
    {
        private Fixture(GitTestRepository repository, Func<string, object?, object?> respond)
        {
            Repository = repository;
            Dialogs = new GitDialogHost(respond);
            Session = new GitSession(
                GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
                new FakeWorkspaceInfo { WorkspacePath = repository.Path });
            GitCommandHandler handler = new(Session, ViewHost, Window, Dialogs, Editor, null, () => null, null);
            GitCommands.RegisterCommands(Commands, new CommandMetadataRegistry(), handler, new List<IDisposable>());
        }

        public GitTestRepository Repository { get; }

        public GitSession Session { get; }

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
