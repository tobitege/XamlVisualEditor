using System;
using System.IO;

namespace XamlVisualEditor.Core;

/// <summary>
/// Accepts only fully qualified local paths in the canonical syntax of the current operating system.
/// A path that fails the check is rejected, never repaired, so that a crafted path cannot reach the file system.
/// </summary>
public static class FileSystemPathValidation
{
    private const char WINDOWS_SEPARATOR = '\\';

    /// <summary>Returns whether a path is accepted on the current operating system.</summary>
    public static bool IsAccepted(string? path) =>
        OperatingSystem.IsWindows() ? IsAcceptedWindowsPath(path) : IsAcceptedUnixPath(path);

    /// <summary>
    /// Returns whether a path is a drive path such as <c>C:\folder\file.txt</c> or a UNC path such as
    /// <c>\\server\share\file.txt</c>. Forward slashes, relative, root-relative (<c>\folder</c>) and
    /// drive-relative (<c>C:folder</c>) paths, device paths (<c>\\?\</c>, <c>\\.\</c>), empty segments,
    /// <c>.</c> and <c>..</c> segments and characters that Windows does not allow in names are rejected.
    /// </summary>
    public static bool IsAcceptedWindowsPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('/') || ContainsControlCharacter(path))
            return false;

        ReadOnlySpan<char> segments;
        if (path.Length >= 3 && IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == WINDOWS_SEPARATOR)
        {
            segments = path.AsSpan(3);
            if (segments.IsEmpty)
                return true;
        }
        else if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> unc = path.AsSpan(2);
            int serverEnd = unc.IndexOf(WINDOWS_SEPARATOR);
            // IsValidWindowsName also rejects the device prefixes "\\?\" and "\\.\".
            if (serverEnd <= 0 || !IsValidWindowsName(unc[..serverEnd]))
                return false;

            ReadOnlySpan<char> afterServer = unc[(serverEnd + 1)..];
            int shareEnd = afterServer.IndexOf(WINDOWS_SEPARATOR);
            ReadOnlySpan<char> share = shareEnd < 0 ? afterServer : afterServer[..shareEnd];
            if (!IsValidWindowsName(share))
                return false;
            if (shareEnd < 0)
                return true;

            segments = afterServer[(shareEnd + 1)..];
            if (segments.IsEmpty)
                return true;
        }
        else
        {
            return false;
        }

        // A single trailing separator is allowed for folders; every other segment must be a valid name.
        if (segments[^1] == WINDOWS_SEPARATOR)
            segments = segments[..^1];

        foreach (Range range in segments.Split(WINDOWS_SEPARATOR))
        {
            if (!IsValidWindowsName(segments[range]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Returns whether a path is a fully qualified Unix path without empty, <c>.</c> or <c>..</c> segments
    /// and without control characters.
    /// </summary>
    public static bool IsAcceptedUnixPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '/' || ContainsControlCharacter(path))
            return false;

        ReadOnlySpan<char> segments = path.AsSpan(1);
        if (segments.IsEmpty)
            return true;
        if (segments[^1] == '/')
            segments = segments[..^1];

        foreach (Range range in segments.Split('/'))
        {
            ReadOnlySpan<char> segment = segments[range];
            if (segment.IsEmpty || segment is "." or "..")
                return false;
        }

        return true;
    }

    private static bool IsValidWindowsName(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name is "." or "..")
            return false;

        // Windows strips a trailing dot or space, so such a name would address a different file.
        if (name[^1] is '.' or ' ')
            return false;

        return name.IndexOfAny("<>:\"|?*") < 0;
    }

    private static bool ContainsControlCharacter(string path)
    {
        foreach (char character in path)
        {
            if (char.IsControl(character))
                return true;
        }

        return false;
    }

    private static bool IsAsciiLetter(char character) => character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}
