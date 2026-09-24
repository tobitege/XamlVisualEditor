using System.Text.Json;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.TextEditingExtension.Services;

/// <summary>Line and column of the caret in a file.</summary>
public sealed record CaretPosition(string FilePath, int Line, int Column);

/// <summary>
/// Remembers the caret line and column per file and restores them when a file becomes active for the first time.
/// Changes are collected in memory and written together after a short delay, away from the UI thread.
/// </summary>
public sealed class CaretPositionMemory : IDisposable
{
    private const string POSITIONS_KEY = "textEditing.caretPositions";
    private const int MAX_POSITIONS = 200;
    private static readonly TimeSpan DefaultSaveDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShutdownSaveTimeout = TimeSpan.FromSeconds(2);
    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly IEditorServices _editor;
    private readonly IExtensionStorage _storage;
    private readonly IExtensionLogger _logger;
    private readonly TimeSpan _saveDelay;
    private readonly object _gate = new();

    // Keyed by the full path; the first entry is the most recent one.
    private readonly OrderedDictionary<string, CaretPosition> _positions = new(PathComparer);
    private readonly HashSet<string> _restored = new(PathComparer);
    private readonly CancellationTokenSource _shutdown = new();
    private Task _pendingSave = Task.CompletedTask;
    private bool _saveScheduled;
    private IEditorDocument? _active;
    private IEditorDocument? _pendingRestore;
    private bool _disposed;

    /// <summary>Creates the memory; <see cref="LoadAsync"/> reads the saved positions.</summary>
    public CaretPositionMemory(IEditorServices editor, IExtensionStorage storage, IExtensionLogger logger, TimeSpan? saveDelay = null)
    {
        _editor = editor;
        _storage = storage;
        _logger = logger;
        _saveDelay = saveDelay ?? DefaultSaveDelay;
    }

    /// <summary>Gets the remembered positions, the most recent first.</summary>
    public IReadOnlyList<CaretPosition> Positions
    {
        get
        {
            lock (_gate)
            {
                return _positions.Values.ToList();
            }
        }
    }

    /// <summary>Reads the saved positions and starts following the active document.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<CaretPosition>? saved = await _storage.GetAsync<List<CaretPosition>>(POSITIONS_KEY, cancellationToken)
                .ConfigureAwait(true);
            lock (_gate)
            {
                foreach (CaretPosition position in (saved ?? new()).Where(item => !string.IsNullOrWhiteSpace(item.FilePath)))
                {
                    _positions.TryAdd(GetKey(position.FilePath), position);
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Warn("Saved caret positions are unreadable: " + error.Message);
        }

        _editor.ActiveDocumentChanged += OnActiveDocumentChanged;
        await ActivateAsync(_editor.ActiveDocument).ConfigureAwait(true);
    }

