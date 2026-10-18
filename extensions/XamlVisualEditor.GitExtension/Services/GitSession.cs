using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// The Git state of the workspace, shared by the Git panel, the status bar, the context keys and the commands.
/// Finds the repository of the workspace, publishes its status as <see cref="Snapshots"/>, refreshes it when the
/// git directory or, when switched on, the working folder changes, and after every command run through
/// <see cref="RunAsync"/>.
/// </summary>
/// <remarks>
/// Refreshes never overlap: a refresh requested while one runs starts exactly one more refresh after it, and
/// every request made meanwhile completes with that one. A snapshot equal to the current one is not published.
/// </remarks>
public sealed class GitSession : IDisposable
{
    /// <summary>
    /// Gets the interval in which file system changes are collected; changes within one interval cause one refresh
    /// at its end, also while further changes keep coming.
    /// </summary>
    public static TimeSpan RefreshDelay { get; } = TimeSpan.FromMilliseconds(500);

    private readonly GitServiceSet _services;
    private readonly IWorkspaceInfo? _workspaceInfo;
    private readonly GitSettingsStore? _settings;
    private readonly IExtensionLogger? _logger;
    private readonly BehaviorSubject<GitSnapshot> _snapshots = new(GitSnapshot.Initial);
    private readonly BehaviorSubject<bool> _isRefreshing = new(false);
    private readonly Subject<Unit> _refreshCompleted = new();
    private readonly Subject<Unit> _changeRequests = new();
    private readonly IDisposable _changeSubscription;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sync = new();
    private readonly object _publishSync = new();
    private readonly object _watcherSync = new();
    private EventHandler<WorkspaceChangedEventArgs>? _workspaceChangedHandler;
    private TaskCompletionSource? _pendingRefresh;
    private bool _refreshRunning;
    private bool _started;
    private bool _disposed;
    private bool _workingTreeWatcherEnabled;
    private bool _workingTreeWatcherFailed;
    private bool _gitDirectoryWatcherFailed;
    private bool _gitUnavailableLogged;
    private int _refsVersion;
    private string? _cachedWorkspacePath;
    private string? _cachedRepositoryRoot;
    private GitRepositoryWatcher? _watcher;

    /// <summary>Creates the session; it reads nothing before <see cref="StartAsync"/> or <see cref="RefreshAsync"/>.</summary>
    /// <param name="services">The git services.</param>
    /// <param name="workspaceInfo">The workspace whose repository is tracked; null tracks no workspace.</param>
    /// <param name="settings">Persists the working folder watcher switch; null keeps it in memory.</param>
    /// <param name="logger">Receives watcher failures and refresh errors.</param>
    /// <param name="scheduler">Times <see cref="RefreshDelay"/>; <see cref="Scheduler.Default"/> when null.</param>
    public GitSession(
        GitServiceSet services,
        IWorkspaceInfo? workspaceInfo,
        GitSettingsStore? settings = null,
        IExtensionLogger? logger = null,
        IScheduler? scheduler = null)
    {
        _services = services;
        _workspaceInfo = workspaceInfo;
        _settings = settings;
        _logger = logger;
        _changeSubscription = _changeRequests
            .Sample(RefreshDelay, scheduler ?? Scheduler.Default)
            .Subscribe(OnChangeRequestsSettled);
    }

    /// <summary>Gets the git services of the session.</summary>
    public GitServiceSet Services => _services;

    /// <summary>
    /// Gets the snapshots of the session. A new subscriber receives the current snapshot first. Snapshots arrive on
    /// thread-pool threads.
    /// </summary>
    public IObservable<GitSnapshot> Snapshots => _snapshots.AsObservable();

    /// <summary>Gets whether a refresh runs; a new subscriber receives the current value first.</summary>
    public IObservable<bool> IsRefreshing => _isRefreshing.AsObservable();

    /// <summary>
    /// Gets a signal after every finished refresh, also one whose snapshot equals the current one and is therefore
    /// not published, after the snapshot of that refresh. A file whose content changed while its status stayed the
    /// same leaves the snapshot unchanged, so views of content, such as a diff, reload on this signal. Signals
    /// arrive on thread-pool threads.
    /// </summary>
    public IObservable<Unit> RefreshCompleted => _refreshCompleted.AsObservable();

    /// <summary>Gets the latest snapshot.</summary>
    public GitSnapshot Current => _snapshots.Value;

    /// <summary>Gets whether <see cref="InitializeRepositoryAsync"/> can create repositories.</summary>
    public bool CanInitializeRepository => _services.Repository is not null;

