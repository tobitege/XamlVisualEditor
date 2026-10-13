using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class ExtensionManagerViewModel : ReactiveObject, IDisposable
{
    private readonly IExtensionManager _manager;
    private readonly Func<Task<string?>> _selectPackagePathAsync;
    private readonly CompositeDisposable _subscriptions = new();
    private readonly CompositeDisposable _itemSubscriptions = new();
    private bool _disposed;
    private int _refreshVersion;

    public ExtensionManagerViewModel(
        IExtensionManager manager,
        Func<Task<string?>> selectPackagePathAsync)
    {
        _manager = manager;
        _selectPackagePathAsync = selectPackagePathAsync;

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        InstallCommand = ReactiveCommand.CreateFromTask(InstallAsync);
        CheckUpdatesCommand = ReactiveCommand.CreateFromTask(CheckUpdatesAsync);
        CloseCommand = ReactiveCommand.CreateFromTask(() => CloseInteraction.Handle(RxVoid.Default));

        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedPackage)
            .Select(item => item is not null);
        UninstallCommand = ReactiveCommand.CreateFromTask(UninstallAsync, hasSelection);

        IDisposable selectionSubscription = this.WhenAnyValue(x => x.SelectedPackage)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(HasSelection)));
        _subscriptions.Add(selectionSubscription);

        // The constructor may run inside ExtensionManager's lazy built-in package
        // discovery (extensions are resolved from DI there). The refresh MUST run after the
        // current DI resolution completes: the Rx main-thread scheduler executes INLINE when
        // already on the UI thread, which re-entered the container while MainWindowViewModel's
        // singleton slot was still unfilled and produced a second MainWindowViewModel instance
        // (window bound to one, services to the other). Dispatcher.Post defers for real.
        Avalonia.Threading.Dispatcher.UIThread.Post(
            () => _ = InitialRefreshAsync(),
            Avalonia.Threading.DispatcherPriority.Background);
    }

    private async Task InitialRefreshAsync()
    {
        if (_disposed) return;
        try
        {
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UpdateUiAsync(() => StatusMessage = string.Format(UiStrings.ExtensionRefreshFailed, (object?)(ex.Message))).ConfigureAwait(false);
        }
    }

    public ObservableCollection<ExtensionPackageItemViewModel> InstalledPackages { get; } = new();

    [Reactive]
    public partial ExtensionPackageItemViewModel? SelectedPackage { get; set; }

    [Reactive]
    public partial string StatusMessage { get; set; } = string.Empty;

    public bool HasSelection => SelectedPackage is not null;

    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> InstallCommand { get; }

    public Interaction<RxVoid, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> CloseCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> UninstallCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CheckUpdatesCommand { get; }

    public void Dispose()
    {
        _disposed = true;
        Interlocked.Increment(ref _refreshVersion);
        _itemSubscriptions.Dispose();
        _subscriptions.Dispose();
    }

    private async Task RefreshAsync()
    {
        int version = Interlocked.Increment(ref _refreshVersion);
        IReadOnlyList<ExtensionPackageInfo> packages = await _manager
            .GetInstalledAsync(CancellationToken.None)
            .ConfigureAwait(false);

        List<(ExtensionPackageInfo Package, bool Enabled)> items = new();
        foreach (ExtensionPackageInfo package in packages)
        {
            bool enabled = await _manager.GetEnabledAsync(package.Manifest.ExtensionId, CancellationToken.None)
                .ConfigureAwait(false);

            items.Add((package, enabled));
        }

        await UpdateUiAsync(() =>
        {
            if (version != _refreshVersion) return;
            _itemSubscriptions.Clear();
            InstalledPackages.Clear();
            foreach (var entry in items)
            {
                ExtensionPackageItemViewModel item = new(entry.Package, entry.Enabled);
                item.EnabledChanged += OnItemEnabledChanged;
                InstalledPackages.Add(item);
                _itemSubscriptions.Add(Disposable.Create(() => item.EnabledChanged -= OnItemEnabledChanged));
            }
            StatusMessage = InstalledPackages.Count == 0 ? UiStrings.NoExtensionsInstalled : string.Empty;
        }).ConfigureAwait(false);
    }

    private async Task InstallAsync()
    {
        string? path = await _selectPackagePathAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path))
        {
            await UpdateUiAsync(() => StatusMessage = UiStrings.InstallCanceled).ConfigureAwait(false);
            return;
        }

        try
        {
            ExtensionPackageInfo info = await _manager.InstallAsync(path, CancellationToken.None)
                .ConfigureAwait(false);
            await _manager.SetEnabledAsync(info.Manifest.ExtensionId, true, CancellationToken.None)
                .ConfigureAwait(false);
            await UpdateUiAsync(() => StatusMessage = UiStrings.InstalledPrefix + info.Manifest.ExtensionId).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UpdateUiAsync(() => StatusMessage = UiStrings.InstallFailedPrefix + ex.Message).ConfigureAwait(false);
        }
    }

    private async Task UninstallAsync()
    {
        if (SelectedPackage is null)
        {
            return;
        }

        string extensionId = SelectedPackage.ExtensionId;
        try
        {
            await _manager.UninstallAsync(extensionId, CancellationToken.None).ConfigureAwait(false);
            await UpdateUiAsync(() => StatusMessage = UiStrings.UninstalledPrefix + extensionId).ConfigureAwait(false);
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UpdateUiAsync(() => StatusMessage = UiStrings.UninstallFailedPrefix + ex.Message).ConfigureAwait(false);
        }
    }

    private async Task CheckUpdatesAsync()
    {
        IReadOnlyList<ExtensionUpdateInfo> updates = await _manager
            .CheckForUpdatesAsync(CancellationToken.None)
            .ConfigureAwait(false);

        Dictionary<string, ExtensionUpdateInfo> lookup = updates.ToDictionary(
            update => update.Installed.Manifest.ExtensionId,
            StringComparer.OrdinalIgnoreCase);

        await UpdateUiAsync(() =>
        {
            foreach (ExtensionPackageItemViewModel item in InstalledPackages)
            {
                item.LatestVersion = lookup.TryGetValue(item.ExtensionId, out ExtensionUpdateInfo? update)
                    ? update.Available.Manifest.Version : null;
            }
            StatusMessage = updates.Count == 0 ? UiStrings.ExtensionsCurrent : UiStrings.UpdatesAvailable;
        }).ConfigureAwait(false);
    }

    private void OnItemEnabledChanged(object? sender, bool enabled)
    {
        if (sender is not ExtensionPackageItemViewModel item)
        {
            return;
        }

        _ = UpdateEnabledStateAsync(item, enabled);
    }

    private async Task UpdateEnabledStateAsync(ExtensionPackageItemViewModel item, bool enabled)
    {
        try
        {
            await _manager.SetEnabledAsync(item.ExtensionId, enabled, CancellationToken.None)
                .ConfigureAwait(false);
            await UpdateUiAsync(() => StatusMessage = enabled
                ? UiStrings.EnabledPrefix + item.ExtensionId
                : UiStrings.DisabledPrefix + item.ExtensionId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await UpdateUiAsync(() =>
            {
                item.SetEnabledSilently(!enabled);
                StatusMessage = UiStrings.EnablementFailedPrefix + ex.Message;
            }).ConfigureAwait(false);
        }
    }

    private Task UpdateUiAsync(Action action) => Signal.Start(() =>
    {
        if (!_disposed) action();
    }, RxSchedulers.MainThreadScheduler).ToTask();
}

