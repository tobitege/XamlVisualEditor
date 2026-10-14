using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Extensions.Debugging;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class DebugSettingsViewModel : ReactiveObject
{
    private const string NetcoredbgServiceId = "debugger.netcoredbg";
    private readonly IDebuggerServiceRegistry _debuggerRegistry;
    private readonly IDebugToolInstaller? _installer;
    private readonly Func<string> _getAdapterPath;
    private readonly Action<string> _setAdapterPath;
    private readonly Func<bool> _getAutoDownload;
    private readonly Action<bool> _setAutoDownload;
    private readonly Func<DebugToolConsentRequest, Task<bool>> _confirmAsync;

    [Reactive]
    public partial string AdapterPath { get; set; }

    [Reactive]
    public partial bool AutoDownloadTools { get; set; }

    [Reactive]
    public partial bool IsBusy { get; private set; }

    [Reactive]
    public partial string StatusText { get; private set; } = "";

    public ObservableCollection<DebuggerServiceOption> DebuggerServices { get; } = new();

    [Reactive]
    public partial DebuggerServiceOption? SelectedDebuggerService { get; set; }

    public bool IsNetcoredbgSelected => string.Equals(SelectedDebuggerService?.Id, NetcoredbgServiceId, StringComparison.Ordinal);

    public ReactiveCommand<RxVoid, RxVoid> DownloadNetcoredbgCommand { get; }

    public DebugSettingsViewModel(
        IDebuggerServiceRegistry debuggerRegistry,
        IDebugToolInstaller? installer,
        Func<string> getAdapterPath,
        Action<string> setAdapterPath,
        IObservable<string> adapterPathChanges,
        Func<bool> getAutoDownload,
        Action<bool> setAutoDownload,
        Func<DebugToolConsentRequest, Task<bool>> confirmAsync)
    {
        _debuggerRegistry = debuggerRegistry;
        _installer = installer;
        _getAdapterPath = getAdapterPath;
        _setAdapterPath = setAdapterPath;
        _getAutoDownload = getAutoDownload;
        _setAutoDownload = setAutoDownload;
        _confirmAsync = confirmAsync;

        AdapterPath = _getAdapterPath();
        AutoDownloadTools = _getAutoDownload();

        // Deliveries are queued on the main thread scheduler. A delivered value can be stale by then,
        // and writing it back through AdapterPath would bounce between two values forever, so the
        // current source value is taken instead.
        adapterPathChanges
            .DistinctUntilChanged()
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ =>
            {
                string current = _getAdapterPath();
                if (!string.Equals(AdapterPath, current, StringComparison.Ordinal))
                {
                    AdapterPath = current;
                }
            });

        this.WhenAnyValue(x => x.AdapterPath)
            .Skip(1)
            .Subscribe(path => _setAdapterPath(path));

        this.WhenAnyValue(x => x.AutoDownloadTools)
            .Skip(1)
            .Subscribe(value => _setAutoDownload(value));

        IObservable<bool> canDownloadNetcoredbg = this.WhenAnyValue(x => x.SelectedDebuggerService, x => x.IsBusy)
            .Select(tuple => string.Equals(tuple.Property1?.Id, NetcoredbgServiceId, StringComparison.Ordinal) && !tuple.Property2);
        DownloadNetcoredbgCommand = ReactiveCommand.CreateFromTask(DownloadNetcoredbgAsync, canDownloadNetcoredbg);

        RefreshServices();

        Observable.FromEvent<EventHandler, EventArgs>(
                handler => (_, _) => handler(EventArgs.Empty),
                h => _debuggerRegistry.Changed += h,
                h => _debuggerRegistry.Changed -= h)
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => RefreshServices());

        this.WhenAnyValue(x => x.SelectedDebuggerService)
            .Skip(1)
            .Subscribe(service =>
            {
                string? serviceId = service?.Id;
                if (!string.IsNullOrWhiteSpace(serviceId))
                {
                    _debuggerRegistry.ActiveServiceId = serviceId;
                }
            });

        this.WhenAnyValue(x => x.SelectedDebuggerService)
            .Subscribe(_ => this.RaisePropertyChanged(nameof(IsNetcoredbgSelected)));
    }

    private void RefreshServices()
    {
        DebuggerServices.Clear();
        foreach (DebuggerServiceRegistration service in _debuggerRegistry.Services.OrderBy(s => s.DisplayName))
        {
            DebuggerServices.Add(new DebuggerServiceOption(service.Id, service.DisplayName));
        }

        string? activeId = _debuggerRegistry.ActiveServiceId;
        DebuggerServiceOption? next = DebuggerServices.FirstOrDefault(s => s.Id == activeId)
            ?? DebuggerServices.FirstOrDefault();
        if (!ReferenceEquals(SelectedDebuggerService, next))
        {
            SelectedDebuggerService = next;
        }
    }

    private async Task DownloadNetcoredbgAsync()
    {
        if (_installer is null)
        {
            StatusText = "Debug tool installer unavailable.";
            return;
        }

        IsBusy = true;
        StatusText = "Downloading netcoredbg...";
        try
        {
            string? path = await _installer.EnsureNetcoredbgAsync(_confirmAsync).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(path))
            {
                AdapterPath = path;
                StatusText = "netcoredbg installed.";
            }
            else
            {
                StatusText = "netcoredbg download cancelled.";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Download failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>Represents a selectable debugger service in settings UI.</summary>
public sealed partial record DebuggerServiceOption(string Id, string DisplayName);
