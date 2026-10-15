#if CDP_DIAGNOSTICS
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Chrome.DevTools.Protocol;
using Serilog;
using AvaloniaCdpServer = Avalonia.Diagnostics.Cdp.CdpServer;
using ProtocolCdpServer = Chrome.DevTools.Protocol.CdpServer;
using ProtocolCdpSession = Chrome.DevTools.Protocol.CdpSession;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// CDP diagnostics endpoint on the loopback interface, enabled by XVE_CDP_PORT in builds with EnableCdpDiagnostics.
/// The CDP library listener accepts any local client and has no request hook, so it runs on an internal port and
/// refuses to create sessions; clients connect through a guard that requires the per-process path token from the
/// application log and rejects web page origins.
/// </summary>
internal sealed class CdpDiagnostics : IDisposable
{
    private const int MaxRequestHeadBytes = 16 * 1024;
    private static readonly TimeSpan RequestHeadTimeout = TimeSpan.FromSeconds(10);

    [ThreadStatic]
    private static bool _creatingGuardedSession;

    private Func<WebSocket, ICdpTarget?, ProtocolCdpSession>? _librarySessionFactory;
    private CdpAccessPolicy? _policy;
    private TcpListener? _listener;
    private CancellationTokenSource? _cancellation;
    private int _port;
    private bool _started;

