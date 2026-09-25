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

    private void UpdateReadOnlyState()
    {
        IsReadOnlyFile = _readOnlyMonitor?.IsReadOnly ?? false;
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
}
