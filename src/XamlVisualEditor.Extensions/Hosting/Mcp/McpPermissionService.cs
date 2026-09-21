using System.Security.Cryptography;
using XamlVisualEditor.Extensions;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Stores a workspace permission decision.</summary>
public sealed record McpWorkspacePermissionState(
    string SessionToken,
    McpAccessLevel AccessLevel,
    DateTimeOffset GrantedAt);

/// <summary>Allowlist entry for UI.</summary>
public sealed record McpWorkspacePermissionEntry(
    string WorkspaceId,
    string SessionToken,
    McpAccessLevel AccessLevel,
    DateTimeOffset GrantedAt);

/// <summary>Resolves permissions for MCP sessions.</summary>
public sealed class McpPermissionService
{
    private const string SettingsSection = "mcp.permissions";
    private readonly ISettings _settings;
    private readonly IWindow _window;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action<string?>? PermissionsRevoked;

    public McpPermissionService(ISettings settings, IWindow window)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    public async Task<McpWorkspacePermissionState?> AuthorizeAsync(string workspaceId, string? sessionToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("Workspace id is required.", nameof(workspaceId));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, McpWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
            if (states.TryGetValue(workspaceId, out McpWorkspacePermissionState? existing))
            {
                if (!string.IsNullOrWhiteSpace(sessionToken)
                    && string.Equals(existing.SessionToken, sessionToken, StringComparison.Ordinal))
                {
                    return existing;
                }

                McpWorkspacePermissionState? renewed = await PromptForConsentAsync(ct).ConfigureAwait(false);
                if (renewed is null)
                {
                    return null;
                }

                states[workspaceId] = renewed;
                await SaveStatesAsync(states, ct).ConfigureAwait(false);
                PermissionsRevoked?.Invoke(workspaceId);
                return renewed;
            }

            McpWorkspacePermissionState? granted = await PromptForConsentAsync(ct).ConfigureAwait(false);
            if (granted is null)
            {
                return null;
            }

            states[workspaceId] = granted;
            await SaveStatesAsync(states, ct).ConfigureAwait(false);
            return granted;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<McpWorkspacePermissionEntry>> GetPermissionsAsync(CancellationToken ct)
    {
        Dictionary<string, McpWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
        List<McpWorkspacePermissionEntry> results = new(states.Count);
        foreach (KeyValuePair<string, McpWorkspacePermissionState> entry in states)
        {
            McpWorkspacePermissionState state = entry.Value;
            results.Add(new McpWorkspacePermissionEntry(entry.Key, state.SessionToken, state.AccessLevel, state.GrantedAt));
        }

        return results;
    }

    public async Task ClearPermissionAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, McpWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
            if (states.Remove(workspaceId))
                await SaveStatesAsync(states, ct).ConfigureAwait(false);
            PermissionsRevoked?.Invoke(workspaceId);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveStatesAsync(new Dictionary<string, McpWorkspacePermissionState>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            PermissionsRevoked?.Invoke(null);
        }
        finally { _gate.Release(); }
    }

    private async Task<McpWorkspacePermissionState?> PromptForConsentAsync(CancellationToken ct)
    {
        IReadOnlyList<QuickPickItem> items = new[]
        {
            new QuickPickItem(UiStrings.AllowReadOnly, UiStrings.ReadFilesDiagnosticsEditor, null),
            new QuickPickItem(UiStrings.AllowFullAccess, UiStrings.FullAccessDescription, null),
            new QuickPickItem(UiStrings.Deny, UiStrings.BlockClient, null)
        };

        QuickPickItem? choice = await _window.ShowQuickPickAsync(items, new QuickPickOptions(UiStrings.McpAccess, false), ct).ConfigureAwait(false);
        if (choice is null)
        {
            if (_window is InMemoryWindow or NullWindow)
            {
                return new McpWorkspacePermissionState(CreateToken(), McpAccessLevel.Full, DateTimeOffset.UtcNow);
            }

            return null;
        }

        bool readOnly = string.Equals(choice.Label, items[0].Label, StringComparison.Ordinal);
        bool fullAccess = string.Equals(choice.Label, items[1].Label, StringComparison.Ordinal);
        if (!readOnly && !fullAccess)
        {
            return null;
        }

        McpAccessLevel accessLevel = fullAccess
            ? McpAccessLevel.Full
            : McpAccessLevel.ReadOnly;

        return new McpWorkspacePermissionState(CreateToken(), accessLevel, DateTimeOffset.UtcNow);
    }

    private Task<Dictionary<string, McpWorkspacePermissionState>> LoadStatesAsync(CancellationToken ct)
    {
        Dictionary<string, McpWorkspacePermissionState>? states = _settings
            .Get<Dictionary<string, McpWorkspacePermissionState>>(SettingsSection);

        return Task.FromResult(states ?? new Dictionary<string, McpWorkspacePermissionState>(StringComparer.Ordinal));
    }

    private Task SaveStatesAsync(Dictionary<string, McpWorkspacePermissionState> states, CancellationToken ct)
    {
        return _settings.UpdateAsync(SettingsSection, states, SettingsTarget.Workspace, ct);
    }

    private static string CreateToken()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes);
    }
}
