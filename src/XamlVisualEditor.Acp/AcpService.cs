using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Acp;

public sealed class AcpService : IAcpService, IDisposable
{
    private readonly IAcpAgentHostFactory _hostFactory;
    private readonly IAcpSettings? _settings;
    private readonly AcpFileSystemHandler _fileSystemHandler = new();
    private readonly AcpTerminalManager _terminalManager = new();
    private Func<AcpPermissionRequest, CancellationToken, Task<AcpPermissionOutcome>>? _permissionHandler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private AcpAgentHost? _host;
    private string? _activeSessionId;
    private CancellationTokenSource? _permissionCancellation;
    private readonly object _permissionGate = new();

    public AcpService(IAcpAgentHostFactory hostFactory, IAcpSettings? settings = null)
    {
        _hostFactory = hostFactory;
        _settings = settings;
    }

    public bool IsConnected => _host?.Client.IsRunning == true;

    public string? ActiveSessionId => _activeSessionId;

    public JsonElement? GetTerminalOutput(string terminalId) => _terminalManager.GetOutput(terminalId);

    public event Action<string>? StderrReceived;

    public event Action<string, JsonElement?>? NotificationReceived;
    public event Action? ConnectionChanged;

    public async Task ConnectAsync(AcpAgentProcessOptions options, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await DisconnectHostAsync().ConfigureAwait(false);

            AcpAgentHost host = await _hostFactory.StartAsync(options, ct).ConfigureAwait(false);
            host.StderrReceived += HandleStderr;
            host.Client.NotificationReceived += HandleNotification;
            host.Client.Disconnected += HandleDisconnected;
            _fileSystemHandler.Register(host.Client);
            _terminalManager.Register(host.Client);
            host.Client.RegisterRequestHandler("session/request_permission", HandlePermissionRequestAsync);
            _host = host;
            if (!host.Client.IsRunning)
            {
                await DisconnectHostAsync().ConfigureAwait(false);
                throw new EndOfStreamException("The ACP agent exited during startup.");
            }
            ConnectionChanged?.Invoke();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ConnectMockAgentAsync(CancellationToken ct)
    {
        string? mockPath = _settings?.MockAgentPath;
        if (string.IsNullOrWhiteSpace(mockPath))
        {
            throw new InvalidOperationException("Mock agent path is not configured.");
        }

        if (!File.Exists(mockPath))
        {
            throw new FileNotFoundException("Mock agent executable not found.", mockPath);
        }

        AcpAgentProcessOptions options = new()
        {
            FileName = "dotnet",
            Arguments = $"\"{mockPath}\""
        };

        await ConnectAsync(options, ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await DisconnectHostAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DisconnectHostAsync()
    {
        AcpAgentHost? host = _host;
        _host = null;
        _activeSessionId = null;
        if (host is null) return;
        host.StderrReceived -= HandleStderr;
        host.Client.NotificationReceived -= HandleNotification;
        host.Client.Disconnected -= HandleDisconnected;
        _fileSystemHandler.Unregister(host.Client);
        _terminalManager.Unregister(host.Client);
        host.Client.TryRemoveRequestHandler("session/request_permission");
        await _terminalManager.ReleaseAllAsync().ConfigureAwait(false);
        await host.DisposeAsync().ConfigureAwait(false);
        ConnectionChanged?.Invoke();
    }

    private void HandleDisconnected(Exception? error)
    {
        _activeSessionId = null;
        ConnectionChanged?.Invoke();
        _ = DisconnectClosedHostAsync();
    }

    private async Task DisconnectClosedHostAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_host?.Client.IsRunning == false) await DisconnectHostAsync().ConfigureAwait(false);
        }
        catch (Exception error) { StderrReceived?.Invoke(error.Message); }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisconnectAsync().GetAwaiter().GetResult();

    public async Task<JsonElement> InitializeAsync(object? parameters, CancellationToken ct)
    {
        JsonElement result = await SendRequestAsync("initialize", parameters, ct).ConfigureAwait(false);
        if (!result.TryGetProperty("protocolVersion", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out int protocolVersion) || protocolVersion != 1)
            throw new JsonRpcException(-32603, "The agent did not negotiate ACP protocol version 1.");
        return result;
    }

    public async Task<string> CreateSessionAsync(object? parameters, CancellationToken ct)
    {
        JsonElement result = await SendRequestAsync("session/new", parameters, ct).ConfigureAwait(false);
        if (!result.TryGetProperty("sessionId", out JsonElement sessionIdElement)
            || sessionIdElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(sessionIdElement.GetString()))
            throw new JsonRpcException(-32603, "The agent returned an invalid session ID.");
        _activeSessionId = sessionIdElement.GetString();
        return _activeSessionId!;
    }

    public async Task<JsonElement> PromptAsync(string sessionId, object? content, CancellationToken ct)
    {
        using CancellationTokenSource permissions = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_permissionGate)
        {
            if (_permissionCancellation is not null) throw new InvalidOperationException("An ACP prompt is already running.");
            _permissionCancellation = permissions;
        }
        var payload = new
        {
            sessionId,
            prompt = content
        };

        try { return await SendRequestAsync("session/prompt", payload, ct).ConfigureAwait(false); }
        finally
        {
            lock (_permissionGate)
            {
                _permissionCancellation = null;
                permissions.Cancel();
            }
        }
    }

