using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Acp;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class AcpServiceSessionTests
{
    [Fact]
    public async Task InitializeAndCreateSessionSetsActiveSessionId()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(cts.Token);

        await using AcpAgentHost host = AcpAgentHost.CreateForTests(Process.GetCurrentProcess(), client);
        IAcpAgentHostFactory hostFactory = new StubHostFactory(host);
        AcpService service = new(hostFactory);

        _ = Task.Run(() => HandleSessionLifecycleAsync(pipe, cts.Token), cts.Token);

        await service.ConnectAsync(new AcpAgentProcessOptions { FileName = "test" }, cts.Token);
        _ = await service.InitializeAsync(new { clientInfo = new { name = "XVE", version = "test" } }, cts.Token);

        string sessionId = await service.CreateSessionAsync(new { cwd = "/", mcpServers = Array.Empty<object>() }, cts.Token);

        Assert.Equal("session-123", sessionId);
        Assert.Equal("session-123", service.ActiveSessionId);
    }

    [Fact]
    public async Task PromptAndCancelSendExpectedMessages()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(cts.Token);

        await using AcpAgentHost host = AcpAgentHost.CreateForTests(Process.GetCurrentProcess(), client);
        IAcpAgentHostFactory hostFactory = new StubHostFactory(host);
        AcpService service = new(hostFactory);

        Task serverTask = Task.Run(() => HandlePromptAndCancelAsync(pipe, cts.Token), cts.Token);

        await service.ConnectAsync(new AcpAgentProcessOptions { FileName = "test" }, cts.Token);

        _ = await service.PromptAsync("session-456", new[] { new { type = "text", text = "hello" } }, cts.Token);
        await service.CancelAsync("session-456", cts.Token);

        await serverTask;
    }

    [Fact]
    public async Task ErrorResponseThrowsJsonRpcException()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(cts.Token);

        Task<JsonElement> requestTask = client.SendRequestAsync("initialize", new { }, cts.Token);

        JsonElement request = await ReadMessageAsync(pipe.ServerReader, cts.Token);
        long id = request.GetProperty("id").GetInt64();

        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            error = new { code = -32000, message = "auth required" }
        }));

        JsonRpcException ex = await Assert.ThrowsAsync<JsonRpcException>(async () =>
        {
            _ = await requestTask;
        });
        Assert.Equal(-32000, ex.Code);
    }

    private static async Task HandleSessionLifecycleAsync(DuplexPipe pipe, CancellationToken ct)
    {
        JsonElement initialize = await ReadMessageAsync(pipe.ServerReader, ct);
        Assert.Equal("initialize", initialize.GetProperty("method").GetString());

        long initId = initialize.GetProperty("id").GetInt64();
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = initId,
            result = new { protocolVersion = 1, agentInfo = new { name = "mock" } }
        }));

        JsonElement sessionNew = await ReadMessageAsync(pipe.ServerReader, ct);
        Assert.Equal("session/new", sessionNew.GetProperty("method").GetString());

        long sessionId = sessionNew.GetProperty("id").GetInt64();
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = sessionId,
            result = new { sessionId = "session-123" }
        }));
    }

    private static async Task HandlePromptAndCancelAsync(DuplexPipe pipe, CancellationToken ct)
    {
        JsonElement prompt = await ReadMessageAsync(pipe.ServerReader, ct);
        Assert.Equal("session/prompt", prompt.GetProperty("method").GetString());

        JsonElement parameters = prompt.GetProperty("params");
        Assert.Equal("session-456", parameters.GetProperty("sessionId").GetString());
        Assert.False(parameters.TryGetProperty("content", out _));
        Assert.Equal("text", parameters.GetProperty("prompt")[0].GetProperty("type").GetString());
        Assert.Equal("hello", parameters.GetProperty("prompt")[0].GetProperty("text").GetString());

        long promptId = prompt.GetProperty("id").GetInt64();
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = promptId,
            result = new { stopReason = "end_turn" }
        }));

        JsonElement cancel = await ReadMessageAsync(pipe.ServerReader, ct);
        Assert.Equal("session/cancel", cancel.GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("allow", "selected")]
    [InlineData(null, "cancelled")]
    [InlineData("unadvertised", "cancelled")]
    public async Task PermissionResponsesUseTheAcpDiscriminator(string? selected, string expected)
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(timeout.Token);
        await using AcpAgentHost host = AcpAgentHost.CreateForTests(Process.GetCurrentProcess(), client);
        AcpService service = new(new StubHostFactory(host));
        service.SetPermissionHandler((_, _) => Task.FromResult(selected is null
            ? AcpPermissionOutcome.Cancelled() : AcpPermissionOutcome.Selected(selected)));
        await service.ConnectAsync(new AcpAgentProcessOptions { FileName = "test" }, timeout.Token);
        await pipe.ServerWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":9,"method":"session/request_permission","params":{"sessionId":"s1","toolCall":{"toolCallId":"t1"},"options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}""");
        JsonElement response = await ReadMessageAsync(pipe.ServerReader, timeout.Token);
        JsonElement outcome = response.GetProperty("result").GetProperty("outcome");
        Assert.Equal(expected, outcome.GetProperty("outcome").GetString());
        Assert.Equal(expected == "selected", outcome.TryGetProperty("optionId", out _));
    }

    [Fact]
    public async Task CancelPromptCancelsPendingPermissionWithoutBlockingTheResponse()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(timeout.Token);
        await using AcpAgentHost host = AcpAgentHost.CreateForTests(Process.GetCurrentProcess(), client);
        AcpService service = new(new StubHostFactory(host));
        TaskCompletionSource permissionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SetPermissionHandler(async (_, ct) =>
        {
            permissionStarted.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return AcpPermissionOutcome.Selected("allow");
        });
        await service.ConnectAsync(new AcpAgentProcessOptions { FileName = "test" }, timeout.Token);
        Task<JsonElement> prompt = service.PromptAsync("s1", new[] { new { type = "text", text = "test" } }, timeout.Token);
        JsonElement promptRequest = await ReadMessageAsync(pipe.ServerReader, timeout.Token);
        await pipe.ServerWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":"permission","method":"session/request_permission","params":{"sessionId":"s1","options":[{"optionId":"allow","name":"Allow","kind":"allow_once"}]}}""");
        await permissionStarted.Task.WaitAsync(timeout.Token);
        await service.CancelAsync("s1", timeout.Token);
        JsonElement first = await ReadMessageAsync(pipe.ServerReader, timeout.Token);
        JsonElement second = await ReadMessageAsync(pipe.ServerReader, timeout.Token);
        JsonElement permission = first.TryGetProperty("result", out _) ? first : second;
        JsonElement cancellation = first.TryGetProperty("method", out _) ? first : second;
        Assert.Equal("cancelled", permission.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.Equal("session/cancel", cancellation.GetProperty("method").GetString());
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = promptRequest.GetProperty("id").GetInt64(), result = new { stopReason = "cancelled" }
        }));
        Assert.Equal("cancelled", (await prompt).GetProperty("stopReason").GetString());
    }

    [Fact]
    public async Task TerminalOutputIsLocalAndRemainsAvailableAfterRelease()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(timeout.Token);
        await using AcpAgentHost host = AcpAgentHost.CreateForTests(Process.GetCurrentProcess(), client);
        AcpService service = new(new StubHostFactory(host));
        await service.ConnectAsync(new AcpAgentProcessOptions { FileName = "test" }, timeout.Token);
        try
        {
            await pipe.ServerWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"terminal/create","params":{"sessionId":"s1","command":"dotnet","args":["--version"],"outputByteLimit":4096}}""");
            string terminalId = (await ReadMessageAsync(pipe.ServerReader, timeout.Token)).GetProperty("result").GetProperty("terminalId").GetString()!;
            foreach (string method in new[] { "terminal/wait_for_exit", "terminal/release" })
            {
                await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method, @params = new { sessionId = "s1", terminalId } }));
                JsonElement response = await ReadMessageAsync(pipe.ServerReader, timeout.Token);
                Assert.False(response.TryGetProperty("error", out _));
            }
            JsonElement output = Assert.IsType<JsonElement>(service.GetTerminalOutput(terminalId));
            Assert.False(string.IsNullOrWhiteSpace(output.GetProperty("output").GetString()));
            Assert.Equal(0, output.GetProperty("exitStatus").GetProperty("exitCode").GetInt32());
        }
        finally { await service.DisconnectAsync(); }
    }

    private static async Task<JsonElement> ReadMessageAsync(StreamReader reader, CancellationToken ct)
    {
        string? line = await reader.ReadLineAsync(ct);
        Assert.False(string.IsNullOrWhiteSpace(line));
        using JsonDocument doc = JsonDocument.Parse(line!);
        return doc.RootElement.Clone();
    }

    private sealed class DuplexPipe : IDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();
        private StreamReader? _serverReader;
        private StreamWriter? _serverWriter;

        public StreamReader ServerReader
        {
            get
            {
                _serverReader ??= new StreamReader(_clientToServer.Reader.AsStream());
                return _serverReader;
            }
        }

        public StreamWriter ServerWriter
        {
            get
            {
                _serverWriter ??= new StreamWriter(_serverToClient.Writer.AsStream()) { AutoFlush = true };
                return _serverWriter;
            }
        }

        public AcpProtocolClient CreateClient()
        {
            AcpMessageReader reader = new(_serverToClient.Reader.AsStream());
            AcpMessageWriter writer = new(_clientToServer.Writer.AsStream());
            return new AcpProtocolClient(reader, writer);
        }

        public void Dispose()
        {
            _serverReader?.Dispose();
            _serverWriter?.Dispose();
        }
    }

    private sealed class StubHostFactory : IAcpAgentHostFactory
    {
        private readonly AcpAgentHost _host;

        public StubHostFactory(AcpAgentHost host)
        {
            _host = host;
        }

        public Task<AcpAgentHost> StartAsync(AcpAgentProcessOptions options, CancellationToken ct)
        {
            return Task.FromResult(_host);
        }
    }
}
