using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Core;

/// <summary>Describes file system failures in the current UI language instead of the system message.</summary>
public static class FileSystemErrorMessages
{
    private const int ErrorSharingViolation = unchecked((int)0x80070020);
    private const int ErrorLockViolation = unchecked((int)0x80070021);

    /// <summary>
    /// Finds the read-only entry that denied access for an operation on <paramref name="path"/>: the path itself or,
    /// for a folder, the first read-only entry below it. The name is relative to the folder containing the path.
    /// </summary>
    public static bool TryGetReadOnlyEntryName(Exception error, string path, [NotNullWhen(true)] out string? entryName)
    {
        entryName = null;
        if (error is not UnauthorizedAccessException)
        {
            return false;
        }

        string? entryPath = FindReadOnlyEntry(path);
        if (entryPath is null)
        {
            return false;
        }

        string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        entryName = string.IsNullOrEmpty(parent) ? entryPath : Path.GetRelativePath(parent, entryPath);
        return true;
    }

    /// <summary>Describes why an operation on <paramref name="path"/> failed; unknown failures keep their message.</summary>
    public static string DescribeReason(Exception error, string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if (TryGetReadOnlyEntryName(error, path, out string? readOnlyName))
        {
            return string.Format(UiStrings.FileIsReadOnly, readOnlyName);
        }

        return error switch
        {
            UnauthorizedAccessException => string.Format(UiStrings.FileAccessDenied, name),
            _ when IsInUse(error) => string.Format(UiStrings.FileInUse, name),
            _ => error.Message
        };
    }

    /// <summary>Tells whether another process holds the file open in a way that refused the operation.</summary>
    internal static bool IsInUse(Exception error) =>
        error is IOException { HResult: ErrorSharingViolation or ErrorLockViolation };

    private static string? FindReadOnlyEntry(string path)
    {
        try
        {
            FileSystemInfo root = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!root.Exists)
            {
                return null;
            }

            if (root.Attributes.HasFlag(FileAttributes.ReadOnly))
            {
                return root.FullName;
            }

            if (root is not DirectoryInfo directory)
            {
                return null;
            }

            EnumerationOptions options = new()
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0
            };
            return directory.EnumerateFileSystemInfos("*", options)
                .FirstOrDefault(entry => entry.Attributes.HasFlag(FileAttributes.ReadOnly))?.FullName;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
