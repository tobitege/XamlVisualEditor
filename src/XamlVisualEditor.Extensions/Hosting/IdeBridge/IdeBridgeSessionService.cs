using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Extensions.Hosting.IdeBridge;

/// <summary>Resolves sessions and permissions for IDE bridge clients.</summary>
public sealed class IdeBridgeSessionService
{
    private readonly IdeBridgePermissionService _permissions;
    private readonly IWorkspaceInfo _workspaceInfo;

    /// <summary>Creates the session service.</summary>
    public IdeBridgeSessionService(IdeBridgePermissionService permissions, IWorkspaceInfo workspaceInfo)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _workspaceInfo = workspaceInfo ?? throw new ArgumentNullException(nameof(workspaceInfo));
    }

    /// <summary>Initializes a session for a client of the active workspace.</summary>
    public Task<BridgeInitializeResult> InitializeAsync(BridgeInitializeParams? request, CancellationToken ct) =>
        InitializeAsync(request, null, ct);

    /// <summary>
    /// Initializes a session for a client of the active workspace. <paramref name="register"/> receives the
    /// new session before a revocation of the consent can run.
    /// </summary>
    public async Task<BridgeInitializeResult> InitializeAsync(BridgeInitializeParams? request,
        Action<BridgeInitializeResult>? register, CancellationToken ct)
    {
        string workspaceId = ResolveWorkspaceId(request?.WorkspaceId);
        BridgeInitializeResult? result = null;
        IdeBridgeWorkspacePermissionState? state = await _permissions
            .AuthorizeAsync(workspaceId, granted =>
            {
                result = new BridgeInitializeResult(
                    IdeBridgeProtocol.ProtocolVersion,
                    IdeBridgePermissionService.GetCapabilities(granted.AccessLevel),
                    workspaceId,
                    Guid.NewGuid().ToString("N"));
                register?.Invoke(result);
            }, ct)
            .ConfigureAwait(false);

        if (state is null || result is null)
        {
            throw new IdeBridgeJsonRpcException(-32000, "Permission denied.");
        }

        return result;
    }

    // The session is bound to the active workspace; a client may name it but cannot select another one.
    private string ResolveWorkspaceId(string? requestedId)
    {
        string? path = _workspaceInfo.WorkspacePath;
        string workspaceId = string.IsNullOrWhiteSpace(path) ? "default" : Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(requestedId)
            && !string.Equals(workspaceId, Path.GetFullPath(requestedId),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new IdeBridgeJsonRpcException(-32602, "The requested workspace is not the active workspace.");
        }

        return workspaceId;
    }
}
