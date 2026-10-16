using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitRepositoryPanelViewTests
{
    [AvaloniaFact]
    public async Task Branches_are_listed_in_local_and_remote_groups_with_the_current_one_marked()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitRepositoryPanelView view = new() { DataContext = fixture.Panel };
        Window window = Show(view);
        try
        {
            GitRepositoryPanelViewModel panel = fixture.Panel;
            Assert.Equal(new[] { "feature", "main" }, panel.LocalGroup.Branches.Select(item => item.Name));
            Assert.Equal(new[] { "origin/main" }, panel.RemoteGroup.Branches.Select(item => item.Name));
            GitBranchItemViewModel main = panel.LocalGroup.Branches[1];
            Assert.True(main.IsCurrent);
            Assert.False(main.CanSwitch);
            Assert.False(main.CanDelete);
            Assert.Equal("↑1 ↓2", main.TrackingText);
            Assert.Contains(GitStrings.CurrentBranch, main.ToolTip, StringComparison.Ordinal);
            Assert.True(panel.LocalGroup.Branches[0].CanDelete);
            Assert.False(panel.RemoteGroup.Branches[0].CanDelete);
            Assert.True(panel.RemoteGroup.Branches[0].CanSwitch);

            Avalonia.Controls.TreeView tree = Assert.IsType<Avalonia.Controls.TreeView>(view.FindControl<Avalonia.Controls.TreeView>("BranchTree"));
            TreeViewItem[] groups = tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(item => item.DataContext is GitBranchGroupViewModel)
                .ToArray();
            Assert.Equal(2, groups.Length);
            Assert.All(groups, group => Assert.True(group.IsExpanded));

            TreeViewItem[] rows = tree.GetVisualDescendants().OfType<TreeViewItem>()
                .Where(item => item.DataContext is GitBranchItemViewModel)
                .ToArray();
            Assert.Equal(3, rows.Length);
            foreach (TreeViewItem row in rows)
            {
                GitBranchItemViewModel item = (GitBranchItemViewModel)row.DataContext!;
                PathIcon marker = Assert.Single(row.GetVisualDescendants().OfType<PathIcon>());
                Assert.Equal(item.IsCurrent, marker.IsVisible);
                Assert.False(marker.IsSet(TemplatedControl.ForegroundProperty));
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == item.Name);
            }

            TreeViewItem mainRow = Assert.Single(rows, row => ReferenceEquals(row.DataContext, main));
            Assert.Contains(mainRow.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "↑1 ↓2");
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Every_toolbar_button_has_an_icon_a_label_and_a_tooltip_and_runs_its_git_command()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitRepositoryPanelViewModel panel = fixture.Panel;
        GitRepositoryPanelView view = new() { DataContext = panel };
        Window window = Show(view, width: 1200);
        try
        {
            (ReactiveCommand<RxVoid, RxVoid> Command, string? CommandId)[] expected =
            [
                (panel.RefreshCommand, null),
                (panel.FetchCommand, GitCommands.FETCH),
                (panel.PullCommand, GitCommands.PULL),
                (panel.PushCommand, GitCommands.PUSH),
                (panel.SyncCommand, GitCommands.SYNC),
                (panel.CreateBranchCommand, GitCommands.CREATE_BRANCH)
            ];
            Button[] buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Command is not null)
                .ToArray();
            Assert.Equal(expected.Length, buttons.Length);

            foreach ((ReactiveCommand<RxVoid, RxVoid> command, string? commandId) in expected)
            {
                Button button = Assert.Single(buttons, candidate => ReferenceEquals(candidate.Command, command));
                PathIcon icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
                Assert.NotNull(icon.Data);
                Assert.False(icon.IsSet(TemplatedControl.ForegroundProperty));
                Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => !string.IsNullOrWhiteSpace(text.Text));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.IsType<string>(ToolTip.GetTip(button));
                Assert.True(((ICommand)command).CanExecute(null), commandId ?? "refresh");
                if (commandId is not null)
                {
                    await command.Execute().ToTask();
                    Assert.Equal((commandId, (IReadOnlyList<object?>?)null), fixture.Executed[^1]);
                }
            }
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Branch_context_menu_has_icons_follows_the_branch_and_passes_the_full_name()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitRepositoryPanelViewModel panel = fixture.Panel;
        GitRepositoryPanelView view = new() { DataContext = panel };
        Window window = Show(view);
        try
        {
            Avalonia.Controls.TreeView tree = Assert.IsType<Avalonia.Controls.TreeView>(view.FindControl<Avalonia.Controls.TreeView>("BranchTree"));
            Dictionary<string, MenuItem[]> menus = new();
            foreach (TreeViewItem row in tree.GetVisualDescendants().OfType<TreeViewItem>()
                         .Where(item => item.DataContext is GitBranchItemViewModel))
            {
                Grid grid = row.GetVisualDescendants().OfType<Grid>().First(candidate => candidate.ContextMenu is not null);
                ContextMenu menu = grid.ContextMenu!;
                menu.Open(grid);
                Dispatcher.UIThread.RunJobs();
                menus[((GitBranchItemViewModel)row.DataContext!).Name] = menu.Items.OfType<MenuItem>().ToArray();
                Assert.All(menus[((GitBranchItemViewModel)row.DataContext!).Name], item => Assert.IsType<PathIcon>(item.Icon));
                menu.Close();
            }

            Assert.Equal(3, menus.Count);
            Assert.Equal(new[] { false, true, false }, menus["main"].Select(item => item.IsEnabled));
            Assert.Equal(new[] { true, true, true }, menus["feature"].Select(item => item.IsEnabled));
            Assert.Equal(new[] { true, true, false }, menus["origin/main"].Select(item => item.IsEnabled));

            GitBranchItemViewModel feature = panel.LocalGroup.Branches[0];
            GitBranchItemViewModel remote = panel.RemoteGroup.Branches[0];
            await panel.SwitchCommand.Execute(remote).ToTask();
            await panel.DeleteCommand.Execute(feature).ToTask();
            await panel.CreateBranchFromCommand.Execute(remote).ToTask();

            Assert.Equal(
                new (string, string?)[]
                {
                    (GitCommands.CHECKOUT, "refs/remotes/origin/main"),
                    (GitCommands.DELETE_BRANCH, "refs/heads/feature"),
                    (GitCommands.CREATE_BRANCH, "refs/remotes/origin/main")
                },
                fixture.Executed.Select(call => (call.CommandId, call.Arguments?.Single() as string)));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public async Task Sync_buttons_need_a_remote_and_pull_and_sync_need_an_upstream()
    {
        using Fixture fixture = await Fixture.CreateAsync(upstream: null);
        GitRepositoryPanelViewModel panel = fixture.Panel;

        Assert.False(((ICommand)panel.FetchCommand).CanExecute(null));
        Assert.False(((ICommand)panel.PushCommand).CanExecute(null));
        Assert.False(((ICommand)panel.PullCommand).CanExecute(null));
        Assert.False(((ICommand)panel.SyncCommand).CanExecute(null));
        Assert.True(((ICommand)panel.CreateBranchCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_failing_branch_list_is_reported_in_the_panel()
    {
        using Fixture fixture = await Fixture.CreateAsync(branchError: "fatal: bad object");

        Assert.Equal(string.Format(GitStrings.LoadBranchesFailedFormat, "fatal: bad object"), fixture.Panel.StatusMessage);
        Assert.Empty(fixture.Panel.LocalGroup.Branches);
    }

    [AvaloniaFact]
    public async Task Branches_are_read_only_while_the_view_is_attached()
    {
        using Fixture fixture = await Fixture.CreateAsync(shown: false);
        GitRepositoryPanelViewModel panel = fixture.Panel;
        Assert.Equal(0, fixture.Git.BranchReads);
        Assert.Empty(panel.LocalGroup.Branches);

        GitRepositoryPanelView view = new() { DataContext = panel };
        Window window = Show(view);
        try
        {
            Assert.True(panel.IsShown);
            Assert.Equal(1, fixture.Git.BranchReads);
            Assert.Equal(new[] { "feature", "main" }, panel.LocalGroup.Branches.Select(item => item.Name));
        }
        finally
        {
            Close(window);
        }

        Assert.False(panel.IsShown);
        await fixture.Session.RefreshAllAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fixture.Git.BranchReads);
    }

    [AvaloniaFact]
    public async Task Branches_are_read_again_only_when_refs_or_head_change_and_unchanged_items_are_kept()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        GitRepositoryPanelViewModel panel = fixture.Panel;
        GitBranchItemViewModel[] before = panel.LocalGroup.Branches.Concat(panel.RemoteGroup.Branches).ToArray();
        panel.SelectedItem = before[1];
        Assert.Equal(1, fixture.Git.BranchReads);

        // A change of the working tree only.
        fixture.Git.Changes = [new GitFileChange { Path = "a.txt", WorkTreeStatus = GitChangeKind.Modified }];
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, fixture.Session.Current.ChangeCount);
        Assert.Equal(1, fixture.Git.BranchReads);

        // A branch created outside of HEAD and a commit on the current branch.
        fixture.Git.Branches.Insert(1, new GitBranch { Name = "fix", FullName = "refs/heads/fix" });
        fixture.Git.Branches[2] = fixture.Git.Branches[2] with { Subject = "new main work" };
        await panel.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, fixture.Git.BranchReads);
        Assert.Equal(new[] { "feature", "fix", "main" }, panel.LocalGroup.Branches.Select(item => item.Name));
        Assert.Same(before[0], panel.LocalGroup.Branches[0]);
        Assert.Same(before[2], panel.RemoteGroup.Branches[0]);
        GitBranchItemViewModel main = panel.LocalGroup.Branches[2];
        Assert.NotSame(before[1], main);
        Assert.Contains("new main work", main.ToolTip, StringComparison.Ordinal);
        Assert.Same(main, panel.SelectedItem);

        fixture.Git.Branches.RemoveAt(1);
        fixture.Git.HeadSha = "b2";
        await fixture.Session.RefreshAsync();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(3, fixture.Git.BranchReads);
        Assert.Equal(new[] { "feature", "main" }, panel.LocalGroup.Branches.Select(item => item.Name));
        Assert.Same(main, panel.LocalGroup.Branches[1]);
    }

    [AvaloniaFact]
    public async Task A_detached_head_is_shown_above_the_branches_and_a_gone_upstream_next_to_its_branch()
    {
        using Fixture fixture = await Fixture.CreateAsync(detached: true);
        fixture.Git.Branches[0] = fixture.Git.Branches[0] with { UpstreamName = "origin/feature", IsUpstreamGone = true };
        await fixture.Session.RefreshAllAsync();
        Dispatcher.UIThread.RunJobs();
        GitRepositoryPanelView view = new() { DataContext = fixture.Panel };
        Window window = Show(view);
        try
        {
            string detached = string.Format(GitStrings.StatusBarDetachedFormat, "a1b2c3d");
            Assert.Equal(detached, fixture.Panel.DetachedHeadText);
            TextBlock line = Assert.IsType<TextBlock>(view.FindControl<TextBlock>("DetachedHead"));
            Assert.True(line.IsVisible);
            Assert.Equal(detached, line.Text);

            GitBranchItemViewModel feature = fixture.Panel.LocalGroup.Branches[0];
            Assert.Equal(GitStrings.UpstreamGone, feature.TrackingText);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == GitStrings.UpstreamGone);
        }
        finally
        {
            Close(window);
        }
    }

    /// <summary>Shows the content with the Fluent theme, which templates the tab control and the tree.</summary>
    private static Window Show(Control content, double width = 500, double height = 600)
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Avalonia.Application.Current!.Styles.Add(theme);
        Window window = new() { Content = content, Width = width, Height = height, Tag = theme };
        window.Show();
        // The tab content and the tree items are created in later layout passes.
        for (int pass = 0; pass < 3; pass++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Close(Window window)
    {
        window.Close();
        Avalonia.Application.Current!.Styles.Remove((Avalonia.Themes.Fluent.FluentTheme)window.Tag!);
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(BranchGitService git)
        {
            Git = git;
            Session = new GitSession(new GitServiceSet(git) { Branches = git }, new WorkspaceInfo());
            Panel = new GitRepositoryPanelViewModel(Session, (commandId, arguments) =>
            {
                Executed.Add((commandId, arguments));
                return Task.CompletedTask;
            });
        }

        public BranchGitService Git { get; }

        public GitSession Session { get; }

        public GitRepositoryPanelViewModel Panel { get; }

        public List<(string CommandId, IReadOnlyList<object?>? Arguments)> Executed { get; } = new();

        /// <summary>
        /// Creates the panel over a repository with the branches main, feature and origin/main. Unless
        /// <paramref name="shown"/> is false, the panel is marked as shown like by its attached view, so it reads the
        /// branches.
        /// </summary>
        public static async Task<Fixture> CreateAsync(
            string? upstream = "origin/main",
            string? branchError = null,
            bool shown = true,
            bool detached = false)
        {
            BranchGitService git = new(upstream, branchError) { IsDetached = detached };
            Fixture fixture = new(git);
            fixture.Panel.IsShown = shown;
            await fixture.Session.RefreshAsync();
            Dispatcher.UIThread.RunJobs();
            Assert.True(fixture.Panel.IsRepository);
            return fixture;
        }

        public void Dispose()
        {
            Panel.Dispose();
            Session.Dispose();
        }
    }

    private sealed class WorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath => "test-workspace";

        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    /// <summary>
    /// A repository on branch main, ahead 1 and behind 2 of origin/main, with the local branch feature. The branches
    /// and the status can be changed, and the branch reads are counted.
    /// </summary>
    private sealed class BranchGitService : IGitService, IGitBranchService
    {
        private readonly string? _upstream;
        private readonly string? _branchError;
        private int _branchReads;

        public BranchGitService(string? upstream, string? branchError)
        {
            _upstream = upstream;
            _branchError = branchError;
            Branches =
            [
                new GitBranch { Name = "feature", FullName = "refs/heads/feature", Subject = "feature work" },
                new GitBranch
                {
                    Name = "main",
                    FullName = "refs/heads/main",
                    IsCurrent = true,
                    UpstreamName = _upstream,
                    AheadBy = _upstream is null ? 0 : 1,
                    BehindBy = _upstream is null ? 0 : 2,
                    Subject = "main work"
                },
                new GitBranch { Name = "origin/main", FullName = "refs/remotes/origin/main", IsRemote = true, RemoteName = "origin" }
            ];
        }

        /// <summary>Gets the branches the next read returns.</summary>
        public List<GitBranch> Branches { get; }

        public bool IsDetached { get; init; }

        public string HeadSha { get; set; } = "a1";

        public GitFileChange[] Changes { get; set; } = [];

        public int BranchReads => Volatile.Read(ref _branchReads);

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>("test-repo");

        public Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult(new GitRepositoryStatus
            {
                RepositoryRoot = repoPath,
                BranchName = IsDetached ? "a1b2c3d" : "main",
                IsDetached = IsDetached,
                HeadSha = HeadSha,
                UpstreamName = IsDetached ? null : _upstream,
                AheadBy = 1,
                BehindBy = 2,
                Changes = Changes
            });

        public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
            Task.FromResult(new GitDiff { Files = Array.Empty<GitDiffFile>() });

        public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

        public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

        public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _branchReads);
            if (_branchError is not null)
            {
                return Task.FromException<IReadOnlyList<GitBranch>>(new InvalidOperationException(_branchError));
            }

            return Task.FromResult<IReadOnlyList<GitBranch>>(Branches.ToArray());
        }

        public Task SwitchAsync(string repoPath, string name, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CreateAsync(string repoPath, string name, string? startPoint, bool switchTo, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<bool> IsValidBranchNameAsync(string repoPath, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DetachAsync(string repoPath, string commitish, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<GitBranchDeleteResult> DeleteAsync(string repoPath, string name, bool force, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
