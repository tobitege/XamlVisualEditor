using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class BlockedFileMoveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-blocked-move-" + Guid.NewGuid().ToString("N"));

    // A virus scanner or search indexer opens a freshly written file for a moment without allowing it to be
    // replaced; Windows then refuses the move that replaces it.
    [Fact]
    public async Task Extension_Storage_Saves_While_Another_Process_Briefly_Holds_The_File()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that another handle holds open.");
        FileExtensionStorage storage = new("test.extension", _root);
        await storage.SetAsync("state", "first", CancellationToken.None);
        string path = Assert.Single(Directory.GetFiles(_root, "*.json", SearchOption.AllDirectories));

        Task saving;
        using (FileStream scanner = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            saving = Task.Run(() => storage.SetAsync("state", "second", CancellationToken.None));
            await Task.Delay(300);
            Assert.False(saving.IsCompleted);
        }

        await saving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("second", await storage.GetAsync<string>("state", CancellationToken.None));
        Assert.Empty(Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_Directory_Moves_Once_Another_Process_Releases_A_File_In_It()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to move a directory with an open file.");
        string source = Path.Combine(_root, "source");
        string destination = Path.Combine(_root, "destination");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "file.txt"), "content");

        Task moving;
        using (FileStream scanner = new(Path.Combine(source, "file.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            moving = Task.Run(() => FileSystemMoves.MoveDirectory(source, destination));
            await Task.Delay(300);
            Assert.False(moving.IsCompleted);
        }

        await moving.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("content", File.ReadAllText(Path.Combine(destination, "file.txt")));
        Assert.False(Directory.Exists(source));
    }

    // Waiting never frees a target that may not be replaced, so these moves fail at once on every platform.
    [Fact]
    public void A_Target_That_Waiting_Never_Frees_Fails_At_Once()
    {
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.txt");
        string existing = Path.Combine(_root, "existing.txt");
        string readOnly = Path.Combine(_root, "read-only.txt");
        string directory = Path.Combine(_root, "directory");
        File.WriteAllText(source, "source");
        File.WriteAllText(existing, "existing");
        File.WriteAllText(readOnly, "read-only");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        Directory.CreateDirectory(directory);
        Stopwatch elapsed = Stopwatch.StartNew();

        Assert.ThrowsAny<IOException>(() => FileSystemMoves.MoveFile(source, existing));
        Assert.ThrowsAny<IOException>(() => FileSystemMoves.MoveDirectory(directory, existing));
        Assert.ThrowsAny<FileNotFoundException>(() => FileSystemMoves.MoveFile(Path.Combine(_root, "missing.txt"), existing, overwrite: true));
        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => FileSystemMoves.MoveFile(source, readOnly, overwrite: true));
        }

        if (OperatingSystem.IsWindows())
        {
            Assert.ThrowsAny<IOException>(() => FileSystemMoves.MoveFile(source, Path.Combine(_root, "a<b.txt")));
        }

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(2), $"The refused moves took {elapsed.Elapsed}.");
        Assert.Equal("source", File.ReadAllText(source));
    }

    [Fact]
    public async Task A_Target_Held_Longer_Than_The_Timeout_Fails_After_It()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file that another handle holds open.");
        Directory.CreateDirectory(_root);
        string source = Path.Combine(_root, "source.txt");
        string target = Path.Combine(_root, "target.txt");
        File.WriteAllText(source, "source");
        File.WriteAllText(target, "target");
        using FileStream scanner = new(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Stopwatch elapsed = Stopwatch.StartNew();

        Assert.ThrowsAny<Exception>(() => FileSystemMoves.MoveFile(source, target, overwrite: true, TimeSpan.FromMilliseconds(300)));

        Assert.InRange(elapsed.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3));
        using CancellationTokenSource canceled = new(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            FileSystemMoves.MoveFileAsync(source, target, overwrite: true, timeout: null, canceled.Token));
        Assert.Equal("source", File.ReadAllText(source));
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (string file in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
