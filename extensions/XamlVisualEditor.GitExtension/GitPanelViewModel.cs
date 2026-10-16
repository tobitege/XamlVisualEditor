using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Collections;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using CoreGitDiffLineKind = XamlVisualEditor.Core.GitDiffLineKind;
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

    /// <summary>Creates the entry of <paramref name="change"/> in the staged or in the working tree list.</summary>
    internal GitChangeEntryViewModel(GitFileChange change, bool isStaged)
        : this(
            change.Path,
            change.OldPath,
            isStaged ? change.IndexStatus : change.WorkTreeStatus,
            isStaged,
            GitCommandTarget.IsUntracked(change))
    {
        Change = change;
    }

    public string Path { get; }

    public string? OldPath { get; }

    public string DisplayPath { get; }

    public GitChangeKind ChangeKind { get; }

    public bool IsStaged { get; }

    public bool IsUntracked { get; }

    public string StatusLabel { get; }

    /// <summary>Gets the status entry the item shows, or null for an item created from its values.</summary>
    internal GitFileChange? Change { get; }

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
/// ViewModel of the Git Changes panel. It shows the snapshots of a <see cref="GitSession"/>; its stage, unstage,
/// discard and commit buttons run the registered Git commands, which ask, run git and report failures.
/// </summary>
public sealed partial class GitPanelViewModel : ReactiveObject, IDisposable
{
    private readonly GitSession _session;
    private readonly Func<string, IReadOnlyList<object?>?, System.Threading.Tasks.Task> _executeCommand;
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
    private bool _snapshotAppliedSinceRefresh;

    // The cancellation of the diff load that is running; only used on the UI thread.
    private CancellationTokenSource? _diffLoad;

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

    /// <summary>Runs <see cref="GitCommands.STAGE"/> for the selected working tree change.</summary>
    public ReactiveCommand<RxVoid, RxVoid> StageCommand { get; }

    /// <summary>Runs <see cref="GitCommands.UNSTAGE"/> for the selected staged change.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UnstageCommand { get; }

