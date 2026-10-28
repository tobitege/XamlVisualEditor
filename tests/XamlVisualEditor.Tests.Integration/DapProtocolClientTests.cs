using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.Debugging.Dap;
using XamlVisualEditor.Extensions.Debugging;

namespace XamlVisualEditor.Tests.Integration;

public sealed class DapProtocolClientTests
{
    private const int ThreadId = 3;

    // The client waits for its answers without a limit, so the tests with a scripted adapter have one of their own.
    private const int ScriptedAdapterTimeout = 30000;

    private static readonly TimeSpan ServerTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Client_Receives_Response_Body()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageReader serverReader = new(clientToServer);
        DapMessageWriter serverWriter = new(serverToClient);

        CancellationTokenSource cts = new();
        Task serverTask = Task.Run(async () =>
        {
            string? requestJson = await serverReader.ReadMessageAsync(cts.Token);
            Assert.NotNull(requestJson);

            using JsonDocument requestDoc = JsonDocument.Parse(requestJson!);
            int seq = requestDoc.RootElement.GetProperty("seq").GetInt32();

            var response = new
            {
                seq = 1,
                type = "response",
                request_seq = seq,
                success = true,
                command = "initialize",
                body = new { supports = true }
            };

            string responseJson = JsonSerializer.Serialize(response);
            await serverWriter.WriteMessageAsync(responseJson, cts.Token);
        }, cts.Token);

        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        client.Start(cts.Token);

        JsonElement body = await client.SendRequestAsync("initialize", new { }, cts.Token);
        Assert.True(body.GetProperty("supports").GetBoolean());

