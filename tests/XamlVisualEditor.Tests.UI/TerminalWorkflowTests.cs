using System.Diagnostics;
using System.Text;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Terminal;
using XamlVisualEditor.Terminal.Avalonia.Controls;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TerminalWorkflowTests(ITestOutputHelper output)
{
    [Fact]
    public async Task WindowsPtyTransportsOutputWithoutTheEditor()
    {
        if (!OperatingSystem.IsWindows()) return;
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(File.Exists(shell));
        IPtyProcess pty = new WindowsPtyProvider().StartProcess(new TerminalSessionOptions
        {
            Command = shell,
            Arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('RAW-Grüße'); Start-Sleep -Seconds 60" }
        });
        using Process process = Process.GetProcessById(pty.Pid);
        TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MemoryStream captured = new();
        Task reader = Task.Run(() =>
        {
            byte[] buffer = new byte[4096];
            try
            {
                int count;
                while ((count = pty.Output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    captured.Write(buffer, 0, count);
                    if (Encoding.UTF8.GetString(captured.ToArray()).Contains("RAW-Grüße", StringComparison.Ordinal)) ready.TrySetResult(true);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        });
        try { await ready.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally
        {
            pty.Dispose();
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
            output.WriteLine("Raw output: {0}", Encoding.UTF8.GetString(captured.ToArray()));
            captured.Dispose();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task WindowsPtyOutputReachesEndOfStreamAfterTheShellExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        string shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        IPtyProcess pty = new WindowsPtyProvider().StartProcess(new TerminalSessionOptions
        {
            Command = shell,
            Arguments = new[] { "/c", "echo EXIT-MARKER" }
        });
        MemoryStream captured = new();
        Task reader = Task.Run(() =>
        {
            byte[] buffer = new byte[4096];
            int count;
            while ((count = pty.Output.Read(buffer, 0, buffer.Length)) > 0)
            {
                captured.Write(buffer, 0, count);
            }
        });
        try
        {
            await reader.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("EXIT-MARKER", Encoding.UTF8.GetString(captured.ToArray()), StringComparison.Ordinal);
        }
        finally
        {
            output.WriteLine("Raw output: {0}", Encoding.UTF8.GetString(captured.ToArray()));
            pty.Dispose();
            captured.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task WindowsTerminalClosesItsDockWhenTheShellExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        string shell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        TerminalService service = new(new WindowsPtyProvider(), new ManagedTerminalEmulatorFactory());
        Avalonia.Themes.Fluent.FluentTheme fluent = new();
        Dock.Avalonia.Themes.Fluent.DockFluentTheme dockTheme = new();
        Application.Current!.Styles.Add(fluent);
        Application.Current.Styles.Add(dockTheme);
        using MainWindowViewModel vm = new(terminalService: service);
        MainWindow window = new(vm) { Width = 1000, Height = 800 };
        window.DataTemplates.Add(new ViewLocator());
        try
        {
            window.Show();
            TerminalViewModel terminal = vm.CreateTerminalSession(new TerminalSessionOptions
            {
                Command = shell,
                Arguments = new[] { "/c", "exit" }
            });
            string toolId = "Terminal-" + terminal.Id.ToString("N");
            Assert.NotNull(XamlVisualEditor.Shell.XamlEditorDockFactory.FindDockable<Dock.Model.Core.IDockable>(vm.DockLayout, toolId));
            Stopwatch deadline = Stopwatch.StartNew();
            while (vm.Terminals.Count > 0 && deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                await Task.Delay(50);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            Assert.Empty(vm.Terminals);
            Assert.False(terminal.IsConnected);
            Assert.Null(XamlVisualEditor.Shell.XamlEditorDockFactory.FindDockable<Dock.Model.Core.IDockable>(vm.DockLayout, toolId));
        }
        finally
        {
            foreach (TerminalViewModel terminal in vm.Terminals.ToArray()) vm.CloseTerminalSession(terminal.Id);
            window.Close();
            Application.Current.Styles.Remove(dockTheme);
            Application.Current.Styles.Remove(fluent);
        }
    }

    [AvaloniaFact]
    public async Task WindowsTerminalOpensCollapsedDockAcceptsInputAndStopsItsProcessWhenClosed()
    {
        if (!OperatingSystem.IsWindows()) return;
        string shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        Assert.True(File.Exists(shell), "PowerShell 7 is required for the Windows terminal test.");
        string directory = Path.Combine(Path.GetTempPath(), "XVE Terminal " + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        RecordingPtyProvider provider = new();
        RecordingTerminalService service = new(provider);
        Avalonia.Themes.Fluent.FluentTheme fluent = new();
        Dock.Avalonia.Themes.Fluent.DockFluentTheme dockTheme = new();
        Application.Current!.Styles.Add(fluent);
        Application.Current.Styles.Add(dockTheme);
        using MainWindowViewModel vm = new(terminalService: service);
        MainWindow window = new(vm) { Width = 1000, Height = 800 };
        window.DataTemplates.Add(new ViewLocator());
        vm.DockFactory.CollapseBottomToolDock(vm.DockLayout);
        try
        {
            window.Show();
            TerminalViewModel terminal = vm.CreateTerminalSession(new TerminalSessionOptions
            {
                Command = shell,
                WorkingDirectory = directory,
                Arguments = new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                    "[Console]::WriteLine('READY-' + $env:XVE_TEST_UNICODE); [Console]::WriteLine('CWD-' + (Get-Location).Path); $line = [Console]::ReadLine(); [Console]::WriteLine('REPLY-' + $line); Start-Sleep -Seconds 60" },
                Environment = new Dictionary<string, string> { ["XVE_TEST_UNICODE"] = "Grüße" }
            });
            using Process process = Process.GetProcessById(provider.ProcessId);
            await service.Ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Contains("CWD-" + directory, service.Output);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            TerminalControl control = Assert.Single(window.GetVisualDescendants().OfType<TerminalControl>());
            Assert.True(control.IsVisible);
            Assert.True(control.Bounds.Height > 100);
            Assert.True(control.Focus());
            window.KeyTextInput("terminal-input");
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await service.Reply.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.CloseTerminalSession(terminal.Id));
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Empty(vm.Terminals);
            Assert.False(terminal.IsConnected);
        }
        finally
        {
            output.WriteLine("Terminal process: {0}; connected: {1}", provider.ProcessId, vm.Terminals.FirstOrDefault()?.IsConnected ?? false);
            output.WriteLine("Terminal output: {0}", service.Output);
            foreach (TerminalViewModel terminal in vm.Terminals.ToArray()) vm.CloseTerminalSession(terminal.Id);
            window.Close();
            Application.Current.Styles.Remove(dockTheme);
            Application.Current.Styles.Remove(fluent);
        }
    }

    [AvaloniaFact]
    public async Task TerminalCommandReportsTheActualStartupFailure()
    {
        TerminalService service = new(new FailingPtyProvider(), new ManagedTerminalEmulatorFactory());
        using MainWindowViewModel vm = new(terminalService: service);
        await ((IWorkspaceCommands)vm).NewTerminalAsync(CancellationToken.None);
        Assert.Contains("Shell executable missing", vm.StatusText);
        Assert.Empty(vm.Terminals);
    }

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

    private sealed class FailingPtyProvider : IPtyProvider
    {
        public IPtyProcess StartProcess(TerminalSessionOptions options) => throw new IOException("Shell executable missing");
    }

    private sealed class RecordingTerminalService(IPtyProvider provider) : ITerminalService
    {
        public int StartExternalSession(TerminalSessionOptions options) =>
            new TerminalService(provider, new ManagedTerminalEmulatorFactory()).StartExternalSession(options);
        private readonly MemoryStream _output = new();
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Output { get { lock (_output) return Encoding.UTF8.GetString(_output.ToArray()); } }

        public ITerminalSession CreateSession(TerminalSessionOptions options)
        {
            ITerminalSession session = new TerminalService(provider, new ManagedTerminalEmulatorFactory()).CreateSession(options);
            session.OutputReceived += data =>
            {
                lock (_output)
                {
                    _output.Write(data.Span);
                    string output = Encoding.UTF8.GetString(_output.ToArray());
                    if (output.Contains("READY-Grüße", StringComparison.Ordinal) && output.Contains("CWD-" + options.WorkingDirectory, StringComparison.Ordinal))
                        Ready.TrySetResult(true);
                    if (output.Contains("REPLY-terminal-input", StringComparison.Ordinal)) Reply.TrySetResult(true);
                }
            };
            return session;
        }
    }
}
