using System;
using System.Linq;
using System.Net;
using System.Text.Json;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Configures MCP server transports.</summary>
public sealed class McpServerOptions
{
    public bool EnableStdio { get; set; } = true;

    public bool EnableHttp { get; set; } = true;

    public int HttpPort { get; set; } = 4712;

    public string HttpPath { get; set; } = "/mcp/";
}

/// <summary>Hosts MCP JSON-RPC connections.</summary>
public sealed class McpServer : IAsyncDisposable
{
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromMinutes(1);
    private readonly McpServerOptions _options;
    private readonly IReadOnlyList<IMcpRequestHandler> _handlers;
    private readonly McpSessionRegistry _sessions;
    private readonly List<McpJsonRpcConnection> _connections = new();
    private readonly List<Task> _acceptLoops = new();
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private McpRequestRouter? _router;
    private McpHttpServer? _httpServer;
    private ITimer? _idleSweep;

    public event EventHandler<McpConnectionChangedEventArgs>? ConnectionChanged;

    public int ConnectionCount => _sessions.Count;

    public McpServer(McpServerOptions options, IEnumerable<IMcpRequestHandler> handlers, McpSessionRegistry sessions)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _handlers = handlers?.ToList() ?? throw new ArgumentNullException(nameof(handlers));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _sessions.Changed += OnSessionsChanged;
    }

    private void OnSessionsChanged() => ConnectionChanged?.Invoke(this,
        new McpConnectionChangedEventArgs(ConnectionCount, DateTimeOffset.UtcNow));

    public void Start(CancellationToken ct)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _router = new McpRequestRouter(_sessions);
        foreach (IMcpRequestHandler handler in _handlers)
        {
            handler.Register(_router);
        }

        if (_options.EnableHttp)
        {
            StartHttp(_cts.Token);
        }
        if (_options.EnableStdio)
        {
            StartStdio(_cts.Token);
        }
    }

    private void StartStdio(CancellationToken ct)
    {
        if (_router is null)
        {
            return;
        }

        Stream input = Console.OpenStandardInput();
        Stream output = Console.OpenStandardOutput();
        McpJsonRpcConnection connection = new(input, output, _router, ownsStreams: false);
        connection.Disconnected += _ => RemoveConnection(connection);
        AddConnection(connection);
        connection.Start(ct);
    }

    private void StartHttp(CancellationToken ct)
    {
        if (_router is null)
        {
            return;
        }

        _httpServer = new McpHttpServer(_options.HttpPort, _options.HttpPath, _router, _sessions);
        _acceptLoops.Add(_httpServer.StartAsync(ct));
        _idleSweep = _sessions.TimeProvider.CreateTimer(
            static state => ((McpSessionRegistry)state!).ExpireIdle(), _sessions, IdleSweepInterval, IdleSweepInterval);
    }

    private void AddConnection(McpJsonRpcConnection connection)
    {
        lock (_sync)
        {
            _connections.Add(connection);
        }

    }

    private void RemoveConnection(McpJsonRpcConnection connection)
    {
        lock (_sync)
        {
            _connections.Remove(connection);
            _sessions.Remove(connection);
        }

    }

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _idleSweep?.Dispose();
        _sessions.Changed -= OnSessionsChanged;
        Task[] loops;
        lock (_sync)
        {
            loops = _acceptLoops.ToArray();
        }

        if (loops.Length > 0)
        {
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        McpJsonRpcConnection[] connections;
        lock (_sync)
        {
            connections = _connections.ToArray();
            _connections.Clear();
        }

        foreach (McpJsonRpcConnection connection in connections)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        if (_httpServer is not null)
        {
            await _httpServer.DisposeAsync().ConfigureAwait(false);
        }

        _cts?.Dispose();
        _sessions.Revoke(null);
    }

    private sealed class McpHttpServer : IAsyncDisposable
    {
        private readonly int _port;
        private readonly string _path;
        private readonly McpRequestRouter _router;
        private readonly McpSessionRegistry _sessions;
        private readonly HttpListener _listener = new();
        private CancellationTokenSource? _cts;
        private Task? _loop;
        private CancellationTokenRegistration _shutdownRegistration;

        public McpHttpServer(int port, string path, McpRequestRouter router, McpSessionRegistry sessions)
        {
            _port = port;
            string normalized = string.IsNullOrWhiteSpace(path) ? "/mcp/" : path;
            if (!normalized.StartsWith("/", StringComparison.Ordinal))
            {
                normalized = "/" + normalized;
            }
            if (!normalized.EndsWith("/", StringComparison.Ordinal))
            {
                normalized += "/";
            }

            _path = normalized;
            _router = router;
            _sessions = sessions;
        }

        public Task StartAsync(CancellationToken ct)
        {
            if (_loop is not null)
            {
                return _loop;
            }

            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            string prefix = $"http://127.0.0.1:{_port}{_path}";
            _listener.Prefixes.Add(prefix);
            _listener.Start();
            _shutdownRegistration = _cts.Token.Register(static state =>
            {
                if (state is HttpListener listener)
                {
                    try
                    {
                        listener.Close();
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                    catch (HttpListenerException)
                    {
                    }
                }
            }, _listener);
            _loop = Task.Run(() => AcceptLoopAsync(_cts.Token), _cts.Token);
            return _loop;
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    HttpListenerContext context = await _listener.GetContextAsync().ConfigureAwait(false);
                    _ = Task.Run(() => HandleRequestAsync(context, ct));
                }
            }
            catch (HttpListenerException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            catch (OperationCanceledException)
            {
            }
            // Closing the listener during a pending accept can also report its released handle as invalid.
            catch (ArgumentException) when (ct.IsCancellationRequested)
            {
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context, CancellationToken ct)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;
            try
            {
                string? origin = request.Headers["Origin"];
                if (origin is not null && (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? originUri)
                    || originUri.Scheme != request.Url?.Scheme || originUri.Authority != request.Url?.Authority))
                {
                    response.StatusCode = (int)HttpStatusCode.Forbidden;
                    return;
                }

                string? sessionToken = request.Headers["Mcp-Session-Id"];
                if (request.HttpMethod == "DELETE")
                {
                    response.StatusCode = string.IsNullOrEmpty(sessionToken) ? (int)HttpStatusCode.BadRequest
                        : _sessions.Remove(sessionToken) ? (int)HttpStatusCode.OK : (int)HttpStatusCode.NotFound;
                    return;
                }
                if (request.HttpMethod != "POST")
                {
                    response.Headers["Allow"] = "POST, DELETE";
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }
                if (request.ContentType?.Split(';')[0].Trim() != "application/json")
                {
                    response.StatusCode = (int)HttpStatusCode.UnsupportedMediaType;
                    return;
                }
                string? protocolVersion = request.Headers["MCP-Protocol-Version"];
                if (protocolVersion is not null && !McpProtocol.HttpProtocolVersions.Contains(protocolVersion, StringComparer.Ordinal))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }

                using JsonDocument message = await JsonDocument.ParseAsync(request.InputStream, cancellationToken: ct).ConfigureAwait(false);
                JsonElement root = message.RootElement;
                // A 2025-03-26 client may send a batch: an array of requests and notifications.
                bool isBatch = root.ValueKind == JsonValueKind.Array;
                JsonElement[] messages = isBatch ? root.EnumerateArray().ToArray() : [root];
                if (messages.Length == 0 || !messages.All(IsValidMessage))
                {
                    response.StatusCode = (int)HttpStatusCode.BadRequest;
                    return;
                }
                McpRequestContext ctx = new(null, sessionToken);
                if (messages.Any(item => GetMethod(item) == McpProtocol.InitializeMethod))
                {
                    if (isBatch || !root.TryGetProperty("id", out _))
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                }
                else
                {
                    McpSessionInfo? session = _sessions.Resolve(ctx);
                    if (session is null)
                    {
                        response.StatusCode = string.IsNullOrEmpty(sessionToken)
                            ? (int)HttpStatusCode.BadRequest : (int)HttpStatusCode.NotFound;
                        return;
                    }
                    // Without the header the version negotiated at initialization applies.
                    if (protocolVersion is not null && protocolVersion != session.ProtocolVersion)
                    {
                        response.StatusCode = (int)HttpStatusCode.BadRequest;
                        return;
                    }
                }

                List<object> payloads = new();
                foreach (JsonElement item in messages)
                {
                    string method = GetMethod(item)!;
                    JsonElement? parameters = item.TryGetProperty("params", out JsonElement paramsElement) ? paramsElement.Clone() : null;
                    if (!item.TryGetProperty("id", out JsonElement idElement))
                    {
                        if (method == McpProtocol.InitializedNotification)
                            await _router.DispatchAsync(method, ctx, parameters, ct).ConfigureAwait(false);
                        continue;
                    }
                    TryGetIdValue(idElement, out object? idValue);
                    payloads.Add(await DispatchRequestAsync(method, ctx, parameters, idValue!, response, ct).ConfigureAwait(false));
                }
                if (payloads.Count == 0)
                {
                    response.StatusCode = (int)HttpStatusCode.Accepted;
                    return;
                }

                object payload = isBatch ? payloads : payloads[0];
                byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, McpMessageFraming.SerializerOptions);
                response.ContentType = "application/json";
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body, ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                response.StatusCode = (int)HttpStatusCode.BadRequest;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (HttpListenerException) { }
            finally { response.Close(); }
        }

        private async Task<object> DispatchRequestAsync(string method, McpRequestContext ctx, JsonElement? parameters, object idValue,
            HttpListenerResponse response, CancellationToken ct)
        {
            try
            {
                object? result = await _router.DispatchAsync(method, ctx, parameters, ct).ConfigureAwait(false);
                if (result is McpInitializeResult initialized)
                    response.Headers["Mcp-Session-Id"] = initialized.SessionToken;
                return new { jsonrpc = "2.0", id = idValue, result = result ?? new { } };
            }
            catch (McpJsonRpcException ex)
            {
                return new { jsonrpc = "2.0", id = idValue, error = new { code = ex.Code, message = ex.Message } };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new { jsonrpc = "2.0", id = idValue, error = new { code = -32603, message = ex.Message } };
            }
        }

        // A JSON-RPC 2.0 request with a string or integer id, or a notification.
        private static bool IsValidMessage(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("jsonrpc", out JsonElement version)
            && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0"
            && !string.IsNullOrEmpty(GetMethod(element))
            && (!element.TryGetProperty("id", out JsonElement id) || TryGetIdValue(id, out object? idValue) && idValue is not null);

        private static string? GetMethod(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty("method", out JsonElement methodElement)
            && methodElement.ValueKind == JsonValueKind.String ? methodElement.GetString() : null;

        private static bool TryGetIdValue(JsonElement element, out object? id)
        {
            id = null;
            switch (element.ValueKind)
            {
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out long numberId))
                    {
                        id = numberId;
                        return true;
                    }
                    return false;
                case JsonValueKind.String:
                    id = element.GetString();
                    return id is not null;
                default:
                    return false;
            }
        }

        public ValueTask DisposeAsync()
        {
            _cts?.Cancel();
            _shutdownRegistration.Dispose();
            _listener.Close();
            _cts?.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Provides connection count changes.</summary>
public sealed class McpConnectionChangedEventArgs : EventArgs
{
    public McpConnectionChangedEventArgs(int connectionCount, DateTimeOffset timestamp)
    {
        ConnectionCount = connectionCount;
        Timestamp = timestamp;
    }

    public int ConnectionCount { get; }

    public DateTimeOffset Timestamp { get; }
}
