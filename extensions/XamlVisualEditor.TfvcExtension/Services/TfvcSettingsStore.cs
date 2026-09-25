using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Persists the TFVC connection settings and per-workspace panel state in the extension storage.</summary>
public sealed class TfvcSettingsStore
{
    private const string COLLECTION_URL_KEY = "tfvc.collectionUrl";
    private const string TF_EXE_PATH_KEY = "tfvc.tfExePath";
    private const string LAST_WORKSPACE_KEY = "tfvc.lastWorkspace";
    private const string EXPLORER_FOCUS_KEY = "tfvc.explorerFocus";
    private const string EXCLUDED_CHANGES_KEY_PREFIX = "tfvc.excludedChanges.";
    private const string SETTINGS_VISIBLE_KEY_PREFIX = "tfvc.settingsVisible.";
    private const string CACHED_WORKSPACES_KEY = "tfvc.cachedWorkspaces";
    private const string OFFLINE_EDITS_KEY = "tfvc.offlineEdits";

    private readonly IExtensionStorage _storage;

    /// <summary>Creates the store.</summary>
    public TfvcSettingsStore(IExtensionStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Loads the connection settings.</summary>
    public async Task<TfvcSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        string? collectionUrl = await _storage.GetAsync<string>(COLLECTION_URL_KEY, cancellationToken).ConfigureAwait(false);
        string? tfExePath = await _storage.GetAsync<string>(TF_EXE_PATH_KEY, cancellationToken).ConfigureAwait(false);
        return new TfvcSettings(NullIfEmpty(collectionUrl), NullIfEmpty(tfExePath));
    }

    /// <summary>Saves the connection settings.</summary>
    public async Task SaveSettingsAsync(TfvcSettings settings, CancellationToken cancellationToken)
    {
        await SetOrRemoveAsync(COLLECTION_URL_KEY, settings.CollectionUrl, cancellationToken).ConfigureAwait(false);
        await SetOrRemoveAsync(TF_EXE_PATH_KEY, settings.TfExePath, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Loads the name of the last selected workspace.</summary>
    public async Task<string?> LoadLastWorkspaceAsync(CancellationToken cancellationToken)
    {
        return NullIfEmpty(await _storage.GetAsync<string>(LAST_WORKSPACE_KEY, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Saves the name of the selected workspace.</summary>
    public Task SaveLastWorkspaceAsync(string? workspaceName, CancellationToken cancellationToken)
    {
        return SetOrRemoveAsync(LAST_WORKSPACE_KEY, workspaceName, cancellationToken);
    }

    /// <summary>Loads the folder the source control explorer is focused on.</summary>
    public async Task<string?> LoadExplorerFocusAsync(CancellationToken cancellationToken)
    {
        return NullIfEmpty(await _storage.GetAsync<string>(EXPLORER_FOCUS_KEY, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Saves the folder the source control explorer is focused on.</summary>
    public Task SaveExplorerFocusAsync(string? serverPath, CancellationToken cancellationToken)
    {
        return SetOrRemoveAsync(EXPLORER_FOCUS_KEY, serverPath, cancellationToken);
    }

    /// <summary>Loads whether a panel shows its connection settings; null when the panel has not stored it yet.</summary>
    public Task<bool?> LoadSettingsVisibleAsync(string panelId, CancellationToken cancellationToken)
    {
        return _storage.GetAsync<bool?>(SETTINGS_VISIBLE_KEY_PREFIX + panelId, cancellationToken);
    }

    /// <summary>Saves whether a panel shows its connection settings.</summary>
    public Task SaveSettingsVisibleAsync(string panelId, bool isVisible, CancellationToken cancellationToken)
    {
        return _storage.SetAsync(SETTINGS_VISIBLE_KEY_PREFIX + panelId, isVisible, cancellationToken);
    }

    /// <summary>Loads the server paths excluded from check-in for a workspace.</summary>
    public async Task<IReadOnlyList<string>> LoadExcludedChangesAsync(string workspaceName, CancellationToken cancellationToken)
    {
        string[]? paths = await _storage.GetAsync<string[]>(EXCLUDED_CHANGES_KEY_PREFIX + workspaceName, cancellationToken).ConfigureAwait(false);
        return paths ?? Array.Empty<string>();
    }

    /// <summary>Saves the server paths excluded from check-in for a workspace.</summary>
    public Task SaveExcludedChangesAsync(string workspaceName, IReadOnlyCollection<string> serverPaths, CancellationToken cancellationToken)
    {
        string key = EXCLUDED_CHANGES_KEY_PREFIX + workspaceName;
        if (serverPaths.Count == 0)
        {
            return _storage.RemoveAsync(key, cancellationToken);
        }

        string[] values = new string[serverPaths.Count];
        int index = 0;
        foreach (string path in serverPaths)
        {
            values[index++] = path;
        }

        return _storage.SetAsync(key, values, cancellationToken);
    }

    /// <summary>Loads the workspaces of the last successful connection.</summary>
    public async Task<IReadOnlyList<TfvcWorkspace>> LoadCachedWorkspacesAsync(CancellationToken cancellationToken)
    {
        TfvcCachedWorkspace[]? cached = await _storage.GetAsync<TfvcCachedWorkspace[]>(CACHED_WORKSPACES_KEY, cancellationToken).ConfigureAwait(false);
        if (cached is null)
        {
            return Array.Empty<TfvcWorkspace>();
        }

        TfvcWorkspace[] workspaces = new TfvcWorkspace[cached.Length];
        for (int i = 0; i < cached.Length; i++)
        {
            workspaces[i] = cached[i].ToWorkspace();
        }

        return workspaces;
    }

    /// <summary>Saves the workspaces of a successful connection.</summary>
    public Task SaveCachedWorkspacesAsync(IReadOnlyList<TfvcWorkspace> workspaces, CancellationToken cancellationToken)
    {
        TfvcCachedWorkspace[] cached = new TfvcCachedWorkspace[workspaces.Count];
        for (int i = 0; i < workspaces.Count; i++)
        {
            cached[i] = TfvcCachedWorkspace.From(workspaces[i]);
        }

        return _storage.SetAsync(CACHED_WORKSPACES_KEY, cached, cancellationToken);
    }

    /// <summary>Loads the local paths whose write protection was removed while TFVC was offline.</summary>
    public async Task<IReadOnlyList<string>> LoadOfflineEditsAsync(CancellationToken cancellationToken)
    {
        string[]? paths = await _storage.GetAsync<string[]>(OFFLINE_EDITS_KEY, cancellationToken).ConfigureAwait(false);
        return paths ?? Array.Empty<string>();
    }

    /// <summary>Saves the local paths whose write protection was removed while TFVC was offline.</summary>
    public Task SaveOfflineEditsAsync(IReadOnlyCollection<string> localPaths, CancellationToken cancellationToken)
    {
        if (localPaths.Count == 0)
        {
            return _storage.RemoveAsync(OFFLINE_EDITS_KEY, cancellationToken);
        }

        string[] values = [.. localPaths];
        return _storage.SetAsync(OFFLINE_EDITS_KEY, values, cancellationToken);
    }

    private Task SetOrRemoveAsync(string key, string? value, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(value)
            ? _storage.RemoveAsync(key, cancellationToken)
            : _storage.SetAsync(key, value.Trim(), cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
