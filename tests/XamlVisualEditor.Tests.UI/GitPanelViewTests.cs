using System.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Controls;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using GitChangeKind = XamlVisualEditor.Core.GitChangeKind;
using GitDiff = XamlVisualEditor.Core.GitDiff;
using GitDiffRequest = XamlVisualEditor.Core.GitDiffRequest;
using GitFileChange = XamlVisualEditor.Core.GitFileChange;
using GitRepositoryStatus = XamlVisualEditor.Core.GitRepositoryStatus;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitPanelViewTests
{
    [AvaloniaFact]
    public async Task Git_refresh_coalesces_overlapping_requests()
    {
        ControlledGitService gitService = new();
        using GitPanelViewModel vm = new(gitService, new TestWorkspaceInfo());

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        Task firstRefresh = vm.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.RefreshAsync();
        await vm.RefreshAsync();
        Assert.Equal(1, gitService.StatusCalls);
        gitService.ReleaseFirstStatus.TrySetResult(true);
        await firstRefresh;
        await vm.ToggleMonitoringCommand.Execute().ToTask();

        Assert.Equal(1, gitService.RootCalls);
        Assert.Equal(2, gitService.StatusCalls);
        Assert.Equal(1, gitService.MaxConcurrentStatusCalls);
    }

    [AvaloniaFact]
    public async Task Git_remains_off_until_enabled_and_cancels_running_scan()
    {
        ControlledGitService gitService = new();
        using GitPanelViewModel vm = new(gitService, new TestWorkspaceInfo());

        Assert.False(vm.IsGitMonitoringEnabled);
        await vm.RefreshAsync();
        Assert.Equal(0, gitService.RootCalls);

        await vm.ToggleMonitoringCommand.Execute().ToTask();
        Task scan = vm.RefreshAsync();
        await gitService.FirstStatusStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await scan;
        await vm.RefreshAsync();

        Assert.False(vm.IsGitMonitoringEnabled);
        Assert.Equal(1, gitService.RootCalls);
        Assert.Equal(1, gitService.StatusCalls);
        Assert.True(gitService.FirstStatusCancelled);
    }

    [AvaloniaFact]
    public async Task Unstage_requires_confirmation_with_affected_file_count()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        RecordingDialogHost dialogs = new();
        using GitPanelViewModel vm = new(gitService, new TestWorkspaceInfo(), dialogs);
        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await vm.RefreshAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        vm.SelectedStagedChange = Assert.Single(vm.StagedItems, item => item.Path == "a.cs");
        await vm.UnstageCommand.Execute().ToTask();
        Assert.Equal(0, gitService.UnstageCalls);
        Assert.Equal(string.Format(UiStrings.GitUnstageOnePrompt, "a.cs"), dialogs.Requests[^1].Message);
        Assert.False(dialogs.Requests[^1].IsDestructive);

        dialogs.Confirm = true;
        await vm.UnstageCommand.Execute().ToTask();
        Assert.Equal(1, gitService.UnstageCalls);
        Assert.Equal("a.cs", Assert.Single(gitService.LastUnstagedPaths));

        await vm.UnstageAllCommand.Execute().ToTask();
        Assert.Equal(2, gitService.UnstageCalls);
        Assert.Equal(2, gitService.LastUnstagedPaths.Count);
        Assert.Equal(string.Format(UiStrings.GitUnstageManyPrompt, 2), dialogs.Requests[^1].Message);
    }

    [AvaloniaFact]
    public async Task Discard_requires_confirmation_and_never_removes_untracked_files()
    {
        ControlledGitService gitService = new(CreateStatus(), blockFirstStatus: false);
        RecordingDialogHost dialogs = new();
        using GitPanelViewModel vm = new(gitService, new TestWorkspaceInfo(), dialogs);
        await vm.ToggleMonitoringCommand.Execute().ToTask();
        await vm.RefreshAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { });

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "new.txt");
        Assert.False(((System.Windows.Input.ICommand)vm.DiscardCommand).CanExecute(null));

        vm.SelectedUnstagedChange = Assert.Single(vm.UnstagedItems, item => item.Path == "c.cs");
        await vm.DiscardCommand.Execute().ToTask();
        Assert.Equal(0, gitService.DiscardCalls);
        Assert.Equal(string.Format(UiStrings.GitDiscardOnePrompt, "c.cs"), dialogs.Requests[^1].Message);
        Assert.True(dialogs.Requests[^1].IsDestructive);

        dialogs.Confirm = true;
        await vm.DiscardCommand.Execute().ToTask();
        Assert.Equal(1, gitService.DiscardCalls);
        Assert.Equal("c.cs", Assert.Single(gitService.LastDiscardedPaths));
    }

    private static GitRepositoryStatus CreateStatus() => new()
    {
        RepositoryRoot = string.Empty,
        Changes = new[]
        {
            new GitFileChange { Path = "a.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "b.cs", IndexStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "c.cs", WorkTreeStatus = GitChangeKind.Modified },
            new GitFileChange { Path = "new.txt", WorkTreeStatus = GitChangeKind.Untracked, IsUntracked = true }
        }
    };

    [AvaloniaFact]
    public async Task GitPanelView_Renders_Diff_Lines()
    {
        GitPanelViewModel vm = new();
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
                    button => Equals(button.Content, vm.GitMonitoringLabel));
                Assert.False(toggle.IsChecked);
                Assert.Equal(vm.GitMonitoringLabel, toggle.Content);
                Button discard = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                    button => Equals(button.Content, UiStrings.Discard));
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
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task Diff_pane_hides_and_restores_the_resized_width()
    {
        using GitPanelViewModel vm = new();
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
        public int UnstageCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public IReadOnlyList<string> LastUnstagedPaths { get; private set; } = Array.Empty<string>();
        public IReadOnlyList<string> LastDiscardedPaths { get; private set; } = Array.Empty<string>();
        public bool FirstStatusCancelled { get; private set; }
        public TaskCompletionSource<bool> FirstStatusStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirstStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
                        FirstStatusCancelled = true;
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

        public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
            Task.FromResult(new GitDiff { Files = Array.Empty<XamlVisualEditor.Core.GitDiffFile>() });
        public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
        public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            UnstageCalls++;
            LastUnstagedPaths = paths.ToArray();
            return Task.CompletedTask;
        }
        public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            DiscardCalls++;
            LastDiscardedPaths = paths.ToArray();
            return Task.CompletedTask;
        }
        public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class RecordingDialogHost : IDialogHost
    {
        public bool Confirm { get; set; }
        public List<ConfirmationDialogRequest> Requests { get; } = new();

        public IDisposable RegisterDialog(string dialogId, Func<object?, object> factory) =>
            System.Reactive.Disposables.Disposable.Empty;

        public Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken)
        {
            Assert.Equal(ConfirmationDialogRequest.DIALOG_ID, dialogId);
            Requests.Add(Assert.IsType<ConfirmationDialogRequest>(viewModel));
            return Task.FromResult<T?>((T)(object)Confirm);
        }
    }
}
