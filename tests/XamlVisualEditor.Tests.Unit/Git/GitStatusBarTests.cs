using System;
using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitStatusBarTests
{
    [Fact]
    public void Repository_shows_branch_ahead_behind_and_changes_and_opens_the_changes_panel()
    {
        RecordingStatusBarItem item = new();

        GitStatusBar.Update(item, Snapshot("main", "origin/main", ahead: 1, behind: 2, changes: 5));

        Assert.True(item.IsVisible);
        Assert.Equal("main ↑1 ↓2 · 5", item.Text);
        Assert.Equal(GitCommands.SHOW_CHANGES, item.CommandId);
        Assert.Equal(
            string.Join(
                Environment.NewLine,
                string.Format(GitStrings.StatusBarBranchFormat, "main"),
                string.Format(GitStrings.StatusBarUpstreamFormat, "origin/main", 1, 2),
                string.Format(GitStrings.StatusBarChangesFormat, 5),
                GitStrings.StatusBarClickHint),
            item.Tooltip);
    }

    [Fact]
    public void Failed_status_stays_visible_with_git_error_in_the_tooltip()
    {
        RecordingStatusBarItem item = new();

        GitStatusBar.Update(item, new GitSnapshot
        {
            State = GitSessionState.StatusFailed,
            RepositoryRoot = "repo",
            ErrorMessage = "fatal: .git/index: index file smaller than expected"
        });

        Assert.True(item.IsVisible);
        Assert.Equal(GitStrings.StatusBarStatusFailed, item.Text);
        Assert.Equal(
            "fatal: .git/index: index file smaller than expected" + Environment.NewLine + GitStrings.StatusBarClickHint,
            item.Tooltip);
        Assert.Equal(GitCommands.SHOW_CHANGES, item.CommandId);
    }

    [Fact]
    public void Merge_in_progress_is_shown_after_the_changes()
    {
        RecordingStatusBarItem item = new();
        GitSnapshot snapshot = Snapshot("main", "origin/main", ahead: 0, behind: 0, changes: 2);

        GitStatusBar.Update(item, snapshot with { Status = snapshot.Status! with { Operation = GitRepositoryOperation.Merging } });

        Assert.Equal("main · 2 · " + GitStrings.StatusBarMerging, item.Text);
    }

    [Fact]
    public void Clean_branch_without_upstream_shows_only_the_branch()
    {
        RecordingStatusBarItem item = new();

        GitStatusBar.Update(item, Snapshot("feature", upstream: null, ahead: 0, behind: 0, changes: 0));

        Assert.Equal("feature", item.Text);
        Assert.Contains(GitStrings.StatusBarNoUpstream, item.Tooltip);
    }

    [Fact]
    public void Detached_head_shows_the_short_commit_id()
    {
        RecordingStatusBarItem item = new();
        GitSnapshot snapshot = Snapshot("1a2b3c4", upstream: null, ahead: 0, behind: 0, changes: 1);
        snapshot = snapshot with
        {
            Status = new GitRepositoryStatus
            {
                RepositoryRoot = "repo",
                BranchName = "1a2b3c4",
                IsDetached = true,
                Changes = snapshot.Changes
            }
        };

        GitStatusBar.Update(item, snapshot);

        Assert.Equal("1a2b3c4 · 1", item.Text);
        Assert.StartsWith(string.Format(GitStrings.StatusBarDetachedFormat, "1a2b3c4"), item.Tooltip);
    }

    [Fact]
    public void Item_is_hidden_without_repository()
    {
        RecordingStatusBarItem item = new();
        GitStatusBar.Update(item, Snapshot("main", null, 0, 0, 0));
        Assert.True(item.IsVisible);

        GitStatusBar.Update(item, new GitSnapshot { State = GitSessionState.NotRepository });

        Assert.False(item.IsVisible);
    }

    [Fact]
    public void Status_bar_item_is_left_aligned_after_the_tfvc_item_and_has_its_command()
    {
        using MainWindowViewModel viewModel = new();
        RecordingStatusBarItem item = new();
        GitStatusBar.Update(item, Snapshot("main", null, 0, 0, 2));

        viewModel.UpsertStatusBarItem("git", item.Text, item.Tooltip, item.CommandId, StatusBarAlignment.Left, GitStatusBar.PRIORITY, item.IsVisible);
        viewModel.UpsertStatusBarItem("tfvc", "TFVC", null, null, StatusBarAlignment.Left, 40, isVisible: true);

        Assert.Equal(["TFVC", "main · 2"], viewModel.LeftStatusBarItems.Select(entry => entry.Text));
        Assert.NotNull(viewModel.LeftStatusBarItems[1].Command);
        Assert.Empty(viewModel.RightStatusBarItems);
    }

    private static GitSnapshot Snapshot(string branch, string? upstream, int ahead, int behind, int changes)
    {
        GitFileChange[] files = new GitFileChange[changes];
        for (int i = 0; i < changes; i++)
        {
            files[i] = new GitFileChange { Path = "file" + i + ".txt", WorkTreeStatus = GitChangeKind.Modified };
        }

        return new GitSnapshot
        {
            State = GitSessionState.Repository,
            RepositoryRoot = "repo",
            Status = new GitRepositoryStatus
            {
                RepositoryRoot = "repo",
                BranchName = branch,
                UpstreamName = upstream,
                AheadBy = ahead,
                BehindBy = behind,
                Changes = files
            }
        };
    }

    private sealed class RecordingStatusBarItem : IStatusBarItem
    {
        public string Text { get; set; } = string.Empty;

        public string? Tooltip { get; set; }

        public string? CommandId { get; set; }

        public bool IsVisible { get; private set; }

        public void Show() => IsVisible = true;

        public void Hide() => IsVisible = false;

        public void Dispose()
        {
        }
    }
}
