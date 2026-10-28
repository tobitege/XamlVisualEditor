using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Core;

/// <summary>
/// Moves files and directories like <see cref="File.Move(string, string, bool)"/> and
/// <see cref="Directory.Move(string, string)"/>, and retries while Windows refuses the move because another
/// process briefly holds the source or the target open, as virus scanners, the search indexer and sync clients do
/// after every write. Other platforms and other failures, such as an invalid name, an existing target or a read-only
/// target, fail at once.
/// </summary>
public static class FileSystemMoves
{
    // A scan of a freshly written file can outlast a second under load, so the retry waits with doubling pauses
    // from 20 ms, capped at 500 ms, until the timeout has passed.
    private const int ErrorAccessDenied = unchecked((int)0x80070005);
    private const int InitialWaitMilliseconds = 20;
    private const int MaxWaitMilliseconds = 500;

    /// <summary>Gets the time a move keeps retrying when the caller passes no timeout.</summary>
    public static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Gets the timeout for a rename the user waits for: a file that another program keeps open stays refused, so
    /// the retry only bridges the short hold of a scanner or indexer.
    /// </summary>
    public static TimeSpan InteractiveTimeout { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Moves a file, replacing an existing target when <paramref name="overwrite"/> is true.</summary>
    public static void MoveFile(string source, string destination, bool overwrite = false, TimeSpan? timeout = null) =>
        Move(() => File.Move(source, destination, overwrite), destination, timeout);

    /// <summary>Moves a directory to a path that does not exist yet.</summary>
    public static void MoveDirectory(string source, string destination, TimeSpan? timeout = null) =>
        Move(() => Directory.Move(source, destination), destination, timeout);

    /// <summary>Moves a file like <see cref="MoveFile"/> without blocking the calling thread while it waits.</summary>
    public static Task MoveFileAsync(string source, string destination, bool overwrite, TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        MoveAsync(() => File.Move(source, destination, overwrite), destination, timeout, cancellationToken);

    /// <summary>Moves a directory like <see cref="MoveDirectory"/> without blocking the calling thread while it waits.</summary>
    public static Task MoveDirectoryAsync(string source, string destination, TimeSpan? timeout,
        CancellationToken cancellationToken) =>
        MoveAsync(() => Directory.Move(source, destination), destination, timeout, cancellationToken);

    private static void Move(Action move, string destination, TimeSpan? timeout)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                move();
                return;
            }
            catch (Exception exception) when (GetRetryWait(exception, destination, elapsed, timeout, attempt) is { } wait)
            {
                Thread.Sleep(wait);
            }
        }
    }

    private static async Task MoveAsync(Action move, string destination, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                move();
                return;
            }
            catch (Exception exception) when (GetRetryWait(exception, destination, elapsed, timeout, attempt) is { } wait)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Only a sharing or lock violation, or a denied access to a target that is neither a directory nor read-only,
    // can end while the caller waits. Windows reports a denied access as UnauthorizedAccessException when a held
    // target file refuses the replacement, but as IOException when a held file refuses moving its directory.
    private static TimeSpan? GetRetryWait(Exception exception, string destination, Stopwatch elapsed, TimeSpan? timeout,
        int attempt)
    {
        TimeSpan remaining = (timeout ?? DefaultTimeout) - elapsed.Elapsed;
        if (!OperatingSystem.IsWindows() || remaining <= TimeSpan.Zero)
        {
            return null;
        }

        bool readOnlyTarget = File.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReadOnly);
        bool canEnd = FileSystemErrorMessages.IsInUse(exception)
            || exception.HResult == ErrorAccessDenied && !Directory.Exists(destination) && !readOnlyTarget;
        if (!canEnd)
        {
            return null;
        }

        TimeSpan wait = TimeSpan.FromMilliseconds(Math.Min(InitialWaitMilliseconds << Math.Min(attempt, 5), MaxWaitMilliseconds));
        return wait < remaining ? wait : remaining;
    }
}
