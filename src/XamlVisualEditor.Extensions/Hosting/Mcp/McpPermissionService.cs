using XamlVisualEditor.Extensions;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Stores a workspace permission decision. Session tokens are issued per session and never persisted.</summary>
public sealed record McpWorkspacePermissionState(
    McpAccessLevel AccessLevel,
    DateTimeOffset GrantedAt);

/// <summary>Allowlist entry for UI.</summary>
public sealed record McpWorkspacePermissionEntry(
    string WorkspaceId,
    McpAccessLevel AccessLevel,
    DateTimeOffset GrantedAt);

/// <summary>Resolves permissions for MCP sessions.</summary>
public sealed class McpPermissionService
{
    private const string SettingsSection = "mcp.permissions";
    private readonly ISettings _settings;
    private readonly IWindow _window;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after the consent of one workspace, or of all workspaces (<c>null</c>), was removed.</summary>
    public event Action<string?>? PermissionsRevoked;

    public McpPermissionService(ISettings settings, IWindow window)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>Returns the stored consent of the workspace or prompts for and persists a new one.</summary>
    public Task<McpWorkspacePermissionState?> AuthorizeAsync(string workspaceId, CancellationToken ct) =>
        AuthorizeAsync(workspaceId, null, ct);

    /// <summary>
    /// Returns the stored consent of the workspace or prompts for and persists a new one.
    /// <paramref name="onAuthorized"/> runs with the granted consent before a revocation can remove it,
    /// so a session registered there is ended by every later revocation.
    /// </summary>
    public async Task<McpWorkspacePermissionState?> AuthorizeAsync(string workspaceId,
        Action<McpWorkspacePermissionState>? onAuthorized, CancellationToken ct)
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
                onAuthorized?.Invoke(existing);
                return existing;
            }

            McpWorkspacePermissionState? granted = await PromptForConsentAsync(ct).ConfigureAwait(false);
            if (granted is null)
            {
                return null;
            }

            states[workspaceId] = granted;
            await SaveStatesAsync(states, ct).ConfigureAwait(false);
            onAuthorized?.Invoke(granted);
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
            results.Add(new McpWorkspacePermissionEntry(entry.Key, state.AccessLevel, state.GrantedAt));
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
                return new McpWorkspacePermissionState(McpAccessLevel.Full, DateTimeOffset.UtcNow);
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

        return new McpWorkspacePermissionState(accessLevel, DateTimeOffset.UtcNow);
    }

    // The settings return the stored dictionary itself, which other readers enumerate concurrently; it is
    // never changed in place, every change is made on a copy that is then saved.
    private Task<Dictionary<string, McpWorkspacePermissionState>> LoadStatesAsync(CancellationToken ct)
    {
        Dictionary<string, McpWorkspacePermissionState>? states = _settings
            .Get<Dictionary<string, McpWorkspacePermissionState>>(SettingsSection);

        return Task.FromResult(states is null
            ? new Dictionary<string, McpWorkspacePermissionState>(StringComparer.Ordinal)
            : new Dictionary<string, McpWorkspacePermissionState>(states, StringComparer.Ordinal));
    }

    private Task SaveStatesAsync(Dictionary<string, McpWorkspacePermissionState> states, CancellationToken ct)
    {
        return _settings.UpdateAsync(SettingsSection, states, SettingsTarget.Workspace, ct);
    }
}
