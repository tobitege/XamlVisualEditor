using System;
using System.Reactive.Linq;
using System.Threading.Tasks;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DesignerDocumentViewModel
{
    private readonly DocumentFileState _fileState = new();

    private void InitializeDesignerFileState()
    {
        _disposables.Add(Observable.FromEventPattern<EventHandler, EventArgs>(
                handler => CodeEditor.Document.TextChanged += handler,
                handler => CodeEditor.Document.TextChanged -= handler)
            .Subscribe(_ => UpdateFileModifiedState()));
    }

    private void UpdateFileModifiedState()
    {
        bool modified = _fileState.IsModified(CodeEditor.Document.Text);
        IsModified = modified;
        CodeEditor.IsModified = modified;
    }

    private async Task SaveDocumentFileAsync()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsLoading) throw new InvalidOperationException("Wait until the document finishes loading before saving.");
        CodeEditor.FlushPendingTextChanges();
        string text = await SyncEngine.SaveAsync();
        await _fileState.SaveAsync(FilePath, text);
        UpdateFileModifiedState();
    }
}
