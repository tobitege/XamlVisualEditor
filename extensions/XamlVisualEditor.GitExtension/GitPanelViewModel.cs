using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using CoreGitDiffLineKind = XamlVisualEditor.Core.GitDiffLineKind;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;

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

public sealed partial class GitPanelViewModel : ReactiveObject, IDisposable
{
    private readonly IGitService? _gitService;
    private readonly IWorkspaceInfo? _workspaceInfo;
    private readonly IDialogHost? _dialogHost;
    private readonly CompositeDisposable _disposables = new();
    private readonly ObservableCollection<GitChangeEntryViewModel> _stagedItems = new();
    private readonly ObservableCollection<GitChangeEntryViewModel> _unstagedItems = new();
    private readonly DataGridCollectionView _stagedView;
    private readonly DataGridCollectionView _unstagedView;
    private FileSystemWatcher? _watcher;
    private IDisposable? _watcherSubscription;
    private CancellationTokenSource? _monitoringCancellation;
    private readonly Subject<Unit> _refreshRequests = new();
    private readonly object _refreshSync = new();
    private bool _refreshRunning;
    private bool _refreshRequested;
    private string? _repositoryWorkspacePath;
    private string? _cachedRepositoryRoot;
    private readonly ObservableAsPropertyHelper<int> _stagedCount;
    private readonly ObservableAsPropertyHelper<int> _unstagedCount;
    private readonly ObservableAsPropertyHelper<int> _diffLineCount;

    [Reactive]
    public partial string? RepositoryRoot { get; private set; }

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

    public ReactiveCommand<RxVoid, RxVoid> ToggleMonitoringCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ToggleDiffCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StageCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UnstageCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> StageAllCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UnstageAllCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CommitCommand { get; }

    public GitPanelViewModel(
        IGitService? gitService = null,
        IWorkspaceInfo? workspaceInfo = null,
        IDialogHost? dialogHost = null)
    {
        _gitService = gitService;
        _workspaceInfo = workspaceInfo;
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

        ToggleMonitoringCommand = ReactiveCommand.Create(ToggleMonitoring);
        _disposables.Add(ToggleMonitoringCommand);
        ToggleDiffCommand = ReactiveCommand.Create(ToggleDiff);
        _disposables.Add(ToggleDiffCommand);
        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync,
            this.WhenAnyValue(x => x.IsGitMonitoringEnabled));

        IObservable<bool> canStage = this.WhenAnyValue(x => x.SelectedUnstagedChange, x => x.IsGitMonitoringEnabled,
            (change, enabled) => enabled && change is not null);
        IObservable<bool> canUnstage = this.WhenAnyValue(x => x.SelectedStagedChange, x => x.IsGitMonitoringEnabled,
            (change, enabled) => enabled && change is not null);
        IObservable<bool> canDiscard = this.WhenAnyValue(x => x.SelectedUnstagedChange, x => x.IsGitMonitoringEnabled,
            (change, enabled) => enabled && change is { IsUntracked: false });

        StageCommand = ReactiveCommand.CreateFromTask(StageSelectedAsync, canStage);
        UnstageCommand = ReactiveCommand.CreateFromTask(UnstageSelectedAsync, canUnstage);
        DiscardCommand = ReactiveCommand.CreateFromTask(DiscardSelectedAsync, canDiscard);

        StageAllCommand = ReactiveCommand.CreateFromTask(StageAllAsync,
            this.WhenAnyValue(x => x.UnstagedCount, x => x.IsGitMonitoringEnabled,
                (count, enabled) => enabled && count > 0));
        UnstageAllCommand = ReactiveCommand.CreateFromTask(UnstageAllAsync,
            this.WhenAnyValue(x => x.StagedCount, x => x.IsGitMonitoringEnabled,
                (count, enabled) => enabled && count > 0));

        IObservable<bool> canCommit = this.WhenAnyValue(x => x.CommitMessage, x => x.StagedCount,
                x => x.IsRepository, x => x.IsGitMonitoringEnabled,
                (message, count, isRepo, enabled) => enabled && isRepo && count > 0 && !string.IsNullOrWhiteSpace(message))
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