        cts.Cancel();
        await serverTask;
        await client.DisposeAsync();
    }

    [Fact(Timeout = ScriptedAdapterTimeout)]
    public async Task Client_Leaves_Out_Null_Fields()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageReader serverReader = new(clientToServer);
        DapMessageWriter serverWriter = new(serverToClient);
        CancellationTokenSource cts = new();
        Task<(string Breakpoints, string Done)> serverTask = Task.Run(async () =>
        {
            string breakpoints = await AnswerAsync(serverReader, serverWriter, cts.Token);
            string done = await AnswerAsync(serverReader, serverWriter, cts.Token);
            return (breakpoints, done);
        }, cts.Token);

        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        client.Start(cts.Token);
        await client.SendRequestAsync("setBreakpoints", new
        {
            source = new { path = "Program.cs" },
            breakpoints = new[] { new { line = 7, column = (int?)null, condition = (string?)null } }
        }, cts.Token);
        await client.SendRequestAsync("configurationDone", null, cts.Token);
        (string breakpointsJson, string doneJson) = await serverTask.WaitAsync(ServerTimeout, cts.Token);

        using JsonDocument breakpointsDocument = JsonDocument.Parse(breakpointsJson);
        JsonElement breakpoint = breakpointsDocument.RootElement.GetProperty("arguments").GetProperty("breakpoints")[0];
        Assert.Equal(7, breakpoint.GetProperty("line").GetInt32());
        Assert.False(breakpoint.TryGetProperty("column", out _));
        Assert.False(breakpoint.TryGetProperty("condition", out _));
        using JsonDocument doneDocument = JsonDocument.Parse(doneJson);
        Assert.False(doneDocument.RootElement.TryGetProperty("arguments", out _));

        cts.Cancel();
        await client.DisposeAsync();
    }

    [Fact(Timeout = ScriptedAdapterTimeout)]
    public async Task Session_Sends_Breakpoints_Before_ConfigurationDone_And_A_Thread_With_Pause()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageReader serverReader = new(clientToServer);
        DapMessageWriter serverWriter = new(serverToClient);
        CancellationTokenSource cts = new();
        List<string> requests = new();
        Task serverTask = Task.Run(async () =>
        {
            for (int i = 0; i < 5; i++)
            {
                requests.Add(await AnswerAsync(serverReader, serverWriter, cts.Token));
            }
        }, cts.Token);

        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        client.Start(cts.Token);
        DapDebugSession session = new(client, client, "coreclr", "test");
        await session.LaunchAsync(new DebugLaunchOptions
        {
            AdapterPath = "adapter",
            ProgramPath = "App.dll",
            Breakpoints = new Dictionary<string, IReadOnlyList<SourceBreakpoint>>
            {
                ["Program.cs"] = new[] { new SourceBreakpoint { Line = 7 } }
            }
        }, cts.Token);
        await session.PauseAsync(null, cts.Token);
        await serverTask.WaitAsync(ServerTimeout, cts.Token);

        Assert.Equal(
            new[] { "launch", "setBreakpoints", "configurationDone", "threads", "pause" },
            requests.Select(CommandOf));
        using JsonDocument pause = JsonDocument.Parse(requests[4]);
        Assert.Equal(ThreadId, pause.RootElement.GetProperty("arguments").GetProperty("threadId").GetInt32());

        cts.Cancel();
        await client.DisposeAsync();
    }

    [Fact(Timeout = ScriptedAdapterTimeout)]
    public async Task A_Stop_During_The_Launch_Reaches_The_First_Subscriber_And_Stays()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageReader serverReader = new(clientToServer);
        DapMessageWriter serverWriter = new(serverToClient);
        CancellationTokenSource cts = new();
        Task serverTask = Task.Run(async () =>
        {
            await AnswerAsync(serverReader, serverWriter, cts.Token);
            await AnswerAsync(serverReader, serverWriter, cts.Token);
            // The program reaches a breakpoint in its first lines before the configuration is answered.
            await serverWriter.WriteMessageAsync(JsonSerializer.Serialize(new
            {
                seq = 2, type = "event", @event = "stopped", body = new { reason = "breakpoint", threadId = ThreadId }
            }), cts.Token);
            await AnswerAsync(serverReader, serverWriter, cts.Token);
        }, cts.Token);

        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        client.Start(cts.Token);
        DapDebugSession session = new(client, client, "coreclr", "test");
        await session.InitializeAsync(cts.Token);
        await session.LaunchAsync(new DebugLaunchOptions { AdapterPath = "adapter", ProgramPath = "App.dll" }, cts.Token);
        await serverTask.WaitAsync(ServerTimeout, cts.Token);
        List<DebugEvent> received = new();
        session.EventReceived += received.Add;

        Assert.Equal(DebugSessionState.Paused, session.State);
        DebugStoppedEvent stopped = Assert.IsType<DebugStoppedEvent>(Assert.Single(received));
        Assert.Equal(ThreadId, stopped.ThreadId);

        cts.Cancel();
        await client.DisposeAsync();
    }

    [Fact(Timeout = ScriptedAdapterTimeout)]
    public async Task Events_During_The_Replay_Reach_The_Subscriber_After_It()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageWriter serverWriter = new(serverToClient);
        CancellationTokenSource cts = new();
        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        DapDebugSession session = new(client, client, "coreclr", "test");
        // Runs after the session's own handler, so it reports an output the session has dealt with.
        TaskCompletionSource firstHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource secondHandled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.EventReceived += (_, body) =>
            (body.GetProperty("output").GetString() == "first" ? firstHandled : secondHandled).TrySetResult();
        client.Start(cts.Token);

        await WriteOutputAsync(serverWriter, "first", cts.Token);
        await firstHandled.Task.WaitAsync(ServerTimeout, cts.Token);
        List<string> received = new();
        session.EventReceived += debugEvent =>
        {
            string text = ((DebugOutputEvent)debugEvent).Text;
            if (text == "first")
            {
                // The adapter goes on while the subscriber still takes the buffered event.
                WriteOutputAsync(serverWriter, "second", cts.Token).GetAwaiter().GetResult();
                secondHandled.Task.Wait(ServerTimeout);
            }

            lock (received)
            {
                received.Add(text);
            }
        };

        Assert.Equal(new[] { "first", "second" }, received);
        cts.Cancel();
        await client.DisposeAsync();
    }

    private static Task WriteOutputAsync(DapMessageWriter writer, string output, CancellationToken ct) =>
        writer.WriteMessageAsync(JsonSerializer.Serialize(new
        {
            seq = 1, type = "event", @event = "output", body = new { category = "stdout", output }
        }), ct);

    [Fact(Timeout = ScriptedAdapterTimeout)]
    public async Task A_Refused_Launch_Ends_The_Adapter()
    {
        using AnonymousPipeServerStream serverToClient = new(PipeDirection.Out, HandleInheritability.Inheritable);
        using AnonymousPipeServerStream clientToServer = new(PipeDirection.In, HandleInheritability.Inheritable);
        using AnonymousPipeClientStream clientRead = new(PipeDirection.In, serverToClient.GetClientHandleAsString());
        using AnonymousPipeClientStream clientWrite = new(PipeDirection.Out, clientToServer.GetClientHandleAsString());

        DapMessageReader serverReader = new(clientToServer);
        DapMessageWriter serverWriter = new(serverToClient);
        CancellationTokenSource cts = new();
        Task serverTask = Task.Run(async () =>
        {
            await AnswerAsync(serverReader, serverWriter, cts.Token);
            await AnswerAsync(serverReader, serverWriter, cts.Token, success: false);
        }, cts.Token);

        DapProtocolClient client = new(new DapMessageReader(clientRead), new DapMessageWriter(clientWrite));
        client.Start(cts.Token);
        TrackingHost host = new();
        DapDebugSession session = new(client, host, "coreclr", "test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => DapDebuggerService.StartSessionAsync(
            session, started => started.LaunchAsync(new DebugLaunchOptions { AdapterPath = "adapter", ProgramPath = "App.dll" }, cts.Token),
            cts.Token));
        await serverTask.WaitAsync(ServerTimeout, cts.Token);

        Assert.True(host.IsDisposed);
        cts.Cancel();
        await client.DisposeAsync();
    }

    // Stands in for the adapter process, which the session ends on dispose.
    private sealed class TrackingHost : IAsyncDisposable
    {
        public bool IsDisposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            IsDisposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static string CommandOf(string requestJson)
    {
        using JsonDocument document = JsonDocument.Parse(requestJson);
        return document.RootElement.GetProperty("command").GetString()!;
    }

    private static async Task<string> AnswerAsync(
        DapMessageReader reader,
        DapMessageWriter writer,
        CancellationToken ct,
        bool success = true)
    {
        string? requestJson = await reader.ReadMessageAsync(ct);
        Assert.NotNull(requestJson);
        using JsonDocument requestDocument = JsonDocument.Parse(requestJson!);
        int seq = requestDocument.RootElement.GetProperty("seq").GetInt32();
        string command = requestDocument.RootElement.GetProperty("command").GetString()!;
        object body = command switch
        {
            "threads" => new { threads = new[] { new { id = ThreadId, name = "Main" } } },
            "setBreakpoints" => new { breakpoints = Array.Empty<object>() },
            _ => new { }
        };
        string responseJson = JsonSerializer.Serialize(new
        {
            seq = 1, type = "response", request_seq = seq, success, command, body
        });
        await writer.WriteMessageAsync(responseJson, ct);
        return requestJson!;
    }
}
