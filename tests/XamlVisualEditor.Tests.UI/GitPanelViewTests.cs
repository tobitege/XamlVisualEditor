using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Controls;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using GitChangeKind = XamlVisualEditor.Core.GitChangeKind;
using GitConflictKind = XamlVisualEditor.Core.GitConflictKind;
using GitDiff = XamlVisualEditor.Core.GitDiff;
using GitDiffRequest = XamlVisualEditor.Core.GitDiffRequest;
using GitFileChange = XamlVisualEditor.Core.GitFileChange;
using GitRepositoryStatus = XamlVisualEditor.Core.GitRepositoryStatus;
using GitServiceSet = XamlVisualEditor.Core.GitServiceSet;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitPanelViewTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task Git_refresh_coalesces_overlapping_requests()
    {
        ControlledGitService gitService = new();
        using PanelFixture panel = new(gitService);

        Task first = panel.ViewModel.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(WaitLimit);
        Task second = panel.ViewModel.RefreshAsync();
        Task third = panel.ViewModel.RefreshAsync();
        Assert.Equal(1, gitService.StatusCalls);
        gitService.ReleaseFirstStatus.TrySetResult(true);
        await Task.WhenAll(first, second, third).WaitAsync(WaitLimit);

        Assert.Equal(1, gitService.RootCalls);
        Assert.Equal(2, gitService.StatusCalls);
        Assert.Equal(1, gitService.MaxConcurrentStatusCalls);
    }

    [AvaloniaFact]
    public async Task Disposing_the_session_cancels_a_running_status()
    {
        ControlledGitService gitService = new();
        PanelFixture panel = new(gitService);
        _ = panel.ViewModel.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(WaitLimit);

        panel.Dispose();

        Assert.True(await gitService.FirstStatusCancelled.Task.WaitAsync(WaitLimit));
        Assert.Equal(1, gitService.StatusCalls);
    }

    [AvaloniaFact]
    public async Task Status_is_read_without_the_working_folder_watcher_and_lists_each_change_once()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        Assert.False(vm.IsGitMonitoringEnabled);
        Assert.Equal(1, gitService.StatusCalls);
        Assert.Equal(new[] { "a.cs", "b.cs" }, vm.StagedItems.Select(item => item.Path));
        // The conflicted file is only in the working tree list, where staging marks it as resolved.
        Assert.Equal(new[] { "c.cs", "conflict.cs", "new.txt" }, vm.UnstagedItems.Select(item => item.Path));
    }

    [AvaloniaFact]
    public async Task Watcher_switch_only_controls_the_working_folder_watcher()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        Assert.Equal(UiStrings.GitMonitoringOff, vm.GitMonitoringLabel);

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.True(vm.IsGitMonitoringEnabled);
        Assert.Equal(UiStrings.GitMonitoringOn, vm.GitMonitoringLabel);
        // Switching the watcher on reads the status once, because files may have changed while it was off.
        Assert.Equal(2, gitService.StatusCalls);

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        Assert.False(vm.IsGitMonitoringEnabled);
        Assert.True(vm.IsRepository);
        Assert.Equal(2, gitService.StatusCalls);
        Assert.Equal(2, vm.StagedCount);
    }

    [AvaloniaFact]
    public async Task Buttons_run_the_git_commands_with_the_selected_path()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedStagedChange = Assert.Single(vm.StagedItems, item => item.Path == "a.cs");
        await vm.UnstageCommand.Execute().ToTask();
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        await vm.StageCommand.Execute().ToTask();
        await vm.DiscardCommand.Execute().ToTask();
        await vm.StageAllCommand.Execute().ToTask();
        await vm.UnstageAllCommand.Execute().ToTask();
        vm.CommitMessage = "Message";
        await vm.CommitCommand.Execute().ToTask();

        Assert.Equal(
            new (string, string?)[]
            {
                (GitCommands.UNSTAGE, "a.cs"),
                (GitCommands.STAGE, "c.cs"),
                (GitCommands.DISCARD, "c.cs"),
                (GitCommands.STAGE_ALL, null),
                (GitCommands.UNSTAGE_ALL, null),
                (GitCommands.COMMIT, null)
            },
            panel.Executed.Select(call => (call.CommandId, call.Arguments?.Single() as string)));
    }

    [AvaloniaFact]
    public async Task Discard_is_disabled_for_untracked_and_conflicted_files()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "new.txt");
        Assert.False(((ICommand)vm.DiscardCommand).CanExecute(null));
        Assert.True(((ICommand)vm.StageCommand).CanExecute(null));

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "conflict.cs");
        Assert.False(((ICommand)vm.DiscardCommand).CanExecute(null));
        Assert.True(((ICommand)vm.StageCommand).CanExecute(null));

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        Assert.True(((ICommand)vm.DiscardCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public async Task A_failing_command_is_shown_in_the_panel()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService, failure: new System.IO.IOException("pipe broken"));
        GitPanelViewModel vm = panel.ViewModel;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        Task<string?> shown = vm.WhenAnyValue(x => x.ErrorMessage)
            .FirstAsync(message => message is not null)
            .Timeout(WaitLimit)
            .ToTask();
        await Assert.ThrowsAsync<System.IO.IOException>(() => vm.StageCommand.Execute().ToTask());

        // ReactiveUI reports the failure through ThrownExceptions on the UI thread after the execution ends.
        Assert.Equal(string.Format(UiStrings.GitCommandFailed, "pipe broken"), await shown);
    }

    [AvaloniaFact]
    public async Task The_diff_of_the_selected_file_reloads_after_a_refresh_with_an_equal_status()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = "first" };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        Task<string> first = vm.WhenAnyValue(x => x.DiffText).FirstAsync(text => text == "first").Timeout(WaitLimit).ToTask();
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        await first;
        int diffCalls = gitService.DiffCalls;

        // The file changed again, but its status stays " M", so the session publishes no new snapshot.
        gitService.DiffText = "second";
        Task<string> second = vm.WhenAnyValue(x => x.DiffText).FirstAsync(text => text == "second").Timeout(WaitLimit).ToTask();
        await vm.RefreshAsync().WaitAsync(WaitLimit);

        Assert.Equal("second", await second);
        Assert.Equal(diffCalls + 1, gitService.DiffCalls);
        Assert.Equal("c.cs", vm.SelectedUnstagedChange?.Path);
    }

    [AvaloniaFact]
    public async Task A_diff_that_finishes_after_another_file_was_selected_is_not_shown()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false) { DiffText = "diff of new.txt" };
        using PanelFixture panel = await PanelFixture.CreateAsync(gitService);
        GitPanelViewModel vm = panel.ViewModel;
        TaskCompletionSource<GitDiff> slowDiff = new();
        gitService.GatedDiffs["c.cs"] = slowDiff;
        int diffCalls = gitService.DiffCalls;

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "new.txt");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("diff of new.txt", vm.DiffText);

        // The diff of c.cs completes after new.txt was selected and its diff is shown.
        slowDiff.SetResult(ControlledGitService.CreateDiff("diff of c.cs"));
        Dispatcher.UIThread.RunJobs();

        Assert.True(slowDiff.Task.IsCompletedSuccessfully);
        Assert.Equal("diff of new.txt", vm.DiffText);
        Assert.Equal("new.txt", vm.SelectedUnstagedChange?.Path);
        Assert.Equal(diffCalls + 2, gitService.DiffCalls);
    }

    private static GitRepositoryStatus CreateStatus() => new()
    {
        RepositoryRoot = string.Empty,
        Changes = new[]
        {
            new GitFileChange { Path = "a.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "b.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "c.cs", WorkTreeStatus = GitChangeKind.Modified },
            new GitFileChange
            {
                Path = "conflict.cs",
                IndexStatus = GitChangeKind.Unmerged,
                WorkTreeStatus = GitChangeKind.Unmerged,
                ConflictKind = GitConflictKind.BothModified
            },
            new GitFileChange { Path = "new.txt", WorkTreeStatus = GitChangeKind.Untracked, IsUntracked = true }
        }
    };

    [AvaloniaFact]
    public async Task GitPanelView_Renders_Diff_Lines()
    {
        using PanelFixture panel = new(new ControlledGitService(blockFirstStatus: false));
        GitPanelViewModel vm = panel.ViewModel;
        vm.DiffLines.Add(new GitDiffLineViewModel(GitDiffLineKind.FileHeader, "diff --git a/a.txt b/a.txt", string.Empty, null, null));
        vm.DiffLines.Add(new GitDiffLineViewModel(GitDiffLineKind.HunkHeader, "@@ -1 +1 @@", "@@", null, null));
        vm.DiffLines.Add(new GitDiffLineViewModel(GitDiffLineKind.Added, "added", "+", null, 1));
        vm.DiffLines.Add(new GitDiffLineViewModel(GitDiffLineKind.Removed, "removed", "-", 1, null));

        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                view.ApplyTemplate();
                ToggleButton toggle = Assert.Single(view.GetVisualDescendants().OfType<ToggleButton>(),
                    button => ReferenceEquals(button.Command, vm.ToggleMonitoringCommand));
                Assert.False(toggle.IsChecked);
                Assert.Contains(toggle.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == vm.GitMonitoringLabel);
                Button discard = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => ReferenceEquals(button.Command, vm.DiscardCommand));
                Assert.True(ToolTip.GetShowOnDisabled(discard));
                Assert.Equal(UiStrings.GitDiscardTooltip, ToolTip.GetTip(discard));
                GitDiffTextEditor? editor = view.GetVisualDescendants().OfType<GitDiffTextEditor>().FirstOrDefault();
                Assert.NotNull(editor);
                Assert.Contains("diff --git", editor!.Text);
                Assert.Contains("+ added", editor.Text);
                Assert.Contains("- removed", editor.Text);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    [AvaloniaFact]
    public async Task Diff_pane_hides_and_restores_the_resized_width()
    {
        using PanelFixture panel = new(new ControlledGitService(blockFirstStatus: false));
        GitPanelViewModel vm = panel.ViewModel;
        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view);
        try
        {
            Grid grid = Assert.IsType<Grid>(view.FindControl<Grid>("GitContentGrid"));
            GridSplitter splitter = Assert.IsType<GridSplitter>(view.FindControl<GridSplitter>("GitDiffSplitter"));
            DockPanel diff = Assert.IsType<DockPanel>(view.FindControl<DockPanel>("GitDiffPane"));
            DockPanel list = Assert.IsType<DockPanel>(view.FindControl<DockPanel>("GitListPane"));
            GridLength resizedWidth = new(280);
            await Dispatcher.UIThread.InvokeAsync(() => grid.ColumnDefinitions[2].Width = resizedWidth);

            await vm.ToggleDiffCommand.Execute().ToTask();
            Assert.False(vm.IsDiffVisible);
            Assert.Equal(UiStrings.GitShowDiff, vm.DiffToggleLabel);
            Assert.Equal(0, grid.ColumnDefinitions[1].Width.Value);
            Assert.Equal(0, grid.ColumnDefinitions[2].Width.Value);
            Assert.False(splitter.IsVisible);
            Assert.False(diff.IsVisible);
            Assert.Contains(list.GetVisualDescendants().OfType<TextBox>(),
                box => box.PlaceholderText == UiStrings.CommitMessage);

            await vm.ToggleDiffCommand.Execute().ToTask();
            Assert.True(vm.IsDiffVisible);
            Assert.Equal(resizedWidth, grid.ColumnDefinitions[2].Width);
            Assert.True(splitter.IsVisible);
            Assert.True(diff.IsVisible);
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    [AvaloniaFact]
    public async Task Every_panel_button_has_an_icon_a_label_and_a_tooltip()
    {
        using PanelFixture panel = new(new ControlledGitService(blockFirstStatus: false));
        GitPanelViewModel vm = panel.ViewModel;
        GitPanelView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view, width: 1400);
        try
        {
            ICommand[] commands =
            [
                vm.RefreshCommand, vm.ToggleDiffCommand, vm.ToggleMonitoringCommand, vm.StageCommand,
                vm.UnstageCommand, vm.DiscardCommand, vm.StageAllCommand, vm.UnstageAllCommand, vm.CommitCommand
            ];
            Button[] buttons = view.GetVisualDescendants().OfType<Button>()
                .Where(button => button.Command is not null)
                .ToArray();
            Assert.Equal(commands.Length, buttons.Length);

            foreach (ICommand command in commands)
            {
                Button button = Assert.Single(buttons, candidate => ReferenceEquals(candidate.Command, command));
                PathIcon icon = Assert.Single(button.GetVisualDescendants().OfType<PathIcon>());
                Assert.NotNull(icon.Data);
                Assert.False(icon.IsSet(TemplatedControl.ForegroundProperty));
                Assert.Contains(button.GetVisualDescendants().OfType<TextBlock>(), text => !string.IsNullOrWhiteSpace(text.Text));
                Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
                Assert.IsType<string>(ToolTip.GetTip(button));
            }
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
        }
    }

    private static async Task<Window> ShowInWindowAsync(Control control, double width = 900, double height = 600)
    {
        Window window = new()
        {
            Content = control,
            Width = width,
            Height = height
        };

        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        return window;
    }

    /// <summary>A Changes panel over a session of a git service; the commands it runs are recorded.</summary>
    private sealed class PanelFixture : IDisposable
    {
        public PanelFixture(IGitService git, Exception? failure = null)
        {
            Session = new GitSession(GitServiceSet.FromService(git), new TestWorkspaceInfo());
            ViewModel = new GitPanelViewModel(Session, (commandId, arguments) =>
            {
                Executed.Add((commandId, arguments));
                return failure is null ? Task.CompletedTask : Task.FromException(failure);
            });
        }

        public GitSession Session { get; }

        public GitPanelViewModel ViewModel { get; }

        public List<(string CommandId, IReadOnlyList<object?>? Arguments)> Executed { get; } = new();

        /// <summary>Creates the panel and shows the first status.</summary>
        public static async Task<PanelFixture> CreateAsync(IGitService git, Exception? failure = null)
        {
            PanelFixture panel = new(git, failure);
            await panel.ViewModel.RefreshAsync().WaitAsync(WaitLimit);
            await panel.ViewModel.WhenAnyValue(x => x.IsRepository).FirstAsync(isRepository => isRepository).Timeout(WaitLimit);
            return panel;
        }

        public void Dispose()
        {
            ViewModel.Dispose();
            Session.Dispose();
        }
    }

    private sealed class TestWorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath => "test-workspace";
        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ControlledGitService : IGitService
    {
        private readonly GitRepositoryStatus? _status;
        private readonly bool _blockFirstStatus;
        private int _activeStatusCalls;
        public ControlledGitService(GitRepositoryStatus? status = null, bool blockFirstStatus = true)
        {
            _status = status;
            _blockFirstStatus = blockFirstStatus;
        }
        public int RootCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public int MaxConcurrentStatusCalls { get; private set; }
        public TaskCompletionSource<bool> FirstStatusStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> FirstStatusCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
        {
            RootCalls++;
            return Task.FromResult<string?>("test-repo");
        }

        public async Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
        {
            StatusCalls++;
            int active = Interlocked.Increment(ref _activeStatusCalls);
            MaxConcurrentStatusCalls = Math.Max(MaxConcurrentStatusCalls, active);
            try
            {
                if (StatusCalls == 1 && _blockFirstStatus)
                {
                    FirstStatusStarted.TrySetResult(true);
                    try
                    {
                        await ReleaseFirstStatus.Task.WaitAsync(ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        FirstStatusCancelled.TrySetResult(true);
                        throw;
                    }
                }
                return _status ?? new GitRepositoryStatus { RepositoryRoot = string.Empty, Changes = Array.Empty<GitFileChange>() };
            }
            finally
            {
                Interlocked.Decrement(ref _activeStatusCalls);
            }
        }

        /// <summary>Gets or sets the raw text of every diff.</summary>
        public string DiffText { get; set; } = string.Empty;

        public int DiffCalls { get; private set; }

        /// <summary>
        /// Gets the diffs of these paths, which complete only when the test sets their result; the cancellation token is
        /// ignored, like a git process that ends before it is killed.
        /// </summary>
        public Dictionary<string, TaskCompletionSource<GitDiff>> GatedDiffs { get; } = new();

        public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default)
        {
            DiffCalls++;
            return request.Path is { } path && GatedDiffs.TryGetValue(path, out TaskCompletionSource<GitDiff>? gate)
                ? gate.Task
                : Task.FromResult(CreateDiff(DiffText));
        }

        public static GitDiff CreateDiff(string text) =>
            new() { Files = Array.Empty<XamlVisualEditor.Core.GitDiffFile>(), RawText = text };
        public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
