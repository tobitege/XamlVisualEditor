using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

/// <summary>Runs <see cref="TfCommandRunner"/> against cmd.exe in place of tf.exe.</summary>
public sealed class TfCommandRunnerTests
{
    private const string WINDOWS_ONLY = "The runner starts cmd.exe and reads the ANSI code page of Windows.";
    private const string MARKER_FILE = "marker.txt";
    private const string START_MARKER_FILE = "start.txt";
    private const string START_TEMPORARY_FILE = "start.tmp";

    [Fact]
    public async Task RunAsync_GrandchildFinishesWithoutTimeout()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        using TemporaryFolder folder = new();

        TfCommandResult result = await CreateRunner().RunAsync(folder.Path, CreateGrandchildArguments(pingCount: 2), TimeSpan.FromSeconds(60), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(Path.Combine(folder.Path, MARKER_FILE)));
    }

    [Fact]
    public async Task RunAsync_TimeoutKillsProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        Assert.SkipUnless(File.Exists(PowerShellPath), "Windows PowerShell is not installed.");
        using TemporaryFolder folder = new();
        using StartMarker marker = new(folder.Path);
        ManualTimeProvider clock = new();
        TfCommandRunner runner = new(new FixedLocator(CmdPath), null, clock);

        Task<TfCommandResult> run = runner.RunAsync(folder.Path, CreateWaitingGrandchildArguments(), TimeSpan.FromMinutes(5), CancellationToken.None);
        using Process grandchild = await marker.WaitForProcessAsync(run);
        try
        {
            clock.FireTimers();

            await Assert.ThrowsAsync<TimeoutException>(() => run);
            Assert.True(grandchild.WaitForExit(TimeSpan.FromSeconds(30)), "The grandchild survived the timeout.");
        }
        finally
        {
            KillIfRunning(grandchild);
        }
    }

    [Fact]
    public async Task RunAsync_CancellationKillsProcessTree()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);
        Assert.SkipUnless(File.Exists(PowerShellPath), "Windows PowerShell is not installed.");
        using TemporaryFolder folder = new();
        using StartMarker marker = new(folder.Path);
        using CancellationTokenSource cancellation = new();

        Task<TfCommandResult> run = CreateRunner().RunAsync(folder.Path, CreateWaitingGrandchildArguments(), TimeSpan.FromMinutes(5), cancellation.Token);
        using Process grandchild = await marker.WaitForProcessAsync(run);
        try
        {
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.True(grandchild.WaitForExit(TimeSpan.FromSeconds(30)), "The grandchild survived the cancellation.");
        }
        finally
        {
            KillIfRunning(grandchild);
        }
    }

    [Fact]
    public void CreateOutputEncoding_UsesAnsiCodePageOfTheSystem()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), WINDOWS_ONLY);

        Assert.Equal((int)GetACP(), TfCommandRunner.CreateOutputEncoding().CodePage);
    }

    private static string CmdPath => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static string PowerShellPath => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static TfCommandRunner CreateRunner()
    {
        return new TfCommandRunner(new FixedLocator(CmdPath));
    }

    // cmd.exe starts PowerShell as grandchild of the runner. PowerShell writes its process ID into a temporary file, renames
    // it to the start marker in the working directory, so the marker appears complete, and then waits two minutes.
    private static IReadOnlyList<string> CreateWaitingGrandchildArguments()
    {
        return
        [
            "/c",
            PowerShellPath,
            "-NoProfile",
            "-NonInteractive",
            "-Command",
            "[IO.File]::WriteAllText('" + START_TEMPORARY_FILE + "', $PID); [IO.File]::Move('" + START_TEMPORARY_FILE + "', '" + START_MARKER_FILE + "'); Start-Sleep -Seconds 120"
        ];
    }

    private static void KillIfRunning(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    // cmd.exe starts a second cmd.exe that waits about pingCount - 1 seconds and then writes the marker file into the
    // working directory. The carets keep the outer cmd.exe from interpreting the redirections and the command separator.
    private static IReadOnlyList<string> CreateGrandchildArguments(int pingCount)
    {
        return ["/c", "cmd", "/c", "ping", "-n", pingCount.ToString(CultureInfo.InvariantCulture), "127.0.0.1", "^>nul", "^&", "echo", "done^>" + MARKER_FILE];
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private sealed class FixedLocator : ITfExeLocator
    {
        private readonly string _path;

        public FixedLocator(string path)
        {
            _path = path;
        }

        public string? Locate(string? configuredPath) => _path;
    }

    // Completes when the start marker appears in the folder; the watcher starts before the command.
    private sealed class StartMarker : IDisposable
    {
        private readonly string _path;
        private readonly FileSystemWatcher _watcher;
        private readonly TaskCompletionSource _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public StartMarker(string folder)
        {
            _path = Path.Combine(folder, START_MARKER_FILE);
            _watcher = new FileSystemWatcher(folder, START_MARKER_FILE) { NotifyFilter = NotifyFilters.FileName };
            _watcher.Renamed += (_, _) => _created.TrySetResult();
            _watcher.Created += (_, _) => _created.TrySetResult();
            _watcher.EnableRaisingEvents = true;
        }

        public async Task<Process> WaitForProcessAsync(Task run)
        {
            Task first = await Task.WhenAny(_created.Task, run).WaitAsync(TimeSpan.FromSeconds(60));
            if (first == run)
            {
                TfCommandResult result = await (Task<TfCommandResult>)run;
                Assert.Fail("The command ended before the grandchild started: " + result.CombinedOutput);
            }

            // The rename event can arrive while PowerShell still holds the marker open with delete access for the move.
            using FileStream stream = new(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            int processId = int.Parse(reader.ReadToEnd().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture);
            return Process.GetProcessById(processId);
        }

        public void Dispose() => _watcher.Dispose();
    }

    // Runs the timers only when the test fires them, so a timeout starts after the grandchild did.
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new(callback, state);
            lock (_timers)
            {
                _timers.Add(timer);
            }

            return timer;
        }

        public void FireTimers()
        {
            ManualTimer[] timers;
            lock (_timers)
            {
                timers = _timers.ToArray();
            }

            foreach (ManualTimer timer in timers)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer : ITimer
        {
            private readonly TimerCallback _callback;
            private readonly object? _state;
            private bool _isDisposed;

            public ManualTimer(TimerCallback callback, object? state)
            {
                _callback = callback;
                _state = state;
            }

            public void Fire()
            {
                if (!_isDisposed)
                {
                    _callback(_state);
                }
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_isDisposed;

            public void Dispose() => _isDisposed = true;

            public ValueTask DisposeAsync()
            {
                _isDisposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class TemporaryFolder : IDisposable
    {
        public TemporaryFolder()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tf-runner-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