    public async Task CancelAsync(string sessionId, CancellationToken ct)
    {
        lock (_permissionGate) _permissionCancellation?.Cancel();
        var payload = new
        {
            sessionId
        };

        await SendNotificationAsync("session/cancel", payload, ct).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        AcpProtocolClient? client = _host?.Client;
        if (client is null)
        {
            throw new InvalidOperationException("ACP client is not connected.");
        }

        return await client.SendRequestAsync(method, parameters, ct).ConfigureAwait(false);
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        AcpProtocolClient? client = _host?.Client;
        if (client is null)
        {
            throw new InvalidOperationException("ACP client is not connected.");
        }

        await client.SendNotificationAsync(method, parameters, ct).ConfigureAwait(false);
    }

    public void SetPermissionHandler(Func<AcpPermissionRequest, CancellationToken, Task<AcpPermissionOutcome>>? handler)
    {
        _permissionHandler = handler;
    }

    private async Task<JsonElement?> HandlePermissionRequestAsync(JsonElement? parameters, CancellationToken ct)
    {
        AcpPermissionRequest request = AcpPermissionRequest.Parse(parameters);
        CancellationTokenSource cancellation;
        lock (_permissionGate) cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ct, _permissionCancellation?.Token ?? CancellationToken.None);
        using var permissionLifetime = cancellation;
        request.CancellationToken = cancellation.Token;
        AcpPermissionOutcome outcome;
        try
        {
            outcome = _permissionHandler is null ? AcpPermissionOutcome.Cancelled()
                : await _permissionHandler(request, cancellation.Token).WaitAsync(cancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            outcome = AcpPermissionOutcome.Cancelled();
        }

        if (outcome.IsCancelled || string.IsNullOrWhiteSpace(outcome.SelectedOptionId)
            || !request.Options.Any(option => option.OptionId == outcome.SelectedOptionId))
        {
            return JsonSerializer.SerializeToElement(new
            {
                outcome = new
                {
                    outcome = "cancelled"
                }
            });
        }

        return JsonSerializer.SerializeToElement(new
        {
            outcome = new
            {
                outcome = "selected",
                optionId = outcome.SelectedOptionId
            }
        });
    }

    private void HandleStderr(string message)
    {
        StderrReceived?.Invoke(message);
    }

    private void HandleNotification(string method, JsonElement? parameters)
    {
        NotificationReceived?.Invoke(method, parameters);
    }
}