        IDisposable refreshSubscription = _refreshRequests
            .Throttle(TimeSpan.FromMilliseconds(500))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(request => _ = RefreshFromWatcherAsync());
        _disposables.Add(refreshSubscription);

        if (_workspaceInfo is not null)
        {
            EventHandler<WorkspaceChangedEventArgs> handler = (_, _) =>
            {
                if (IsGitMonitoringEnabled) _refreshRequests.OnNext(Unit.Default);
            };
            _workspaceInfo.WorkspaceChanged += handler;
            _disposables.Add(Disposable.Create(() => _workspaceInfo.WorkspaceChanged -= handler));
        }
    }

    public void Dispose()
    {
        IsGitMonitoringEnabled = false;
        _monitoringCancellation?.Cancel();
        _monitoringCancellation?.Dispose();
        _monitoringCancellation = null;
        StopWatcher();
        _refreshRequests.Dispose();
        _disposables.Dispose();
    }

    private void ToggleMonitoring()
    {
        if (IsGitMonitoringEnabled)
        {
            IsGitMonitoringEnabled = false;
            this.RaisePropertyChanged(nameof(GitMonitoringLabel));
            _monitoringCancellation?.Cancel();
            _monitoringCancellation?.Dispose();
            _monitoringCancellation = null;
            StopWatcher();
            lock (_refreshSync) _refreshRequested = false;
            _repositoryWorkspacePath = null;
            _cachedRepositoryRoot = null;
            RepositoryRoot = null;
            BranchName = string.Empty;
            UpstreamName = null;
            AheadBy = 0;
            BehindBy = 0;
            IsRepository = false;
            ErrorMessage = null;
            SelectedStagedChange = null;
            SelectedUnstagedChange = null;
            ClearChanges();
            return;
        }

        _monitoringCancellation = new CancellationTokenSource();
        IsGitMonitoringEnabled = true;
        this.RaisePropertyChanged(nameof(GitMonitoringLabel));
        _refreshRequests.OnNext(Unit.Default);
    }

    private void ToggleDiff()
    {
        IsDiffVisible = !IsDiffVisible;
        this.RaisePropertyChanged(nameof(DiffToggleLabel));
    }

    private async System.Threading.Tasks.Task RefreshFromWatcherAsync()
    {
        if (!IsGitMonitoringEnabled) return;
        try
        {
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsGitMonitoringEnabled) ErrorMessage = exception.Message;
            });
        }
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        CancellationTokenSource? monitoringCancellation = _monitoringCancellation;
        if (!IsGitMonitoringEnabled || monitoringCancellation is null) return;
        CancellationToken cancellationToken = monitoringCancellation.Token;

        lock (_refreshSync)
        {
            if (_refreshRunning)
            {
                _refreshRequested = true;
                return;
            }
            _refreshRunning = true;
        }

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await RefreshCoreAsync(cancellationToken);
                lock (_refreshSync)
                {
                    if (!_refreshRequested)
                    {
                        _refreshRunning = false;
                        return;
                    }
                    _refreshRequested = false;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool restart = IsGitMonitoringEnabled && !ReferenceEquals(_monitoringCancellation, monitoringCancellation);
            lock (_refreshSync)
            {
                _refreshRunning = false;
                _refreshRequested = false;
            }
            if (restart) _refreshRequests.OnNext(Unit.Default);
        }
        catch
        {
            lock (_refreshSync)
            {
                _refreshRunning = false;
                _refreshRequested = false;
            }
            throw;
        }
    }

    private async System.Threading.Tasks.Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_gitService is null)
        {
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    ErrorMessage = UiStrings.GitUnavailable;
                    IsRepository = false;
                    ClearChanges();
                });
            return;
        }

        string? workspacePath = _workspaceInfo?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            _repositoryWorkspacePath = null;
            _cachedRepositoryRoot = null;
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    RepositoryRoot = null;
                    ErrorMessage = UiStrings.NoWorkspaceLoadedStatus;
                    IsRepository = false;
                    ClearChanges();
                });
            return;
        }

        IsBusy = true;
        try
        {
            if (string.IsNullOrWhiteSpace(_cachedRepositoryRoot)
                || !string.Equals(_repositoryWorkspacePath, workspacePath, StringComparison.OrdinalIgnoreCase))
            {
                _cachedRepositoryRoot = await _gitService.GetRepositoryRootAsync(workspacePath, cancellationToken);
                _repositoryWorkspacePath = workspacePath;
            }
            string? repoRoot = _cachedRepositoryRoot;
            if (string.IsNullOrWhiteSpace(repoRoot))
            {
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        RepositoryRoot = null;
                        ErrorMessage = UiStrings.WorkspaceNotGitRepository;
                        IsRepository = false;
                        ClearChanges();
                    });
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            GitRepositoryStatus status = await _gitService.GetStatusAsync(repoRoot, cancellationToken);
                Dispatcher.UIThread.Post(() =>
                {
                    if (cancellationToken.IsCancellationRequested) return;
                    RepositoryRoot = repoRoot;
                    ApplyStatus(status);
                });
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ApplyStatus(GitRepositoryStatus status)
    {
        string? selectedPath = SelectedStagedChange?.Path ?? SelectedUnstagedChange?.Path;
        bool wasStaged = SelectedStagedChange is not null;

        BranchName = status.BranchName;
        UpstreamName = status.UpstreamName;
        AheadBy = status.AheadBy;
        BehindBy = status.BehindBy;
        IsRepository = status.IsRepository;
        ErrorMessage = status.ErrorMessage;

        ReplaceItems(_stagedItems, status, isStaged: true);
        ReplaceItems(_unstagedItems, status, isStaged: false);
        ApplyFilter();
        RestoreSelection(selectedPath, wasStaged);
        ConfigureWatcher(status.RepositoryRoot);
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
        CancellationToken cancellationToken = _monitoringCancellation?.Token ?? CancellationToken.None;
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
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
            GitDiff diff = await _gitService.GetDiffAsync(RepositoryRoot, request, cancellationToken);
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

    private async System.Threading.Tasks.Task StageSelectedAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        GitChangeEntryViewModel? selected = SelectedUnstagedChange;
        if (selected is null)
        {
            return;
        }

        await _gitService.StageAsync(RepositoryRoot, new[] { selected.Path });
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task UnstageSelectedAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        GitChangeEntryViewModel? selected = SelectedStagedChange;
        if (selected is null)
        {
            return;
        }

        string repoPath = RepositoryRoot!;
        string path = selected.Path;
        bool confirmed = await ConfirmGitActionAsync(UiStrings.GitUnstageConfirmTitle,
            string.Format(UiStrings.GitUnstageOnePrompt, path), UiStrings.Unstage);
        if (!confirmed || !IsGitMonitoringEnabled || !string.Equals(RepositoryRoot, repoPath, StringComparison.OrdinalIgnoreCase))
            return;

        await _gitService.UnstageAsync(repoPath, new[] { path });
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task DiscardSelectedAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        GitChangeEntryViewModel? selected = SelectedUnstagedChange;
        if (selected is null || selected.IsUntracked)
        {
            return;
        }

        string repoPath = RepositoryRoot!;
        string path = selected.Path;
        bool confirmed = await ConfirmGitActionAsync(UiStrings.GitDiscardConfirmTitle,
            string.Format(UiStrings.GitDiscardOnePrompt, path), UiStrings.Discard, destructive: true);
        if (!confirmed || !IsGitMonitoringEnabled || !string.Equals(RepositoryRoot, repoPath, StringComparison.OrdinalIgnoreCase))
            return;

        await _gitService.DiscardAsync(repoPath, new[] { path });
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task StageAllAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        if (_unstagedItems.Count == 0)
        {
            return;
        }

        string[] paths = new string[_unstagedItems.Count];
        for (int i = 0; i < _unstagedItems.Count; i++)
        {
            paths[i] = _unstagedItems[i].Path;
        }

        await _gitService.StageAsync(RepositoryRoot, paths);
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task UnstageAllAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        if (_stagedItems.Count == 0)
        {
            return;
        }

        string[] paths = new string[_stagedItems.Count];
        for (int i = 0; i < _stagedItems.Count; i++)
        {
            paths[i] = _stagedItems[i].Path;
        }

        string repoPath = RepositoryRoot!;
        string prompt = paths.Length == 1
            ? string.Format(UiStrings.GitUnstageOnePrompt, paths[0])
            : string.Format(UiStrings.GitUnstageManyPrompt, paths.Length);
        bool confirmed = await ConfirmGitActionAsync(UiStrings.GitUnstageConfirmTitle,
            prompt, UiStrings.UnstageAll);
        if (!confirmed || !IsGitMonitoringEnabled || !string.Equals(RepositoryRoot, repoPath, StringComparison.OrdinalIgnoreCase))
            return;

        await _gitService.UnstageAsync(repoPath, paths);
        await RefreshAsync();
    }

    private async System.Threading.Tasks.Task<bool> ConfirmGitActionAsync(
        string title, string message, string action, bool destructive = false)
    {
        if (_dialogHost is null || _monitoringCancellation is null) return false;
        bool? result = await _dialogHost.ShowDialogAsync<bool>(ConfirmationDialogRequest.DialogId,
            new ConfirmationDialogRequest(title, message, action, destructive), _monitoringCancellation.Token);
        return result == true;
    }

    private async System.Threading.Tasks.Task CommitAsync()
    {
        if (!IsGitMonitoringEnabled || _gitService is null || string.IsNullOrWhiteSpace(RepositoryRoot))
        {
            return;
        }

        string? message = CommitMessage?.Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        await _gitService.CommitAsync(RepositoryRoot, message);
        CommitMessage = string.Empty;
        await RefreshAsync();
    }

    private void ConfigureWatcher(string? repoRoot)
    {
        if (!IsGitMonitoringEnabled || string.IsNullOrWhiteSpace(repoRoot))
        {
            StopWatcher();
            return;
        }

        if (_watcher is not null && string.Equals(_watcher.Path, repoRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StopWatcher();
        FileSystemWatcher watcher = new(repoRoot)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = false
        };

        FileSystemEventHandler handler = (_, args) =>
        {
            if (!IsGitMonitoringEnabled || IsIgnoredWatcherPath(repoRoot, args.FullPath))
            {
                return;
            }

            _refreshRequests.OnNext(Unit.Default);
        };
        RenamedEventHandler renamedHandler = (_, args) =>
        {
            if (!IsGitMonitoringEnabled || IsIgnoredWatcherPath(repoRoot, args.FullPath))
            {
                return;
            }

            _refreshRequests.OnNext(Unit.Default);
        };
        ErrorEventHandler errorHandler = (_, _) =>
        {
            if (IsGitMonitoringEnabled) _refreshRequests.OnNext(Unit.Default);
        };

        watcher.Changed += handler;
        watcher.Created += handler;
        watcher.Deleted += handler;
        watcher.Renamed += renamedHandler;
        watcher.Error += errorHandler;

        _watcherSubscription = Disposable.Create(() =>
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= handler;
            watcher.Created -= handler;
            watcher.Deleted -= handler;
            watcher.Renamed -= renamedHandler;
            watcher.Error -= errorHandler;
        });
        _watcher = watcher;
        watcher.EnableRaisingEvents = true;
    }

    private void StopWatcher()
    {
        _watcherSubscription?.Dispose();
        _watcherSubscription = null;
        _watcher?.Dispose();
        _watcher = null;
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

    private static bool IsIgnoredWatcherPath(string repoRoot, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        char separator = Path.DirectorySeparatorChar;
        string normalized = separator + Path.GetRelativePath(repoRoot, path)
            .Replace(Path.AltDirectorySeparatorChar, separator);
        return ContainsDirectory(".git") || ContainsDirectory("artifacts")
            || ContainsDirectory("bin") || ContainsDirectory("obj")
            || ContainsDirectory("TestResults");

        bool ContainsDirectory(string name) =>
            normalized.Contains(separator + name + separator, StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith(separator + name, StringComparison.OrdinalIgnoreCase);
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
