using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Collections;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using CoreGitDiffLineKind = XamlVisualEditor.Core.GitDiffLineKind;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Services;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// ViewModel entry representing a git file change for the panel.
/// </summary>
public sealed partial class GitChangeEntryViewModel : ReactiveObject
{
    public GitChangeEntryViewModel(
        string path,
        string? oldPath,
        GitChangeKind changeKind,
        bool isStaged,
        bool isUntracked)
    {
        Path = path;
        OldPath = oldPath;
        ChangeKind = changeKind;
        IsStaged = isStaged;
        IsUntracked = isUntracked;
        DisplayPath = BuildDisplayPath(path, oldPath);
        StatusLabel = FormatChangeKind(changeKind, isStaged, isUntracked);
    }

    public string Path { get; }

    public string? OldPath { get; }

    public string DisplayPath { get; }

    public GitChangeKind ChangeKind { get; }

    public bool IsStaged { get; }

    public bool IsUntracked { get; }

    public string StatusLabel { get; }

    private static string BuildDisplayPath(string path, string? oldPath)
    {
        return string.IsNullOrWhiteSpace(oldPath)
            ? path
            : oldPath + " -> " + path;
    }

    private static string FormatChangeKind(GitChangeKind kind, bool isStaged, bool isUntracked)
    {
        if (isUntracked)
        {
            return UiStrings.GitUntracked;
        }

        string scope = isStaged ? UiStrings.GitStaged : UiStrings.GitUnstaged;
        return kind switch
        {
            GitChangeKind.Added => scope + UiStrings.GitAddedSuffix,
            GitChangeKind.Modified => scope + UiStrings.GitModifiedSuffix,
            GitChangeKind.Deleted => scope + UiStrings.GitDeletedSuffix,
            GitChangeKind.Renamed => scope + UiStrings.GitRenamedSuffix,
            GitChangeKind.Copied => scope + UiStrings.GitCopiedSuffix,
            GitChangeKind.TypeChanged => scope + UiStrings.GitTypeChangedSuffix,
            GitChangeKind.Unmerged => scope + UiStrings.GitUnmergedSuffix,
            _ => scope + UiStrings.GitChangedSuffix
        };
    }
}

/// <summary>
/// ViewModel for the git panel UI.
/// </summary>
public enum GitDiffLineKind
{
    FileHeader,
    HunkHeader,
    Added,
    Removed,
    Context,
    NoNewline
}

/// <summary>
/// ViewModel entry representing a diff line for the preview.
/// </summary>
public sealed partial class GitDiffLineViewModel
{
    public GitDiffLineViewModel(
        GitDiffLineKind kind,
        string text,
        string marker,
        int? oldLine,
        int? newLine)
    {
        Kind = kind;
        Text = text;
        Marker = marker;
        OldLine = oldLine;
        NewLine = newLine;
    }

    public GitDiffLineKind Kind { get; }

    public string Text { get; }

    public string Marker { get; }

    public int? OldLine { get; }

    public int? NewLine { get; }
}

/// <summary>
/// ViewModel of the Git Changes panel. It shows the snapshots of a <see cref="GitSession"/> and runs its git
/// commands through the session, which refreshes the status afterwards.
/// </summary>
public sealed partial class GitPanelViewModel : ReactiveObject, IDisposable
{
    private readonly GitSession? _session;
    private readonly bool _ownsSession;
    private readonly IDialogHost? _dialogHost;
    private readonly CompositeDisposable _disposables = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<GitChangeEntryViewModel> _stagedItems = new();
    private readonly ObservableCollection<GitChangeEntryViewModel> _unstagedItems = new();
    private readonly DataGridCollectionView _stagedView;
    private readonly DataGridCollectionView _unstagedView;
    private readonly ObservableAsPropertyHelper<int> _stagedCount;
    private readonly ObservableAsPropertyHelper<int> _unstagedCount;
    private readonly ObservableAsPropertyHelper<int> _diffLineCount;
    private bool _isDisposed;

    [Reactive]
    public partial string? RepositoryRoot { get; private set; }

    /// <summary>Gets whether the working folder watcher of the session is switched on.</summary>
    [Reactive]
    public partial bool IsGitMonitoringEnabled { get; private set; }

