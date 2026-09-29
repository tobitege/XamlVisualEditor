using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Acp;

public sealed class AcpProtocolClient : IAsyncDisposable
{
    private int _disposed;
    private readonly AcpMessageReader _reader;
    private readonly AcpMessageWriter _writer;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly ConcurrentDictionary<Task, byte> _incomingRequests = new();
    private volatile bool _isRunning;
    private readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement?>>> _requestHandlers = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private long _sequence;
    private CancellationTokenSource? _loopCts;
    private Task? _readLoop;

    public AcpProtocolClient(AcpMessageReader reader, AcpMessageWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    public event Action<string, JsonElement?>? NotificationReceived;
    public event Action<Exception?>? Disconnected;
    public bool IsRunning => _isRunning;

    public void Start(CancellationToken ct)
    {
        if (_readLoop is not null) throw new InvalidOperationException("ACP client has already started.");
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _isRunning = true;
        _readLoop = Task.Run(() => ReadLoopAsync(_loopCts.Token));
    }

    public void RegisterRequestHandler(string method, Func<JsonElement?, CancellationToken, Task<JsonElement?>> handler)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method name is required.", nameof(method));
        }

        _requestHandlers[method] = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public bool TryRemoveRequestHandler(string method)
    {
        return _requestHandlers.TryRemove(method, out _);
    }

    public async Task<JsonElement> SendRequestAsync(string method, object? parameters, CancellationToken ct)
    {
        if (!_isRunning) throw new InvalidOperationException("ACP connection is closed.");
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("Method name is required.", nameof(method));
        }

        long id = Interlocked.Increment(ref _sequence);
        string key = id.ToString(CultureInfo.InvariantCulture);
        TaskCompletionSource<JsonElement> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[key] = tcs;

        var payload = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        };

        try
        {
            if (!_isRunning) throw new EndOfStreamException("ACP connection closed.");
            await SendAsync(payload, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally { _pending.TryRemove(key, out _); }
    }

    public async Task SendNotificationAsync(string method, object? parameters, CancellationToken ct)
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

        await SendAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task SendAsync(object payload, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(payload);
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteMessageAsync(json, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        Exception? failure = null;
        try
        {
        while (!ct.IsCancellationRequested)
        {
            string? json = await _reader.ReadMessageAsync(ct).ConfigureAwait(false);
            if (json is null)
            {
                break;
            }

            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                bool hasId = root.TryGetProperty("id", out JsonElement idElement);
                bool hasMethod = root.TryGetProperty("method", out JsonElement methodElement);

                if (hasId && !hasMethod)
                {
                    HandleResponse(root, idElement);
                    continue;
                }

                if (hasMethod)
                {
                    string? method = methodElement.GetString();
                    if (string.IsNullOrWhiteSpace(method))
                    {
                        continue;
                    }

                    JsonElement? parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                        ? paramsElement.Clone()
                        : null;

                    if (hasId)
                    {
                        Task request = HandleRequestAsync(method, idElement.Clone(), parameters, ct);
                        _incomingRequests.TryAdd(request, 0);
                        _ = request.ContinueWith(completed =>
                        {
                            _incomingRequests.TryRemove(completed, out _);
                            _ = completed.Exception;
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        continue;
                    }

                    NotificationReceived?.Invoke(method, parameters);
                }
            }
            catch (JsonException)
            {
            }
            finally
            {
                document?.Dispose();
            }
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception error) { failure = error; }
        finally
        {
            _isRunning = false;
            _loopCts?.Cancel();
            foreach (var pending in _pending.Values)
                pending.TrySetException(failure ?? new EndOfStreamException("ACP connection closed."));
            Disconnected?.Invoke(failure);
        }
    }

    private void HandleResponse(JsonElement root, JsonElement idElement)
    {
        if (!TryGetIdKey(idElement, out string key))
        {
            return;
        }

        if (!_pending.TryRemove(key, out TaskCompletionSource<JsonElement>? tcs))
        {
            return;
        }

        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            JsonRpcException exception = CreateException(errorElement);
            tcs.TrySetException(exception);
            return;
        }

        JsonElement result = root.TryGetProperty("result", out JsonElement resultElement)
            ? resultElement.Clone()
            : default;
        tcs.TrySetResult(result);
    }

    private async Task HandleRequestAsync(string method, JsonElement idElement, JsonElement? parameters, CancellationToken ct)
    {
        if (!TryGetIdValue(idElement, out object? idValue) || idValue is null)
        {
            return;
        }

        if (!_requestHandlers.TryGetValue(method, out Func<JsonElement?, CancellationToken, Task<JsonElement?>>? handler))
        {
            await SendErrorAsync(idValue, -32601, "Method not found", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            JsonElement? result = await handler(parameters, ct).WaitAsync(ct).ConfigureAwait(false);
            await SendResultAsync(idValue, result, ct).ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            await SendErrorAsync(idValue, ex.Code, ex.Message, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendErrorAsync(idValue, -32000, ex.Message, ct).ConfigureAwait(false);
        }
    }

    private async Task SendResultAsync(object idValue, JsonElement? result, CancellationToken ct)
    {
        object? payloadResult = null;
        if (result is not null)
        {
            JsonElement clone = result.Value.Clone();
            payloadResult = clone;
        }
        var payload = new
        {
            jsonrpc = "2.0",
            id = idValue,
            result = payloadResult
        };

        await SendAsync(payload, ct).ConfigureAwait(false);
    }

    private async Task SendErrorAsync(object idValue, int code, string message, CancellationToken ct)
    {
        var payload = new
        {
            jsonrpc = "2.0",
            id = idValue,
            error = new
            {
                code,
                message
            }
        };

        await SendAsync(payload, ct).ConfigureAwait(false);
    }

    private static JsonRpcException CreateException(JsonElement errorElement)
    {
        int code = errorElement.TryGetProperty("code", out JsonElement codeElement)
            ? codeElement.GetInt32()
            : -32000;
        string message = errorElement.TryGetProperty("message", out JsonElement messageElement)
            ? messageElement.GetString() ?? "ACP request failed."
            : UiStrings.AcpRequestFailed;
        return new JsonRpcException(code, message);
    }

    private static bool TryGetIdKey(JsonElement idElement, out string key)
    {
        if (idElement.ValueKind == JsonValueKind.String)
        {
            string? value = idElement.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                key = value;
                return true;
            }
        }

        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long numeric))
        {
            key = numeric.ToString(CultureInfo.InvariantCulture);
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static bool TryGetIdValue(JsonElement idElement, out object? idValue)
    {
        if (idElement.ValueKind == JsonValueKind.String)
        {
            string? value = idElement.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                idValue = value;
                return true;
            }
        }

        if (idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out long numeric))
        {
            idValue = numeric;
            return true;
        }

        idValue = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        if (_loopCts is not null)
        {
            _loopCts.Cancel();
        }

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        foreach (TaskCompletionSource<JsonElement> pending in _pending.Values)
        {
            pending.TrySetCanceled();
        }

        try { await Task.WhenAll(_incomingRequests.Keys).ConfigureAwait(false); }
        catch (Exception) when (_loopCts?.IsCancellationRequested == true) { }

        _sendLock.Dispose();
        _loopCts?.Dispose();
    }
}

public sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message) : base(message)
    {
        Code = code;
    }

    public int Code { get; }
}