    /// <summary>
    /// Loads the working folder watcher switch, follows workspace changes and runs the first refresh. Calling it
    /// again has no effect.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_started || _disposed)
            {
                return;
            }

            _started = true;
        }

        if (_settings is not null)
        {
            try
            {
                bool enabled = await _settings.LoadAutoRefreshAsync(cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _workingTreeWatcherEnabled = enabled;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.Error(GitStrings.LogSettingsFailed, exception);
            }
        }

        if (_workspaceInfo is not null)
        {
            EventHandler<WorkspaceChangedEventArgs> handler = (_, _) => _ = RefreshAsync();
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _workspaceChangedHandler = handler;
            }

            _workspaceInfo.WorkspaceChanged += handler;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the repository and its status again and publishes a snapshot. While a refresh runs, the call starts
    /// one more refresh after it; the returned task completes when the refresh that includes this request is done.
    /// <paramref name="cancellationToken"/> only stops waiting.
    /// </summary>
    public Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_sync)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            if (_refreshRunning)
            {
                _pendingRefresh ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                task = _pendingRefresh.Task;
            }
            else
            {
                _refreshRunning = true;
                // Under the lock, so the end of a previous refresh cannot report false after this true.
                _isRefreshing.OnNext(true);
                TaskCompletionSource current = new(TaskCreationOptions.RunContinuationsAsynchronously);
                task = current.Task;
                _ = Task.Run(() => RunRefreshLoopAsync(current));
            }
        }

        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>
    /// Refreshes like <see cref="RefreshAsync"/> and increases <see cref="GitSnapshot.RefsVersion"/>, so consumers
    /// read the branches again; for a refresh the user asked for.
    /// </summary>
    public Task RefreshAllAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _refsVersion);
        return RefreshAsync(cancellationToken);
    }

    /// <summary>
    /// Requests a refresh at the end of the current <see cref="RefreshDelay"/> interval; all requests of an interval
    /// cause one refresh.
    /// </summary>
    public void RequestRefresh()
    {
        _changeRequests.OnNext(Unit.Default);
    }

    /// <summary>
    /// Switches the recursive working folder watcher on or off and saves the switch. The git directory is watched
    /// either way. Switching on refreshes the status.
    /// </summary>
    public async Task SetWorkingTreeWatcherEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            if (_disposed || _workingTreeWatcherEnabled == enabled)
            {
                return;
            }

            _workingTreeWatcherEnabled = enabled;
            _workingTreeWatcherFailed = false;
        }

        Publish(Current);
        GitSnapshot current = Current;
        if (current.RepositoryRoot is not null)
        {
            ConfigureWatcher(current.RepositoryRoot, current.GitDirectory, current.CommonDirectory);
        }

        if (_settings is not null)
        {
            try
            {
                await _settings.SaveAutoRefreshAsync(enabled, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger?.Error(GitStrings.LogSettingsFailed, exception);
            }
        }

        if (enabled)
        {
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs a git command on the repository of the current snapshot and refreshes the status afterwards, also when
    /// the command failed. Exceptions of the command, such as <see cref="InvalidOperationException"/> with git's
    /// error output, are passed on after the refresh.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workspace is not inside a repository.</exception>
    public async Task RunAsync(
        Func<IGitService, string, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        string repositoryRoot = Current.RepositoryRoot
            ?? throw new InvalidOperationException(UiStrings.WorkspaceNotGitRepository);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            await RunWatchedAsync(stop => operation(_services.Service, repositoryRoot, linked.Token), null).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gets or sets what watches a running operation: it gets the operation and the source that stops its writing git
    /// commands, shows a progress once the operation runs longer than a moment and lets the user stop git that hangs,
    /// for example in a hook. Null runs operations without watching.
    /// </summary>
    public Func<Task, CancellationTokenSource, Task>? Watch { get; set; }

    /// <summary>
    /// Starts <paramref name="operation"/> in a scope where <paramref name="stop"/>, or a source of its own that
    /// <see cref="Watch"/> gets, can stop its writing git commands, and reports a stop as
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task<T> RunWatchedAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationTokenSource? stop)
    {
        using CancellationTokenSource ownStop = new();
        CancellationTokenSource stopSource = stop ?? ownStop;
        Task<T> run;
        using (GitProcessStop.Allow(stopSource.Token))
        {
            run = operation(stopSource.Token);
        }

        if (stop is null && Watch is { } watch)
        {
            await watch(run, ownStop).ConfigureAwait(false);
        }

        try
        {
            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopSource.IsCancellationRequested)
        {
            throw new InvalidOperationException(GitStrings.GitStopped);
        }
    }

    private Task RunWatchedAsync(Func<CancellationToken, Task> operation, CancellationTokenSource? stop) =>
        RunWatchedAsync<bool>(async token =>
        {
            await operation(token).ConfigureAwait(false);
            return true;
        }, stop);

    /// <summary>
    /// Runs an operation with all git services on the repository of the current snapshot and refreshes the status
    /// afterwards, also when the operation failed or was cancelled. The operation may change branches, so the refresh
    /// increases <see cref="GitSnapshot.RefsVersion"/>. Exceptions of the operation are passed on after the refresh.
    /// </summary>
    /// <exception cref="InvalidOperationException">The workspace is not inside a repository.</exception>
    /// <param name="operation">The operation.</param>
    /// <param name="cancellationToken">Cancels the operation; a writing git command that started runs to its end.</param>
    /// <param name="stop">
    /// Stops the writing git commands of the operation, for a caller that shows its own progress; without it,
    /// <see cref="Watch"/> watches the operation.
    /// </param>
    public async Task<T> RunWithServicesAsync<T>(
        Func<GitServiceSet, string, CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default,
        CancellationTokenSource? stop = null)
    {
        string repositoryRoot = Current.RepositoryRoot
            ?? throw new InvalidOperationException(UiStrings.WorkspaceNotGitRepository);
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _lifetime.Token);
            return await RunWatchedAsync(_ => operation(_services, repositoryRoot, linked.Token), stop).ConfigureAwait(false);
        }
        finally
        {
            await RefreshAllAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Creates a repository in the workspace folder, or in the folder of the workspace file, and refreshes the
    /// status. A folder that already belongs to a repository is left unchanged; the result then names that
    /// repository.
    /// </summary>
    /// <exception cref="InvalidOperationException">No workspace is open, the services cannot create repositories, or git failed.</exception>
    public async Task<GitInitializeResult> InitializeRepositoryAsync(CancellationToken cancellationToken = default)
    {
        string workspacePath = _workspaceInfo?.WorkspacePath is { Length: > 0 } path
            ? path
            : throw new InvalidOperationException(GitStrings.InitNoWorkspace);
        IGitRepositoryService repository = _services.Repository
            ?? throw new InvalidOperationException(GitStrings.InitUnavailable);
        string folder = File.Exists(workspacePath)
            ? Path.GetDirectoryName(Path.GetFullPath(workspacePath))!
            : workspacePath;
        string? existingRoot = await _services.Service.GetRepositoryRootAsync(folder, cancellationToken).ConfigureAwait(false);
        if (existingRoot is not null)
        {
            return new GitInitializeResult(folder, Path.GetFullPath(existingRoot));
        }

        try
        {
            await repository.InitAsync(folder, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_sync)
            {
                _cachedRepositoryRoot = null;
                _cachedWorkspacePath = null;
            }

            await RefreshAsync().ConfigureAwait(false);
        }

        return new GitInitializeResult(folder, null);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        TaskCompletionSource? pending;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pending = _pendingRefresh;
            _pendingRefresh = null;
        }

        _lifetime.Cancel();
        if (_workspaceInfo is not null && _workspaceChangedHandler is not null)
        {
            _workspaceInfo.WorkspaceChanged -= _workspaceChangedHandler;
        }

        _changeSubscription.Dispose();
        lock (_watcherSync)
        {
            _watcher?.Dispose();
            _watcher = null;
        }

        pending?.TrySetResult();
        lock (_publishSync)
        {
            _snapshots.OnCompleted();
            _refreshCompleted.OnCompleted();
        }

        _isRefreshing.OnCompleted();
    }

    private void OnChangeRequestsSettled(Unit unit)
    {
        _ = RefreshAsync();
    }

    private async Task RunRefreshLoopAsync(TaskCompletionSource current)
    {
        while (true)
        {
            try
            {
                await RefreshCoreAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                _logger?.Error(GitStrings.LogRefreshFailed, exception);
                Publish(Current with { ErrorMessage = exception.Message });
            }

            SignalRefreshCompleted();
            current.TrySetResult();
            lock (_sync)
            {
                if (_disposed || _pendingRefresh is null)
                {
                    _refreshRunning = false;
                    _isRefreshing.OnNext(false);
                    break;
                }

                current = _pendingRefresh;
                _pendingRefresh = null;
            }
        }
    }

    private async Task RefreshCoreAsync(CancellationToken cancellationToken)
    {
        string? workspacePath = _workspaceInfo?.WorkspacePath;
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            StopWatcher();
            Publish(new GitSnapshot { State = GitSessionState.NoWorkspace, ErrorMessage = UiStrings.NoWorkspaceLoadedStatus });
            return;
        }

        string? repositoryRoot;
        try
        {
            repositoryRoot = await ResolveRepositoryRootAsync(workspacePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Win32Exception exception)
        {
            StopWatcher();
            if (!_gitUnavailableLogged)
            {
                _gitUnavailableLogged = true;
                _logger?.Error(GitStrings.LogGitUnavailable, exception);
            }

            Publish(new GitSnapshot
            {
                State = GitSessionState.GitUnavailable,
                WorkspacePath = workspacePath,
                ErrorMessage = UiStrings.GitUnavailable
            });
            return;
        }

        _gitUnavailableLogged = false;
        if (repositoryRoot is null)
        {
            StopWatcher();
            Publish(new GitSnapshot
            {
                State = GitSessionState.NotRepository,
                WorkspacePath = workspacePath,
                ErrorMessage = UiStrings.WorkspaceNotGitRepository
            });
            return;
        }

        string? gitDirectory;
        string? commonDirectory;
        if (_services.Repository is null)
        {
            gitDirectory = FindDefaultGitDirectory(repositoryRoot);
            commonDirectory = gitDirectory;
        }
        else
        {
            gitDirectory = await _services.Repository.GetGitDirectoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
            commonDirectory = await _services.Repository.GetCommonDirectoryAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        }

        // The repository was found, so a failing status, for example of a damaged index or while another git
        // command rewrites it, keeps the repository and its watchers; the next change reads the status again.
        GitRepositoryStatus? status = null;
        string? statusError = null;
        try
        {
            status = await _services.Service.GetStatusAsync(repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            statusError = exception.Message.Trim();
        }

        // The cached root hides a repository that was deleted; asking git again tells a deleted repository from a
        // damaged one.
        if (status is null)
        {
            lock (_sync)
            {
                _cachedRepositoryRoot = null;
                _cachedWorkspacePath = null;
            }

            string? currentRoot = await ResolveRepositoryRootAsync(workspacePath, cancellationToken).ConfigureAwait(false);
            if (currentRoot is null)
            {
                StopWatcher();
                Publish(new GitSnapshot
                {
                    State = GitSessionState.NotRepository,
                    WorkspacePath = workspacePath,
                    ErrorMessage = UiStrings.WorkspaceNotGitRepository
                });
                return;
            }

            if (!FileSystemPathComparison.Equals(currentRoot, repositoryRoot))
            {
                // The workspace now belongs to another repository, such as an enclosing one; the root is cached, so
                // this second pass does not come here again.
                await RefreshCoreAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        bool hasRemote = !string.IsNullOrEmpty(status?.UpstreamName);
        if (!hasRemote && _services.Repository is not null)
        {
            IReadOnlyList<string> remotes = await _services.Repository
                .GetRemoteNamesAsync(repositoryRoot, cancellationToken)
                .ConfigureAwait(false);
            hasRemote = remotes.Count > 0;
        }

        cancellationToken.ThrowIfCancellationRequested();
        ConfigureWatcher(repositoryRoot, gitDirectory, commonDirectory);
        Publish(new GitSnapshot
        {
            State = status is null ? GitSessionState.StatusFailed : GitSessionState.Repository,
            WorkspacePath = workspacePath,
            RepositoryRoot = repositoryRoot,
            GitDirectory = gitDirectory,
            CommonDirectory = commonDirectory,
            Status = status,
            HasRemote = hasRemote,
            ErrorMessage = statusError ?? status?.ErrorMessage
        });
    }

    private async Task<string?> ResolveRepositoryRootAsync(string workspacePath, CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_cachedRepositoryRoot is not null && FileSystemPathComparison.Equals(_cachedWorkspacePath, workspacePath))
            {
                return _cachedRepositoryRoot;
            }
        }

        string? repositoryRoot = await _services.Service
            .GetRepositoryRootAsync(workspacePath, cancellationToken)
            .ConfigureAwait(false);
        lock (_sync)
        {
            _cachedWorkspacePath = repositoryRoot is null ? null : workspacePath;
            _cachedRepositoryRoot = repositoryRoot;
        }

        return repositoryRoot;
    }

    private static string? FindDefaultGitDirectory(string repositoryRoot)
    {
        string gitDirectory = Path.Combine(repositoryRoot, ".git");
        return Directory.Exists(gitDirectory) ? gitDirectory : null;
    }

    private void Publish(GitSnapshot snapshot)
    {
        lock (_publishSync)
        {
            bool enabled;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                enabled = _workingTreeWatcherEnabled;
            }

            GitSnapshot next = snapshot with
            {
                IsWorkingTreeWatcherEnabled = enabled,
                RefsVersion = Volatile.Read(ref _refsVersion)
            };
            if (!next.Equals(_snapshots.Value))
            {
                _snapshots.OnNext(next);
            }
        }
    }

    private void SignalRefreshCompleted()
    {
        lock (_publishSync)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }
            }

            _refreshCompleted.OnNext(Unit.Default);
        }
    }

    private void ConfigureWatcher(string repositoryRoot, string? gitDirectory, string? commonDirectory)
    {
        string fullRepositoryRoot = Path.GetFullPath(repositoryRoot);
        lock (_watcherSync)
        {
            GitRepositoryWatcher? existing = _watcher;
            bool watchWorkingTree;
            string? watchedGitDirectory;
            string? watchedCommonDirectory;
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                // A watcher failure stops that area for its repository only.
                if (existing is not null && !FileSystemPathComparison.Equals(existing.RepositoryRoot, fullRepositoryRoot))
                {
                    _workingTreeWatcherFailed = false;
                    _gitDirectoryWatcherFailed = false;
                }

                watchWorkingTree = _workingTreeWatcherEnabled && !_workingTreeWatcherFailed;
                bool watchGitDirectories = !_gitDirectoryWatcherFailed && gitDirectory is not null;
                watchedGitDirectory = watchGitDirectories ? Path.GetFullPath(gitDirectory!) : null;
                watchedCommonDirectory = watchGitDirectories ? Path.GetFullPath(commonDirectory ?? gitDirectory!) : null;
            }

            if (existing is not null
                && FileSystemPathComparison.Equals(existing.RepositoryRoot, fullRepositoryRoot)
                && FileSystemPathComparison.Equals(existing.GitDirectory, watchedGitDirectory)
                && FileSystemPathComparison.Equals(existing.CommonDirectory, watchedCommonDirectory)
                && existing.WatchWorkingTree == watchWorkingTree)
            {
                return;
            }

            existing?.Dispose();
            _watcher = null;
            if (watchedGitDirectory is null && !watchWorkingTree)
            {
                return;
            }

            GitRepositoryWatcher watcher = new(repositoryRoot, watchedGitDirectory, watchedCommonDirectory, watchWorkingTree);
            watcher.RefsChanged += (_, _) => Interlocked.Increment(ref _refsVersion);
            watcher.Changed += (_, _) => RequestRefresh();
            watcher.Failed += (_, args) => OnWatcherFailed(repositoryRoot, gitDirectory, commonDirectory, args);
            _watcher = watcher;
            watcher.Start();
        }
    }

    /// <summary>
    /// Stops watching the failed area for this repository: a failing git or common directory ends the watching of
    /// both, a failing working folder ends that one. The status is then refreshed after the editor's own commands.
    /// </summary>
    private void OnWatcherFailed(
        string repositoryRoot,
        string? gitDirectory,
        string? commonDirectory,
        GitWatcherFailedEventArgs args)
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (args.Area == GitWatchedArea.WorkingTree)
            {
                _workingTreeWatcherFailed = true;
            }
            else
            {
                _gitDirectoryWatcherFailed = true;
            }
        }

        string format = args.Area == GitWatchedArea.WorkingTree
            ? GitStrings.LogWorkingTreeWatcherFailedFormat
            : GitStrings.LogGitDirectoryWatcherFailedFormat;
        _logger?.Warn(string.Format(CultureInfo.CurrentCulture, format, args.Path, args.Exception.Message));

        // The failing watcher may still run its start or error handler; the replacement starts outside of it.
        _ = Task.Run(() => ConfigureWatcher(repositoryRoot, gitDirectory, commonDirectory));
    }

    private void StopWatcher()
    {
        lock (_watcherSync)
        {
            _watcher?.Dispose();
            _watcher = null;
            lock (_sync)
            {
                _workingTreeWatcherFailed = false;
                _gitDirectoryWatcherFailed = false;
            }
        }
    }
}

/// <summary>Result of <see cref="GitSession.InitializeRepositoryAsync"/>.</summary>
/// <param name="Folder">The folder of the workspace.</param>
/// <param name="ExistingRepositoryRoot">
/// The root of the repository the folder already belonged to, in which case no repository was created; otherwise null.
/// </param>
public sealed record GitInitializeResult(string Folder, string? ExistingRepositoryRoot)
{
    /// <summary>Gets whether a repository was created.</summary>
    public bool Created => ExistingRepositoryRoot is null;
}