    /// <summary>Runs <see cref="GitCommands.DISCARD"/> for the selected working tree change.</summary>
    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }

    /// <summary>Runs <see cref="GitCommands.STAGE_ALL"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> StageAllCommand { get; }

    /// <summary>Runs <see cref="GitCommands.UNSTAGE_ALL"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UnstageAllCommand { get; }

    /// <summary>Runs <see cref="GitCommands.COMMIT"/>, which commits with <see cref="CommitMessage"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CommitCommand { get; }

    /// <summary>Creates the panel for a shared session; the session stays alive when the panel is disposed.</summary>
    /// <param name="session">The Git session whose status is shown.</param>
    /// <param name="executeCommand">Runs a Git command by id with arguments.</param>
    public GitPanelViewModel(GitSession session, Func<string, IReadOnlyList<object?>?, System.Threading.Tasks.Task> executeCommand)
    {
        _session = session;
        _executeCommand = executeCommand;

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

        ToggleMonitoringCommand = AddCommand(ReactiveCommand.CreateFromTask(ToggleMonitoringAsync));
        ToggleDiffCommand = AddCommand(ReactiveCommand.Create(ToggleDiff));
        RefreshCommand = AddCommand(ReactiveCommand.CreateFromTask(RefreshAsync));

        // The buttons are enabled for the same changes the commands work on.
        IObservable<bool> canStage = this.WhenAnyValue(x => x.SelectedUnstagedChange)
            .Select(entry => entry?.Change is { } change && GitCommandTarget.HasWorkingTreeChange(change));
        IObservable<bool> canUnstage = this.WhenAnyValue(x => x.SelectedStagedChange)
            .Select(entry => entry?.Change is { } change && GitCommandTarget.HasStagedChange(change));
        IObservable<bool> canDiscard = this.WhenAnyValue(x => x.SelectedUnstagedChange)
            .Select(entry => entry?.Change is { } change && GitCommandTarget.CanDiscard(change));

        StageCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForAsync(GitCommands.STAGE, SelectedUnstagedChange), canStage));
        UnstageCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForAsync(GitCommands.UNSTAGE, SelectedStagedChange), canUnstage));
        DiscardCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForAsync(GitCommands.DISCARD, SelectedUnstagedChange), canDiscard));

        StageAllCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => _executeCommand(GitCommands.STAGE_ALL, null),
            this.WhenAnyValue(x => x.UnstagedCount).Select(count => count > 0)));
        UnstageAllCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => _executeCommand(GitCommands.UNSTAGE_ALL, null),
            this.WhenAnyValue(x => x.StagedCount).Select(count => count > 0)));

        IObservable<bool> canCommit = this.WhenAnyValue(x => x.CommitMessage, x => x.StagedCount, x => x.IsRepository,
                (message, count, isRepo) => isRepo && count > 0 && !string.IsNullOrWhiteSpace(message))
            .DistinctUntilChanged();
        CommitCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.COMMIT, null), canCommit));

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
                CancelDiffLoad();
                DiffText = string.Empty;
                DiffLines.Clear();
            });
        _disposables.Add(selectionClear);

        IDisposable searchSubscription = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter());
        _disposables.Add(searchSubscription);

        _disposables.Add(_session.Snapshots.WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(ApplySnapshot));
        _disposables.Add(_session.RefreshCompleted.WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(_ => OnRefreshCompleted()));
        _disposables.Add(_session.IsRefreshing.WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(isRefreshing => IsBusy = isRefreshing));
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
    }

    /// <summary>Refreshes the status of the session; completes when the refresh that includes this request is done.</summary>
    public System.Threading.Tasks.Task RefreshAsync()
    {
        return _session.RefreshAsync();
    }

    private ReactiveCommand<RxVoid, RxVoid> AddCommand(ReactiveCommand<RxVoid, RxVoid> command)
    {
        _disposables.Add(command);
        // Without a subscriber ReactiveUI ends the application when a command fails.
        _disposables.Add(command.ThrownExceptions.Subscribe(error =>
            ErrorMessage = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, error.Message)));
        return command;
    }

    private System.Threading.Tasks.Task ExecuteForAsync(string commandId, GitChangeEntryViewModel? entry)
    {
        return entry is null
            ? System.Threading.Tasks.Task.CompletedTask
            : _executeCommand(commandId, [entry.Path]);
    }

    private async System.Threading.Tasks.Task ToggleMonitoringAsync()
    {
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

    /// <summary>
    /// Reloads the diff of the selected change after a refresh that published no snapshot: the file content may
    /// have changed while its status stayed the same. A published snapshot already reloaded it through the restored
    /// selection.
    /// </summary>
    private void OnRefreshCompleted()
    {
        bool snapshotApplied = _snapshotAppliedSinceRefresh;
        _snapshotAppliedSinceRefresh = false;
        if (_isDisposed || snapshotApplied)
        {
            return;
        }

        if ((SelectedStagedChange ?? SelectedUnstagedChange) is { } selected)
        {
            _ = LoadDiffAsync(selected);
        }
    }

    private void ApplySnapshot(GitSnapshot snapshot)
    {
        if (_isDisposed)
        {
            return;
        }

        _snapshotAppliedSinceRefresh = true;

        if (IsGitMonitoringEnabled != snapshot.IsWorkingTreeWatcherEnabled)
        {
            IsGitMonitoringEnabled = snapshot.IsWorkingTreeWatcherEnabled;
            this.RaisePropertyChanged(nameof(GitMonitoringLabel));
        }

        if (snapshot.State == GitSessionState.Unknown)
        {
            return;
        }

        if (snapshot.Status is null)
        {
            RepositoryRoot = snapshot.RepositoryRoot;
            BranchName = string.Empty;
            UpstreamName = null;
            AheadBy = 0;
            BehindBy = 0;
            IsRepository = snapshot.IsRepository;
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

    /// <summary>
    /// Loads the diff of <paramref name="change"/> and shows it while the change is still selected. Each load cancels
    /// the one before it, so a diff that finishes after another file was selected, or after a newer load of the same
    /// file, is not shown.
    /// </summary>
    private async System.Threading.Tasks.Task LoadDiffAsync(GitChangeEntryViewModel change)
    {
        CancelDiffLoad();
        string? repositoryRoot = RepositoryRoot;
        if (string.IsNullOrWhiteSpace(repositoryRoot))
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

        using CancellationTokenSource load = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _diffLoad = load;
        try
        {
            // Started on the UI thread, so the code after the await runs there again.
            GitDiff diff = await _session.Services.Service.GetDiffAsync(repositoryRoot, request, load.Token);
            if (load.IsCancellationRequested || !ReferenceEquals(change, SelectedStagedChange ?? SelectedUnstagedChange))
            {
                return;
            }

            DiffText = diff.RawText ?? string.Empty;
            DiffLines.Clear();
            foreach (GitDiffLineViewModel line in BuildDiffLines(diff))
            {
                DiffLines.Add(line);
            }
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested)
        {
        }
        finally
        {
            if (ReferenceEquals(_diffLoad, load))
            {
                _diffLoad = null;
            }
        }
    }

    /// <summary>Cancels the diff load that is running; it then shows nothing.</summary>
    private void CancelDiffLoad()
    {
        _diffLoad?.Cancel();
        _diffLoad = null;
    }

    /// <summary>
    /// Fills a list with the changes the matching command works on: the staged list with the changes Unstage
    /// takes, the working tree list with the changes Stage takes. A conflicted file is in the working tree list only.
    /// </summary>
    private static void ReplaceItems(
        ObservableCollection<GitChangeEntryViewModel> target,
        GitRepositoryStatus status,
        bool isStaged)
    {
        target.Clear();

        foreach (GitFileChange change in status.Changes)
        {
            bool listed = isStaged
                ? GitCommandTarget.HasStagedChange(change)
                : GitCommandTarget.HasWorkingTreeChange(change);
            if (listed)
            {
                target.Add(new GitChangeEntryViewModel(change, isStaged));
            }
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
            if (FileSystemPathComparison.Equals(item.Path, selectedPath)
                || (item.OldPath is not null && FileSystemPathComparison.Equals(item.OldPath, selectedPath)))
            {
                return item;
            }
        }

        return null;
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
