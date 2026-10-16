using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>Persists the Git settings in the extension storage.</summary>
public sealed class GitSettingsStore
{
    /// <summary>Storage key of the working folder watcher switch; also the name of its context key.</summary>
    public const string AUTO_REFRESH_KEY = "git.autoRefresh";

    private readonly IExtensionStorage _storage;

    /// <summary>Creates the store.</summary>
    public GitSettingsStore(IExtensionStorage storage)
    {
        _storage = storage;
    }

    /// <summary>Loads whether the working folder is watched; false when the setting was never saved.</summary>
    public async Task<bool> LoadAutoRefreshAsync(CancellationToken cancellationToken)
    {
        bool? enabled = await _storage.GetAsync<bool?>(AUTO_REFRESH_KEY, cancellationToken).ConfigureAwait(false);
        return enabled ?? false;
    }

    /// <summary>Saves whether the working folder is watched.</summary>
    public Task SaveAutoRefreshAsync(bool enabled, CancellationToken cancellationToken)
    {
        return _storage.SetAsync(AUTO_REFRESH_KEY, enabled, cancellationToken);
    }
}
