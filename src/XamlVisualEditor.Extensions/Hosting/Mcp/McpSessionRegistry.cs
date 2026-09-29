using System.Collections.Concurrent;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Stores MCP sessions by connection or token.</summary>
public sealed class McpSessionRegistry
{
    private readonly ConcurrentDictionary<McpJsonRpcConnection, McpSessionInfo> _byConnection = new();
    private readonly ConcurrentDictionary<string, McpSessionInfo> _byToken = new(StringComparer.Ordinal);

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

    public McpSessionInfo? Resolve(McpRequestContext context)
    {
        if (context.Connection is not null && _byConnection.TryGetValue(context.Connection, out McpSessionInfo? session))
        {
            return session;
        }

        if (!string.IsNullOrWhiteSpace(context.SessionToken) && _byToken.TryGetValue(context.SessionToken, out McpSessionInfo? tokenSession))
        {
            return tokenSession;
        }

        return null;
    }
}
