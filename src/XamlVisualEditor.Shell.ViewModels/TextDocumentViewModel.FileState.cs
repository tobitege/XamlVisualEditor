using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class TextDocumentViewModel
{
    private readonly SemaphoreSlim _fileSaveGate = new(1, 1);
    private Encoding _fileEncoding = new UTF8Encoding(false, true);
    private byte[]? _persistedBytes;
    private string _persistedText = string.Empty;

    private async Task<string> ReadDocumentFileAsync()
    {
        byte[] bytes = await File.ReadAllBytesAsync(FilePath);
        using MemoryStream stream = new(bytes, writable: false);
        using StreamReader reader = new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text = await reader.ReadToEndAsync();
        _fileEncoding = reader.CurrentEncoding;
        _persistedBytes = bytes;
        _persistedText = text;
        return text;
    }

    private void UpdateFileModifiedState()
    {
        IsModified = !string.Equals(Document.Text, _persistedText, StringComparison.Ordinal);
    }

    private async Task SaveDocumentFileAsync()
    {
        await _fileSaveGate.WaitAsync();
        try
        {
            string text = Document.Text;
            byte[] preamble = _fileEncoding.GetPreamble();
            byte[] content = _fileEncoding.GetBytes(text);
            byte[] bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);

            // Keep the comparison and write under the same file lock.
            FileMode mode = _persistedBytes is null ? FileMode.CreateNew : FileMode.Open;
            await using FileStream stream = new(FilePath, mode, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous);
            if (_persistedBytes is not null)
            {
                if (stream.Length != _persistedBytes.Length)
                {
                    throw new IOException($"The file changed outside the editor. Reload it before saving: {FilePath}");
                }

                byte[] currentBytes = new byte[_persistedBytes.Length];
                await stream.ReadExactlyAsync(currentBytes);
                if (!currentBytes.AsSpan().SequenceEqual(_persistedBytes))
                {
                    throw new IOException($"The file changed outside the editor. Reload it before saving: {FilePath}");
                }

                if (currentBytes.AsSpan().SequenceEqual(bytes))
                {
                    UpdateFileModifiedState();
                    return;
                }
            }

            stream.Position = 0;
            await stream.WriteAsync(bytes);
            stream.SetLength(bytes.Length);
            await stream.FlushAsync();
            _persistedBytes = bytes;
            _persistedText = text;
            UpdateFileModifiedState();
        }
        finally
        {
            _fileSaveGate.Release();
        }
    }
}