    public string GitMonitoringLabel => IsGitMonitoringEnabled ? UiStrings.GitMonitoringOn : UiStrings.GitMonitoringOff;

    [Reactive]
    public partial string BranchName { get; private set; } = string.Empty;

    [Reactive]
    public partial string? UpstreamName { get; private set; }

    [Reactive]
    public partial int AheadBy { get; private set; }

    [Reactive]
    public partial int BehindBy { get; private set; }

    [Reactive]
    public partial bool IsRepository { get; private set; }

    [Reactive]
    public partial string? ErrorMessage { get; private set; }

    [Reactive]
    public partial bool IsBusy { get; private set; }

    [Reactive]
    public partial string? SearchText { get; set; }

    [Reactive]
    public partial string? CommitMessage { get; set; }

    [Reactive]
    public partial string DiffText { get; private set; } = string.Empty;

    [Reactive]
    public partial bool IsDiffVisible { get; private set; } = true;

    public string DiffToggleLabel => IsDiffVisible ? UiStrings.GitHideDiff : UiStrings.GitShowDiff;

    [Reactive]
    public partial GitChangeEntryViewModel? SelectedStagedChange { get; set; }

    [Reactive]
    public partial GitChangeEntryViewModel? SelectedUnstagedChange { get; set; }

    public IReadOnlyList<GitChangeEntryViewModel> StagedItems => _stagedItems;

    public IReadOnlyList<GitChangeEntryViewModel> UnstagedItems => _unstagedItems;

    public DataGridCollectionView StagedView => _stagedView;

    public DataGridCollectionView UnstagedView => _unstagedView;

    public ObservableCollection<GitDiffLineViewModel> DiffLines { get; } = new();

    public int StagedCount => _stagedCount.Value;

    public int UnstagedCount => _unstagedCount.Value;

    public int DiffLineCount => _diffLineCount.Value;

    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Switches the working folder watcher of the session on or off.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleMonitoringCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ToggleDiffCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StageCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UnstageCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StageAllCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UnstageAllCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CommitCommand { get; }

    /// <summary>
    /// Creates a panel with its own session for the workspace, which starts at once and ends with the panel.
    /// Without <paramref name="gitService"/> the panel shows that git is unavailable.
    /// </summary>
    public GitPanelViewModel(
        IGitService? gitService = null,
        IWorkspaceInfo? workspaceInfo = null,
        IDialogHost? dialogHost = null)
        : this(gitService is null ? null : new GitSession(GitServiceSet.FromService(gitService), workspaceInfo), dialogHost, ownsSession: true)
    {
        if (_session is not null)
        {
            _ = _session.StartAsync(_lifetime.Token);
        }
    }

    /// <summary>Creates a panel for a shared session; the session stays alive when the panel is disposed.</summary>
    public GitPanelViewModel(GitSession session, IDialogHost? dialogHost)
        : this(session, dialogHost, ownsSession: false)
    {
    }

    private GitPanelViewModel(GitSession? session, IDialogHost? dialogHost, bool ownsSession)
    {
        _session = session;
        _ownsSession = ownsSession;
        _dialogHost = dialogHost;

        _stagedView = new DataGridCollectionView(_stagedItems);
        _unstagedView = new DataGridCollectionView(_unstagedItems);

        EventHandler stagedCurrentChanged = (_, _) =>
        {
            SelectedStagedChange = _stagedView.CurrentItem as GitChangeEntryViewModel;
        };
        _stagedView.CurrentChanged += stagedCurrentChanged;
        _disposables.Add(Disposable.Create(() => _stagedView.CurrentChanged -= stagedCurrentChanged));

        EventHandler unstagedCurrentChanged = (_, _) =>
        {
            SelectedUnstagedChange = _unstagedView.CurrentItem as GitChangeEntryViewModel;
        };
        _unstagedView.CurrentChanged += unstagedCurrentChanged;
        _disposables.Add(Disposable.Create(() => _unstagedView.CurrentChanged -= unstagedCurrentChanged));

        IObservable<int> stagedCountChanged = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _stagedItems.CollectionChanged += handler,
                handler => _stagedItems.CollectionChanged -= handler)
            .Select(_ => _stagedItems.Count)
            .StartWith(_stagedItems.Count);
        _stagedCount = stagedCountChanged.ToProperty(this, x => x.StagedCount);
        _disposables.Add(_stagedCount);

