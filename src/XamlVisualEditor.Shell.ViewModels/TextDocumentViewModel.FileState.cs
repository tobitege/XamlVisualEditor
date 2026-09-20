using System.Threading.Tasks;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class TextDocumentViewModel
{
    private readonly DocumentFileState _fileState = new();

    private Task<string> ReadDocumentFileAsync() => _fileState.ReadAsync(FilePath);

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
