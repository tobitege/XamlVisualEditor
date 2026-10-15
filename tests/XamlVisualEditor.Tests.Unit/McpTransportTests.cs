using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Extensions.Hosting.Mcp;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class McpTransportTests
{
    private const string InitializeJson = """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":"test","version":"1"},"capabilities":{}}}""";
    private const string PingJson = """{"jsonrpc":"2.0","id":2,"method":"ping"}""";

    private static string InitializeJsonFor(string protocolVersion) =>
        InitializeJson.Replace("2025-11-25", protocolVersion, StringComparison.Ordinal);

    [Fact]
    public async Task HttpRejectsForeignOriginAndAcceptsOwnOrigin()
    {
        await using HttpHarness harness = HttpHarness.Start();
        using HttpRequestMessage foreign = harness.Post(InitializeJson);
        foreign.Headers.Add("Origin", "http://localhost.evil.example");
        using HttpResponseMessage rejected = await harness.Client.SendAsync(foreign);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal(0, harness.Registry.Count);

        using HttpRequestMessage own = harness.Post(InitializeJson);
        own.Headers.Add("Origin", $"http://127.0.0.1:{harness.Port}");
        using HttpResponseMessage accepted = await harness.Client.SendAsync(own);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(1, harness.Registry.Count);
    }

    [Fact]
    public async Task HttpRequiresAKnownSessionIdAfterInitialize()
    {
        await using HttpHarness harness = HttpHarness.Start();
        string sessionId = await harness.InitializeAsync();

        using HttpResponseMessage missing = await harness.Client.SendAsync(harness.Post(PingJson));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);

        using HttpResponseMessage unknown = await harness.Client.SendAsync(harness.Post(PingJson, "unknown-session"));
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        using HttpResponseMessage known = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await known.Content.ReadAsStringAsync());
        Assert.Equal(2, body.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(JsonValueKind.Object, body.RootElement.GetProperty("result").ValueKind);
    }

    [Fact]
    public async Task HttpDeleteEndsTheSession()
    {
        await using HttpHarness harness = HttpHarness.Start();
        string sessionId = await harness.InitializeAsync();

        using HttpResponseMessage withoutId = await harness.Client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, withoutId.StatusCode);

        using HttpResponseMessage deleted = await harness.Client.SendAsync(harness.Delete(sessionId));
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(0, harness.Registry.Count);

        using HttpResponseMessage afterDelete = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.NotFound, afterDelete.StatusCode);

        using HttpResponseMessage deletedAgain = await harness.Client.SendAsync(harness.Delete(sessionId));
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task HttpRejectsUnsupportedProtocolVersionHeader()
    {
        await using HttpHarness harness = HttpHarness.Start();
        string sessionId = await harness.InitializeAsync();

        using HttpRequestMessage unsupported = harness.Post(PingJson, sessionId);
        unsupported.Headers.Add("MCP-Protocol-Version", "1999-01-01");
        using HttpResponseMessage rejected = await harness.Client.SendAsync(unsupported);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        using HttpRequestMessage supported = harness.Post(PingJson, sessionId);
        supported.Headers.Add("MCP-Protocol-Version", McpProtocol.ProtocolVersion);
        using HttpResponseMessage accepted = await harness.Client.SendAsync(supported);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
    }

    [Fact]
    public async Task HttpNegotiatesAnOlderSupportedVersionAndValidatesTheHeaderAgainstIt()
    {
        await using HttpHarness harness = HttpHarness.Start();
        using HttpResponseMessage initialized = await harness.Client.SendAsync(harness.Post(InitializeJsonFor("2025-06-18")));
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await initialized.Content.ReadAsStringAsync());
        Assert.Equal("2025-06-18", result.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        string sessionId = Assert.Single(initialized.Headers.GetValues("Mcp-Session-Id"));

        using HttpRequestMessage matching = harness.Post(PingJson, sessionId);
        matching.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        using HttpResponseMessage accepted = await harness.Client.SendAsync(matching);
        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);

        using HttpResponseMessage withoutHeader = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.OK, withoutHeader.StatusCode);

        using HttpRequestMessage otherVersion = harness.Post(PingJson, sessionId);
        otherVersion.Headers.Add("MCP-Protocol-Version", McpProtocol.ProtocolVersion);
        using HttpResponseMessage rejected = await harness.Client.SendAsync(otherVersion);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Fact]
    public async Task HttpAnswersAVersionItDoesNotServeWithTheLatestVersion()
    {
        await using HttpHarness harness = HttpHarness.Start();
        using HttpResponseMessage initialized = await harness.Client.SendAsync(harness.Post(InitializeJsonFor("2024-11-05")));
        Assert.Equal(HttpStatusCode.OK, initialized.StatusCode);
        using JsonDocument result = JsonDocument.Parse(await initialized.Content.ReadAsStringAsync());
        Assert.Equal(McpProtocol.ProtocolVersion, result.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task HttpAnswersABatchFromA20250326Client()
    {
        await using HttpHarness harness = HttpHarness.Start();
        using HttpResponseMessage initialized = await harness.Client.SendAsync(harness.Post(InitializeJsonFor("2025-03-26")));
        using JsonDocument result = JsonDocument.Parse(await initialized.Content.ReadAsStringAsync());
        Assert.Equal("2025-03-26", result.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        string sessionId = Assert.Single(initialized.Headers.GetValues("Mcp-Session-Id"));

        const string batch = """[{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":3,"method":"ping"},{"jsonrpc":"2.0","id":"four","method":"ping"}]""";
        using HttpResponseMessage answered = await harness.Client.SendAsync(harness.Post(batch, sessionId));

        Assert.Equal(HttpStatusCode.OK, answered.StatusCode);
        using JsonDocument body = JsonDocument.Parse(await answered.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, body.RootElement.ValueKind);
        Assert.Equal(2, body.RootElement.GetArrayLength());
        Assert.Equal(3, body.RootElement[0].GetProperty("id").GetInt32());
        Assert.Equal("four", body.RootElement[1].GetProperty("id").GetString());
    }

    [Fact]
    public async Task StdioNegotiates20241105AndAnswersABatch()
    {
        McpSessionRegistry registry = new();
        McpRequestRouter router = new(registry);
        McpPermissionService permissions = new(new InMemorySettingsStore(), new InMemoryWindow());
        new McpHandshakeHandler(new McpSessionService(permissions, new FixedWorkspaceInfo("/tmp/xve")), registry).Register(router);
        byte[] input = Encoding.UTF8.GetBytes(InitializeJsonFor("2024-11-05") + "\n"
            + """[{"jsonrpc":"2.0","id":5,"method":"ping"},{"jsonrpc":"2.0","id":6,"method":"ping"}]""" + "\n");
        using MemoryStream output = new();
        await using McpJsonRpcConnection connection = new(new MemoryStream(input), output, router);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += error => { if (error is null) disconnected.TrySetResult(); else disconnected.TrySetException(error); };
        connection.Start(CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using JsonDocument initialized = JsonDocument.Parse(lines[0]);
        Assert.Equal("2024-11-05", initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("2024-11-05", registry.Resolve(new McpRequestContext(connection, null))?.ProtocolVersion);
        using JsonDocument batch = JsonDocument.Parse(lines[1]);
        Assert.Equal(new[] { 5, 6 }, batch.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetInt32()));
    }

    [Fact]
    public async Task HttpSessionExpiresAfterIdleTimeoutAndActivityExtendsIt()
    {
        ManualTimeProvider time = new();
        await using HttpHarness harness = HttpHarness.Start(new McpSessionRegistry(TimeSpan.FromMinutes(10), time));
        string sessionId = await harness.InitializeAsync();

        time.Advance(TimeSpan.FromMinutes(6));
        using HttpResponseMessage active = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);

        time.Advance(TimeSpan.FromMinutes(6));
        using HttpResponseMessage stillActive = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.OK, stillActive.StatusCode);

        time.Advance(TimeSpan.FromMinutes(10));
        using HttpResponseMessage expired = await harness.Client.SendAsync(harness.Post(PingJson, sessionId));
        Assert.Equal(HttpStatusCode.NotFound, expired.StatusCode);
        Assert.Equal(0, harness.Registry.Count);
    }

    [Fact]
    public async Task IdleSweepRemovesOnlyExpiredTokenSessions()
    {
        ManualTimeProvider time = new();
        McpSessionRegistry registry = new(TimeSpan.FromMinutes(10), time);
        int changes = 0;
        registry.Changed += () => changes++;
        registry.Set("stale", new McpSessionInfo("ws", McpAccessLevel.Full, "stale"));
        await using McpJsonRpcConnection connection = new(new MemoryStream(), new MemoryStream(), new McpRequestRouter(registry));
        registry.Set(connection, new McpSessionInfo("ws", McpAccessLevel.Full, "stdio"));
        time.Advance(TimeSpan.FromMinutes(5));
        registry.Set("fresh", new McpSessionInfo("ws", McpAccessLevel.Full, "fresh"));

        time.Advance(TimeSpan.FromMinutes(6));
        registry.ExpireIdle();

        Assert.Equal(2, registry.Count);
        Assert.Null(registry.Resolve(new McpRequestContext(null, "stale")));
        Assert.NotNull(registry.Resolve(new McpRequestContext(null, "fresh")));
        Assert.NotNull(registry.Resolve(new McpRequestContext(connection, null)));
        Assert.Equal(4, changes);
    }

    [Fact]
    public async Task StdioFramingWritesExactlyOneLinePerMessage()
    {
        using MemoryStream output = new();
        await McpMessageFraming.WriteMessageAsync(output, new { jsonrpc = "2.0", id = 1, result = new { text = "a\r\nb\nc" } }, CancellationToken.None);
        await McpMessageFraming.WriteMessageAsync(output, new { jsonrpc = "2.0", method = "notifications/test" }, CancellationToken.None);

        byte[] bytes = output.ToArray();
        Assert.Equal((byte)'{', bytes[0]);
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        string[] lines = Encoding.UTF8.GetString(bytes).Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Empty, lines[2]);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal("a\r\nb\nc", first.RootElement.GetProperty("result").GetProperty("text").GetString());
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("notifications/test", second.RootElement.GetProperty("method").GetString());
    }

    [Fact]
    public async Task StdioFramingReadsCrLfLinesSkipsBlankLinesAndReportsEndOfInput()
    {
        byte[] input = Encoding.UTF8.GetBytes("\r\n\n{\"id\":1}\r\n   \n{\"id\":2}\n");
        using StreamReader reader = new(new TrickleStream(input), Encoding.UTF8);

        using JsonDocument first = await McpMessageFraming.ReadMessageAsync(reader, CancellationToken.None);
        using JsonDocument second = await McpMessageFraming.ReadMessageAsync(reader, CancellationToken.None);

        Assert.Equal(1, first.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(2, second.RootElement.GetProperty("id").GetInt32());
        await Assert.ThrowsAsync<EndOfStreamException>(() => McpMessageFraming.ReadMessageAsync(reader, CancellationToken.None));
    }

    [Fact]
    public async Task StdioConnectionAnswersRequestsDeliveredByteByByte()
    {
        McpRequestRouter router = new(new McpSessionRegistry());
        router.Register("ping", (_, _, _) => Task.FromResult<object?>(new { }));
        byte[] input = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}\r\n{\"jsonrpc\":\"2.0\",\"id\":\"x\",\"method\":\"ping\"}");
        using MemoryStream output = new();
        await using McpJsonRpcConnection connection = new(new TrickleStream(input), output, router);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += error => { if (error is null) disconnected.TrySetResult(); else disconnected.TrySetException(error); };
        connection.Start(CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        using JsonDocument first = JsonDocument.Parse(lines[0]);
        Assert.Equal(7, first.RootElement.GetProperty("id").GetInt32());
        using JsonDocument second = JsonDocument.Parse(lines[1]);
        Assert.Equal("x", second.RootElement.GetProperty("id").GetString());
    }

    private sealed class HttpHarness : IAsyncDisposable
    {
        private const int PortAttempts = 5;
        private readonly McpServer _server;

        private HttpHarness(McpServer server, McpSessionRegistry registry, int port)
        {
            _server = server;
            Registry = registry;
            Port = port;
            Client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/"), Timeout = TimeSpan.FromSeconds(5) };
        }

        public McpSessionRegistry Registry { get; }

        public int Port { get; }

        public HttpClient Client { get; }

        public static HttpHarness Start(McpSessionRegistry? registry = null)
        {
            registry ??= new McpSessionRegistry();
            McpPermissionService permissions = new(new InMemorySettingsStore(), new InMemoryWindow());
            McpHandshakeHandler handshake = new(new McpSessionService(permissions, new FixedWorkspaceInfo("/tmp/xve")), registry);
            for (int attempt = 1; ; attempt++)
            {
                int port = GetAvailablePort();
                McpServer server = new(new McpServerOptions { EnableStdio = false, EnableHttp = true, HttpPort = port }, new IMcpRequestHandler[] { handshake }, registry);
                try
                {
                    server.Start(CancellationToken.None);
                    return new HttpHarness(server, registry, port);
                }
                catch (HttpListenerException) when (attempt < PortAttempts)
                {
                    // Another process took the probed port before the server bound it.
                    server.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
        }

        public HttpRequestMessage Post(string json, string? sessionId = null)
        {
            HttpRequestMessage request = new(HttpMethod.Post, string.Empty)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            if (sessionId is not null)
            {
                request.Headers.Add("Mcp-Session-Id", sessionId);
            }

            return request;
        }

        public HttpRequestMessage Delete(string sessionId)
        {
            HttpRequestMessage request = new(HttpMethod.Delete, string.Empty);
            request.Headers.Add("Mcp-Session-Id", sessionId);
            return request;
        }

        public async Task<string> InitializeAsync()
        {
            using HttpResponseMessage response = await Client.SendAsync(Post(InitializeJson));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return Assert.Single(response.Headers.GetValues("Mcp-Session-Id"));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _server.DisposeAsync();
        }

        private static int GetAvailablePort()
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }

    private sealed class FixedWorkspaceInfo : IWorkspaceInfo
    {
        public FixedWorkspaceInfo(string workspacePath)
        {
            WorkspacePath = workspacePath;
        }

        public string? WorkspacePath { get; }

        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>Returns at most one byte per read to exercise line reassembly.</summary>
    private sealed class TrickleStream : MemoryStream
    {
        public TrickleStream(byte[] buffer) : base(buffer, writable: false)
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => base.ReadAsync(buffer, offset, Math.Min(count, 1), cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
    }
}