    /// <summary>Waits for the save that is scheduled or running; the next change schedules a new one.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            return _pendingSave;
        }
    }

    /// <summary>Keeps the caret of the active document and writes the positions; shutdown waits a short time for it.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _editor.ActiveDocumentChanged -= OnActiveDocumentChanged;
        StopPendingRestore();
        _shutdown.Cancel();
        try
        {
            Task remember = _active is null ? Task.CompletedTask : RememberAsync(_active);
            Task save = remember.ContinueWith(_ => SaveNowAsync(), TaskScheduler.Default).Unwrap();
            if (!save.Wait(ShutdownSaveTimeout))
            {
                _logger.Warn("The caret positions were not saved before shutdown.");
            }
        }
        catch (Exception error)
        {
            _logger.Error("Saving the caret positions failed.", error);
        }
        finally
        {
            _shutdown.Dispose();
        }
    }

    // A caret position is a convenience: a failure is logged and never reaches the dispatcher.
    private async void OnActiveDocumentChanged(object? sender, EditorActiveDocumentChangedEventArgs e)
    {
        try
        {
            await ActivateAsync(e.Document).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.Error("Restoring the caret position failed.", error);
        }
    }

    private async void OnPendingDocumentChanged(object? sender, EditorDocumentChangedEventArgs e)
    {
        if (_pendingRestore is not { } document)
        {
            return;
        }

        StopPendingRestore();
        try
        {
            await RestoreAsync(document).ConfigureAwait(true);
        }
        catch (Exception error)
        {
            _logger.Error("Restoring the caret position failed.", error);
        }
    }

    private async Task ActivateAsync(IEditorDocument? document)
    {
        if (_disposed)
        {
            return;
        }

        IEditorDocument? previous = _active;
        _active = document;
        StopPendingRestore();
        if (previous is not null && !ReferenceEquals(previous, document))
        {
            await RememberAsync(previous).ConfigureAwait(true);
            ScheduleSave();
        }

        if (document is null || string.IsNullOrWhiteSpace(document.FilePath) || !_restored.Add(GetKey(document.FilePath)))
        {
            return;
        }

        if (!await RestoreAsync(document).ConfigureAwait(true))
        {
            // A document that is still loading has no text yet; the first change brings it.
            _pendingRestore = document;
            document.Changed += OnPendingDocumentChanged;
        }
    }

    private void StopPendingRestore()
    {
        if (_pendingRestore is not null)
        {
            _pendingRestore.Changed -= OnPendingDocumentChanged;
            _pendingRestore = null;
        }
    }

    // Returns false while the document has no text to place the caret in.
    private async Task<bool> RestoreAsync(IEditorDocument document)
    {
        CaretPosition? position;
        lock (_gate)
        {
            _positions.TryGetValue(GetKey(document.FilePath), out position);
        }

        if (position is null)
        {
            return true;
        }

        string text = await document.GetTextAsync(CancellationToken.None).ConfigureAwait(true);
        if (text.Length == 0)
        {
            return false;
        }

        if (document.CaretOffset != 0 || document.SelectionLength != 0)
        {
            return true;
        }

        int lineStart = TextEditOperations.GetLineStartOffset(text, position.Line);
        int lineEnd = lineStart;
        while (lineEnd < text.Length && text[lineEnd] is not ('\r' or '\n'))
        {
            lineEnd++;
        }

        int caret = Math.Min(lineStart + Math.Max(0, position.Column - 1), lineEnd);
        document.SetSelection(caret, 0, caret);
        return true;
    }

    private async Task RememberAsync(IEditorDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.FilePath))
        {
            return;
        }

        int caret = document.CaretOffset;
        string text = await document.GetTextAsync(CancellationToken.None).ConfigureAwait(false);
        (int line, int column) = TextEditOperations.GetLineAndColumn(text, caret);
        string key = GetKey(document.FilePath);
        lock (_gate)
        {
            _positions.Remove(key);
            _positions.Insert(0, key, new CaretPosition(document.FilePath, line, column));
            while (_positions.Count > MAX_POSITIONS)
            {
                _positions.RemoveAt(_positions.Count - 1);
            }
        }
    }

    // One save after the delay covers every change until then.
    private void ScheduleSave()
    {
        lock (_gate)
        {
            if (_saveScheduled || _disposed)
            {
                return;
            }

            _saveScheduled = true;
            Task previous = _pendingSave;
            CancellationToken shutdown = _shutdown.Token;
            _pendingSave = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                try
                {
                    await Task.Delay(_saveDelay, shutdown).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await SaveNowAsync().ConfigureAwait(false);
            });
        }
    }

    private async Task SaveNowAsync()
    {
        List<CaretPosition> snapshot;
        lock (_gate)
        {
            _saveScheduled = false;
            snapshot = _positions.Values.ToList();
        }

        try
        {
            await _storage.SetAsync(POSITIONS_KEY, snapshot, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            _logger.Error("Saving the caret positions failed.", error);
        }
    }

    private static string GetKey(string filePath)
    {
        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return filePath;
        }
    }
}
