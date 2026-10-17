using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// A Git setting that a command switches on and off. It is saved in the extension storage and published as a context
/// key, both under its key.
/// </summary>
internal sealed class GitSettingSwitch
{
    private readonly string _key;
    private readonly bool _defaultValue;
    private readonly GitSettingsStore? _settings;
    private readonly IContextKeys? _contextKeys;
    private readonly IExtensionLogger? _logger;
    private volatile bool _enabled;

    /// <summary>Creates the switch with its default value until <see cref="LoadAsync"/> reads the saved one.</summary>
    public GitSettingSwitch(
        string key,
        bool defaultValue,
        GitSettingsStore? settings,
        IContextKeys? contextKeys,
        IExtensionLogger? logger)
    {
        _key = key;
        _defaultValue = defaultValue;
        _settings = settings;
        _contextKeys = contextKeys;
        _logger = logger;
        _enabled = defaultValue;
        _contextKeys?.Set(key, defaultValue);
    }

    /// <summary>Gets whether the setting is switched on.</summary>
    public bool IsEnabled => _enabled;

    /// <summary>Raised after the setting was loaded or toggled, on the thread that did it.</summary>
    public event EventHandler? Changed;

    /// <summary>Reads the saved value.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_settings is null)
        {
            return;
        }

        try
        {
            Set(await _settings.LoadSwitchAsync(_key, _defaultValue, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error(GitStrings.LogSettingsFailed, exception);
        }
    }

    /// <summary>Switches the setting over and saves it.</summary>
    public async Task ToggleAsync()
    {
        bool enabled = !_enabled;
        Set(enabled);
        if (_settings is null)
        {
            return;
        }

        try
        {
            await _settings.SaveSwitchAsync(_key, enabled, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error(GitStrings.LogSettingsFailed, exception);
        }
    }

    private void Set(bool enabled)
    {
        _enabled = enabled;
        _contextKeys?.Set(_key, enabled);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
