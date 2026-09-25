using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>
/// Shared TFVC state of the extension: connection settings, the workspaces of the current user,
/// the selected workspace and its pending changes. Both panels and the automatic check-out use one instance.
/// </summary>
/// <remarks>
/// The editor workspace only preselects the TFVC workspace that maps it; the visible content is defined
/// by the selected TFVC workspace and its working folder mappings.
/// </remarks>
public sealed class TfvcSession : IDisposable
{
    private readonly TfvcSettingsStore _store;
    private readonly IWorkspaceInfo _workspaceInfo;
    private readonly IExtensionLogger _logger;
    private readonly HashSet<string> _excludedServerPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offlineEdits = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private int _pendingRefreshVersion;
    private int _busyCount;
    private bool _isDisposed;

    /// <summary>Creates the session.</summary>
    public TfvcSession(
        ITfvcClient client,
        ITfvcServerClient server,
        TfvcSettingsStore store,
        IWorkspaceInfo workspaceInfo,
        IExtensionLogger logger)
    {
        Client = client;
        Server = server;
        _store = store;
        _workspaceInfo = workspaceInfo;
        _logger = logger;
        _workspaceInfo.WorkspaceChanged += OnEditorWorkspaceChanged;
    }

    /// <summary>Raised when settings, workspaces, the selected workspace, the busy state or the status message change.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when the pending changes or the excluded changes of the selected workspace change.</summary>
    public event EventHandler? PendingChangesChanged;

    /// <summary>Gets the workspace operations client.</summary>
    public ITfvcClient Client { get; }

    /// <summary>Gets the server query client.</summary>
    public ITfvcServerClient Server { get; }

    /// <summary>Gets the connection settings.</summary>
    public TfvcSettings Settings { get; private set; } = TfvcSettings.Empty;

    /// <summary>Gets the workspaces of the current user on this computer.</summary>
    public IReadOnlyList<TfvcWorkspace> Workspaces { get; private set; } = Array.Empty<TfvcWorkspace>();

    /// <summary>Gets the selected workspace.</summary>
    public TfvcWorkspace? CurrentWorkspace { get; private set; }

    /// <summary>Gets the pending changes of the selected workspace.</summary>
    public IReadOnlyList<TfvcPendingChange> PendingChanges { get; private set; } = Array.Empty<TfvcPendingChange>();

    /// <summary>Gets the server paths excluded from check-in.</summary>
    public IReadOnlyCollection<string> ExcludedServerPaths => _excludedServerPaths;

    /// <summary>Gets the last connection message, typically an error.</summary>
    public string? StatusMessage { get; private set; }

    /// <summary>Gets whether a connection or refresh is running.</summary>
    public bool IsBusy { get; private set; }

    /// <summary>Gets whether the session holds workspaces of a configured collection and works online.</summary>
    public bool IsConnected => !IsOffline && Settings.IsConfigured && Workspaces.Count > 0;

    /// <summary>
    /// Gets whether the user chose to work offline. The workspaces of the last successful connection stay known for
    /// their mappings, but no tf.exe or server call is made.
    /// </summary>
    public bool IsOffline { get; private set; }

    /// <summary>Gets whether the last connection attempt failed.</summary>
    public bool ConnectionFailed { get; private set; }

    /// <summary>Gets the local paths whose write protection was removed while working offline.</summary>
    public IReadOnlyCollection<string> OfflineEdits => _offlineEdits;

    /// <summary>Loads the stored settings and connects when a collection URL is configured.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Settings = await _store.LoadSettingsAsync(cancellationToken);
        Client.ConfiguredTfExePath = Settings.TfExePath;
        foreach (string path in await _store.LoadOfflineEditsAsync(cancellationToken))
        {
            _offlineEdits.Add(path);
        }

        if (!Settings.IsConfigured)
        {
            StatusMessage = TfvcStrings.NotConfigured;
            RaiseStateChanged();
            return;
        }

