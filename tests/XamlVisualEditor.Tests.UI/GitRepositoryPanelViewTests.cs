using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
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

    [AvaloniaFact]
    public async Task Tags_tab_lists_the_tags_with_icons_and_its_context_menu_and_double_click_pass_the_full_name()
    {
        ScriptedHistoryGitService tags = new();
        tags.Tags.Add(new GitTag
        {
            Name = "v2.0",
            FullName = "refs/tags/v2.0",
            TargetSha = "0123456789abcdef0123456789abcdef01234567",
            TargetShortSha = "01234567",
            IsAnnotated = true,
            TaggerName = "Anna",
            Subject = "Release zwei",
            Date = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
        });
        tags.Tags.Add(new GitTag { Name = "v1.0", FullName = "refs/tags/v1.0", TargetSha = "fedcba9876543210fedcba9876543210fedcba98", Subject = "first" });
        using Fixture fixture = await Fixture.CreateAsync(history: tags);
        GitRepositoryPanelViewModel panel = fixture.Panel;
        GitRepositoryPanelView view = new() { DataContext = panel };
        using ThemedWindow window = ThemedWindow.Show(view, 900, 600);
        view.FindControl<TabItem>("TagsTab")!.IsSelected = true;
        window.Pump();

        Assert.Null(panel.TagsMessage);
        Assert.Equal(new[] { "v2.0", "v1.0" }, panel.Tags.Select(tag => tag.Name));
        // The commit is abbreviated as git abbreviates it.
        Assert.Equal(("01234567", string.Format(GitStrings.AnnotatedTagFormat, "Anna")), (panel.Tags[0].TargetText, panel.Tags[0].ToolTip));
        Assert.Equal(GitStrings.LightweightTag, panel.Tags[1].ToolTip);
        DataGrid grid = view.FindControl<DataGrid>("TagGrid")!;
        DataGridRow[] rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
        Assert.Equal(2, rows.Length);
        Assert.All(rows, row => Assert.NotNull(Assert.Single(row.GetVisualDescendants().OfType<PathIcon>()).Data));
        Assert.False(((ICommand)panel.ShowTagHistoryCommand).CanExecute(null));

        grid.SelectedItem = panel.Tags[1];
        window.Pump();
        ContextMenu menu = grid.ContextMenu!;
        menu.Open(grid);
        window.Pump();
        MenuItem[] items = menu.Items.OfType<MenuItem>().ToArray();
        Assert.Equal(2, items.Length);
        Assert.All(items, item => Assert.IsType<PathIcon>(item.Icon));
        Assert.All(items, item => Assert.True(item.IsEnabled));
        menu.Close();
        await panel.ShowTagHistoryCommand.Execute().ToTask();
        await panel.CreateBranchFromTagCommand.Execute().ToTask();

        Assert.Equal(
            new (string, string?)[] { (GitCommands.SHOW_HISTORY, "refs/tags/v1.0"), (GitCommands.CREATE_BRANCH, "refs/tags/v1.0") },
            fixture.Executed.Select(call => (call.CommandId, call.Arguments?.Single() as string)));
    }

    [AvaloniaFact]
    public async Task Tags_of_a_tree_or_a_blob_show_their_object_type_and_offer_neither_history_nor_a_branch()
    {
        ScriptedHistoryGitService tags = new();
        tags.Tags.Add(new GitTag { Name = "treetag", FullName = "refs/tags/treetag", TargetSha = new string('a', 40), TargetShortSha = "aaaaaaa", TargetType = "tree" });
        tags.Tags.Add(new GitTag
        {
            Name = "anntree",
            FullName = "refs/tags/anntree",
            TargetSha = new string('a', 40),
            TargetShortSha = "aaaaaaa",
            TargetType = "tree",
            IsAnnotated = true,
            TaggerName = "Anna"
        });
        tags.Tags.Add(new GitTag { Name = "blobtag", FullName = "refs/tags/blobtag", TargetSha = new string('b', 40), TargetShortSha = "bbbbbbb", TargetType = "blob" });
        tags.Tags.Add(new GitTag { Name = "v1", FullName = "refs/tags/v1", TargetSha = new string('c', 40), TargetShortSha = "ccccccc" });
        using Fixture fixture = await Fixture.CreateAsync(history: tags);
        GitRepositoryPanelViewModel panel = fixture.Panel;

        Assert.Equal(
            new[]
            {
                string.Format(GitStrings.TagTargetObjectFormat, "tree"),
                string.Format(GitStrings.TagTargetObjectFormat, "tree"),
                string.Format(GitStrings.TagTargetObjectFormat, "blob"),
                "ccccccc"
            },
            panel.Tags.Select(tag => tag.TargetText));
        Assert.Equal(
            GitStrings.LightweightTag + Environment.NewLine + string.Format(GitStrings.TagNotCommitFormat, "tree"),
            panel.Tags[0].ToolTip);
        Assert.Equal(
            string.Format(GitStrings.AnnotatedTagFormat, "Anna") + Environment.NewLine + string.Format(GitStrings.TagNotCommitFormat, "tree"),
            panel.Tags[1].ToolTip);
        for (int index = 0; index < 3; index++)
        {
            panel.SelectedTag = panel.Tags[index];
            Dispatcher.UIThread.RunJobs();
            Assert.False(((ICommand)panel.ShowTagHistoryCommand).CanExecute(null));
            Assert.False(((ICommand)panel.CreateBranchFromTagCommand).CanExecute(null));
        }

        panel.SelectedTag = panel.Tags[3];
        Dispatcher.UIThread.RunJobs();
        Assert.True(((ICommand)panel.ShowTagHistoryCommand).CanExecute(null));
        Assert.True(((ICommand)panel.CreateBranchFromTagCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Tags_tab_shows_the_tag_and_its_commit_in_the_narrow_side_panel_and_scrolls_to_the_other_columns()
    {
        ScriptedHistoryGitService tags = new();
        tags.Tags.Add(new GitTag
        {
            Name = "v2.0",
            FullName = "refs/tags/v2.0",
            TargetSha = "1b12990" + new string('0', 33),
            TargetShortSha = "1b12990",
            IsAnnotated = true,
            TaggerName = "Anna",
            Subject = "Merge feature/merge-demo",
            Date = new DateTimeOffset(2026, 10, 17, 5, 37, 50, TimeSpan.Zero)
        });
        tags.Tags.Add(new GitTag
        {
            Name = "v1.0-light",
            FullName = "refs/tags/v1.0-light",
            TargetSha = "070df50" + new string('0', 33),
            TargetShortSha = "070df50",
            Subject = "Change 120: Größe angepasst",
            Date = new DateTimeOffset(2026, 10, 17, 5, 37, 49, TimeSpan.Zero)
        });
        using Fixture fixture = await Fixture.CreateAsync(history: tags);
        GitRepositoryPanelView view = new() { DataContext = fixture.Panel };
        using ThemedWindow window = ThemedWindow.Show(view, 340, 600);
        view.FindControl<TabItem>("TagsTab")!.IsSelected = true;
        window.Pump();

        DataGrid grid = view.FindControl<DataGrid>("TagGrid")!;
        DataGridRow row = grid.GetVisualDescendants().OfType<DataGridRow>().First(candidate => candidate.IsVisible && candidate.DataContext == fixture.Panel.Tags[1]);
        TextBlock[] texts = row.GetVisualDescendants().OfType<TextBlock>().ToArray();
        TextBlock name = Assert.Single(texts, text => text.Text == "v1.0-light");
        TextBlock commit = Assert.Single(texts, text => text.Text == "070df50");
        double nameRight = name.TranslatePoint(new Avalonia.Point(name.Bounds.Width, 0), grid)!.Value.X;
        double commitRight = commit.TranslatePoint(new Avalonia.Point(commit.Bounds.Width, 0), grid)!.Value.X;

        Assert.True(MeasureWidth(name) <= name.Bounds.Width + 0.5, $"The tag name is {name.Bounds.Width} wide.");
        Assert.True(MeasureWidth(commit) <= commit.Bounds.Width + 0.5, $"The commit is {commit.Bounds.Width} wide.");
        Assert.True(nameRight <= grid.Bounds.Width && commitRight <= grid.Bounds.Width, $"Tag and commit end at {nameRight} and {commitRight} of {grid.Bounds.Width}.");
        Assert.True(grid.Columns.Sum(column => column.ActualWidth) > grid.Bounds.Width);
        Assert.Contains(
            grid.GetVisualDescendants().OfType<ScrollBar>(),
            scrollBar => scrollBar.Orientation == Avalonia.Layout.Orientation.Horizontal && scrollBar.IsEffectivelyVisible);

        static double MeasureWidth(TextBlock text)
        {
            TextBlock probe = new() { Text = text.Text, FontFamily = text.FontFamily, FontSize = text.FontSize, FontWeight = text.FontWeight };
            probe.Measure(Avalonia.Size.Infinity);
            return probe.DesiredSize.Width;
        }
    }

    [AvaloniaTheory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    public async Task Tags_tab_shows_the_whole_date_when_the_tags_arrive_after_the_layout(string cultureName)
    {
        using CultureScope culture = new(cultureName);
        ScriptedHistoryGitService tags = new();
        using Fixture fixture = await Fixture.CreateAsync(history: tags);
        GitRepositoryPanelView view = new() { DataContext = fixture.Panel };
        using ThemedWindow window = ThemedWindow.Show(view, 340, 600, appDensity: true);
        view.FindControl<TabItem>("TagsTab")!.IsSelected = true;
        window.Pump();

        // The tags arrive after the grid was laid out, as from git in the running application.
        DateTime morning = new(2034, 10, 30, 10, 44, 44);
        tags.Tags.Add(CreateTag("v1.2", new DateTimeOffset(morning, TimeZoneInfo.Local.GetUtcOffset(morning))));
        tags.Tags.Add(CreateTag("v1.1", new DateTimeOffset(2026, 12, 28, 22, 58, 58, TimeSpan.Zero)));
        tags.Tags.Add(CreateTag("v1.0", new DateTimeOffset(2026, 10, 17, 23, 37, 51, TimeSpan.Zero)));
        await fixture.Panel.RefreshCommand.Execute().ToTask();
        window.Pump();

        DataGrid grid = view.FindControl<DataGrid>("TagGrid")!;
        DataGridRow[] rows = grid.GetVisualDescendants().OfType<DataGridRow>().Where(row => row.IsVisible).ToArray();
        Assert.Equal(3, rows.Length);
        foreach (DataGridRow row in rows)
        {
            GitTagItemViewModel tag = (GitTagItemViewModel)row.DataContext!;
            Assert.Equal(tag.Tag.Date!.Value.ToLocalTime().ToString("G", System.Globalization.CultureInfo.GetCultureInfo(cultureName)), tag.DateText);
            GitHistoryViewTests.AssertNotClipped(Assert.Single(row.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == tag.DateText));
        }

        // A copied row holds the tag name and the date of the template columns.
        GitTagItemViewModel copied = (GitTagItemViewModel)rows[0].DataContext!;
        Assert.Equal(
            new[] { copied.Name, copied.TargetText, copied.DateText, copied.Subject },
            await GitHistoryViewTests.CopyRowAsync(window, grid, copied));

        static GitTag CreateTag(string name, DateTimeOffset date) => new()
        {
            Name = name,
            FullName = "refs/tags/" + name,
            TargetSha = "1b12990" + new string('0', 33),
            TargetShortSha = "1b12990",
            Subject = "Release " + name,
            Date = date
        };
    }

    [AvaloniaFact]
    public async Task Branches_tab_keeps_ahead_and_behind_whole_and_ends_a_long_name_with_an_ellipsis_in_the_narrow_side_panel()
    {
        const string LONG_NAME = "origin/feature/settings-page-with-a-much-longer-name";
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Git.Branches.Add(new GitBranch { Name = LONG_NAME, FullName = "refs/remotes/" + LONG_NAME, IsRemote = true, RemoteName = "origin" });
        await fixture.Panel.RefreshCommand.Execute().ToTask();
        Dispatcher.UIThread.RunJobs();
        GitRepositoryPanelView view = new() { DataContext = fixture.Panel };
        using ThemedWindow window = ThemedWindow.Show(view, 340, 600);

        Avalonia.Controls.TreeView tree = view.FindControl<Avalonia.Controls.TreeView>("BranchTree")!;
        TreeViewItem[] rows = tree.GetVisualDescendants().OfType<TreeViewItem>()
            .Where(item => item.DataContext is GitBranchItemViewModel)
            .ToArray();
        TreeViewItem mainRow = Assert.Single(rows, row => ((GitBranchItemViewModel)row.DataContext!).Name == "main");
        TextBlock tracking = Assert.Single(mainRow.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "TrackingText");
        Assert.Equal("↑1 ↓2", tracking.Text);
        GitHistoryViewTests.AssertNotClipped(tracking);
        Assert.True(RightEdge(tracking) <= tree.Bounds.Width + 0.5, $"The ahead and behind text ends at {RightEdge(tracking)} of {tree.Bounds.Width}.");

        TreeViewItem longRow = Assert.Single(rows, row => ((GitBranchItemViewModel)row.DataContext!).Name == LONG_NAME);
        GitBranchItemViewModel longItem = (GitBranchItemViewModel)longRow.DataContext!;
        TextBlock name = Assert.Single(longRow.GetVisualDescendants().OfType<TextBlock>(), text => text.Name == "BranchName");
        TextBlock probe = new() { Text = name.Text, FontFamily = name.FontFamily, FontSize = name.FontSize, FontWeight = name.FontWeight };
        probe.Measure(Size.Infinity);
        Assert.Equal(TextTrimming.CharacterEllipsis, name.TextTrimming);
        Assert.True(probe.DesiredSize.Width > name.Bounds.Width, "The long name fits; the test needs a name that is cut.");
        Assert.True(RightEdge(name) <= tree.Bounds.Width + 0.5, $"The name ends at {RightEdge(name)} of {tree.Bounds.Width}.");
        Assert.StartsWith(LONG_NAME, longItem.ToolTip, StringComparison.Ordinal);
        Assert.Equal(longItem.ToolTip, ToolTip.GetTip(Assert.IsType<Grid>(name.Parent)));
        Assert.DoesNotContain(
            tree.GetVisualDescendants().OfType<ScrollBar>(),
            scrollBar => scrollBar.Orientation == Avalonia.Layout.Orientation.Horizontal && scrollBar.IsEffectivelyVisible);

        double RightEdge(Visual visual) => visual.TranslatePoint(new Point(visual.Bounds.Width, 0), tree)!.Value.X;
    }

    [AvaloniaFact]
    public async Task Tags_tab_says_when_there_are_no_tags_or_the_service_cannot_list_them()
    {
        using Fixture withoutService = await Fixture.CreateAsync();
        using Fixture withoutTags = await Fixture.CreateAsync(history: new ScriptedHistoryGitService());

        Assert.Equal(GitStrings.TagsUnavailable, withoutService.Panel.TagsMessage);
        Assert.Equal(GitStrings.NoTags, withoutTags.Panel.TagsMessage);
        Assert.Empty(withoutTags.Panel.Tags);
    }

    [AvaloniaFact]
    public async Task Stashes_tab_lists_the_stashes_and_its_buttons_pass_the_commit_id_of_the_selected_stash()
    {
        StashList stashes = new(
            new GitStashEntry(0, new string('b', 40), "On main: Grüße", new DateTimeOffset(2026, 10, 2, 8, 0, 0, TimeSpan.Zero)),
            new GitStashEntry(1, new string('a', 40), "WIP on main: 1234567 first", new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero)));
        using Fixture fixture = await Fixture.CreateAsync(stash: stashes);
        GitRepositoryPanelViewModel panel = fixture.Panel;
        GitRepositoryPanelView view = new() { DataContext = panel };
        using ThemedWindow window = ThemedWindow.Show(view, 900, 600);
        view.FindControl<TabItem>("StashesTab")!.IsSelected = true;
        window.Pump();

        Assert.Null(panel.StashesMessage);
        Assert.Equal(new[] { "stash@{0}", "stash@{1}" }, panel.Stashes.Select(stash => stash.Name));
        DataGrid grid = view.FindControl<DataGrid>("StashGrid")!;
        Assert.Equal(2, grid.GetVisualDescendants().OfType<DataGridRow>().Count(row => row.IsVisible));
        (ReactiveCommand<RxVoid, RxVoid> Command, string CommandId)[] selected =
        [
            (panel.ApplyStashCommand, GitCommands.STASH_APPLY),
            (panel.PopStashCommand, GitCommands.STASH_POP),
            (panel.DropStashCommand, GitCommands.STASH_DROP)
        ];
        (ReactiveCommand<RxVoid, RxVoid> Command, string CommandId)[] always =
        [
            (panel.StashCommand, GitCommands.STASH),
            (panel.StashIncludeUntrackedCommand, GitCommands.STASH_INCLUDE_UNTRACKED)
        ];
        foreach ((ReactiveCommand<RxVoid, RxVoid> command, string _) in selected.Concat(always))
        {
            Button button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), candidate => ReferenceEquals(candidate.Command, command));
            PathIcon icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
            Assert.NotNull(icon.Data);
            Assert.False(icon.IsSet(TemplatedControl.ForegroundProperty));
            Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => !string.IsNullOrWhiteSpace(text.Text));
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
            Assert.IsType<string>(ToolTip.GetTip(button));
        }

        Assert.All(selected, entry => Assert.False(((ICommand)entry.Command).CanExecute(null)));
        grid.SelectedItem = panel.Stashes[1];
        window.Pump();
        foreach ((ReactiveCommand<RxVoid, RxVoid> command, string commandId) in selected)
        {
            await command.Execute().ToTask();
            Assert.Equal((commandId, new string('a', 40)), (fixture.Executed[^1].CommandId, fixture.Executed[^1].Arguments?.Single() as string));
        }

        foreach ((ReactiveCommand<RxVoid, RxVoid> command, string commandId) in always)
        {
            await command.Execute().ToTask();
            Assert.Equal((commandId, (IReadOnlyList<object?>?)null), fixture.Executed[^1]);
        }
    }

    [AvaloniaFact]
    public async Task Stashes_tab_says_when_there_are_no_stashes_or_the_service_cannot_list_them()
    {
        using Fixture withoutService = await Fixture.CreateAsync();
        using Fixture withoutStashes = await Fixture.CreateAsync(stash: new StashList());

        Assert.Equal(GitStrings.StashesUnavailable, withoutService.Panel.StashesMessage);
        Assert.Equal(GitStrings.NoStashes, withoutStashes.Panel.StashesMessage);
        Assert.Empty(withoutStashes.Panel.Stashes);
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
        private Fixture(BranchGitService git, IGitHistoryService? history, IGitStashService? stash)
        {
            Git = git;
            Session = new GitSession(new GitServiceSet(git) { Branches = git, History = history, Stash = stash }, new WorkspaceInfo());
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
            bool detached = false,
            IGitHistoryService? history = null,
            IGitStashService? stash = null)
        {
            BranchGitService git = new(upstream, branchError) { IsDetached = detached };
            Fixture fixture = new(git, history, stash);
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

    /// <summary>A fixed list of stashes; changing them is not part of the panel.</summary>
    private sealed class StashList(params GitStashEntry[] stashes) : IGitStashService
    {
        public Task<IReadOnlyList<GitStashEntry>> ListAsync(string repoPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GitStashEntry>>(stashes);

        public Task PushAsync(string repoPath, string? message, bool includeUntracked, CancellationToken ct = default) => throw new NotSupportedException();

        public Task ApplyAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> PopAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<bool> DropAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default) => throw new NotSupportedException();
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

        public Task<GitMergeResult> MergeAsync(string repoPath, string name, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task AbortMergeAsync(string repoPath, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
