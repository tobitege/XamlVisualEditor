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

    private Task SetOrRemoveAsync(string key, string? value, CancellationToken cancellationToken)
    {
        return string.IsNullOrWhiteSpace(value)
            ? _storage.RemoveAsync(key, cancellationToken)
            : _storage.SetAsync(key, value.Trim(), cancellationToken);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
