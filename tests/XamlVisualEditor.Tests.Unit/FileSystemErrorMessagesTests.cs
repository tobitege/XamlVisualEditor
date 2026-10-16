using System;
using System.IO;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class FileSystemErrorMessagesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-FileErrors-" + Guid.NewGuid().ToString("N"));

    public FileSystemErrorMessagesTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void DeletingReadOnlyFile_NamesTheFile()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnlyReason);
        string path = CreateReadOnlyFile(_root, "Locked.tmp");

        UnauthorizedAccessException error = Assert.Throws<UnauthorizedAccessException>(() => File.Delete(path));

        Assert.True(FileSystemErrorMessages.TryGetReadOnlyEntryName(error, path, out string? name));
        Assert.Equal("Locked.tmp", name);
        Assert.Equal(string.Format(UiStrings.FileIsReadOnly, "Locked.tmp"), FileSystemErrorMessages.DescribeReason(error, path));
    }

    [Fact]
    public void DeletingFolderWithReadOnlyFile_NamesTheFileBelowTheFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnlyReason);
        string folder = Path.Combine(_root, "Folder");
        string nested = Path.Combine(folder, "Nested");
        Directory.CreateDirectory(nested);
        CreateReadOnlyFile(nested, "Locked.tmp");

        UnauthorizedAccessException error = Assert.Throws<UnauthorizedAccessException>(
            () => Directory.Delete(folder, recursive: true));

        Assert.True(FileSystemErrorMessages.TryGetReadOnlyEntryName(error, folder, out string? name));
        Assert.Equal(Path.Combine("Folder", "Nested", "Locked.tmp"), name);
    }

    [Fact]
    public void DeletingFileInUse_DescribesTheLock()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WindowsOnlyReason);
        string path = Path.Combine(_root, "Open.txt");
        File.WriteAllText(path, "content");
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.None);

        IOException error = Assert.Throws<IOException>(() => File.Delete(path));

        Assert.False(FileSystemErrorMessages.TryGetReadOnlyEntryName(error, path, out _));
        Assert.Equal(string.Format(UiStrings.FileInUse, "Open.txt"), FileSystemErrorMessages.DescribeReason(error, path));
    }

    [Fact]
    public void UnknownFailure_KeepsItsMessage()
    {
        InvalidOperationException error = new("Custom failure");

        Assert.Equal("Custom failure", FileSystemErrorMessages.DescribeReason(error, Path.Combine(_root, "Any.txt")));
    }

    private const string WindowsOnlyReason =
        "Only Windows refuses to delete read-only or open files; other systems delete them.";

    private static string CreateReadOnlyFile(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        File.WriteAllText(path, "content");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    public void Dispose()
    {
        // Only the files this test created are made writable again, so the temporary folder can be removed.
        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
