using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class UnixPtyProviderTests
{
    [Fact]
    public async Task ResizeUpdatesKernelWindowSize()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix PTY provider runs on Linux and macOS.");

        TerminalSessionOptions options = new()
        {
            Columns = 80,
            Rows = 24,
            Command = "/bin/sh",
            Arguments = new[] { "-c", "echo READY; while :; do stty size; sleep 1; done" }
        };

        using TerminalSession session = new(options, new UnixPtyProvider(), new ManagedTerminalEmulatorFactory());
        session.Start();

        bool ready = await WaitForTextAsync(session.Emulator, "READY", TimeSpan.FromSeconds(10));
        Assert.True(ready, GetVisibleText(session.Emulator));

        bool initial = await WaitForTextAsync(session.Emulator, "24 80", TimeSpan.FromSeconds(10));
        Assert.True(initial, GetVisibleText(session.Emulator));

        session.Resize(100, 40);

        bool resized = await WaitForTextAsync(session.Emulator, "40 100", TimeSpan.FromSeconds(10));
        if (!resized)
        {
            session.Resize(101, 40);
            session.Resize(100, 40);
            resized = await WaitForTextAsync(session.Emulator, "40 100", TimeSpan.FromSeconds(10));
        }

        Assert.True(resized, GetVisibleText(session.Emulator));
    }

    [Fact]
    public async Task ShellSeesOverridesWorkingDirectoryAndTerminalDefaults()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix PTY provider runs on Linux and macOS.");

        string removed = "XVE_PTY_TEST_REMOVED_" + Guid.NewGuid().ToString("N");
        DirectoryInfo workingDirectory = Directory.CreateTempSubdirectory("xve-pty-");
        Environment.SetEnvironmentVariable(removed, "present");
        try
        {
            TerminalSessionOptions options = new()
            {
                Columns = 120,
                Rows = 24,
                Command = "/bin/sh",
                Arguments = new[] { "-c", "echo \"T=$TERM C=$COLORTERM A=${XVE_PTY_TEST_ADDED-unset} R=${" + removed + "-unset}\"; pwd; echo DONE" },
                WorkingDirectory = workingDirectory.FullName,
                Environment = new Dictionary<string, string>
                {
                    ["XVE_PTY_TEST_ADDED"] = "added",
                    ["COLORTERM"] = "24bit",
                    [removed] = string.Empty
                }
            };

            using TerminalSession session = new(options, new UnixPtyProvider(), new ManagedTerminalEmulatorFactory());
            session.Start();

            bool done = await WaitForTextAsync(session.Emulator, "DONE", TimeSpan.FromSeconds(10));
            string text = GetVisibleText(session.Emulator);
            Assert.True(done, text);
            Assert.Contains("T=xterm-256color C=24bit A=added R=unset", text, StringComparison.Ordinal);
            Assert.Contains(workingDirectory.Name, text, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(removed, null);
            workingDirectory.Delete();
        }
    }

    [Fact]
    public async Task ShellExitEndsTheSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix PTY provider runs on Linux and macOS.");

        TerminalSessionOptions options = new()
        {
            Columns = 80,
            Rows = 24,
            Command = "/bin/sh",
            Arguments = new[] { "-c", "echo BYE" }
        };

        using TerminalSession session = new(options, new UnixPtyProvider(), new ManagedTerminalEmulatorFactory());
        TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += _ => exited.TrySetResult();
        session.Start();

        Task finished = await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(exited.Task, finished);
        Assert.Contains("BYE", GetVisibleText(session.Emulator), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeEndsARunningShellAndIgnoresALaterResize()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix PTY provider runs on Linux and macOS.");

        TerminalSessionOptions options = new()
        {
            Columns = 80,
            Rows = 24,
            Command = "/bin/sh",
            Arguments = new[] { "-c", "echo READY; exec sleep 60" }
        };

        IPtyProcess process = new UnixPtyProvider().StartProcess(options);
        Assert.True(await WaitForOutputAsync(process.Output, "READY", TimeSpan.FromSeconds(10)));
        int pid = process.Pid;

        process.Dispose();
        process.Resize(100, 40);

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (IsRunning(pid) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        Assert.False(IsRunning(pid));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> WaitForOutputAsync(Stream output, string expected, TimeSpan timeout)
    {
        Task<bool> read = Task.Run(() =>
        {
            StringBuilder text = new();
            byte[] buffer = new byte[1024];
            int count;
            while ((count = output.Read(buffer, 0, buffer.Length)) > 0)
            {
                text.Append(Encoding.UTF8.GetString(buffer, 0, count));
                if (text.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        });

        return await Task.WhenAny(read, Task.Delay(timeout)) == read && await read;
    }

    private static async Task<bool> WaitForTextAsync(ITerminalEmulator emulator, string expected, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (GetVisibleText(emulator).Contains(expected, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(50).ConfigureAwait(false);
        }

        return GetVisibleText(emulator).Contains(expected, StringComparison.Ordinal);
    }

    private static string GetVisibleText(ITerminalEmulator emulator)
    {
        StringBuilder builder = new();
        emulator.Read((buffer, _) =>
        {
            for (int row = 0; row < buffer.Rows; row++)
            {
                TerminalLine line = buffer.GetLine(row);
                for (int col = 0; col < buffer.Columns; col++)
                {
                    TerminalCell cell = line.Cells[col];
                    if (cell.Width == 0)
                    {
                        continue;
                    }

                    builder.Append(cell.Rune.ToString());
                }

                builder.Append('\n');
            }
        });

        return builder.ToString();
    }
}