        await ConnectAsync(cancellationToken);
    }

    /// <summary>Stores new settings and reconnects.</summary>
    public async Task ApplySettingsAsync(TfvcSettings settings, CancellationToken cancellationToken)
    {
        TfvcSettings normalized = new(
            string.IsNullOrWhiteSpace(settings.CollectionUrl) ? null : settings.CollectionUrl.Trim().TrimEnd('/'),
            string.IsNullOrWhiteSpace(settings.TfExePath) ? null : settings.TfExePath.Trim().Trim('"'));
        await _store.SaveSettingsAsync(normalized, cancellationToken);
        Settings = normalized;
        Client.ConfiguredTfExePath = normalized.TfExePath;
        if (!normalized.IsConfigured)
        {
            Workspaces = Array.Empty<TfvcWorkspace>();
            CurrentWorkspace = null;
            PendingChanges = Array.Empty<TfvcPendingChange>();
            StatusMessage = TfvcStrings.NotConfigured;
            RaiseStateChanged();
            RaisePendingChangesChanged();
            return;
        }

        await ConnectAsync(cancellationToken);
    }

    /// <summary>Loads the workspaces of the configured collection and selects the preferred one.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        string? collectionUrl = Settings.CollectionUrl;
        if (string.IsNullOrWhiteSpace(collectionUrl))
        {
            return;
        }

        await _connectGate.WaitAsync(cancellationToken);
        try
        {
            IsOffline = false;
            SetBusy(true);
            IReadOnlyList<TfvcWorkspace> workspaces = await Client.GetWorkspacesAsync(collectionUrl, cancellationToken);
            Workspaces = workspaces;
            ConnectionFailed = false;
            StatusMessage = workspaces.Count == 0 ? TfvcStrings.NoWorkspace : null;
            if (workspaces.Count > 0)
            {
                await _store.SaveCachedWorkspacesAsync(workspaces, cancellationToken);
            }

            string? lastWorkspace = await _store.LoadLastWorkspaceAsync(cancellationToken);
            TfvcWorkspace? preferred = SelectPreferredWorkspace(workspaces, _workspaceInfo.WorkspacePath, lastWorkspace);
            _logger.Info(string.Format(
                CultureInfo.CurrentCulture,
                TfvcStrings.LogConnectedFormat,
                collectionUrl,
                workspaces.Count,
                preferred?.Name ?? TfvcStrings.LogNoWorkspaceSelected));
            await SetCurrentWorkspaceAsync(preferred, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(TfvcStrings.LogConnectionFailed, ex);
            ConnectionFailed = true;
            Workspaces = Array.Empty<TfvcWorkspace>();
            CurrentWorkspace = null;
            PendingChanges = Array.Empty<TfvcPendingChange>();
            StatusMessage = TfvcDisplayText.DescribeError(ex);
            RaisePendingChangesChanged();
        }
        finally
        {
            SetBusy(false);
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Switches to offline work. The workspaces of the current or last successful connection keep their mappings
    /// known; pending changes are not queried until the next connection.
    /// </summary>
    public async Task GoOfflineAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TfvcWorkspace> workspaces = Workspaces.Count > 0
            ? Workspaces
            : await _store.LoadCachedWorkspacesAsync(cancellationToken);
        _logger.Info(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogWorkingOfflineFormat, workspaces.Count));
        IsOffline = true;
        Workspaces = workspaces;
        CurrentWorkspace = null;
        PendingChanges = Array.Empty<TfvcPendingChange>();
        StatusMessage = TfvcStrings.OfflineStatus;
        RaiseStateChanged();
        RaisePendingChangesChanged();
    }

    /// <summary>Runs an operation while the session reports itself busy, so the panels show their activity.</summary>
    public async Task<T> RunBusyAsync<T>(Func<Task<T>> operation)
    {
        SetBusy(true);
        try
        {
            return await operation();
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Remembers a local file whose write protection was removed while working offline.</summary>
    public Task RecordOfflineEditAsync(string localPath, CancellationToken cancellationToken)
    {
        return _offlineEdits.Add(localPath)
            ? _store.SaveOfflineEditsAsync(_offlineEdits, cancellationToken)
            : Task.CompletedTask;
    }

    /// <summary>Forgets local files remembered as offline edits.</summary>
    public Task ForgetOfflineEditsAsync(IEnumerable<string> localPaths, CancellationToken cancellationToken)
    {
        bool changed = false;
        foreach (string path in localPaths)
        {
            changed |= _offlineEdits.Remove(path);
        }

        return changed ? _store.SaveOfflineEditsAsync(_offlineEdits, cancellationToken) : Task.CompletedTask;
    }

    /// <summary>Selects a workspace and loads its pending changes.</summary>
    public async Task SelectWorkspaceAsync(TfvcWorkspace? workspace, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(workspace, CurrentWorkspace))
        {
            return;
        }

        await SetCurrentWorkspaceAsync(workspace, cancellationToken);
        await _store.SaveLastWorkspaceAsync(workspace?.Name, cancellationToken);
    }

    /// <summary>Reloads the pending changes of the selected workspace.</summary>
    public async Task RefreshPendingChangesAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = CurrentWorkspace;
        string? collectionUrl = Settings.CollectionUrl;
        int version = Interlocked.Increment(ref _pendingRefreshVersion);
        if (IsOffline || workspace is null || string.IsNullOrWhiteSpace(collectionUrl))
        {
            PendingChanges = Array.Empty<TfvcPendingChange>();
            RaisePendingChangesChanged();
            return;
        }

        try
        {
            SetBusy(true);
            IReadOnlyList<TfvcPendingChange> changes = await Client.GetPendingChangesAsync(collectionUrl, workspace, cancellationToken);
            if (version != Volatile.Read(ref _pendingRefreshVersion) || !ReferenceEquals(workspace, CurrentWorkspace))
            {
                return;
            }

            PendingChanges = changes;
            if (StatusMessage is not null && Workspaces.Count > 0)
            {
                StatusMessage = null;
                RaiseStateChanged();
            }

            RaisePendingChangesChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(TfvcStrings.LogPendingChangesLoadFailed, ex);
            StatusMessage = TfvcDisplayText.DescribeError(ex);
            RaiseStateChanged();
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>Loads the folder the source control explorer is focused on.</summary>
    public Task<string?> LoadExplorerFocusAsync(CancellationToken cancellationToken) => _store.LoadExplorerFocusAsync(cancellationToken);

    /// <summary>Saves the folder the source control explorer is focused on.</summary>
    public Task SaveExplorerFocusAsync(string? serverPath, CancellationToken cancellationToken) => _store.SaveExplorerFocusAsync(serverPath, cancellationToken);

    /// <summary>Loads whether a panel shows its connection settings; null when the panel has not stored it yet.</summary>
    public Task<bool?> LoadSettingsVisibleAsync(string panelId, CancellationToken cancellationToken) =>
        _store.LoadSettingsVisibleAsync(panelId, cancellationToken);

    /// <summary>Saves whether a panel shows its connection settings.</summary>
    public Task SaveSettingsVisibleAsync(string panelId, bool isVisible, CancellationToken cancellationToken) =>
        _store.SaveSettingsVisibleAsync(panelId, isVisible, cancellationToken);

    /// <summary>Includes or excludes pending changes from check-in.</summary>
    public async Task SetExcludedAsync(IEnumerable<string> serverPaths, bool excluded, CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = CurrentWorkspace;
        if (workspace is null)
        {
            return;
        }

        bool changed = false;
        foreach (string serverPath in serverPaths)
        {
            changed |= excluded ? _excludedServerPaths.Add(serverPath) : _excludedServerPaths.Remove(serverPath);
        }

        if (!changed)
        {
            return;
        }

        RaisePendingChangesChanged();
        await _store.SaveExcludedChangesAsync(workspace.Name, _excludedServerPaths, cancellationToken);
    }

    /// <summary>Gets whether a pending change is excluded from check-in.</summary>
    public bool IsExcluded(TfvcPendingChange change) => _excludedServerPaths.Contains(change.ServerPath);

    /// <summary>Finds the known workspace that maps a local path, preferring the selected workspace.</summary>
    public TfvcWorkspace? FindWorkspaceForLocalPath(string localPath)
    {
        if (CurrentWorkspace?.ContainsLocalPath(localPath) == true)
        {
            return CurrentWorkspace;
        }

        foreach (TfvcWorkspace workspace in Workspaces)
        {
            if (workspace.ContainsLocalPath(localPath))
            {
                return workspace;
            }
        }

        return null;
    }

    /// <summary>Finds the pending change of a local path in the selected workspace.</summary>
    public TfvcPendingChange? FindPendingChangeByLocalPath(string localPath)
    {
        foreach (TfvcPendingChange change in PendingChanges)
        {
            if (change.LocalPath is not null && string.Equals(change.LocalPath, localPath, StringComparison.OrdinalIgnoreCase))
            {
                return change;
            }
        }

        return null;
    }

    /// <summary>Finds the pending change of a server path in the selected workspace.</summary>
    public TfvcPendingChange? FindPendingChangeByServerPath(string serverPath)
    {
        foreach (TfvcPendingChange change in PendingChanges)
        {
            if (string.Equals(change.ServerPath, serverPath, StringComparison.OrdinalIgnoreCase))
            {
                return change;
            }
        }

        return null;
    }

    /// <summary>Detects the collection URL of the TFVC workspace that maps the editor workspace.</summary>
    public async Task<string?> SuggestCollectionUrlAsync(CancellationToken cancellationToken)
    {
        string? directory = GetExistingDirectory(_workspaceInfo.WorkspacePath);
        if (directory is null)
        {
            return null;
        }

        try
        {
            return await Client.DetectCollectionUrlAsync(directory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogCollectionDetectionFailedFormat, ex.Message));
            return null;
        }
    }

    /// <summary>Selects the workspace that maps the editor workspace, then the last used one, then the first one with local folders.</summary>
    public static TfvcWorkspace? SelectPreferredWorkspace(
        IReadOnlyList<TfvcWorkspace> workspaces,
        string? editorWorkspacePath,
        string? lastWorkspaceName)
    {
        if (workspaces.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(editorWorkspacePath))
        {
            foreach (TfvcWorkspace workspace in workspaces)
            {
                if (workspace.ContainsLocalPath(editorWorkspacePath))
                {
                    return workspace;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(lastWorkspaceName))
        {
            foreach (TfvcWorkspace workspace in workspaces)
            {
                if (string.Equals(workspace.Name, lastWorkspaceName, StringComparison.OrdinalIgnoreCase))
                {
                    return workspace;
                }
            }
        }

        foreach (TfvcWorkspace workspace in workspaces)
        {
            if (workspace.GetWorkingDirectory() is not null)
            {
                return workspace;
            }
        }

        return workspaces[0];
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _workspaceInfo.WorkspaceChanged -= OnEditorWorkspaceChanged;
        _connectGate.Dispose();
    }

    private async Task SetCurrentWorkspaceAsync(TfvcWorkspace? workspace, CancellationToken cancellationToken)
    {
        CurrentWorkspace = workspace;
        _excludedServerPaths.Clear();
        if (workspace is not null)
        {
            foreach (string path in await _store.LoadExcludedChangesAsync(workspace.Name, cancellationToken))
            {
                _excludedServerPaths.Add(path);
            }
        }

        PendingChanges = Array.Empty<TfvcPendingChange>();
        RaiseStateChanged();
        await RefreshPendingChangesAsync(cancellationToken);
    }

    private async void OnEditorWorkspaceChanged(object? sender, WorkspaceChangedEventArgs e)
    {
        try
        {
            if (!IsConnected || string.IsNullOrWhiteSpace(e.WorkspacePath) || CurrentWorkspace?.ContainsLocalPath(e.WorkspacePath) == true)
            {
                return;
            }

            TfvcWorkspace? workspace = SelectPreferredWorkspace(Workspaces, e.WorkspacePath, null);
            if (workspace?.ContainsLocalPath(e.WorkspacePath) == true)
            {
                await SelectWorkspaceAsync(workspace, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(TfvcStrings.LogWorkspaceSelectionFailed, ex);
        }
    }

    private static string? GetExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        string? parent = Path.GetDirectoryName(path);
        return parent is not null && Directory.Exists(parent) ? parent : null;
    }

    private void SetBusy(bool isBusy)
    {
        int count = isBusy ? Interlocked.Increment(ref _busyCount) : Interlocked.Decrement(ref _busyCount);
        bool busy = count > 0;
        if (IsBusy == busy)
        {
            return;
        }

        IsBusy = busy;
        RaiseStateChanged();
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RaisePendingChangesChanged() => PendingChangesChanged?.Invoke(this, EventArgs.Empty);
}
