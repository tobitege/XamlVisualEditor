using System;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private readonly DocumentFileState _fileState = new();
    private ReadOnlyFileMonitor? _readOnlyMonitor;

    private void InitializeDesignerFileState()
    {
        _disposables.Add(Observable.FromEventPattern<EventHandler, EventArgs>(
                handler => CodeEditor.Document.TextChanged += handler,
                handler => CodeEditor.Document.TextChanged -= handler)
            .Subscribe(_ => UpdateFileModifiedState()));

        _readOnlyMonitor = new ReadOnlyFileMonitor(FilePath,
            () => RxSchedulers.MainThreadScheduler.Schedule(UpdateReadOnlyState));
        _disposables.Add(_readOnlyMonitor);
        UpdateReadOnlyState();
    }

    private bool _isWriteProtectionManaged;

    /// <summary>Gets or sets whether source control lifts the write protection on the first edit, which makes the notice unnecessary.</summary>
    public bool IsWriteProtectionManaged
    {
        get => _isWriteProtectionManaged;
        set
        {
            this.RaiseAndSetIfChanged(ref _isWriteProtectionManaged, value);
            this.RaisePropertyChanged(nameof(ShowReadOnlyNotice));
        }
    }

    /// <summary>Gets whether the notice about a write-protected file is shown.</summary>
    public bool ShowReadOnlyNotice => IsReadOnlyFile && !IsWriteProtectionManaged;

    private void UpdateReadOnlyState()
    {
        IsReadOnlyFile = _readOnlyMonitor?.IsReadOnly ?? false;
        this.RaisePropertyChanged(nameof(ShowReadOnlyNotice));
    }

    private void UpdateFileModifiedState()
    {
        bool modified = _fileState.IsModified(CodeEditor.Document.Text);
        IsModified = modified;
        CodeEditor.IsModified = modified;
    }

    private Task SaveDocumentFileAsync() => WriteDocumentFileAsync(overwrite: false);

    /// <inheritdoc />
    public Task OverwriteFileAsync() => WriteDocumentFileAsync(overwrite: true);

    private async Task WriteDocumentFileAsync(bool overwrite)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsLoading) throw new InvalidOperationException("Wait until the document finishes loading before saving.");
        CodeEditor.FlushPendingTextChanges();
        string text = await SyncEngine.SaveAsync();
        await _fileState.SaveAsync(FilePath, text, overwrite);
        UpdateFileModifiedState();
    }

    /// <inheritdoc />
    /// <remarks>The disk text enters the document like typed text, so the reload can be undone.</remarks>
    public async Task ReloadFromDiskAsync()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsLoading) throw new InvalidOperationException("Wait until the document finishes loading before reloading.");
        string text = await _fileState.ReadAsync(FilePath);
        CodeEditor.Document.Text = text;
        CodeEditor.FlushPendingTextChanges();
        UpdateFileModifiedState();
    }
}
