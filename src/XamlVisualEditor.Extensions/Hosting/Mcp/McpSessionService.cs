using System.IO;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Resolves sessions and permissions for MCP clients.</summary>
public sealed class McpSessionService
{
    private readonly McpPermissionService _permissions;
    private readonly IWorkspaceInfo _workspaceInfo;

    public McpSessionService(McpPermissionService permissions, IWorkspaceInfo workspaceInfo)
    {
        _permissions = permissions ?? throw new ArgumentNullException(nameof(permissions));
        _workspaceInfo = workspaceInfo ?? throw new ArgumentNullException(nameof(workspaceInfo));
    }

    public Task<McpInitializeOutcome> InitializeAsync(McpInitializeParams? request, CancellationToken ct) =>
        InitializeAsync(request, McpProtocol.StdioProtocolVersions, ct);

    /// <summary>Authorizes the client and answers with the requested protocol version when it is one of <paramref name="supportedVersions"/>.</summary>
    public Task<McpInitializeOutcome> InitializeAsync(McpInitializeParams? request, IReadOnlyList<string> supportedVersions, CancellationToken ct) =>
        InitializeAsync(request, supportedVersions, null, ct);

    /// <summary>
    /// Authorizes the client and answers with the requested protocol version when it is one of <paramref name="supportedVersions"/>.
    /// <paramref name="register"/> receives the new session before a revocation of the consent can run.
    /// </summary>
    public async Task<McpInitializeOutcome> InitializeAsync(McpInitializeParams? request, IReadOnlyList<string> supportedVersions,
        Action<McpInitializeOutcome>? register, CancellationToken ct)
    {
        string workspaceId = ResolveWorkspaceId(request?.WorkspaceId);
        McpInitializeOutcome? outcome = null;
        McpWorkspacePermissionState? state = await _permissions
            .AuthorizeAsync(workspaceId, granted =>
            {
                outcome = CreateOutcome(request, supportedVersions, workspaceId, granted);
                register?.Invoke(outcome);
            }, ct)
            .ConfigureAwait(false);

        if (state is null || outcome is null)
        {
            throw new McpJsonRpcException(-32000, "Permission denied.");
        }

        return outcome;
    }

    private static McpInitializeOutcome CreateOutcome(McpInitializeParams? request, IReadOnlyList<string> supportedVersions,
        string workspaceId, McpWorkspacePermissionState state)
    {
        McpServerInfo serverInfo = new("XamlVisualEditor", ExtensionSdkInfo.ApiVersion.ToString());
        McpServerCapabilities capabilities = new(
            new McpToolsCapability(),
            new McpResourcesCapability(),
            new McpPromptsCapability());

        McpInitializeResult result = new(
            McpProtocol.NegotiateVersion(request?.ProtocolVersion, supportedVersions),
            serverInfo,
            capabilities,
            workspaceId,
            Guid.NewGuid().ToString("N"));

        return new McpInitializeOutcome(result, state.AccessLevel);
    }

    private string ResolveWorkspaceId(string? requestedId)
    {
        string? path = _workspaceInfo.WorkspacePath;
        string workspaceId = string.IsNullOrWhiteSpace(path) ? "default" : Path.GetFullPath(path);
        if (!string.IsNullOrWhiteSpace(requestedId)
            && !string.Equals(workspaceId, Path.GetFullPath(requestedId),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new McpJsonRpcException(-32602, "The requested workspace is not the active workspace.");
        }
        return workspaceId;
    }
}
