using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Shell.ViewModels;

internal sealed class DocumentFileState
{
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private Encoding _encoding = new UTF8Encoding(false, true);
    private byte[]? _persistedBytes;
    private string _persistedText = string.Empty;

    public bool IsModified(string text) => !string.Equals(text, _persistedText, StringComparison.Ordinal);

    public async Task<string> ReadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            using MemoryStream stream = new(bytes, writable: false);
            using StreamReader reader = new(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string text = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            _encoding = reader.CurrentEncoding;
            _persistedBytes = bytes;
            _persistedText = text;
            return text;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task SaveAsync(string filePath, string text)
    {
        await _fileGate.WaitAsync();
        try
        {
            byte[] preamble = _encoding.GetPreamble();
            byte[] content = _encoding.GetBytes(text);
            byte[] bytes = new byte[preamble.Length + content.Length];
            preamble.CopyTo(bytes, 0);
            content.CopyTo(bytes, preamble.Length);

            // Keep the comparison and write under the same file lock.
            FileMode mode = _persistedBytes is null ? FileMode.CreateNew : FileMode.Open;
            await using FileStream stream = new(filePath, mode, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous);
            if (_persistedBytes is not null)
            {
                if (stream.Length != _persistedBytes.Length)
                {
                    throw new IOException($"The file changed outside the editor. Reload it before saving: {filePath}");
                }

                byte[] currentBytes = new byte[_persistedBytes.Length];
                await stream.ReadExactlyAsync(currentBytes);
                if (!currentBytes.AsSpan().SequenceEqual(_persistedBytes))
                {
                    throw new IOException($"The file changed outside the editor. Reload it before saving: {filePath}");
                }

                if (currentBytes.AsSpan().SequenceEqual(bytes))
                {
                    _persistedText = text;
                    return;
                }
            }

            stream.Position = 0;
            await stream.WriteAsync(bytes);
            stream.SetLength(bytes.Length);
            await stream.FlushAsync();
            _persistedBytes = bytes;
            _persistedText = text;
        }
        finally
        {
            _fileGate.Release();
        }
    }
}