public sealed partial class ExtensionPackageItemViewModel : ReactiveObject
{
    private bool _isEnabled;
    private string? _latestVersion;
    private bool _suppressEnabledChange;

    public ExtensionPackageItemViewModel(ExtensionPackageInfo package, bool isEnabled)
    {
        Package = package;
        ExtensionId = package.Manifest.ExtensionId;
        DisplayName = string.IsNullOrWhiteSpace(package.Manifest.DisplayName)
            ? package.Manifest.Name
            : package.Manifest.DisplayName;
        Version = package.Manifest.Version;
        IsEnabled = isEnabled;
    }

    public ExtensionPackageInfo Package { get; }

    public string ExtensionId { get; }

    public string DisplayName { get; }

    public string Version { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            this.RaiseAndSetIfChanged(ref _isEnabled, value);
            if (!_suppressEnabledChange)
            {
                EnabledChanged?.Invoke(this, value);
            }
        }
    }

    public string? LatestVersion
    {
        get => _latestVersion;
        set
        {
            this.RaiseAndSetIfChanged(ref _latestVersion, value);
            this.RaisePropertyChanged(nameof(UpdateLabel));
        }
    }

    public string UpdateLabel => string.IsNullOrWhiteSpace(LatestVersion)
        ? UiStrings.ExtensionUpToDate
        : UiStrings.ExtensionUpdatePrefix + LatestVersion;

    public event EventHandler<bool>? EnabledChanged;

    public void SetEnabledSilently(bool enabled)
    {
        _suppressEnabledChange = true;
        IsEnabled = enabled;
        _suppressEnabledChange = false;
    }
}
