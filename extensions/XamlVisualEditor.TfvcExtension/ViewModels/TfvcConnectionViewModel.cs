using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>Connection settings and workspace selection shared by the TFVC panels.</summary>
public sealed partial class TfvcConnectionViewModel : ReactiveObject, IDisposable
{
    private readonly TfvcSession _session;
    private readonly string _panelId;
    private readonly IDisposable _settingsVisibilitySubscription;
    private bool _isSyncing;
    private bool _isDisposed;
    private bool _isRestoringSettingsVisibility;
    private bool _settingsVisibilityChanged;

    /// <summary>Creates the view model.</summary>
    /// <param name="session">The shared TFVC session.</param>
    /// <param name="panelId">Identifies the panel whose settings visibility persists.</param>
    public TfvcConnectionViewModel(TfvcSession session, string panelId)
    {
        _session = session;
        _panelId = panelId;
        ConnectCommand = ReactiveCommand.CreateFromTask(ConnectAsync);
        ToggleSettingsCommand = ReactiveCommand.Create(() => { IsSettingsVisible = !IsSettingsVisible; });

        this.WhenAnyValue(x => x.SelectedWorkspace)
            .Skip(1)
            .Where(_ => !_isSyncing)
            .Subscribe(workspace => _ = SelectWorkspaceAsync(workspace));

        _session.StateChanged += OnSessionStateChanged;
        SyncFromSession();
        if (!_session.Settings.IsConfigured)
        {
            IsSettingsVisible = true;
            _ = SuggestCollectionUrlAsync();
        }

        _settingsVisibilitySubscription = this.WhenAnyValue(x => x.IsSettingsVisible)
            .Skip(1)
            .Where(_ => !_isRestoringSettingsVisibility)
            .Subscribe(isVisible =>
            {
                _settingsVisibilityChanged = true;
                _ = _session.SaveSettingsVisibleAsync(_panelId, isVisible, CancellationToken.None);
            });
        _ = RestoreSettingsVisibilityAsync();
    }

    /// <summary>Gets the workspaces of the current user.</summary>
    public ObservableCollection<TfvcWorkspace> Workspaces { get; } = new();

    /// <summary>Connects with the entered settings.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ConnectCommand { get; }

    /// <summary>Shows or hides the connection settings.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleSettingsCommand { get; }

    /// <summary>Gets or sets the entered collection URL.</summary>
    [Reactive]
    public partial string? CollectionUrl { get; set; }

    /// <summary>Gets or sets the entered tf.exe path.</summary>
    [Reactive]
    public partial string? TfExePath { get; set; }

    /// <summary>Gets or sets whether the connection settings are visible.</summary>
    [Reactive]
    public partial bool IsSettingsVisible { get; set; }

    /// <summary>Gets or sets the selected workspace.</summary>
    [Reactive]
    public partial TfvcWorkspace? SelectedWorkspace { get; set; }

    /// <summary>Gets the connection message, typically an error.</summary>
    [Reactive]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets whether a connection or workspace change started in this panel is running.</summary>
    [Reactive]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets whether the session is connected.</summary>
    [Reactive]
    public partial bool IsConnected { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _settingsVisibilitySubscription.Dispose();
        _session.StateChanged -= OnSessionStateChanged;
    }

    // The stored visibility applies unless the panel changed it while the value was loading.
    private async Task RestoreSettingsVisibilityAsync()
    {
        bool? stored = await _session.LoadSettingsVisibleAsync(_panelId, CancellationToken.None);
        if (stored is not { } isVisible || _isDisposed || _settingsVisibilityChanged)
        {
            return;
        }

        _isRestoringSettingsVisibility = true;
        try
        {
            IsSettingsVisible = isVisible;
        }
        finally
        {
            _isRestoringSettingsVisibility = false;
        }
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            await _session.ApplySettingsAsync(new TfvcSettings(CollectionUrl, TfExePath), cancellationToken);
        }
        finally
        {
            IsBusy = false;
        }

        if (_session.IsConnected)
        {
            IsSettingsVisible = false;
        }
    }

    private async Task SelectWorkspaceAsync(TfvcWorkspace? workspace)
    {
        if (workspace is null)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _session.SelectWorkspaceAsync(workspace, CancellationToken.None);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SuggestCollectionUrlAsync()
    {
        string? suggestion = await _session.SuggestCollectionUrlAsync(CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(suggestion))
        {
            RxSchedulers.MainThreadScheduler.Schedule(() =>
            {
                if (string.IsNullOrWhiteSpace(CollectionUrl))
                {
                    CollectionUrl = suggestion;
                }
            });
        }
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(SyncFromSession);
    }

    private void SyncFromSession()
    {
        if (_isDisposed)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            if (!IsSettingsVisible || string.IsNullOrWhiteSpace(CollectionUrl))
            {
                CollectionUrl = _session.Settings.CollectionUrl ?? CollectionUrl;
                TfExePath = _session.Settings.TfExePath;
            }

            bool sameWorkspaces = Workspaces.Count == _session.Workspaces.Count;
            for (int i = 0; sameWorkspaces && i < Workspaces.Count; i++)
            {
                sameWorkspaces = ReferenceEquals(Workspaces[i], _session.Workspaces[i]);
            }

            if (!sameWorkspaces)
            {
                Workspaces.Clear();
                foreach (TfvcWorkspace workspace in _session.Workspaces)
                {
                    Workspaces.Add(workspace);
                }
            }

            SelectedWorkspace = _session.CurrentWorkspace;
            StatusMessage = _session.StatusMessage;
            IsConnected = _session.IsConnected;
        }
        finally
        {
            _isSyncing = false;
        }
    }
}
