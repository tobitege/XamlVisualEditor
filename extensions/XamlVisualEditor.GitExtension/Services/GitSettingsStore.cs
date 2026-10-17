using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>Persists the Git settings in the extension storage.</summary>
public sealed class GitSettingsStore
{
    /// <summary>Storage key of the working folder watcher switch; also the name of its context key.</summary>
    public const string AUTO_REFRESH_KEY = "git.autoRefresh";

    /// <summary>Storage key of the explorer status switch; also the name of its context key.</summary>
    public const string EXPLORER_DECORATIONS_KEY = "git.explorerDecorations";

    /// <summary>Storage key of the editor change marker switch; also the name of its context key.</summary>
    public const string CHANGE_MARKERS_KEY = "git.changeMarkers";

    private readonly IExtensionStorage _storage;

    /// <summary>Creates the store.</summary>
    public GitSettingsStore(IExtensionStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Loads whether the working folder is watched; false when the setting was never saved.</summary>
    public Task<bool> LoadAutoRefreshAsync(CancellationToken cancellationToken)
    {
        return LoadSwitchAsync(AUTO_REFRESH_KEY, false, cancellationToken);
    }

    /// <summary>Saves whether the working folder is watched.</summary>
    public Task SaveAutoRefreshAsync(bool enabled, CancellationToken cancellationToken)
    {
        return SaveSwitchAsync(AUTO_REFRESH_KEY, enabled, cancellationToken);
    }

    /// <summary>Loads the switch saved under <paramref name="key"/>; <paramref name="defaultValue"/> when it was never saved.</summary>
    public async Task<bool> LoadSwitchAsync(string key, bool defaultValue, CancellationToken cancellationToken)
    {
        bool? enabled = await _storage.GetAsync<bool?>(key, cancellationToken).ConfigureAwait(false);
        return enabled ?? defaultValue;
    }

    /// <summary>Saves the switch under <paramref name="key"/>.</summary>
    public Task SaveSwitchAsync(string key, bool enabled, CancellationToken cancellationToken)
    {
        return _storage.SetAsync(key, enabled, cancellationToken);
    }
}
