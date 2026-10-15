using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;

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
            Encoding encoding = DetectStrictEncoding(bytes);
            int preambleLength = encoding.GetPreamble().Length;
            string text = encoding.GetString(bytes, preambleLength, bytes.Length - preambleLength);
            cancellationToken.ThrowIfCancellationRequested();
            _encoding = encoding;
            _persistedBytes = bytes;
            _persistedText = text;
            return text;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    // Every decoder throws on invalid bytes, also behind a byte order mark, so a file is never
    // opened with replaced characters that a later save would write back.
    private static Encoding DetectStrictEncoding(byte[] bytes) => bytes switch
    {
        [0xEF, 0xBB, 0xBF, ..] => new UTF8Encoding(true, true),
        [0xFF, 0xFE, 0x00, 0x00, ..] => new UTF32Encoding(false, true, true),
        [0x00, 0x00, 0xFE, 0xFF, ..] => new UTF32Encoding(true, true, true),
        [0xFF, 0xFE, ..] => new UnicodeEncoding(false, true, true),
        [0xFE, 0xFF, ..] => new UnicodeEncoding(true, true, true),
        _ => new UTF8Encoding(false, true)
    };

    /// <summary>
    /// Writes the text with the encoding the file was read with. Throws <see cref="FileChangedOutsideEditorException"/>
    /// when the file on disk no longer holds the bytes last read or written, unless <paramref name="overwrite"/> is set.
    /// </summary>
    public async Task SaveAsync(string filePath, string text, bool overwrite = false)
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
            FileMode mode = _persistedBytes is null ? FileMode.CreateNew
                : overwrite ? FileMode.OpenOrCreate : FileMode.Open;
            await using FileStream stream = new(filePath, mode, FileAccess.ReadWrite, FileShare.Read,
                bufferSize: 4096, options: FileOptions.Asynchronous);
            if (_persistedBytes is not null && !overwrite)
            {
                if (stream.Length != _persistedBytes.Length)
                {
                    throw new FileChangedOutsideEditorException(filePath);
                }

                byte[] currentBytes = new byte[_persistedBytes.Length];
                await stream.ReadExactlyAsync(currentBytes);
                if (!currentBytes.AsSpan().SequenceEqual(_persistedBytes))
                {
                    throw new FileChangedOutsideEditorException(filePath);
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

/// <summary>Reports that a file changed on disk after the editor read or last saved it.</summary>
internal sealed class FileChangedOutsideEditorException(string filePath)
    : IOException(string.Format(UiStrings.FileChangedOutsideEditor, Path.GetFileName(filePath)));
