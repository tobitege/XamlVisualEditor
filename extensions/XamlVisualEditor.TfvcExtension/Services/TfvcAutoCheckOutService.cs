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
/// Checks out editor documents on their first modification.
/// </summary>
/// <remarks>
/// The version control state of a document is determined as soon as it becomes the active document,
/// before the user can change it: mapping, pending change (tf status) and server existence.
/// The first text change of a versioned document without pending change then checks the file out.
/// A text change that leaves the content equal to the file on disk (for example a reload) does not
/// count as modification.
/// </remarks>
public sealed class TfvcAutoCheckOutService : IDisposable
{
    private const char ByteOrderMark = (char)0xFEFF;

    private readonly TfvcSession _session;
    private readonly IEditorServices _editor;
    private readonly IWindow _window;
    private readonly IExtensionLogger _logger;
    private readonly Func<string, string?> _readFile;
    private readonly TimeSpan _retryDelay;
    private readonly Dictionary<string, Task<TfvcFileState>> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _checkOutsInProgress = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _failedCheckOuts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _declinedOfflineEdits = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDialogHost? _dialogHost;
    private bool _wasOffline;
    private readonly Dictionary<IEditorDocument, EventHandler<EditorDocumentChangedEventArgs>> _documentHandlers = new();
    private readonly object _gate = new();
    private int _stateGeneration;
    private bool _isStarted;
    private bool _isDisposed;

    /// <summary>Creates the service.</summary>
    public TfvcAutoCheckOutService(
        TfvcSession session,
        IEditorServices editor,
        IWindow window,
        IExtensionLogger logger,
        Func<string, string?>? readFile = null,
        TimeSpan? retryDelay = null,
        IDialogHost? dialogHost = null)
    {
        _session = session;
        _editor = editor;
        _window = window;
        _logger = logger;
        _readFile = readFile ?? ReadFileOrNull;
        _retryDelay = retryDelay ?? TimeSpan.FromSeconds(15);
        _dialogHost = dialogHost;
    }

    /// <summary>Raised when the state of the active document changes.</summary>
    public event EventHandler? ActiveDocumentStateChanged;

    /// <summary>Gets the path of the active document.</summary>
    public string? ActiveDocumentPath { get; private set; }

    /// <summary>Gets the version control state of the active document.</summary>
    public TfvcFileState ActiveDocumentState { get; private set; } = TfvcFileState.Unknown;

    /// <summary>Gets whether the state of the active document is being determined.</summary>
    public bool IsCheckingActiveDocument { get; private set; }

    /// <summary>Starts to observe the editor.</summary>
    public void Start()
    {
        if (_isStarted)
        {
            return;
        }

        _isStarted = true;
        _editor.ActiveDocumentChanged += OnActiveDocumentChanged;
        _session.PendingChangesChanged += OnSessionChanged;
        _session.StateChanged += OnSessionStateChanged;
        SyncDocumentSubscriptions();
        _ = UpdateActiveDocumentStateAsync(_editor.ActiveDocument?.FilePath);
    }

    /// <summary>Determines the version control state of a local file; results are cached until the pending changes change.</summary>
    public Task<TfvcFileState> GetStateAsync(string localPath, CancellationToken cancellationToken)
    {
        if (_session.IsOffline)
        {
            TfvcWorkspace? workspace = _session.FindWorkspaceForLocalPath(localPath);
            string? serverPath = workspace?.TryGetServerPath(localPath);
            return Task.FromResult(workspace is null || serverPath is null
                ? TfvcFileState.NotInWorkspace
                : new TfvcFileState(TfvcFileStateKind.Offline, workspace, serverPath, null));
        }

        if (!_session.IsConnected)
        {
            return Task.FromResult(TfvcFileState.Unknown);
        }

        lock (_gate)
        {
            if (_states.TryGetValue(localPath, out Task<TfvcFileState>? cached) && !cached.IsFaulted && !cached.IsCanceled)
            {
                return cached;
            }

            Task<TfvcFileState> evaluation = EvaluateAsync(localPath, cancellationToken);
            _states[localPath] = evaluation;
            return evaluation;
        }
    }

