using System;
using System.IO;
using System.IO.Pipelines;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Acp;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class AcpProtocolClientTests
{
    [Fact]
    public async Task SendRequestReceivesResponse()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();

        client.Start(cts.Token);

        Task<JsonElement> responseTask = client.SendRequestAsync("initialize", new { foo = "bar" }, cts.Token);

        string? requestLine = await pipe.ServerReader.ReadLineAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(requestLine));

        using JsonDocument requestDoc = JsonDocument.Parse(requestLine!);
        long id = requestDoc.RootElement.GetProperty("id").GetInt64();

        var response = new
        {
            jsonrpc = "2.0",
            id,
            result = new { ok = true }
        };

        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(response));

        JsonElement result = await responseTask;
        Assert.True(result.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task NotificationIsRaised()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();

        TaskCompletionSource<(string Method, JsonElement? Params)> tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.NotificationReceived += (method, parameters) => tcs.TrySetResult((method, parameters));

        client.Start(cts.Token);

        var payload = new
        {
            jsonrpc = "2.0",
            method = "session/update",
            @params = new { sessionId = "s1" }
        };

        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(payload));

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(tcs.Task.IsCompleted, "Expected notification was not received.");
        var result = await tcs.Task;
        Assert.Equal("session/update", result.Method);
    }

    [Fact]
    public async Task RequestHandlerSendsResult()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();

        client.RegisterRequestHandler("ping", (_, _) =>
        {
            using JsonDocument doc = JsonDocument.Parse("{\"pong\":true}");
            JsonElement result = doc.RootElement.Clone();
            return Task.FromResult<JsonElement?>(result);
        });

        client.Start(cts.Token);

        var request = new
        {
            jsonrpc = "2.0",
            id = 99,
            method = "ping",
            @params = new { }
        };

        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(request));

        string? responseLine = await pipe.ServerReader.ReadLineAsync(cts.Token);
        Assert.False(string.IsNullOrWhiteSpace(responseLine));

        using JsonDocument responseDoc = JsonDocument.Parse(responseLine!);
        JsonElement root = responseDoc.RootElement;
        Assert.Equal(99, root.GetProperty("id").GetInt32());
        Assert.True(root.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingRequestEndsWhenCancelledOrDisconnected(bool disconnect)
    {
        using DuplexPipe pipe = new();
        await using AcpProtocolClient client = pipe.CreateClient();
        client.Start(CancellationToken.None);
        using CancellationTokenSource cancellation = new();
        Task<JsonElement> response = client.SendRequestAsync("session/prompt", new { }, cancellation.Token);
        await pipe.ServerReader.ReadLineAsync();
        if (disconnect)
        {
            pipe.ServerWriter.Dispose();
            await Assert.ThrowsAsync<EndOfStreamException>(() => response.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(client.IsRunning);
        }
        else
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => response.WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    [Fact]
    public async Task WaitingAgentRequestDoesNotBlockOtherRequests()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.RegisterRequestHandler("slow", async (_, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return JsonSerializer.SerializeToElement(new { });
        });
        client.RegisterRequestHandler("ping", (_, _) => Task.FromResult<JsonElement?>(JsonSerializer.SerializeToElement(new { })));
        client.Start(timeout.Token);
        await pipe.ServerWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":1,"method":"slow"}""");
        await pipe.ServerWriter.WriteLineAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        using JsonDocument fast = JsonDocument.Parse((await pipe.ServerReader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(2, fast.RootElement.GetProperty("id").GetInt32());
        release.SetResult();
        using JsonDocument slow = JsonDocument.Parse((await pipe.ServerReader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(1, slow.RootElement.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task FileCallbacksPreserveTextAndAllowAnEmptyFile()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        await using AcpProtocolClient client = pipe.CreateClient();
        new AcpFileSystemHandler().Register(client);
        client.Start(timeout.Token);
        string path = Path.Combine(AppContext.BaseDirectory, "acp-file-" + Guid.NewGuid().ToString("N") + ".txt");
        string original = "\n\nGrüße\r\n";
        await File.WriteAllTextAsync(path, original);
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "fs/read_text_file", @params = new { sessionId = "s", path } }));
        using JsonDocument read = JsonDocument.Parse((await pipe.ServerReader.ReadLineAsync(timeout.Token))!);
        Assert.Equal(original, read.RootElement.GetProperty("result").GetProperty("content").GetString());
        await pipe.ServerWriter.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "fs/write_text_file", @params = new { sessionId = "s", path, content = "" } }));
        using JsonDocument write = JsonDocument.Parse((await pipe.ServerReader.ReadLineAsync(timeout.Token))!);
        Assert.True(write.RootElement.TryGetProperty("result", out _));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(path));
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
}
