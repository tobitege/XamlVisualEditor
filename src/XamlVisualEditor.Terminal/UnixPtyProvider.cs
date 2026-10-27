using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Porta.Pty;

namespace XamlVisualEditor.Terminal;

/// <summary>
/// Starts terminal processes on Linux and macOS through Porta.Pty, which forks and executes the
/// child entirely in native code. Running managed code in a forked child corrupts the code memory
/// that the runtime shares between parent and child, and crashes the parent process.
/// </summary>
public sealed class UnixPtyProvider : IPtyProvider
{
    public IPtyProcess StartProcess(TerminalSessionOptions options)
    {
        PtyOptions ptyOptions = new()
        {
            App = options.Command ?? Environment.GetEnvironmentVariable("SHELL") ?? "/bin/zsh",
            CommandLine = options.Arguments.ToArray(),
            Cwd = string.IsNullOrWhiteSpace(options.WorkingDirectory) ? Environment.CurrentDirectory : options.WorkingDirectory,
            Cols = ClampSize(options.Columns),
            Rows = ClampSize(options.Rows),
            Environment = CreateEnvironment(TerminalEnvironment.Base, TerminalEnvironment.ReadCurrent(), options.Environment)
        };

        // Porta.Pty spawns synchronously on Linux and macOS; the returned task is already completed.
        IPtyConnection connection = PtyProvider.SpawnAsync(ptyOptions, CancellationToken.None).GetAwaiter().GetResult();
        return new UnixPtyProcess(connection, ptyOptions.Cols, ptyOptions.Rows);
    }

    internal static int ClampSize(int value) => Math.Clamp(value, 1, ushort.MaxValue);

    /// <summary>
    /// Returns the entries to pass to Porta.Pty for a session that starts from
    /// <paramref name="startup"/>. Porta.Pty takes the <paramref name="current"/> process
    /// environment, sets <c>TERM</c>, removes terminal multiplexer variables such as <c>TMUX</c> and
    /// <c>COLUMNS</c>, and then applies the entries, where an empty value removes the variable.
    /// The entries are therefore only the differences to the current environment and every
    /// override, so its removals still apply to variables the host did not change.
    /// </summary>
    internal static Dictionary<string, string> CreateEnvironment(
        IReadOnlyDictionary<string, string> startup,
        IReadOnlyDictionary<string, string> current,
        IReadOnlyDictionary<string, string> overrides)
    {
        Dictionary<string, string> session = TerminalEnvironment.Compose(startup, static environment =>
        {
            environment["TERM"] = "xterm-256color";
            environment["COLORTERM"] = "truecolor";
        }, overrides);

        Dictionary<string, string> entries = new(StringComparer.Ordinal);
        foreach (KeyValuePair<string, string> variable in session)
        {
            if (!current.TryGetValue(variable.Key, out string? value) || value != variable.Value)
            {
                entries[variable.Key] = variable.Value;
            }
        }

        foreach (string key in current.Keys)
        {
            if (!session.ContainsKey(key))
            {
                entries[key] = string.Empty;
            }
        }

        foreach (string key in overrides.Keys)
        {
            entries[key] = session.TryGetValue(key, out string? value) ? value : string.Empty;
        }

        return entries;
    }
}

public sealed class UnixPtyProcess : IPtyProcess
{
    private readonly IPtyConnection _connection;
    private int _columns;
    private int _rows;
    private int _disposed;

    internal UnixPtyProcess(IPtyConnection connection, int columns, int rows)
    {
        _connection = connection;
        _columns = columns;
        _rows = rows;
    }

    public Stream Input => _connection.WriterStream;
    public Stream Output => _connection.ReaderStream;
    public int Pid => _connection.Pid;

    /// <remarks>
    /// The pixel size is not passed to the kernel; the emulator answers pixel size queries itself.
    /// A failed resize leaves the cached size unchanged, so the next call with the same size retries.
    /// </remarks>
    public void Resize(int columns, int rows, int pixelWidth = 0, int pixelHeight = 0)
    {
        int clampedColumns = UnixPtyProvider.ClampSize(columns);
        int clampedRows = UnixPtyProvider.ClampSize(rows);
        if (Volatile.Read(ref _disposed) != 0 || (_columns == clampedColumns && _rows == clampedRows))
        {
            return;
        }

        try
        {
            _connection.Resize(clampedColumns, clampedRows);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        _columns = clampedColumns;
        _rows = clampedRows;
    }

    /// <summary>
    /// Porta.Pty sends SIGHUP to the shell and closes the PTY; on macOS it sleeps 100 ms before it
    /// sends SIGKILL, so the close runs on the thread pool instead of blocking the caller.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(static connection => connection.Dispose(), _connection, preferLocal: false);
    }
}
