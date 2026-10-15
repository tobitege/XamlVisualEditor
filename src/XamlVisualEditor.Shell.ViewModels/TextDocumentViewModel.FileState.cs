using ReactiveUI.Primitives.Concurrency;
using System.Reactive.Concurrency;
using System.Threading.Tasks;
using ReactiveUI;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class TextDocumentViewModel
{
    private readonly DocumentFileState _fileState = new();
    private ReadOnlyFileMonitor? _readOnlyMonitor;

    private Task<string> ReadDocumentFileAsync() => _fileState.ReadAsync(FilePath);

    private void InitializeReadOnlyState()
    {
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
        IsModified = _fileState.IsModified(Document.Text);
    }

    private async Task SaveDocumentFileAsync()
    {
        await _fileState.SaveAsync(FilePath, Document.Text);
        UpdateFileModifiedState();
    }

    /// <inheritdoc />
    public async Task OverwriteFileAsync()
    {
        await _fileState.SaveAsync(FilePath, Document.Text, overwrite: true);
        UpdateFileModifiedState();
    }

    /// <inheritdoc />
    public async Task ReloadFromDiskAsync()
    {
        string text = await _fileState.ReadAsync(FilePath);
        Document.Text = text;
        UpdateFileModifiedState();
    }
}