    public void StartFromEnvironment()
    {
        string? setting = Environment.GetEnvironmentVariable("XVE_CDP_PORT");
        if (_started || string.IsNullOrWhiteSpace(setting) || setting == "0") return;
        if (!int.TryParse(setting, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port < 1 || port > 65535)
        {
            Log.Warning("XVE_CDP_PORT must be a TCP port from 1 to 65535; CDP remains disabled");
            return;
        }

        try
        {
            AvaloniaCdpServer.EnsureInitialized();
            _librarySessionFactory = ProtocolCdpServer.SessionFactory;
            ProtocolCdpServer.SessionFactory = CreateSessionIfGuarded;
            AvaloniaCdpServer.Start(GetFreeLoopbackPort());

            _policy = new CdpAccessPolicy(CdpAccessPolicy.CreateToken());
            _listener = new TcpListener(IPAddress.Loopback, port);
            _listener.Start();
            _port = port;
            _cancellation = new CancellationTokenSource();
            _started = true;
            _ = AcceptLoopAsync(_listener, _cancellation.Token);
            Log.Information(
                "CDP diagnostics listening at http://127.0.0.1:{CdpPort}{CdpPath}",
                port, _policy.BuildPath("/json"));
        }
        catch (Exception exception)
        {
            Stop();
            Log.Error(exception, "Could not start CDP diagnostics on port {CdpPort}", port);
        }
    }

    public void Dispose()
    {
        if (!_started) return;
        Stop();
    }

    private void Stop()
    {
        _started = false;
        _cancellation?.Cancel();
        _listener?.Stop();
        _listener = null;
        AvaloniaCdpServer.Stop();
        if (_librarySessionFactory is not null) ProtocolCdpServer.SessionFactory = _librarySessionFactory;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    private ProtocolCdpSession CreateSessionIfGuarded(WebSocket webSocket, ICdpTarget? target)
    {
        if (!_creatingGuardedSession)
        {
            throw new UnauthorizedAccessException("CDP sessions are only created through the guarded endpoint");
        }

        return _librarySessionFactory?.Invoke(webSocket, target) ?? new ProtocolCdpSession(webSocket, target);
    }

    private static int GetFreeLoopbackPort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (SocketException exception)
            {
                Log.Warning(exception, "CDP diagnostics accept failed");
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                CdpHttpRequestHead? head = await ReadRequestHeadAsync(stream, cancellationToken);
                CdpAccessPolicy? policy = _policy;
                if (head is null || policy is null)
                {
                    await WriteStatusAsync(stream, "400 Bad Request", cancellationToken);
                    return;
                }

                CdpRequestDecision decision = policy.Evaluate(head);
                switch (decision.Kind)
                {
                    case CdpRequestKind.Version:
                        await WriteJsonAsync(stream, CreateVersion(policy), cancellationToken);
                        break;
                    case CdpRequestKind.TargetList:
                        await WriteJsonAsync(stream, CreateTargetList(policy), cancellationToken);
                        break;
                    case CdpRequestKind.BrowserSocket:
                    case CdpRequestKind.PageSocket:
                        await RunSessionAsync(stream, head, decision, cancellationToken);
                        break;
                    case CdpRequestKind.Forbidden:
                        Log.Warning("CDP diagnostics refused a request without valid token or with origin {Origin}",
                            head.GetHeader("Origin"));
                        await WriteStatusAsync(stream, "403 Forbidden", cancellationToken);
                        break;
                    default:
                        await WriteStatusAsync(stream, "404 Not Found", cancellationToken);
                        break;
                }
            }
            catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException
                                                  or WebSocketException)
            {
                Log.Debug(exception, "CDP diagnostics connection closed");
            }
        }
    }

    private static async Task RunSessionAsync(
        NetworkStream stream, CdpHttpRequestHead head, CdpRequestDecision decision, CancellationToken cancellationToken)
    {
        IEnumerable<ICdpTarget> targets = ProtocolCdpServer.GetTargets();
        ICdpTarget? target = decision.Kind == CdpRequestKind.PageSocket
            ? targets.FirstOrDefault(candidate => candidate.Id == decision.TargetId)
            : targets.FirstOrDefault();
        if (target is null)
        {
            await WriteStatusAsync(stream, "404 Not Found", cancellationToken);
            return;
        }

        string accept = CdpAccessPolicy.ComputeWebSocketAccept(head.GetHeader("Sec-WebSocket-Key")!);
        await WriteAsync(stream,
            "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n"
            + "Sec-WebSocket-Accept: " + accept + "\r\n\r\n",
            cancellationToken);

        WebSocket webSocket = WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
        });
        ProtocolCdpSession session;
        _creatingGuardedSession = true;
        try
        {
            session = ProtocolCdpServer.SessionFactory?.Invoke(webSocket, target)
                      ?? new ProtocolCdpSession(webSocket, target);
        }
        finally
        {
            _creatingGuardedSession = false;
        }

        await session.StartAsync();
    }

    private static async Task<CdpHttpRequestHead?> ReadRequestHeadAsync(
        NetworkStream stream, CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestHeadTimeout);
        byte[] buffer = new byte[MaxRequestHeadBytes];
        int length = 0;
        // Reads byte-wise so that no WebSocket frame bytes after the request head are consumed.
        while (length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(length, 1), timeout.Token);
            if (read == 0) return null;
            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n'
                && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                string text = Encoding.ASCII.GetString(buffer, 0, length - 4);
                return CdpHttpRequestHead.TryParse(text, out CdpHttpRequestHead? head) ? head : null;
            }
        }

        return null;
    }

    private JsonObject CreateVersion(CdpAccessPolicy policy) => new()
    {
        ["Browser"] = "XamlVisualEditor",
        ["Protocol-Version"] = "1.3",
        ["webSocketDebuggerUrl"] = WebSocketUrl(policy, "/devtools/browser"),
    };

    private JsonArray CreateTargetList(CdpAccessPolicy policy)
    {
        JsonArray targets = [];
        foreach (ICdpTarget target in ProtocolCdpServer.GetTargets())
        {
            if (target.Type == "tab") continue;
            string socketPath = "/devtools/page/" + Uri.EscapeDataString(target.Id);
            targets.Add(new JsonObject
            {
                ["description"] = "",
                ["devtoolsFrontendUrl"] = "devtools://devtools/bundled/inspector.html?ws="
                                          + Authority + policy.BuildPath(socketPath),
                ["id"] = target.Id,
                ["title"] = target.Title,
                ["type"] = target.Type,
                ["url"] = target.Url,
                ["webSocketDebuggerUrl"] = WebSocketUrl(policy, socketPath),
            });
        }

        return targets;
    }

    private string Authority => "127.0.0.1:" + _port.ToString(CultureInfo.InvariantCulture);

    private string WebSocketUrl(CdpAccessPolicy policy, string path) => "ws://" + Authority + policy.BuildPath(path);

    private static Task WriteJsonAsync(NetworkStream stream, JsonNode json, CancellationToken cancellationToken)
    {
        byte[] body = Encoding.UTF8.GetBytes(json.ToJsonString());
        string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: "
                      + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\nConnection: close\r\n\r\n";
        return WriteAsync(stream, head, cancellationToken, body);
    }

    private static Task WriteStatusAsync(NetworkStream stream, string status, CancellationToken cancellationToken) =>
        WriteAsync(stream, "HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", cancellationToken);

    private static async Task WriteAsync(
        NetworkStream stream, string head, CancellationToken cancellationToken, byte[]? body = null)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), cancellationToken);
        if (body is not null) await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}
#endif
