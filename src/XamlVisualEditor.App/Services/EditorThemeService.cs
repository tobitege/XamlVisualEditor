using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Avalonia.Themes.Fluent;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Services;

/// <summary>Verwaltet Editor-Themes, Paketwechsel und die unabhängigen Ressourcen der lokalen Vorschau.</summary>
public sealed class EditorThemeService : IEditorThemeService, IDisposable
{
    private sealed record Provider(EditorThemeProviderRegistration Registration, EditorThemeProviderLoader Loader);
    private readonly Application _application;
    private readonly IResourceDictionary _originalResources;
    private readonly ThemeVariant? _originalVariant;
    private readonly IExtensionContributionRegistry _contributions;
    private readonly IExtensionThemeProviderLifecycle? _lifecycle;
    private readonly ILogger<EditorThemeService> _logger;
    private readonly Dictionary<string, Provider> _providers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changingPackages = new(StringComparer.OrdinalIgnoreCase);
    private readonly DockFluentThemeManager _dockThemeManager = new();
    private readonly IReadOnlyDictionary<ThemeVariant, IReadOnlyDictionary<string, object>> _previewDefaults;
    private EditorThemeSelection? _appliedSelection;
    private bool _disposed;

    public EditorThemeService(IExtensionContributionRegistry contributions, IExtensionManager extensionManager, EditorOptionsStore options,
        ILogger<EditorThemeService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<EditorThemeService>.Instance;
        _application = Application.Current ?? throw new InvalidOperationException("The editor application is not initialized.");
        _originalResources = _application.Resources;
        _originalVariant = _application.RequestedThemeVariant;
        PreviewDefaultVariant = _application.ActualThemeVariant;
        _previewDefaults = EditorThemeResources.CaptureDefaults(_application);
        _contributions = contributions;
        _lifecycle = extensionManager as IExtensionThemeProviderLifecycle;
        Selection = options.Current.EditorTheme;
        _contributions.Changed += OnContributionsChanged;
        if (_lifecycle is not null)
        {
            _lifecycle.ThemeProvidersChanging += BeforePackageChangeAsync;
            _lifecycle.ThemeProvidersChanged += AfterPackageChangeAsync;
        }
        Refresh();
    }

    public event EventHandler? Changed;
    public IReadOnlyList<EditorThemeProviderCatalog> Catalogs { get; private set; } = Array.Empty<EditorThemeProviderCatalog>();
    public EditorThemeSelection Selection { get; private set; }
    public string? Error { get; private set; }
    public ThemeVariant PreviewDefaultVariant { get; }

    public ResourceDictionary CreatePreviewResources() => EditorThemeResources.CreatePreviewDefaults(_previewDefaults);