    /// <summary>Handles a text change of an editor document; checks the file out when it is the first modification.</summary>
    public async Task HandleDocumentChangedAsync(IEditorDocument document, CancellationToken cancellationToken)
    {
        string path = document.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_session.IsOffline)
        {
            await HandleOfflineChangeAsync(document, path, cancellationToken);
            return;
        }

        lock (_gate)
        {
            if (_checkOutsInProgress.Contains(path) || _failedCheckOuts.Contains(path))
            {
                return;
            }
        }

        TfvcFileState state = await GetStateAsync(path, cancellationToken);
        if (state.Kind != TfvcFileStateKind.CheckedIn || state.Workspace is null)
        {
            return;
        }

        lock (_gate)
        {
            if (!_checkOutsInProgress.Add(path))
            {
                return;
            }
        }

        try
        {
            if (await IsUnchangedAsync(document, cancellationToken))
            {
                return;
            }

            await _session.Client.CheckOutAsync(state.Workspace, [path], recursive: false, cancellationToken);
            Invalidate(path);
            if (ReferenceEquals(state.Workspace, _session.CurrentWorkspace))
            {
                await _session.RefreshPendingChangesAsync(cancellationToken);
            }
            else
            {
                await UpdateActiveDocumentStateAsync(ActiveDocumentPath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_gate)
            {
                _failedCheckOuts.Add(path);
            }

            _editor.SetWriteProtectionManaged(path, false);
            _logger.Error(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogAutoCheckOutFailedFormat, path), ex);
            string message = string.Format(
                CultureInfo.CurrentCulture,
                TfvcStrings.AutoCheckOutFailedFormat,
                Path.GetFileName(path),
                TfvcDisplayText.DescribeError(ex));
            await _window.ShowErrorMessageAsync(message, CancellationToken.None);
        }
        finally
        {
            lock (_gate)
            {
                _checkOutsInProgress.Remove(path);
            }
        }
    }

    /// <summary>Discards the cached state of a file.</summary>
    public void Invalidate(string localPath)
    {
        lock (_gate)
        {
            _states.Remove(localPath);
            _failedCheckOuts.Remove(localPath);
        }
    }

    /// <summary>Forgets the cached state of every file in a folder and its subfolders.</summary>
    public void InvalidateFolder(string localFolder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(localFolder) + Path.DirectorySeparatorChar;
        lock (_gate)
        {
            foreach (string path in new List<string>(_states.Keys))
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    _states.Remove(path);
                }
            }

            _failedCheckOuts.RemoveWhere(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_isStarted)
        {
            _editor.ActiveDocumentChanged -= OnActiveDocumentChanged;
            _session.PendingChangesChanged -= OnSessionChanged;
            _session.StateChanged -= OnSessionStateChanged;
        }

        foreach (KeyValuePair<IEditorDocument, EventHandler<EditorDocumentChangedEventArgs>> entry in _documentHandlers)
        {
            entry.Key.Changed -= entry.Value;
        }

        _documentHandlers.Clear();
    }

    private async Task<TfvcFileState> EvaluateAsync(string localPath, CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.FindWorkspaceForLocalPath(localPath);
        string? serverPath = workspace?.TryGetServerPath(localPath);
        if (workspace is null || serverPath is null)
        {
            return TfvcFileState.NotInWorkspace;
        }

        IReadOnlyList<TfvcPendingChange> changes;
        try
        {
            changes = await _session.Client.GetPendingChangesForLocalPathAsync(localPath, cancellationToken);
        }
        catch (TfCommandException ex)
        {
            // tf status can fail temporarily (for example TF400324 while the server is busy).
            // The pending changes of the selected workspace are known from the panel query, and a
            // read-only file of a server workspace cannot have a pending edit.
            TfvcPendingChange? known = ReferenceEquals(workspace, _session.CurrentWorkspace)
                ? _session.FindPendingChangeByLocalPath(localPath)
                : null;
            if (known is not null)
            {
                return new TfvcFileState(TfvcFileStateKind.Pending, workspace, serverPath, known);
            }

            if (!IsReadOnlyFile(localPath))
            {
                throw;
            }

            _logger.Warn(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogStatusFallbackFormat, localPath, ex.Message));
            changes = Array.Empty<TfvcPendingChange>();
        }

        foreach (TfvcPendingChange change in changes)
        {
            if (string.Equals(change.ServerPath, serverPath, StringComparison.OrdinalIgnoreCase)
                || (change.LocalPath is not null && string.Equals(change.LocalPath, localPath, StringComparison.OrdinalIgnoreCase)))
            {
                return new TfvcFileState(TfvcFileStateKind.Pending, workspace, serverPath, change);
            }
        }

        string? collectionUrl = _session.Settings.CollectionUrl;
        if (string.IsNullOrWhiteSpace(collectionUrl))
        {
            return TfvcFileState.Unknown;
        }

        try
        {
            TfvcServerItem? item = await _session.Server.GetItemAsync(collectionUrl, serverPath, cancellationToken);
            return new TfvcFileState(
                item is null ? TfvcFileStateKind.NotVersioned : TfvcFileStateKind.CheckedIn,
                workspace,
                serverPath,
                null);
        }
        catch (TfvcServerException ex)
        {
            // Without server access, a read-only file in a mapped folder is treated as a
            // checked-in file of a server workspace.
            _logger.Warn(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogItemLookupFailedFormat, serverPath, ex.Message));
            return IsReadOnlyFile(localPath)
                ? new TfvcFileState(TfvcFileStateKind.CheckedIn, workspace, serverPath, null)
                : new TfvcFileState(TfvcFileStateKind.Unknown, workspace, serverPath, null);
        }
    }

    // Offline, a read-only mapped file cannot be checked out; after confirmation its write protection is removed and
    // the file is remembered, so going online proposes it as an edit.
    private async Task HandleOfflineChangeAsync(IEditorDocument document, string path, CancellationToken cancellationToken)
    {
        if (_dialogHost is null || _session.FindWorkspaceForLocalPath(path) is null || !IsReadOnlyFile(path))
        {
            return;
        }

        lock (_gate)
        {
            if (_declinedOfflineEdits.Contains(path) || !_checkOutsInProgress.Add(path))
            {
                return;
            }
        }

        string fileName = Path.GetFileName(path);
        try
        {
            if (await IsUnchangedAsync(document, cancellationToken))
            {
                return;
            }

            bool confirmed = await _dialogHost.ShowDialogAsync<bool>(
                ConfirmationDialogRequest.DIALOG_ID,
                new ConfirmationDialogRequest(
                    TfvcStrings.OfflineEditTitle,
                    string.Format(CultureInfo.CurrentCulture, TfvcStrings.OfflineEditMessageFormat, fileName),
                    TfvcStrings.OfflineEditConfirm),
                CancellationToken.None);
            if (!confirmed)
            {
                lock (_gate)
                {
                    _declinedOfflineEdits.Add(path);
                }

                return;
            }

            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            await _session.RecordOfflineEditAsync(path, cancellationToken);
            _logger.Info(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogWriteProtectionRemovedFormat, path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogWriteProtectionRemovalFailedFormat, path), ex);
            await _window.ShowErrorMessageAsync(
                string.Format(CultureInfo.CurrentCulture, TfvcStrings.OfflineEditFailedFormat, fileName, ex.Message),
                CancellationToken.None);
        }
        finally
        {
            lock (_gate)
            {
                _checkOutsInProgress.Remove(path);
            }
        }
    }

    private async Task<bool> IsUnchangedAsync(IEditorDocument document, CancellationToken cancellationToken)
    {
        string? diskText = _readFile(document.FilePath);
        if (diskText is null)
        {
            return false;
        }

        string editorText = await document.GetTextAsync(cancellationToken);
        return string.Equals(NormalizeText(editorText), NormalizeText(diskText), StringComparison.Ordinal);
    }

    private static string NormalizeText(string text)
    {
        return text.TrimStart(ByteOrderMark).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool IsReadOnlyFile(string path)
    {
        try
        {
            return File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void OnActiveDocumentChanged(object? sender, EditorActiveDocumentChangedEventArgs e)
    {
        // Activating a document again retries a failed check-out with a fresh state.
        if (e.Document?.FilePath is { Length: > 0 } activePath)
        {
            lock (_gate)
            {
                if (_failedCheckOuts.Remove(activePath))
                {
                    _states.Remove(activePath);
                }
            }
        }

        SyncDocumentSubscriptions();
        _ = UpdateActiveDocumentStateAsync(e.Document?.FilePath);
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            _states.Clear();
            _failedCheckOuts.Clear();
        }

        _ = UpdateActiveDocumentStateAsync(ActiveDocumentPath);
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        if (_session.IsBusy)
        {
            return;
        }

        bool isOffline = _session.IsOffline;
        if (isOffline != _wasOffline)
        {
            _wasOffline = isOffline;
            lock (_gate)
            {
                _states.Clear();
                _failedCheckOuts.Clear();
                _declinedOfflineEdits.Clear();
            }

            _ = UpdateActiveDocumentStateAsync(ActiveDocumentPath);
            return;
        }

        // A new connection or workspace list invalidates mapping based states.
        if (ActiveDocumentState.Kind == TfvcFileStateKind.Unknown && _session.IsConnected && ActiveDocumentPath is not null)
        {
            Invalidate(ActiveDocumentPath);
            _ = UpdateActiveDocumentStateAsync(ActiveDocumentPath);
        }
    }

    private void SyncDocumentSubscriptions()
    {
        IReadOnlyList<IEditorDocument> openDocuments = _editor.GetOpenDocuments();
        HashSet<IEditorDocument> open = new(openDocuments);
        List<IEditorDocument> closed = new();
        foreach (IEditorDocument document in _documentHandlers.Keys)
        {
            if (!open.Contains(document))
            {
                closed.Add(document);
            }
        }

        foreach (IEditorDocument document in closed)
        {
            document.Changed -= _documentHandlers[document];
            _documentHandlers.Remove(document);
        }

        foreach (IEditorDocument document in openDocuments)
        {
            if (_documentHandlers.ContainsKey(document))
            {
                continue;
            }

            EventHandler<EditorDocumentChangedEventArgs> handler = (_, _) => OnDocumentChanged(document);
            document.Changed += handler;
            _documentHandlers[document] = handler;
        }
    }

    private async void OnDocumentChanged(IEditorDocument document)
    {
        try
        {
            await HandleDocumentChangedAsync(document, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(TfvcStrings.LogChangeHandlingFailed, ex);
        }
    }

    private async Task UpdateActiveDocumentStateAsync(string? path, bool allowRetry = true)
    {
        int generation = Interlocked.Increment(ref _stateGeneration);
        ActiveDocumentPath = path;
        if (string.IsNullOrWhiteSpace(path))
        {
            PublishActiveState(generation, TfvcFileState.Unknown, isChecking: false);
            return;
        }

        PublishActiveState(generation, ActiveDocumentState, isChecking: true);
        TfvcFileState state;
        try
        {
            state = await GetStateAsync(path, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(string.Format(CultureInfo.CurrentCulture, TfvcStrings.LogStateLookupFailedFormat, path), ex);
            state = TfvcFileState.Unknown;
            if (allowRetry)
            {
                _ = RetryActiveDocumentStateAsync(path, generation);
            }
        }

        _logger.Info(path + ": " + state.Kind + (state.ServerPath is null ? string.Empty : " (" + state.ServerPath + ")"));
        PublishActiveState(generation, state, isChecking: false);
    }

    // Retries a failed lookup once, as long as the document is still active and nothing else refreshed its state.
    private async Task RetryActiveDocumentStateAsync(string path, int generation)
    {
        await Task.Delay(_retryDelay);
        if (_isDisposed || generation != Volatile.Read(ref _stateGeneration)
            || !string.Equals(path, ActiveDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Invalidate(path);
        await UpdateActiveDocumentStateAsync(path, allowRetry: false);
    }

    private void PublishActiveState(int generation, TfvcFileState state, bool isChecking)
    {
        if (generation != Volatile.Read(ref _stateGeneration))
        {
            return;
        }

        ActiveDocumentState = state;
        IsCheckingActiveDocument = isChecking;
        if (!isChecking && ActiveDocumentPath is { } path)
        {
            // A connected workspace checks a versioned file out on its first edit; offline the user has to confirm.
            bool managed;
            lock (_gate)
            {
                managed = state.Kind is TfvcFileStateKind.CheckedIn or TfvcFileStateKind.Pending && !_failedCheckOuts.Contains(path);
            }
            _editor.SetWriteProtectionManaged(path, managed);
        }
        ActiveDocumentStateChanged?.Invoke(this, EventArgs.Empty);
    }
}
