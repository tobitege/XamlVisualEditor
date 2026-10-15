using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.Extensions.Hosting.IdeBridge;

/// <summary>Access granted to the IDE bridge clients of a workspace.</summary>
public enum IdeBridgeAccessLevel
{
    ReadOnly,
    Full
}

/// <summary>Stores a workspace permission decision. Session tokens are issued per session and never persisted.</summary>
public sealed record IdeBridgeWorkspacePermissionState(
    IdeBridgeAccessLevel AccessLevel,
    DateTimeOffset GrantedAt);

/// <summary>Allowlist entry for UI.</summary>
public sealed record IdeBridgeWorkspacePermissionEntry(
    string WorkspaceId,
    IdeBridgeAccessLevel AccessLevel,
    DateTimeOffset GrantedAt)
{
    /// <summary>Gets the features granted by <see cref="AccessLevel"/>.</summary>
    public IdeBridgeCapabilities Capabilities => IdeBridgePermissionService.GetCapabilities(AccessLevel);
}

/// <summary>Resolves permissions for IDE bridge sessions.</summary>
public sealed class IdeBridgePermissionService
{
    private const string SettingsSection = "ideBridge.permissions";
    private readonly ISettings _settings;
    private readonly IWindow _window;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Raised after the consent of one workspace, or of all workspaces (<c>null</c>), was removed.</summary>
    public event Action<string?>? PermissionsRevoked;

    /// <summary>Creates the permission service.</summary>
    public IdeBridgePermissionService(ISettings settings, IWindow window)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>Gets the features granted by <paramref name="accessLevel"/>.</summary>
    public static IdeBridgeCapabilities GetCapabilities(IdeBridgeAccessLevel accessLevel) =>
        accessLevel == IdeBridgeAccessLevel.Full ? CreateFullCapabilities() : CreateReadOnlyCapabilities();

    /// <summary>Returns the stored consent of the workspace or prompts for and persists a new one.</summary>
    public Task<IdeBridgeWorkspacePermissionState?> AuthorizeAsync(string workspaceId, CancellationToken ct) =>
        AuthorizeAsync(workspaceId, null, ct);

