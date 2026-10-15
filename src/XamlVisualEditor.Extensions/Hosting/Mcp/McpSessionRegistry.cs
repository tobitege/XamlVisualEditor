using System.Collections.Concurrent;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Stores MCP sessions by connection or token.</summary>
/// <remarks>Token-only (HTTP) sessions expire after <see cref="IdleTimeout"/> without activity; connection-bound sessions end with their connection.</remarks>
public sealed class McpSessionRegistry
{
    /// <summary>Default idle time after which an HTTP session expires.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<McpJsonRpcConnection, McpSessionInfo> _byConnection = new();
    private readonly ConcurrentDictionary<string, McpSessionInfo> _byToken = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActivity = new(StringComparer.Ordinal);

    public McpSessionRegistry(TimeSpan? idleTimeout = null, TimeProvider? timeProvider = null)
    {
        IdleTimeout = idleTimeout ?? DefaultIdleTimeout;
        if (IdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        }

        TimeProvider = timeProvider ?? TimeProvider.System;
    }

    public TimeSpan IdleTimeout { get; }

    public TimeProvider TimeProvider { get; }

    public int Count => _byToken.Count;
    public event Action? Changed;

    public void Set(McpJsonRpcConnection connection, McpSessionInfo session)
    {
        if (_byConnection.TryGetValue(connection, out McpSessionInfo? previous))
            _byToken.TryRemove(previous.SessionToken, out _);
        _byConnection[connection] = session;
        _byToken[session.SessionToken] = session;
        Changed?.Invoke();
    }

    public void Set(string sessionToken, McpSessionInfo session)
    {
        ExpireIdle();
        _lastActivity[sessionToken] = TimeProvider.GetUtcNow();
        _byToken[sessionToken] = session;
        Changed?.Invoke();
    }

    public void Remove(McpJsonRpcConnection connection)
    {
        if (_byConnection.TryRemove(connection, out McpSessionInfo? session))
            Remove(session.SessionToken);
    }

    public bool Remove(string sessionToken)
    {
        bool removed = _byToken.TryRemove(sessionToken, out _);
        _lastActivity.TryRemove(sessionToken, out _);
        foreach (var entry in _byConnection)
            if (entry.Value.SessionToken == sessionToken) _byConnection.TryRemove(entry.Key, out _);
        if (removed) Changed?.Invoke();
        return removed;
    }

    public void Revoke(string? workspaceId)
    {
        foreach (var entry in _byToken)
            if (workspaceId is null || entry.Value.WorkspaceId == workspaceId) Remove(entry.Key);
    }

    /// <summary>Removes token-only sessions whose last activity is older than <see cref="IdleTimeout"/>.</summary>
    public void ExpireIdle()
    {
        DateTimeOffset now = TimeProvider.GetUtcNow();
        foreach (var entry in _lastActivity)
            if (now - entry.Value >= IdleTimeout) Remove(entry.Key);
    }

    public McpSessionInfo? Resolve(McpRequestContext context)
    {
        if (context.Connection is not null && _byConnection.TryGetValue(context.Connection, out McpSessionInfo? session))
        {
            return session;
        }

        if (string.IsNullOrWhiteSpace(context.SessionToken))
        {
            return null;
        }

        ExpireIdle();
        if (!_byToken.TryGetValue(context.SessionToken, out McpSessionInfo? tokenSession))
        {
            return null;
        }

        if (_lastActivity.TryGetValue(context.SessionToken, out DateTimeOffset lastActivity))
        {
            _lastActivity.TryUpdate(context.SessionToken, TimeProvider.GetUtcNow(), lastActivity);
        }

        return tokenSession;
    }
}
