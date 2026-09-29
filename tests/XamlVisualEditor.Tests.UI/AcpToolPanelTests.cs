using System;
using System.Linq;
using Avalonia;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.Acp;
using XamlVisualEditor.AcpExtension;
using XamlVisualEditor.AcpExtension.Views;

namespace XamlVisualEditor.Tests.UI;

public sealed class AcpToolPanelTests
{
    [AvaloniaFact]
    public async Task AcpToolView_ShowsMockTranscriptAndLists()
    {
        AcpToolViewModel vm = new();
        AcpToolView view = new() { DataContext = vm };

        Window window = await ShowInWindowAsync(view);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                view.ApplyTemplate();

                ListBox[] listBoxes = view.GetVisualDescendants().OfType<ListBox>().ToArray();
                ListBox? transcript = listBoxes.FirstOrDefault();
                Assert.NotNull(transcript);
                Assert.Equal(vm.Transcript.Count, transcript!.ItemCount);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => window.Close());
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public async Task AcpPermissionDialog_ShowsOptions()
    {
        AcpPermissionOption[] options =
        {
            new("allow_once", "Allow once", "allow_once"),
            new("reject_once", "Reject once", "reject_once")
        };

        AcpPermissionRequest request = new(
            "s1",
            options,
            "tool-1",
            "Write file",
            "edit",
            null);

        AcpPermissionDialogViewModel vm = new(request);
        AcpPermissionDialog dialog = new() { DataContext = vm };
        dialog.Show();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            dialog.ApplyTemplate();
        });
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ItemsControl? optionsControl = dialog.GetVisualDescendants()
                    .OfType<ItemsControl>()
                    .FirstOrDefault(control => ReferenceEquals(control.ItemsSource, vm.Options));

                Assert.NotNull(optionsControl);
                Assert.Equal(vm.Options.Count, optionsControl!.ItemCount);
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => dialog.Close());
        }
    }

    [AvaloniaFact]
    public async Task ComposerAuthenticatesSendsStreamsAndCancelsThroughTheDock()
    {
        FakeAgent service = new();
        using AcpToolViewModel vm = new(service);
        AcpToolView view = new() { DataContext = vm };
        Window window = await ShowInWindowAsync(view, width: 340);
        try
        {
            TextBox input = view.FindControl<TextBox>("PromptInput")!;
            Button send = view.FindControl<Button>("SendPrompt")!;
            Button settings = view.FindControl<Button>("AcpSettings")!;
            Point settingsPosition = settings.TranslatePoint(default, view)!.Value;
            Assert.InRange(settingsPosition.X + settings.Bounds.Width, 1, view.Bounds.Width);
            input.Text = "Check the form";
            Assert.False(send.IsEffectivelyEnabled);
            await vm.ConnectMockCommand.Execute().ToTask();
            Assert.Null(vm.ActiveSessionId);
            Assert.Equal("login", Assert.Single(vm.AuthenticationMethods).Id);
            await vm.AuthenticateCommand.Execute().ToTask();
            Assert.Equal("s1", vm.ActiveSessionId);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(send.IsEffectivelyEnabled);
            send.Command!.Execute(send.CommandParameter);
            await service.PromptStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal("Check the form", service.LastPrompt!.Value[0].GetProperty("text").GetString());
            Assert.True(vm.IsPromptRunning);
            Assert.Contains(vm.Transcript.OfType<AcpTranscriptMessageEntry>(), entry => entry.Text == "Hello world");
            AcpTranscriptToolEntry tool = Assert.Single(vm.Transcript.OfType<AcpTranscriptToolEntry>());
            Assert.Equal("Update form", tool.Title);
            Assert.Equal("edit", tool.ToolKind);
            Assert.Equal("completed", tool.Status);
            Assert.Equal("form.axaml", tool.DiffPath);
            Assert.Equal("changed", tool.OutputText);
            await vm.CancelPromptCommand.Execute().ToTask();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(vm.IsPromptRunning);
            Assert.False(await vm.CancelPromptCommand.CanExecute.FirstAsync());
            service.DropConnection();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(vm.IsConnected);
            Assert.False(send.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task CancelledPermissionClosesItsDialog()
    {
        using AcpToolViewModel vm = new();
        AcpToolView view = new() { DataContext = vm };
        Window owner = await ShowInWindowAsync(view);
        using CancellationTokenSource cancellation = new();
        try
        {
            AcpPermissionRequest request = new("s1", new[] { new AcpPermissionOption("allow", "Allow", "allow_once") },
                "tool", "Write file", "edit", null, cancellation.Token);
            Task<AcpPermissionOutcome> result = vm.PermissionInteraction.Handle(request).ToTask();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Single(owner.OwnedWindows.OfType<AcpPermissionDialog>());
            cancellation.Cancel();
            Assert.True((await result.WaitAsync(TimeSpan.FromSeconds(3))).IsCancelled);
            Assert.Empty(owner.OwnedWindows);
        }
        finally { owner.Close(); }
    }

    private sealed class FakeAgent : IAcpService
    {
        public bool IsConnected { get; private set; }
        public string? ActiveSessionId { get; private set; }
        public JsonElement? LastPrompt { get; private set; }
        public TaskCompletionSource PromptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<JsonElement> _promptFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _authenticated;
        public event Action? ConnectionChanged;
        public event Action<string>? StderrReceived { add { } remove { } }
        public event Action<string, JsonElement?>? NotificationReceived;
        public Task ConnectAsync(AcpAgentProcessOptions options, CancellationToken ct) => ConnectMockAgentAsync(ct);
        public Task ConnectMockAgentAsync(CancellationToken ct) { IsConnected = true; ConnectionChanged?.Invoke(); return Task.CompletedTask; }
        public Task DisconnectAsync() { DropConnection(); return Task.CompletedTask; }
        public void DropConnection() { IsConnected = false; ActiveSessionId = null; ConnectionChanged?.Invoke(); }
        public Task<JsonElement> InitializeAsync(object? parameters, CancellationToken ct) => Task.FromResult(JsonSerializer.SerializeToElement(new
        {
            protocolVersion = 1, agentInfo = new { name = "test-agent" }, authMethods = new[] { new { id = "login", name = "Test login" } }
        }));
        public Task<string> CreateSessionAsync(object? parameters, CancellationToken ct)
        {
            if (!_authenticated) throw new JsonRpcException(-32000, "Authentication required.");
            ActiveSessionId = "s1";
            return Task.FromResult(ActiveSessionId);
        }
        public Task<JsonElement> PromptAsync(string sessionId, object? content, CancellationToken ct)
        {
            LastPrompt = JsonSerializer.SerializeToElement(content);
            foreach (string text in new[] { "Hello", " ", "world" })
                Update(new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text } });
            Update(new { sessionUpdate = "tool_call", toolCallId = "t1", title = "Update form", kind = "edit", status = "in_progress" });
            Update(new
            {
                sessionUpdate = "tool_call_update", toolCallId = "t1", status = "completed",
                content = new object[] { new { type = "diff", path = "form.axaml", oldText = "old", newText = "new" },
                    new { type = "content", content = new { type = "text", text = "changed" } } }
            });
            PromptStarted.SetResult();
            return _promptFinished.Task;
        }
        private void Update(object update) => NotificationReceived?.Invoke("session/update", JsonSerializer.SerializeToElement(new { sessionId = "s1", update }));
        public Task CancelAsync(string sessionId, CancellationToken ct) { _promptFinished.TrySetResult(JsonSerializer.SerializeToElement(new { stopReason = "cancelled" })); return Task.CompletedTask; }
        public Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
        {
            Assert.Equal("authenticate", method);
            Assert.Equal("login", JsonSerializer.SerializeToElement(parameters).GetProperty("methodId").GetString());
            _authenticated = true;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { }));
        }
        public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct) => Task.CompletedTask;
        public JsonElement? GetTerminalOutput(string terminalId) => null;
        public void SetPermissionHandler(Func<AcpPermissionRequest, CancellationToken, Task<AcpPermissionOutcome>>? handler) { }
    }

    private static async Task<Window> ShowInWindowAsync(Control control, double width = 900, double height = 600)
    {
        Window window = new()
        {
            Content = control,
            Width = width,
            Height = height
        };

        window.Show();
        await Dispatcher.UIThread.InvokeAsync(() => { });
        return window;
    }
}
