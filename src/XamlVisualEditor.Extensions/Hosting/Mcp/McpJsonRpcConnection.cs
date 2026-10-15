using System.Collections.Concurrent;
using System.Text.Json;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Handles a JSON-RPC connection over a stream transport.</summary>
public sealed class McpJsonRpcConnection : IAsyncDisposable
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly bool _ownsStreams;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly McpRequestRouter _router;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public McpJsonRpcConnection(Stream input, Stream output, McpRequestRouter router, bool ownsStreams = false)
    {
        _input = input;
        _output = output;
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _ownsStreams = ownsStreams;
    }

    public event Action<string, JsonElement?>? NotificationReceived;

    public event Action<Exception?>? Disconnected;

    public void Start(CancellationToken ct)
    {
        if (_readLoop is not null)
        {
            return;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _readLoop = Task.Run(() => ReadLoopAsync(_cts.Token), _cts.Token);
    }

    public Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method name is required.", nameof(method));
        }

        var payload = new
        {
            jsonrpc = "2.0",
            method,
            @params = parameters
        };

        return SendAsync(payload, ct);
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await McpMessageFraming.WriteMessageAsync(_output, payload, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? error = null;
        using StreamReader reader = new(_input, System.Text.Encoding.UTF8, false, 4096, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                JsonDocument message;
                try { message = await McpMessageFraming.ReadMessageAsync(reader, ct).ConfigureAwait(false); }
                catch (JsonException)
                {
                    await SendErrorAsync(null, -32700, "Parse error", ct).ConfigureAwait(false);
                    continue;
                }
                using JsonDocument messageLifetime = message;
                JsonElement root = message.RootElement;
                // A 2025-03-26 client may send a batch: an array of requests and notifications, answered by one array.
                if (root.ValueKind == JsonValueKind.Array)
                {
                    List<object> responses = new();
                    foreach (JsonElement item in root.EnumerateArray())
                    {
                        if (await ProcessMessageAsync(item, ct).ConfigureAwait(false) is { } itemResponse)
                            responses.Add(itemResponse);
                    }
                    if (root.GetArrayLength() == 0)
                        responses.Add(CreateErrorPayload(null, -32600, "Invalid Request"));
                    if (responses.Count > 0)
                        await SendAsync(responses, ct).ConfigureAwait(false);
                    continue;
                }

                if (await ProcessMessageAsync(root, ct).ConfigureAwait(false) is { } response)
                    await SendAsync(response, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            Disconnected?.Invoke(error);
        }
    }

    // Handles one message and returns the response to send, or null for notifications and ignored messages.
    private async Task<object?> ProcessMessageAsync(JsonElement root, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("jsonrpc", out JsonElement version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0")
        {
            return CreateErrorPayload(null, -32600, "Invalid Request");
        }

        bool hasId = root.TryGetProperty("id", out JsonElement idElement);
        if (!root.TryGetProperty("method", out JsonElement methodElement))
        {
            return null;
        }

        string? method = methodElement.GetString();
        if (string.IsNullOrWhiteSpace(method))
        {
            return null;
        }

        JsonElement? parameters = root.TryGetProperty("params", out JsonElement paramsElement)
            ? paramsElement.Clone()
            : null;

        if (hasId)
        {
            return await HandleRequestAsync(method, idElement, parameters, ct).ConfigureAwait(false);
        }

        if (method == McpProtocol.InitializedNotification)
        {
            await _router.DispatchAsync(method, new McpRequestContext(this, null), parameters, ct).ConfigureAwait(false);
        }
        NotificationReceived?.Invoke(method, parameters);
        return null;
    }

    private async Task<object?> HandleRequestAsync(string method, JsonElement idElement, JsonElement? parameters, CancellationToken ct)
    {
        if (!TryGetIdValue(idElement, out object? idValue) || idValue is null)
        {
            return null;
        }

        McpRequestContext context = new(this, sessionToken: null);
        try
        {
            object? result = await _router.DispatchAsync(method, context, parameters, ct).ConfigureAwait(false);
            return new
            {
                jsonrpc = "2.0",
                id = idValue,
                result
            };
        }
        catch (McpJsonRpcException ex)
        {
            return CreateErrorPayload(idValue, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            return CreateErrorPayload(idValue, -32000, ex.Message);
        }
    }

    private Task SendErrorAsync(object? idValue, int code, string message, CancellationToken ct) =>
        SendAsync(CreateErrorPayload(idValue, code, message), ct);

    // A dictionary keeps a null id in the output, which the serializer options omit for anonymous types.
    private static Dictionary<string, object?> CreateErrorPayload(object? idValue, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = idValue,
        ["error"] = new
        {
            code,
            message
        }
    };

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

    public async ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_ownsStreams)
        {
            if (!ReferenceEquals(_input, _output))
            {
                _output.Dispose();
            }

            _input.Dispose();
        }

        _cts?.Dispose();
        _sendLock.Dispose();
    }
}
