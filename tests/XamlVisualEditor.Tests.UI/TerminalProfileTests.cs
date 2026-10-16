using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TerminalProfileTests(ITestOutputHelper output)
{
    /// <summary>
    /// The external cases open real, visible console windows on the desktop and depend on how the
    /// default terminal host reuses windows. They run only when this variable is set to <c>1</c>.
    /// </summary>
    private const string DesktopTestsVariable = "XVE_RUN_DESKTOP_TESTS";

    [Theory]
    [InlineData(TerminalShell.CommandPrompt, TerminalLocation.Internal)]
    [InlineData(TerminalShell.WindowsPowerShell, TerminalLocation.Internal)]
    [InlineData(TerminalShell.PowerShell7, TerminalLocation.Internal)]
    [InlineData(TerminalShell.CommandPrompt, TerminalLocation.External)]
    [InlineData(TerminalShell.WindowsPowerShell, TerminalLocation.External)]
    [InlineData(TerminalShell.PowerShell7, TerminalLocation.External)]
    public async Task ConfiguredShellStartsInternallyOrInAVisibleExternalWindow(TerminalShell shell, TerminalLocation location)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The configured shells are Windows shells.");
        Assert.SkipWhen(location == TerminalLocation.External &&
            Environment.GetEnvironmentVariable(DesktopTestsVariable) != "1",
            $"External terminal windows open on the desktop; set {DesktopTestsVariable}=1 to run this case.");
        EditorOptions configuration = new() { TerminalShell = shell, TerminalLocation = location };
        Assert.SkipUnless(File.Exists(configuration.ShellPath), "Shell not installed: " + configuration.ShellPath);
        RecordingPtyProvider provider = new();
        TerminalService service = new(provider, new ManagedTerminalEmulatorFactory());
        TerminalSessionOptions options = new()
        {
            Command = configuration.ShellPath,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments = shell == TerminalShell.CommandPrompt ? new[] { "/D" } : new[] { "-NoProfile" }
        };
        string marker = shell == TerminalShell.CommandPrompt ? "Microsoft Windows" : "PowerShell";
        Process? process = null;
        ITerminalSession? session = null;
        IntPtr externalWindow = IntPtr.Zero;
        MemoryStream captured = new();
        try
        {
            if (location == TerminalLocation.Internal)
            {
                TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
                session = service.CreateSession(options);
                session.OutputReceived += bytes =>
                {
                    lock (captured)
                    {
                        captured.Write(bytes.Span);
                        if (Encoding.UTF8.GetString(captured.ToArray()).Contains(marker, StringComparison.Ordinal)) ready.TrySetResult(true);
                    }
                };
                session.Start();
                process = Process.GetProcessById(provider.ProcessId);
                // A cold shell start on a busy CI runner can take longer than 15 s.
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            else
            {
                HashSet<IntPtr> previousWindows = GetWindows();
                int id = service.StartExternalSession(options);
                process = Process.GetProcessById(id);
                Stopwatch deadline = Stopwatch.StartNew();
                while ((externalWindow = FindExternalWindow(previousWindows, shell)) == IntPtr.Zero
                    && !process.HasExited && deadline.Elapsed < TimeSpan.FromSeconds(10))
                {
                    await Task.Delay(100);
                    process.Refresh();
                }
                foreach (IntPtr handle in GetWindows().Except(previousWindows))
                {
                    StringBuilder title = new(512);
                    StringBuilder className = new(128);
                    GetWindowText(handle, title, title.Capacity);
                    GetClassName(handle, className, className.Capacity);
                    GetWindowThreadProcessId(handle, out uint owner);
                    output.WriteLine("Window {0}: process {1}, class {2}, visible {3}, title {4}",
                        handle, owner, className, IsWindowVisible(handle), title);
                }
                Assert.NotEqual(IntPtr.Zero, externalWindow);
                Assert.True(IsWindowVisible(externalWindow));
            }
            Assert.False(process.HasExited);
            Assert.True(FileSystemPathComparison.Equals(configuration.ShellPath, process.MainModule!.FileName));
            output.WriteLine("{0} / {1}: process {2}", shell, location, process.Id);
        }
        finally
        {
            session?.Dispose();
            if (process is not null)
            {
                if (!process.HasExited && location == TerminalLocation.External)
                {
                    if (externalWindow != IntPtr.Zero) PostMessage(externalWindow, 0x0010, IntPtr.Zero, IntPtr.Zero);
                    else if (!process.CloseMainWindow()) process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                process.Dispose();
            }
            captured.Dispose();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    private static HashSet<IntPtr> GetWindows()
    {
        HashSet<IntPtr> windows = new();
        EnumWindows((window, _) => { windows.Add(window); return true; }, IntPtr.Zero);
        return windows;
    }

    private static IntPtr FindExternalWindow(HashSet<IntPtr> previousWindows, TerminalShell shell)
    {
        foreach (IntPtr window in GetWindows().Except(previousWindows))
        {
            if (!IsWindowVisible(window)) continue;
            StringBuilder className = new(128);
            GetClassName(window, className, className.Capacity);
            if (className.ToString() is not ("CASCADIA_HOSTING_WINDOW_CLASS" or "ConsoleWindowClass")) continue;
            StringBuilder title = new(512);
            GetWindowText(window, title, title.Capacity);
            string caption = title.ToString();
            bool matches = shell == TerminalShell.CommandPrompt
                ? caption.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase)
                : caption.Contains("powershell", StringComparison.OrdinalIgnoreCase)
                    || shell == TerminalShell.PowerShell7 && caption.Contains("pwsh", StringComparison.OrdinalIgnoreCase);
            if (matches) return window;
        }
        return IntPtr.Zero;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    private sealed class RecordingPtyProvider : IPtyProvider
    {
        public int ProcessId { get; private set; }
        public IPtyProcess StartProcess(TerminalSessionOptions options)
        {
            IPtyProcess process = new WindowsPtyProvider().StartProcess(options);
            ProcessId = process.Pid;
            return process;
        }
    }
}