        IObservable<int> unstagedCountChanged = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => _unstagedItems.CollectionChanged += handler,
                handler => _unstagedItems.CollectionChanged -= handler)
            .Select(_ => _unstagedItems.Count)
            .StartWith(_unstagedItems.Count);
        _unstagedCount = unstagedCountChanged.ToProperty(this, x => x.UnstagedCount);
        _disposables.Add(_unstagedCount);

        IObservable<int> diffLineCountChanged = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                handler => DiffLines.CollectionChanged += handler,
                handler => DiffLines.CollectionChanged -= handler)
            .Select(_ => DiffLines.Count)
            .StartWith(DiffLines.Count);
        _diffLineCount = diffLineCountChanged.ToProperty(this, x => x.DiffLineCount);
        _disposables.Add(_diffLineCount);

        IObservable<bool> hasSession = Observable.Return(_session is not null);
        ToggleMonitoringCommand = ReactiveCommand.CreateFromTask(ToggleMonitoringAsync, hasSession);
        _disposables.Add(ToggleMonitoringCommand);
        ToggleDiffCommand = ReactiveCommand.Create(ToggleDiff);
        _disposables.Add(ToggleDiffCommand);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync, hasSession);

        IObservable<bool> canStage = this.WhenAnyValue(x => x.SelectedUnstagedChange).Select(change => change is not null);
        IObservable<bool> canUnstage = this.WhenAnyValue(x => x.SelectedStagedChange).Select(change => change is not null);
        IObservable<bool> canDiscard = this.WhenAnyValue(x => x.SelectedUnstagedChange)
            .Select(change => change is { IsUntracked: false });

        StageCommand = ReactiveCommand.CreateFromTask(StageSelectedAsync, canStage);
        UnstageCommand = ReactiveCommand.CreateFromTask(UnstageSelectedAsync, canUnstage);
        DiscardCommand = ReactiveCommand.CreateFromTask(DiscardSelectedAsync, canDiscard);

        StageAllCommand = ReactiveCommand.CreateFromTask(StageAllAsync,
            this.WhenAnyValue(x => x.UnstagedCount).Select(count => count > 0));
        UnstageAllCommand = ReactiveCommand.CreateFromTask(UnstageAllAsync,
            this.WhenAnyValue(x => x.StagedCount).Select(count => count > 0));

        IObservable<bool> canCommit = this.WhenAnyValue(x => x.CommitMessage, x => x.StagedCount, x => x.IsRepository,
                (message, count, isRepo) => isRepo && count > 0 && !string.IsNullOrWhiteSpace(message))
            .DistinctUntilChanged();
        CommitCommand = ReactiveCommand.CreateFromTask(CommitAsync, canCommit);

        IDisposable stagedSelection = this.WhenAnyValue(x => x.SelectedStagedChange)
            .Where(change => change is not null)
            .Subscribe(change =>
            {
                SelectedUnstagedChange = null;
                _ = LoadDiffAsync(change!);
            });
        _disposables.Add(stagedSelection);

        IDisposable unstagedSelection = this.WhenAnyValue(x => x.SelectedUnstagedChange)
            .Where(change => change is not null)
            .Subscribe(change =>
            {
                SelectedStagedChange = null;
                _ = LoadDiffAsync(change!);
            });
        _disposables.Add(unstagedSelection);

        IDisposable selectionClear = this.WhenAnyValue(x => x.SelectedStagedChange, x => x.SelectedUnstagedChange)
            .Where(selection => selection.Property1 is null && selection.Property2 is null)
            .Subscribe(_ =>
            {
                DiffText = string.Empty;
                DiffLines.Clear();
            });
        _disposables.Add(selectionClear);

        IDisposable searchSubscription = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter());
        _disposables.Add(searchSubscription);

        if (_session is null)
        {
            ErrorMessage = UiStrings.GitUnavailable;
            return;
        }

        _disposables.Add(_session.Snapshots.Subscribe(snapshot => PostToUiThread(() => ApplySnapshot(snapshot))));
        _disposables.Add(_session.IsRefreshing.Subscribe(isRefreshing => PostToUiThread(() => IsBusy = isRefreshing)));
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _lifetime.Cancel();
        _disposables.Dispose();
        if (_ownsSession)
        {
            _session?.Dispose();
        }
    }

    /// <summary>Refreshes the status of the session; completes when the refresh that includes this request is done.</summary>
    public System.Threading.Tasks.Task RefreshAsync()
    {
        return _session?.RefreshAsync() ?? System.Threading.Tasks.Task.CompletedTask;
    }

    private async System.Threading.Tasks.Task ToggleMonitoringAsync()
    {
        if (_session is null)
        {
            return;
        }

        bool enabled = !IsGitMonitoringEnabled;
        IsGitMonitoringEnabled = enabled;
        this.RaisePropertyChanged(nameof(GitMonitoringLabel));
        await _session.SetWorkingTreeWatcherEnabledAsync(enabled, _lifetime.Token);
    }

    private void ToggleDiff()
    {
        IsDiffVisible = !IsDiffVisible;
        this.RaisePropertyChanged(nameof(DiffToggleLabel));
    }

    private void ApplySnapshot(GitSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsGitMonitoringEnabled != snapshot.IsWorkingTreeWatcherEnabled)
        {
            IsGitMonitoringEnabled = snapshot.IsWorkingTreeWatcherEnabled;
            this.RaisePropertyChanged(nameof(GitMonitoringLabel));
        }

        if (snapshot.State == GitSessionState.Unknown)
        {
            return;
        }

        if (!snapshot.IsRepository || snapshot.Status is null)
        {
            RepositoryRoot = null;
            BranchName = string.Empty;
            UpstreamName = null;
            AheadBy = 0;
            BehindBy = 0;
            IsRepository = false;
            ErrorMessage = snapshot.ErrorMessage;
            SelectedStagedChange = null;
            SelectedUnstagedChange = null;
            ClearChanges();
            return;
        }

        GitRepositoryStatus status = snapshot.Status;
        string? selectedPath = SelectedStagedChange?.Path ?? SelectedUnstagedChange?.Path;
        bool wasStaged = SelectedStagedChange is not null;

        RepositoryRoot = snapshot.RepositoryRoot;
        BranchName = status.BranchName;
        UpstreamName = status.UpstreamName;
        AheadBy = status.AheadBy;
        BehindBy = status.BehindBy;
        IsRepository = true;
        ErrorMessage = snapshot.ErrorMessage;

        ReplaceItems(_stagedItems, status, isStaged: true);
        ReplaceItems(_unstagedItems, status, isStaged: false);
        ApplyFilter();
        RestoreSelection(selectedPath, wasStaged);
    }

    private void ClearChanges()
    {
        _stagedItems.Clear();
        _unstagedItems.Clear();
        DiffLines.Clear();
        DiffText = string.Empty;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ApplyFilterCore, DispatcherPriority.Background);
            return;
        }

        ApplyFilterCore();
    }

    private void ApplyFilterCore()
    {
        string filter = SearchText?.Trim() ?? string.Empty;
        StringComparison comparison = StringComparison.OrdinalIgnoreCase;

        bool FilterItem(object? item)
        {
            if (item is not GitChangeEntryViewModel entry)
            {
                return false;
            }

            return string.IsNullOrEmpty(filter)
                || entry.DisplayPath.Contains(filter, comparison)
                || entry.StatusLabel.Contains(filter, comparison);
        }

        _stagedView.Filter = FilterItem;
        _unstagedView.Filter = FilterItem;
        _stagedView.Refresh();
        _unstagedView.Refresh();
    }

    private async System.Threading.Tasks.Task LoadDiffAsync(GitChangeEntryViewModel change)
    {
        CancellationToken cancellationToken = _lifetime.Token;
        string? repositoryRoot = RepositoryRoot;
        if (_session is null || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            DiffText = string.Empty;
            return;
        }

        GitDiffRequest request = new()
        {
            Staged = change.IsStaged,
            Path = change.Path,
            IsUntracked = change.IsUntracked
        };

        try
        {
            GitDiff diff = await _session.Services.Service.GetDiffAsync(repositoryRoot, request, cancellationToken);
            IReadOnlyList<GitDiffLineViewModel> lines = BuildDiffLines(diff);
            Dispatcher.UIThread.Post(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                DiffText = diff.RawText ?? string.Empty;
                DiffLines.Clear();
                foreach (GitDiffLineViewModel line in lines)
                {
                    DiffLines.Add(line);
                }
            });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private System.Threading.Tasks.Task StageSelectedAsync()
    {
        GitChangeEntryViewModel? selected = SelectedUnstagedChange;
        if (selected is null)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        string[] paths = [selected.Path];
        return RunGitActionAsync((git, root, ct) => git.StageAsync(root, paths, ct));
    }

    private System.Threading.Tasks.Task UnstageSelectedAsync()
    {
        GitChangeEntryViewModel? selected = SelectedStagedChange;
        if (selected is null)
        {
            return System.Threading.Tasks.Task.CompletedTask;
        }

        string[] paths = [selected.Path];
        return RunGitActionAsync((git, root, ct) => git.UnstageAsync(root, paths, ct));
    }

    private async System.Threading.Tasks.Task DiscardSelectedAsync()
    {
        GitChangeEntryViewModel? selected = SelectedUnstagedChange;
        string? repositoryRoot = RepositoryRoot;
        if (selected is null || selected.IsUntracked || string.IsNullOrWhiteSpace(repositoryRoot))
        {
            return;
        }

        string path = selected.Path;
        bool confirmed = await ConfirmGitActionAsync(UiStrings.GitDiscardConfirmTitle,
            string.Format(UiStrings.GitDiscardWorkingChangesPrompt, path), UiStrings.Discard, destructive: true);
        if (!confirmed || !string.Equals(RepositoryRoot, repositoryRoot, StringComparison.OrdinalIgnoreCase))
            return;

        string[] paths = [path];
        await RunGitActionAsync((git, root, ct) => git.DiscardAsync(root, paths, ct));
    }

    private System.Threading.Tasks.Task StageAllAsync()
    {
        string[] paths = PathsOf(_unstagedItems);
        return paths.Length == 0
            ? System.Threading.Tasks.Task.CompletedTask
            : RunGitActionAsync((git, root, ct) => git.StageAsync(root, paths, ct));
    }

    private System.Threading.Tasks.Task UnstageAllAsync()
    {
        string[] paths = PathsOf(_stagedItems);
        return paths.Length == 0
            ? System.Threading.Tasks.Task.CompletedTask
            : RunGitActionAsync((git, root, ct) => git.UnstageAsync(root, paths, ct));
    }

    private async System.Threading.Tasks.Task<bool> ConfirmGitActionAsync(
        string title, string message, string action, bool destructive = false)
    {
        if (_dialogHost is null) return false;
        bool? result = await _dialogHost.ShowDialogAsync<bool>(ConfirmationDialogRequest.DialogId,
            new ConfirmationDialogRequest(title, message, action, destructive), _lifetime.Token);
        return result == true;
    }

    private async System.Threading.Tasks.Task CommitAsync()
    {
        string? message = CommitMessage?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        string commitMessage = message;
        if (await RunGitActionAsync((git, root, ct) => git.CommitAsync(root, commitMessage, ct)))
        {
            CommitMessage = string.Empty;
        }
    }

    /// <summary>
    /// Runs a modifying git action through the session, which refreshes the status, and shows a failure of the
    /// action in <see cref="ErrorMessage"/> after the refresh. Returns whether the action succeeded.
    /// </summary>
    private async System.Threading.Tasks.Task<bool> RunGitActionAsync(
        Func<IGitService, string, CancellationToken, System.Threading.Tasks.Task> action)
    {
        if (_session is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return false;
        }

        string? failure = null;
        try
        {
            await _session.RunAsync(action, _lifetime.Token);
        }
        catch (InvalidOperationException exception)
        {
            failure = string.Format(UiStrings.GitCommandFailed, exception.Message);
        }

        if (failure is not null)
        {
            PostToUiThread(() => ErrorMessage = failure);
        }

        return failure is null;
    }

    private void PostToUiThread(Action action)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!_isDisposed)
            {
                action();
            }
        });
    }

    private static string[] PathsOf(IReadOnlyList<GitChangeEntryViewModel> items)
    {
        string[] paths = new string[items.Count];
        for (int i = 0; i < items.Count; i++)
        {
            paths[i] = items[i].Path;
        }

        return paths;
    }

    private static void ReplaceItems(
        ObservableCollection<GitChangeEntryViewModel> target,
        GitRepositoryStatus status,
        bool isStaged)
    {
        target.Clear();

        foreach (GitFileChange change in status.Changes)
        {
            bool staged = isStaged
                ? change.IndexStatus != GitChangeKind.None
                : change.WorkTreeStatus != GitChangeKind.None;
            if (!staged)
            {
                continue;
            }

            GitChangeKind kind = isStaged ? change.IndexStatus : change.WorkTreeStatus;
            bool isUntracked = change.IndexStatus == GitChangeKind.Untracked || change.WorkTreeStatus == GitChangeKind.Untracked;
            target.Add(new GitChangeEntryViewModel(
                change.Path,
                change.OldPath,
                kind,
                isStaged,
                isUntracked));
        }
    }

    private void RestoreSelection(string? selectedPath, bool wasStaged)
    {
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        GitChangeEntryViewModel? match = wasStaged
            ? FindMatching(_stagedItems, selectedPath)
            : FindMatching(_unstagedItems, selectedPath);

        if (match is null)
        {
            return;
        }

        if (wasStaged)
        {
            SelectedStagedChange = match;
            _stagedView.MoveCurrentTo(match);
        }
        else
        {
            SelectedUnstagedChange = match;
            _unstagedView.MoveCurrentTo(match);
        }
    }

    private static GitChangeEntryViewModel? FindMatching(
        IEnumerable<GitChangeEntryViewModel> items,
        string selectedPath)
    {
        foreach (GitChangeEntryViewModel item in items)
        {
            if (PathsEqual(item.Path, selectedPath) || (item.OldPath is not null && PathsEqual(item.OldPath, selectedPath)))
            {
                return item;
            }
        }

        return null;
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<GitDiffLineViewModel> BuildDiffLines(GitDiff diff)
    {
        if (diff.Files.Count == 0)
        {
            return Array.Empty<GitDiffLineViewModel>();
        }

        List<GitDiffLineViewModel> results = new();
        foreach (GitDiffFile file in diff.Files)
        {
            foreach (string header in file.HeaderLines)
            {
                results.Add(new GitDiffLineViewModel(GitDiffLineKind.FileHeader, header, string.Empty, null, null));
            }

            foreach (GitDiffHunk hunk in file.Hunks)
            {
                results.Add(new GitDiffLineViewModel(GitDiffLineKind.HunkHeader, hunk.Header, string.Empty, null, null));

                foreach (GitDiffLine line in hunk.Lines)
                {
                    GitDiffLineKind kind = MapLineKind(line.Kind);
                    string marker = GetMarker(kind);
                    results.Add(new GitDiffLineViewModel(kind, line.Text, marker, line.OldLine, line.NewLine));
                }
            }
        }

        return results.Count == 0 ? Array.Empty<GitDiffLineViewModel>() : results;
    }

    private static GitDiffLineKind MapLineKind(CoreGitDiffLineKind kind)
    {
        return kind switch
        {
            CoreGitDiffLineKind.Added => GitDiffLineKind.Added,
            CoreGitDiffLineKind.Removed => GitDiffLineKind.Removed,
            CoreGitDiffLineKind.NoNewline => GitDiffLineKind.NoNewline,
            _ => GitDiffLineKind.Context
        };
    }

    private static string GetMarker(GitDiffLineKind kind)
    {
        return kind switch
        {
            GitDiffLineKind.Added => "+",
            GitDiffLineKind.Removed => "-",
            GitDiffLineKind.Context => " ",
            GitDiffLineKind.NoNewline => "\\",
            _ => string.Empty
        };
    }
}
