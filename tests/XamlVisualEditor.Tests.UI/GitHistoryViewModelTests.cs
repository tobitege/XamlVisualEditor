using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.SourceControl.UI.Models;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitHistoryViewModelTests
{
    [AvaloniaFact]
    public async Task Commits_are_read_only_while_shown_and_in_pages_of_one_hundred()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 230, shown: false);
        GitHistoryViewModel history = fixture.History;
        Assert.Empty(fixture.Git.Queries);

        history.IsShown = true;
        Dispatcher.UIThread.RunJobs();

        GitLogQuery first = Assert.Single(fixture.Git.Queries).Query;
        Assert.Equal((0, GitHistoryViewModel.PAGE_SIZE + 1, null), (first.Skip, first.MaxCount, first.Path));
        Assert.Equal(GitHistoryViewModel.PAGE_SIZE, history.Commits.Count);
        Assert.Equal("Commit 230", history.Commits[0].Subject);
        Assert.True(history.HasMore);
        Assert.Null(history.Message);
        Assert.Equal(new[] { "Anna", "Bob" }, history.KnownAuthors);

        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 0, 100, 200 }, fixture.Git.Queries.Select(query => query.Query.Skip));
        Assert.Equal(230, history.Commits.Count);
        Assert.Equal("Commit 1", history.Commits[^1].Subject);
        Assert.False(history.HasMore);
        Assert.False(((ICommand)history.LoadMoreCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Filters_are_applied_by_search_and_cleared_by_show_all()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 6);
        GitHistoryViewModel history = fixture.History;

        history.SearchText = "  commit 5 ";
        history.AuthorText = "anna";
        await history.SearchCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        GitLogQuery filtered = fixture.Git.Queries[^1].Query;
        Assert.Equal(("anna", "commit 5"), (filtered.Author, filtered.MessageText));
        Assert.Equal(new[] { "Commit 5" }, history.Commits.Select(row => row.Subject));

        history.SearchText = "nothing";
        await history.SearchCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(history.Commits);
        Assert.Equal(GitStrings.NoMatchingCommits, history.Message);

        await history.ClearFilterCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        GitLogQuery cleared = fixture.Git.Queries[^1].Query;
        Assert.Equal(((string?)null, (string?)null), (cleared.Author, cleared.MessageText));
        Assert.Null(history.SearchText);
        Assert.Null(history.AuthorText);
        Assert.Equal(6, history.Commits.Count);
    }

    [AvaloniaFact]
    public async Task A_new_query_cancels_the_running_one_whose_result_is_never_shown()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 4, shown: false);
        GitHistoryViewModel history = fixture.History;
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Git.HoldNextQuery = release.Task;

        history.IsShown = true;
        Dispatcher.UIThread.RunJobs();
        Assert.True(history.IsLoading);
        history.AuthorText = "Bob";
        await history.SearchCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.True(fixture.Git.Queries[0].Token.IsCancellationRequested);
        release.SetResult();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Git.Queries.Count);
        Assert.Equal(new[] { "Commit 4", "Commit 2" }, history.Commits.Select(row => row.Subject));
        Assert.False(history.IsLoading);
    }

    [AvaloniaFact]
    public async Task Scope_sets_path_follow_and_revision_and_head_changes_reload_with_the_selection_kept()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 3);
        GitHistoryViewModel history = fixture.History;
        fixture.Git.Refs["refs/tags/v1"] = fixture.Git.Commits[1].Sha;

        history.Show(new GitHistoryScope("src/a.cs"));
        Dispatcher.UIThread.RunJobs();
        GitLogQuery file = fixture.Git.Queries[^1].Query;
        history.Show(new GitHistoryScope("src", IsDirectory: true, Revision: "refs/tags/v1"));
        Dispatcher.UIThread.RunJobs();
        GitLogQuery folder = fixture.Git.Queries[^1].Query;

        // Without a revision, the history starts at the HEAD commit of the snapshot; a revision is read as the
        // commit it names.
        Assert.Equal(("src/a.cs", true, fixture.Git.HeadSha), (file.Path, file.Follow, file.Revision));
        Assert.Equal(("src", false, fixture.Git.Commits[1].Sha), (folder.Path, folder.Follow, folder.Revision));
        Assert.Equal(
            string.Format(GitStrings.HistoryScopeRevisionFormat, string.Format(GitStrings.HistoryScopeFolderFormat, "src"), "v1"),
            history.ScopeText);
        Assert.True(((ICommand)history.ShowWholeRepositoryCommand).CanExecute(null));

        await history.ShowWholeRepositoryCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(GitHistoryScope.Repository, history.Scope);
        Assert.False(((ICommand)history.ShowWholeRepositoryCommand).CanExecute(null));

        history.SelectedCommit = history.Commits[1];
        Dispatcher.UIThread.RunJobs();
        int queries = fixture.Git.Queries.Count;
        int commitReads = fixture.Git.CommitReads;
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(4, "Anna"));
        fixture.Git.HeadSha = "head-2";
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(queries + 1, fixture.Git.Queries.Count);
        Assert.Equal(new[] { "Commit 4", "Commit 3", "Commit 2", "Commit 1" }, history.Commits.Select(row => row.Subject));
        Assert.Same(history.Commits[2], history.SelectedCommit);
        Assert.Equal(commitReads, fixture.Git.CommitReads);
    }

    [AvaloniaFact]
    public async Task Details_list_the_files_select_the_first_and_navigate_its_hunks()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 2);
        GitCommitInfo merge = ScriptedHistoryGitService.CreateCommit(3, "Anna", 1.ToString("x40"), 2.ToString("x40"));
        fixture.Git.Commits.Insert(0, merge);
        fixture.Git.Files[merge.Sha] =
        [
            new GitCommitFileChange { Path = "a.txt", Kind = GitChangeKind.Modified },
            new GitCommitFileChange { Path = "neu.txt", OldPath = "alt.txt", Kind = GitChangeKind.Renamed, Similarity = 90 }
        ];
        GitHistoryViewModel history = fixture.History;
        await history.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitCommitDetailsViewModel details = history.Details;

        Assert.True(details.HasCommit);
        Assert.Equal("Commit 3", details.Title);
        Assert.Equal("Commit 3\n\nBody 3", details.MessageText);
        // The parents are abbreviated as git abbreviates them.
        Assert.Equal(string.Format(GitStrings.MergeComparisonFormat, "0000001"), details.MergeText);
        Assert.Equal(string.Format(GitStrings.ParentsFormat, "0000001, 0000002"), details.ParentsText);
        Assert.Equal(string.Format(GitStrings.CommitFilesFormat, 2), details.FilesHeader);
        Assert.Equal(
            new[] { (GitStrings.ChangeModified, "a.txt"), (GitStrings.ChangeRenamed, string.Format(GitStrings.RenamedFromFormat, "neu.txt", "alt.txt")) },
            details.Files.Select(file => (file.ChangeText, file.DisplayPath)));
        Assert.Same(details.Files[0], details.SelectedFile);
        Assert.Equal(2, details.DiffLines.Count(line => line.Kind == DiffLineKind.HunkHeader));
        Assert.Contains(details.DiffLines, line => line.Kind == DiffLineKind.Added && line.Text == "+eins");
        Assert.False(details.IsDiffTruncated);
        Assert.Null(details.DiffTruncationNotice);
        // The view sets the number of hunks from the diff editor.
        details.HunkCount = 2;
        Assert.Equal(-1, details.CurrentHunkIndex);
        Assert.False(((ICommand)details.PreviousHunkCommand).CanExecute(null));
        Assert.True(((ICommand)details.NextHunkCommand).CanExecute(null));

        await details.NextHunkCommand.Execute().ToTask();
        await details.NextHunkCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, details.CurrentHunkIndex);
        Assert.False(((ICommand)details.NextHunkCommand).CanExecute(null));
        await details.PreviousHunkCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, details.CurrentHunkIndex);
        Assert.True(((ICommand)details.NextHunkCommand).CanExecute(null));

        details.SelectedFile = details.Files[1];
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, fixture.Git.DiffReads);
        Assert.Equal(-1, details.CurrentHunkIndex);

        // The history of the file starts at the shown commit.
        await details.ShowFileHistoryCommand.Execute().ToTask();
        (string commandId, IReadOnlyList<object?>? arguments) = fixture.Executed[^1];
        Assert.Equal(GitCommands.SHOW_FILE_HISTORY, commandId);
        Assert.Equal(new object?[] { System.IO.Path.GetFullPath(System.IO.Path.Combine("test-repo", "neu.txt")), merge.Sha }, arguments);

        history.SelectedCommit = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(details.HasCommit);
        Assert.Empty(details.Files);
    }

    [AvaloniaFact]
    public async Task An_empty_history_says_so_and_create_branch_passes_the_commit_id()
    {
        using Fixture empty = await Fixture.CreateAsync(commitCount: 0);
        Assert.Equal(GitStrings.NoCommits, empty.History.Message);

        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        fixture.History.SelectedCommit = fixture.History.Commits[0];
        await fixture.History.CreateBranchFromCommitCommand.Execute().ToTask();

        Assert.Equal((GitCommands.CREATE_BRANCH, fixture.History.Commits[0].Sha), (fixture.Executed[^1].CommandId, fixture.Executed[^1].Arguments?.Single()));
    }

    [AvaloniaFact]
    public async Task Further_pages_start_at_the_head_commit_of_the_first_page_so_a_new_commit_repeats_no_row()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 150, shown: false);
        GitHistoryViewModel history = fixture.History;
        string head = fixture.Git.Commits[0].Sha;
        fixture.Git.HeadSha = head;
        await fixture.Session.RefreshAsync();
        history.IsShown = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(GitHistoryViewModel.PAGE_SIZE, history.Commits.Count);

        // A commit arrives before a snapshot reports it; the next page still continues the listed history.
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(151, "Anna"));
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        GitLogQuery second = fixture.Git.Queries[^1].Query;
        Assert.Equal((head, 100, 101), (second.Revision, second.Skip, second.MaxCount));
        Assert.Equal(150, history.Commits.Count);
        Assert.Equal(150, history.Commits.Select(row => row.Sha).Distinct().Count());
        Assert.Equal(("Commit 150", "Commit 1"), (history.Commits[0].Subject, history.Commits[^1].Subject));
    }

    [AvaloniaFact]
    public async Task A_head_change_a_refresh_and_showing_again_read_the_loaded_range_and_keep_a_selection_beyond_the_first_page()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 230);
        GitHistoryViewModel history = fixture.History;
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(230, history.Commits.Count);
        history.SelectedCommit = history.Commits[150];
        Dispatcher.UIThread.RunJobs();
        string selected = history.Commits[150].Sha;
        int commitReads = fixture.Git.CommitReads;

        // A new commit moves HEAD: the 230 listed commits are read again from the new HEAD.
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(231, "Anna"));
        fixture.Git.HeadSha = fixture.Git.Commits[0].Sha;
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        GitLogQuery afterCommit = fixture.Git.Queries[^1].Query;
        Assert.Equal((0, 231, fixture.Git.HeadSha), (afterCommit.Skip, afterCommit.MaxCount, afterCommit.Revision));
        AssertRangeKept(index: 151);

        // A refresh of the branches, the refresh button and showing the panel again read the same range.
        await fixture.Session.RefreshAllAsync();
        Dispatcher.UIThread.RunJobs();
        AssertRangeKept(index: 151);
        await history.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        AssertRangeKept(index: 151);
        history.IsShown = false;
        Dispatcher.UIThread.RunJobs();
        int queries = fixture.Git.Queries.Count;
        history.IsShown = true;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(queries + 1, fixture.Git.Queries.Count);
        AssertRangeKept(index: 151);
        Assert.Equal(commitReads, fixture.Git.CommitReads);

        // A new filter starts with the first page again.
        history.AuthorText = "Anna";
        await history.SearchCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((0, 101), (fixture.Git.Queries[^1].Query.Skip, fixture.Git.Queries[^1].Query.MaxCount));
        Assert.Equal(GitHistoryViewModel.PAGE_SIZE, history.Commits.Count);

        void AssertRangeKept(int index)
        {
            Assert.Equal((0, 231), (fixture.Git.Queries[^1].Query.Skip, fixture.Git.Queries[^1].Query.MaxCount));
            Assert.Equal(230, history.Commits.Count);
            Assert.True(history.HasMore);
            Assert.Same(history.Commits[index], history.SelectedCommit);
            Assert.Equal(selected, history.SelectedCommit!.Sha);
            Assert.Equal(selected, history.Details.Commit?.Sha);
        }
    }

    [AvaloniaFact]
    public async Task Authors_of_another_repository_replace_the_suggestions()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 4);
        GitHistoryViewModel history = fixture.History;
        Assert.Equal(new[] { "Anna", "Bob" }, history.KnownAuthors);

        fixture.Git.Commits.Clear();
        fixture.Git.Commits.Add(ScriptedHistoryGitService.CreateCommit(1, "Carla"));
        fixture.Workspace.Open("other-repo");
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("other-repo", fixture.Session.Current.RepositoryRoot);
        Assert.Equal(new[] { "Carla" }, history.KnownAuthors);
        Assert.Equal(new[] { "Commit 1" }, history.Commits.Select(row => row.Subject));
    }

    [AvaloniaFact]
    public async Task A_diff_longer_than_the_limit_is_read_only_up_to_the_line_and_byte_limits()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        System.Text.StringBuilder diff = new("diff --git a/a.txt b/a.txt\n--- a/a.txt\n+++ b/a.txt\n@@ -0,0 +1,25000 @@\n");
        for (int line = 1; line <= 25_000; line++)
        {
            diff.Append("+line ").Append(line).Append('\n');
        }

        fixture.Git.DiffText = diff.Append("@@ -30000 +30000 @@\n-x\n+y\n").ToString();
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitCommitDetailsViewModel details = history.Details;

        Assert.True(details.IsDiffTruncated);
        Assert.Equal(GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, details.DiffLines.Count);
        Assert.Equal(
            (GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, GitCommitDetailsViewModel.MAX_DIFF_BYTE_COUNT),
            fixture.Git.LastDiffLimits);
        // The notice names the number of shown lines and both limits.
        Assert.Equal(
            string.Format(GitStrings.DiffTooLargeFormat, GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, GitCommitDetailsViewModel.MAX_DIFF_LINE_COUNT, 4),
            details.DiffTruncationNotice);
        Assert.Contains("4 MiB", details.DiffTruncationNotice, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Hunk_moves_are_possible_only_towards_an_existing_hunk_and_none_is_selected_at_first()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 1);
        fixture.Git.DiffText = "@@ -1 +1 @@\n-a\n+b\n";
        GitHistoryViewModel history = fixture.History;
        history.SelectedCommit = history.Commits[0];
        Dispatcher.UIThread.RunJobs();
        GitCommitDetailsViewModel details = history.Details;
        // The view sets the number of hunks from the diff editor.
        details.HunkCount = 1;

        Assert.Equal(-1, details.CurrentHunkIndex);
        Assert.False(((ICommand)details.PreviousHunkCommand).CanExecute(null));
        Assert.True(((ICommand)details.NextHunkCommand).CanExecute(null));

        await details.NextHunkCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, details.CurrentHunkIndex);
        Assert.False(((ICommand)details.NextHunkCommand).CanExecute(null));
        Assert.False(((ICommand)details.PreviousHunkCommand).CanExecute(null));

        details.HunkCount = 0;
        details.CurrentHunkIndex = -1;
        Assert.False(((ICommand)details.NextHunkCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Whole_repository_is_offered_only_for_another_scope_and_a_commit_revision_is_named_by_its_short_id()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 3);
        GitHistoryViewModel history = fixture.History;
        ICommand showWholeRepository = history.ShowWholeRepositoryCommand;
        fixture.Git.Refs["refs/tags/v1"] = fixture.Git.Commits[2].Sha;
        Assert.False(showWholeRepository.CanExecute(null));

        GitCommitInfo start = fixture.Git.Commits[1];
        history.Show(new GitHistoryScope("a.txt", Revision: start.Sha));
        Dispatcher.UIThread.RunJobs();
        Assert.True(showWholeRepository.CanExecute(null));
        Assert.Equal(
            string.Format(GitStrings.HistoryScopeRevisionFormat, string.Format(GitStrings.HistoryScopeFileFormat, "a.txt"), start.ShortSha),
            history.ScopeText);
        Assert.Equal(start.Sha, fixture.Git.Queries[^1].Query.Revision);

        history.Show(new GitHistoryScope(Revision: "refs/tags/v1"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(string.Format(GitStrings.HistoryScopeRevisionFormat, GitStrings.HistoryScopeRepository, "v1"), history.ScopeText);
        Assert.True(showWholeRepository.CanExecute(null));
        await history.ShowWholeRepositoryCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.False(showWholeRepository.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_start_commit_that_is_not_listed_is_named_by_its_short_id()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 3);
        GitHistoryViewModel history = fixture.History;
        // Commit 2 is Bob's; the author filter lists only Anna's commits below it.
        GitCommitInfo start = fixture.Git.Commits[1];
        history.AuthorText = "Anna";
        history.Show(new GitHistoryScope("a.txt", Revision: start.Sha));
        Dispatcher.UIThread.RunJobs();

        Assert.DoesNotContain(history.Commits, row => row.Sha == start.Sha);
        Assert.Equal(
            string.Format(GitStrings.HistoryScopeRevisionFormat, string.Format(GitStrings.HistoryScopeFileFormat, "a.txt"), start.ShortSha),
            history.ScopeText);
    }

    [AvaloniaFact]
    public async Task A_branch_named_like_the_start_of_its_commit_id_is_named_by_its_name()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 3);
        GitHistoryViewModel history = fixture.History;
        GitCommitInfo tip = fixture.Git.Commits[1];
        string branch = tip.Sha[..5];
        fixture.Git.Refs[branch] = tip.Sha;
        history.Show(new GitHistoryScope(Revision: branch));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(tip.Sha, history.Commits[0].Sha);
        Assert.Equal(string.Format(GitStrings.HistoryScopeRevisionFormat, GitStrings.HistoryScopeRepository, branch), history.ScopeText);
    }

    [AvaloniaFact]
    public async Task Further_pages_of_a_branch_start_at_the_commit_it_pointed_to_so_a_new_commit_on_it_repeats_no_row()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 150);
        GitHistoryViewModel history = fixture.History;
        string tip = fixture.Git.Commits[0].Sha;
        fixture.Git.Refs["feature"] = tip;
        history.Show(new GitHistoryScope(Revision: "feature"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(GitHistoryViewModel.PAGE_SIZE, history.Commits.Count);
        Assert.Equal(tip, fixture.Git.Queries[^1].Query.Revision);
        int resolves = fixture.Git.Resolves;

        // A commit lands on the branch before the next page; no snapshot reports it yet.
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(151, "Anna"));
        fixture.Git.Refs["feature"] = fixture.Git.Commits[0].Sha;
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        GitLogQuery second = fixture.Git.Queries[^1].Query;
        Assert.Equal((tip, 100, 101), (second.Revision, second.Skip, second.MaxCount));
        Assert.Equal(resolves, fixture.Git.Resolves);
        Assert.Equal(150, history.Commits.Count);
        Assert.Equal(150, history.Commits.Select(row => row.Sha).Distinct().Count());
        Assert.Equal(("Commit 150", "Commit 1"), (history.Commits[0].Subject, history.Commits[^1].Subject));
        Assert.Equal(string.Format(GitStrings.HistoryScopeRevisionFormat, GitStrings.HistoryScopeRepository, "feature"), history.ScopeText);

        // A refresh starts again at the commit the branch points to now.
        await history.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(fixture.Git.Commits[0].Sha, fixture.Git.Queries[^1].Query.Revision);
        Assert.Equal("Commit 151", history.Commits[0].Subject);
    }

    [AvaloniaFact]
    public async Task Load_more_while_a_new_state_is_read_appends_no_page_of_the_previous_state()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 230);
        GitHistoryViewModel history = fixture.History;
        Assert.True(history.HasMore);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Git.HoldNextQuery = release.Task;

        // A new commit moves HEAD; the listed range is read again from the new HEAD and held.
        fixture.Git.Commits.Insert(0, ScriptedHistoryGitService.CreateCommit(231, "Anna"));
        fixture.Git.HeadSha = fixture.Git.Commits[0].Sha;
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        int queries = fixture.Git.Queries.Count;
        (GitLogQuery reread, System.Threading.CancellationToken rereadToken) = fixture.Git.Queries[^1];
        Assert.Equal((0, fixture.Git.HeadSha), (reread.Skip, reread.Revision));
        Assert.True(history.IsLoading);

        // A Load More that runs before the read is applied, as a click handled in the same turn would.
        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(queries, fixture.Git.Queries.Count);
        Assert.False(rereadToken.IsCancellationRequested);

        release.SetResult();
        await history.WhenAnyValue(x => x.IsLoading).Where(isLoading => !isLoading).FirstAsync().Timeout(TimeSpan.FromSeconds(10)).ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GitHistoryViewModel.PAGE_SIZE, history.Commits.Count);
        Assert.Equal(("Commit 231", "Commit 132"), (history.Commits[0].Subject, history.Commits[^1].Subject));
        Assert.True(history.HasMore);

        await history.LoadMoreCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((fixture.Git.HeadSha, 100), (fixture.Git.Queries[^1].Query.Revision, fixture.Git.Queries[^1].Query.Skip));
        Assert.Equal(200, history.Commits.Select(row => row.Sha).Distinct().Count());
    }

    [AvaloniaFact]
    public async Task An_empty_file_history_says_that_no_commits_were_found()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 0);
        fixture.History.Show(new GitHistoryScope("new.txt"));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(GitStrings.NoCommitsFound, fixture.History.Message);
    }

    [AvaloniaFact]
    public async Task Showing_a_scope_at_a_commit_selects_that_commit_once()
    {
        using Fixture fixture = await Fixture.CreateAsync(commitCount: 6);
        GitHistoryViewModel history = fixture.History;
        string sha = fixture.Git.Commits.Single(commit => commit.Subject == "Commit 4").Sha;

        history.Show(new GitHistoryScope(Revision: sha));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(sha, history.SelectedCommit?.Sha);
        Assert.Equal(sha, history.Details.Commit?.Sha);

        history.SelectedCommit = history.Commits[^1];
        await history.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Commit 1", history.SelectedCommit?.Subject);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(ScriptedHistoryGitService git)
        {
            Git = git;
            Session = new GitSession(new GitServiceSet(git) { History = git }, Workspace);
            History = new GitHistoryViewModel(Session, (commandId, arguments) =>
            {
                Executed.Add((commandId, arguments));
                return Task.CompletedTask;
            });
        }

        public ScriptedHistoryGitService Git { get; }

        public SwitchableWorkspaceInfo Workspace { get; } = new();

        public GitSession Session { get; }

        public GitHistoryViewModel History { get; }

        public List<(string CommandId, IReadOnlyList<object?>? Arguments)> Executed { get; } = new();

        public static async Task<Fixture> CreateAsync(int commitCount, bool shown = true)
        {
            ScriptedHistoryGitService git = new();
            git.Commits.AddRange(ScriptedHistoryGitService.CreateCommits(commitCount));
            Fixture fixture = new(git);
            await fixture.Session.RefreshAsync();
            fixture.History.IsShown = shown;
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.History.IsRepository);
            return fixture;
        }

        public void Dispose()
        {
            History.Dispose();
            Session.Dispose();
        }
    }
}
