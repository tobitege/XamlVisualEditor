using System.Collections.Concurrent;

namespace XamlVisualEditor.Extensions.Hosting.IdeBridge;

/// <summary>Stores active IDE bridge sessions per connection.</summary>
public sealed class IdeBridgeSessionRegistry
{
    private readonly ConcurrentDictionary<IdeBridgeJsonRpcConnection, IdeBridgeSessionInfo> _sessions = new();

    /// <summary>Registers a session.</summary>
    public void Set(IdeBridgeJsonRpcConnection connection, IdeBridgeSessionInfo session)
    {
        _sessions[connection] = session;
    }

    /// <summary>Removes a session.</summary>
    public void Remove(IdeBridgeJsonRpcConnection connection)
    {
        _sessions.TryRemove(connection, out _);
    }

    /// <summary>Ends the sessions of one workspace, or of all workspaces (<c>null</c>); their clients must initialize again.</summary>
    public void Revoke(string? workspaceId)
    {
        foreach (KeyValuePair<IdeBridgeJsonRpcConnection, IdeBridgeSessionInfo> entry in _sessions)
        {
            if (workspaceId is null || entry.Value.WorkspaceId == workspaceId)
            {
                _sessions.TryRemove(entry.Key, out _);
            }
        }
    }

    /// <summary>Gets a session for a connection.</summary>
    public bool TryGet(IdeBridgeJsonRpcConnection connection, out IdeBridgeSessionInfo session)
    {
        return _sessions.TryGetValue(connection, out session!);
    }
}

/// <summary>Represents an active IDE bridge session.</summary>
public sealed record IdeBridgeSessionInfo(
    string WorkspaceId,
    IdeBridgeCapabilities Capabilities,
    string SessionToken);