    public void Apply(EditorThemeSelection selection)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_appliedSelection == selection && Error is null) return;
        if (selection.ProviderId == "builtin")
        {
            if (selection.ThemeId is not ("system" or "light" or "dark"))
                throw new InvalidOperationException(UiStrings.EditorThemeUnavailable);
            ApplyBuiltin(selection.ThemeId);
        }
        else
        {
            EditorThemeProviderCatalog? catalog = Catalogs.FirstOrDefault(item => item.Id == selection.ProviderId);
            if (catalog?.Error is { } error) throw new InvalidOperationException(error);
            if (!_providers.TryGetValue(selection.ProviderId, out Provider? provider)
                || catalog?.Themes.Any(theme => theme.Id == selection.ThemeId) != true)
                throw new InvalidOperationException(UiStrings.EditorThemeUnavailable);
            EditorThemePalette palette = provider.Loader.GetPalette(selection.ThemeId);
            ResourceDictionary resources = EditorThemeResources.Create(palette);
            ApplyBuiltin(palette.IsDark ? "dark" : "light");
            resources.MergedDictionaries.Add(_originalResources);
            _application.Resources = resources;
        }
        Selection = selection;
        _appliedSelection = selection;
        Error = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Restore(EditorThemeSelection selection)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
        try { Apply(selection); }
        catch (Exception error)
        {
            ApplyBuiltin("dark");
            Selection = selection;
            _appliedSelection = null;
            Error = error.GetBaseException().Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ApplyBuiltin(string themeId)
    {
        _application.Resources = _originalResources;
        ThemeVariant variant = themeId switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        _application.RequestedThemeVariant = variant;
        bool isLight = _application.ActualThemeVariant == ThemeVariant.Light;
        _dockThemeManager.Switch(isLight ? 0 : 1);
        _dockThemeManager.SwitchPreset(isLight ? 1 : 2);
        _application.RequestedThemeVariant = variant;
    }

    private void OnContributionsChanged(object? sender, EventArgs args) => Dispatcher.UIThread.Post(() =>
    {
        if (!_disposed) Refresh();
    });

    private void Refresh()
    {
        Dispatcher.UIThread.VerifyAccess();
        List<EditorThemeProviderCatalog> catalogs = new()
        {
            new("builtin", UiStrings.EditorThemeBuiltin, new[]
            {
                new EditorTheme("system", UiStrings.EditorThemeSystem, false),
                new EditorTheme("light", UiStrings.EditorThemeLight, false),
                new EditorTheme("dark", UiStrings.EditorThemeDark, true)
            })
        };
        EditorThemeProviderRegistration[] registrations = _contributions.EditorThemeProviders
            .Where(provider => !_changingPackages.Any(package => BelongsTo(provider.Id, package))).ToArray();
        foreach (var item in _providers.ToArray())
            if (!registrations.Any(registration => registration == item.Value.Registration)) Release(item.Key);
        foreach (EditorThemeProviderRegistration registration in registrations)
        {
            try
            {
                if (!_providers.TryGetValue(registration.Id, out Provider? provider))
                {
                    provider = new(registration, new EditorThemeProviderLoader(registration));
                    _providers.Add(registration.Id, provider);
                }
                IReadOnlyList<EditorTheme> themes = provider.Loader.GetThemes();
                if (themes.Any(theme => string.IsNullOrWhiteSpace(theme.Id) || string.IsNullOrWhiteSpace(theme.Name))
                    || themes.Select(theme => theme.Id).Distinct(StringComparer.Ordinal).Count() != themes.Count)
                    throw new InvalidOperationException(UiStrings.EditorThemeInvalidCatalog);
                catalogs.Add(new(registration.Id, registration.Name, themes));
            }
            catch (Exception error)
            {
                catalogs.Add(new(registration.Id, registration.Name, Array.Empty<EditorTheme>(), error.GetBaseException().Message));
            }
        }
        Catalogs = catalogs;
        EditorThemeProviderCatalog? selectedCatalog = Catalogs.FirstOrDefault(catalog => catalog.Id == Selection.ProviderId);
        if (selectedCatalog?.Error is not null || selectedCatalog?.Themes.Any(theme => theme.Id == Selection.ThemeId) != true)
            _appliedSelection = null;
        Restore(Selection);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task BeforePackageChangeAsync(string package, CancellationToken cancellationToken) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (_disposed) return;
        _changingPackages.Add(package);
        foreach (string id in _providers.Keys.Where(id => BelongsTo(id, package)).ToArray()) Release(id);
    });

    private async Task AfterPackageChangeAsync(string package, CancellationToken cancellationToken) => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        if (_disposed) return;
        _changingPackages.Remove(package);
        Refresh();
    });

    private static bool BelongsTo(string id, string package) => id.StartsWith(package + "/", StringComparison.OrdinalIgnoreCase);

    private void Release(string providerId)
    {
        if (_appliedSelection?.ProviderId == providerId)
        {
            ApplyBuiltin("dark");
            _appliedSelection = null;
        }
        if (_providers.Remove(providerId, out Provider? provider))
        {
            try { provider.Loader.Dispose(); }
            catch (Exception error)
            {
                _logger.LogWarning(error, "Editor theme provider {ProviderId} failed to dispose.", providerId);
                Error = error.GetBaseException().Message;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _contributions.Changed -= OnContributionsChanged;
        if (_lifecycle is not null)
        {
            _lifecycle.ThemeProvidersChanging -= BeforePackageChangeAsync;
            _lifecycle.ThemeProvidersChanged -= AfterPackageChangeAsync;
        }
        foreach (string id in _providers.Keys.ToArray()) Release(id);
        _application.Resources = _originalResources;
        _application.RequestedThemeVariant = _originalVariant;
    }
}
