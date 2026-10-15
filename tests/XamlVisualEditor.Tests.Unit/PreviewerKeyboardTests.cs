using System.Net;
using System.Threading.Channels;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Viewport;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using RemoteInput = Avalonia.Remote.Protocol.Input;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerKeyboardTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task RunModeLeavesWindowShortcutsAndCtrlTabToTheWindow()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Content=\"Live\" /></Canvas>";
        using DesignerDocumentViewModel document = new("PreviewKeys.axaml") { UseExternalPreviewer = true };
        await document.SyncEngine.LoadAsync(xaml).WaitAsync(cancellationToken);
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        await session.SendUpdateXamlAsync(xaml, "Library.dll", "/PreviewKeys.axaml", 200, 100, Guid.NewGuid());
        PreviewerSurfaceControl surface = new() { Document = document, Session = session, Width = 200, Height = 100 };
        Button after = new() { Content = "After" };
        int saves = 0;
        Window window = new() { Content = new StackPanel { Children = { surface, after } }, Width = 400, Height = 300 };
        window.KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = new CountingCommand(() => saves++)
        });
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            window.Show();
            window.UpdateLayout();
            using IAvaloniaRemoteTransportConnection connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
            Channel<RemoteInput.KeyEventMessage> keys = Channel.CreateUnbounded<RemoteInput.KeyEventMessage>();
            connection.OnMessage += (_, message) =>
            {
                if (message is RemoteInput.KeyEventMessage key) keys.Writer.TryWrite(key);
            };
            connection.Start();
            TaskCompletionSource<bool> frameReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
            session.FrameReceived += _ => frameReceived.TrySetResult(true);
            await connection.Send(new FrameMessage
            {
                SequenceId = 1, Width = 200, Height = 100, Stride = 800, Data = new byte[80000],
                Format = PixelFormat.Bgra8888, DpiX = 96, DpiY = 96
            });
            await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            document.DesignSurface.IsEditMode = false;

            Assert.True(surface.Focus());
            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            window.KeyRelease(Key.S, RawInputModifiers.Control, PhysicalKey.S, "s");
            Assert.Equal(1, saves);
            Assert.True(surface.IsFocused);

            window.KeyPress(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);
            Assert.True(after.IsFocused);
            window.KeyRelease(Key.Tab, RawInputModifiers.Control, PhysicalKey.Tab, null);

            Assert.True(surface.Focus());
            window.KeyPress(Key.B, RawInputModifiers.None, PhysicalKey.B, "b");
            window.KeyRelease(Key.B, RawInputModifiers.None, PhysicalKey.B, "b");
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            RemoteInput.KeyEventMessage first = await keys.Reader.ReadAsync(timeout.Token);
            Assert.Equal(RemoteInput.PhysicalKey.B, first.PhysicalKey);
            Assert.True(first.IsDown);
            Assert.Equal(1, saves);
        }
        finally { window.Close(); }
    }

    private sealed class CountingCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => execute();
    }
}