    /// <summary>
    /// Returns the stored consent of the workspace or prompts for and persists a new one.
    /// <paramref name="onAuthorized"/> runs with the granted consent before a revocation can remove it,
    /// so a session registered there is ended by every later revocation.
    /// </summary>
    public async Task<IdeBridgeWorkspacePermissionState?> AuthorizeAsync(string workspaceId,
        Action<IdeBridgeWorkspacePermissionState>? onAuthorized, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new ArgumentException("Workspace id is required.", nameof(workspaceId));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, IdeBridgeWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
            if (states.TryGetValue(workspaceId, out IdeBridgeWorkspacePermissionState? existing))
            {
                onAuthorized?.Invoke(existing);
                return existing;
            }

            IdeBridgeWorkspacePermissionState? granted = await PromptForConsentAsync(ct).ConfigureAwait(false);
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

    /// <summary>Gets all allowlist entries.</summary>
    public async Task<IReadOnlyList<IdeBridgeWorkspacePermissionEntry>> GetPermissionsAsync(CancellationToken ct)
    {
        Dictionary<string, IdeBridgeWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
        List<IdeBridgeWorkspacePermissionEntry> results = new(states.Count);
        foreach (KeyValuePair<string, IdeBridgeWorkspacePermissionState> entry in states)
        {
            IdeBridgeWorkspacePermissionState state = entry.Value;
            results.Add(new IdeBridgeWorkspacePermissionEntry(entry.Key, state.AccessLevel, state.GrantedAt));
        }

        return results;
    }

    /// <summary>Clears a single allowlist entry.</summary>
    public async Task ClearPermissionAsync(string workspaceId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Dictionary<string, IdeBridgeWorkspacePermissionState> states = await LoadStatesAsync(ct).ConfigureAwait(false);
            if (states.Remove(workspaceId))
            {
                await SaveStatesAsync(states, ct).ConfigureAwait(false);
            }

            PermissionsRevoked?.Invoke(workspaceId);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Clears all allowlist entries.</summary>
    public async Task ClearAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SaveStatesAsync(new Dictionary<string, IdeBridgeWorkspacePermissionState>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            PermissionsRevoked?.Invoke(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IdeBridgeWorkspacePermissionState?> PromptForConsentAsync(CancellationToken ct)
    {
        // Headless/batch clients cannot answer the GUI consent prompt. The environment switch grants
        // access without a dialog: "readonly" (or "1"/"true") grants read-only, "full" grants full access.
        string? autoConsent = Environment.GetEnvironmentVariable("XVE_IDEBRIDGE_AUTOCONSENT");
        if (!string.IsNullOrWhiteSpace(autoConsent))
        {
            if (string.Equals(autoConsent, "full", StringComparison.OrdinalIgnoreCase))
            {
                return new IdeBridgeWorkspacePermissionState(IdeBridgeAccessLevel.Full, DateTimeOffset.UtcNow);
            }

            if (string.Equals(autoConsent, "readonly", StringComparison.OrdinalIgnoreCase)
                || string.Equals(autoConsent, "1", StringComparison.Ordinal)
                || string.Equals(autoConsent, "true", StringComparison.OrdinalIgnoreCase))
            {
                return new IdeBridgeWorkspacePermissionState(IdeBridgeAccessLevel.ReadOnly, DateTimeOffset.UtcNow);
            }
        }

        IReadOnlyList<QuickPickItem> items = new[]
        {
            new QuickPickItem(UiStrings.AllowReadOnly, UiStrings.ReadFilesDiagnostics, null),
            new QuickPickItem(UiStrings.AllowFullAccess, UiStrings.FullAccessDescription, null),
            new QuickPickItem(UiStrings.Deny, UiStrings.BlockClient, null)
        };

        QuickPickItem? choice = await _window.ShowQuickPickAsync(items, new QuickPickOptions(UiStrings.IdeBridgeAccess, false), ct).ConfigureAwait(false);
        if (choice is null)
        {
            return null;
        }

        bool readOnly = string.Equals(choice.Label, items[0].Label, StringComparison.Ordinal);
        bool fullAccess = string.Equals(choice.Label, items[1].Label, StringComparison.Ordinal);
        if (!readOnly && !fullAccess)
        {
            return null;
        }

        IdeBridgeAccessLevel accessLevel = fullAccess
            ? IdeBridgeAccessLevel.Full
            : IdeBridgeAccessLevel.ReadOnly;

        return new IdeBridgeWorkspacePermissionState(accessLevel, DateTimeOffset.UtcNow);
    }

    private static IdeBridgeCapabilities CreateReadOnlyCapabilities()
    {
        return new IdeBridgeCapabilities(
            Files: true,
            Commands: false,
            Diagnostics: true,
            Terminal: false,
            Ui: true,
            Documents: true,
            Selection: true,
            Workspace: true,
            Write: false);
    }

    private static IdeBridgeCapabilities CreateFullCapabilities()
    {
        return new IdeBridgeCapabilities(
            Files: true,
            Commands: true,
            Diagnostics: true,
            Terminal: true,
            Ui: true,
            Documents: true,
            Selection: true,
            Workspace: true,
            Write: true);
    }

    // The settings return the stored dictionary itself, which other readers enumerate concurrently; it is
    // never changed in place, every change is made on a copy that is then saved.
    private Task<Dictionary<string, IdeBridgeWorkspacePermissionState>> LoadStatesAsync(CancellationToken ct)
    {
        Dictionary<string, IdeBridgeWorkspacePermissionState>? states = _settings
            .Get<Dictionary<string, IdeBridgeWorkspacePermissionState>>(SettingsSection);

        return Task.FromResult(states is null
            ? new Dictionary<string, IdeBridgeWorkspacePermissionState>(StringComparer.Ordinal)
            : new Dictionary<string, IdeBridgeWorkspacePermissionState>(states, StringComparer.Ordinal));
    }

    private Task SaveStatesAsync(Dictionary<string, IdeBridgeWorkspacePermissionState> states, CancellationToken ct)
    {
        return _settings.UpdateAsync(SettingsSection, states, SettingsTarget.Workspace, ct);
    }
}
