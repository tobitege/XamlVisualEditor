using System.IO;
using System.Text;

namespace XamlVisualEditor.Core;

/// <summary>Reads small settings and state files that other processes may keep open for writing.</summary>
/// <remarks>
/// <see cref="File.ReadAllText(string)"/> only shares read access and therefore fails with a sharing
/// violation while any other handle holds write access, for example a backup or scanning service.
/// Reading a file does not need to exclude writers, so these readers share read, write and delete access.
/// </remarks>
public static class SharedFileReader
{
    /// <summary>Reads a text file while allowing other handles to read, write or delete it.</summary>
    public static string ReadAllText(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
