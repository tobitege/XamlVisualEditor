using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Extensions;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.ToolboxExtension;

public sealed class ToolboxPanelItem : IToolboxDragItem
{
    public ToolboxPanelItem(
        string displayName,
        string commandId,
        IReadOnlyList<string> commandArgs,
        bool? isStandardControl = null,
        string? fullName = null,
        string? assemblyName = null,
        string? category = null,
        string? assemblyVersion = null)
    {
        DisplayName = displayName;
        CommandId = commandId;
        CommandArguments = commandArgs;
        IsStandardControl = isStandardControl ?? false;
        FullName = fullName ?? commandArgs.FirstOrDefault() ?? displayName;
        AssemblyName = assemblyName ?? string.Empty;
        AssemblyVersion = assemblyVersion;
        Category = category ?? "Custom";
    }

    public string DisplayName { get; }

    public string CommandId { get; }

    public IReadOnlyList<string> CommandArguments { get; }

    /// <inheritdoc />
    public bool CanDrag => CommandId == ToolboxPanelViewModel.DefaultInsertCommandId && CommandArguments.Count >= 2;

    /// <inheritdoc />
    public string TypeName => CanDrag ? CommandArguments[0] : string.Empty;

    /// <inheritdoc />
    public string XmlNamespace => CanDrag ? CommandArguments[1] : string.Empty;

    /// <summary>Gets whether this item belongs to the standard Avalonia controls.</summary>
    public bool IsStandardControl { get; }

    /// <summary>Gets the unambiguous CLR type name when supplied by the project catalog.</summary>
    public string FullName { get; }

    /// <summary>Gets the assembly that supplies the control.</summary>
    public string AssemblyName { get; }

    /// <summary>Version of the assembly this control was read from.</summary>
    public string? AssemblyVersion { get; }

    /// <summary>Gets the library-defined toolbox category.</summary>
    public string Category { get; }

    /// <summary>Gets the library and category displayed below the control name.</summary>
    public string CategoryDisplayName => UiLabels.Category(Category);
    public string Subtitle => string.IsNullOrEmpty(AssemblyName) ? CategoryDisplayName : AssemblyName + " / " + CategoryDisplayName;

    /// <summary>Gets the full identity displayed in the tooltip.</summary>
    public string Description => FullName + Environment.NewLine + Subtitle;
}

/// <summary>Represents a filter choice, with null meaning all values.</summary>
public sealed record ToolboxFilterOption(string? Value, string Label);

public sealed class ToolboxAssemblyGroup : ReactiveObject
{
    private readonly ToolboxPanelViewModel _owner;
    private IReadOnlyList<ToolboxPanelItem> _items = Array.Empty<ToolboxPanelItem>();
    private bool _isExpanded = true;
    private int _catalogCount;
    private bool _canFavorite;

    internal ToolboxAssemblyGroup(ToolboxPanelViewModel owner, string assemblyName)
    {
        _owner = owner;
        AssemblyName = assemblyName;
    }

    public string AssemblyName { get; }
    public string DisplayName => string.IsNullOrEmpty(AssemblyName) ? UiStrings.CustomEntries : AssemblyName;
    public string Header => $"{DisplayName} ({Items.Count})";
    public IReadOnlyList<ToolboxPanelItem> Items => _items;
    public string CatalogHeader => $"{DisplayName} ({_catalogCount})";
    public bool CanFavorite => _canFavorite;
    /// <summary>Gets whether the library contains official Avalonia controls only.</summary>
    public bool IsStandardAssembly { get; private set; }
    /// <summary>Version of the current library catalog.</summary>
    public string? AssemblyVersion { get; private set; }
    public string Description => AssemblyVersion is null ? DisplayName : DisplayName + " · " + AssemblyVersion;
    /// <summary>Gets whether the library can be shown or hidden.</summary>
    public bool CanEditIncluded => _owner.CanEditAssemblyVisibility;
    /// <summary>Gets whether the library can be expanded or collapsed.</summary>
    public bool CanEditExpanded => _owner.CanEditCollapsedAssemblies;
    /// <summary>Gets whether the library can be added to or removed from the favorites.</summary>
    public bool CanEditFavorite => _owner.CanEditFavoriteAssemblies;
    public string FavoriteGlyph => IsFavorite ? "\u2605" : "\u2606";
    public string FavoriteToolTip => IsFavorite ? UiStrings.FavoriteAssemblyRemove : UiStrings.FavoriteAssemblyKeep;

    public bool IsIncluded
    {
        get => _owner.IsAssemblyIncluded(AssemblyName);
        set => _owner.SetAssemblyIncluded(this, value);
    }

    public bool IsFavorite
    {
        get => CanFavorite && _owner.IsAssemblyFavorite(AssemblyName);
        set => _owner.SetAssemblyFavorite(this, value);
    }

    internal void UpdateCatalogInfo(int count, bool canFavorite, bool isStandardAssembly, string? assemblyVersion)
    {
        IsStandardAssembly = isStandardAssembly;
        if (AssemblyVersion != assemblyVersion)
        {
            AssemblyVersion = assemblyVersion;
            this.RaisePropertyChanged(nameof(AssemblyVersion));
            this.RaisePropertyChanged(nameof(Description));
        }
        if (_catalogCount != count)
        {
            _catalogCount = count;
            this.RaisePropertyChanged(nameof(CatalogHeader));
        }
        if (_canFavorite != canFavorite)
        {
            _canFavorite = canFavorite;
            this.RaisePropertyChanged(nameof(CanFavorite));
        }
        NotifyPreferencesChanged();
    }

    internal void NotifyPreferencesChanged()
    {
        this.RaisePropertyChanged(nameof(IsIncluded));
        this.RaisePropertyChanged(nameof(IsFavorite));
        this.RaisePropertyChanged(nameof(FavoriteGlyph));
        this.RaisePropertyChanged(nameof(FavoriteToolTip));
        this.RaisePropertyChanged(nameof(CanEditIncluded));
        this.RaisePropertyChanged(nameof(CanEditExpanded));
        this.RaisePropertyChanged(nameof(CanEditFavorite));
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => _owner.SetAssemblyExpanded(this, value);
    }

    internal void ApplyExpandedState(bool expanded) =>
        this.RaiseAndSetIfChanged(ref _isExpanded, expanded, nameof(IsExpanded));

    public ToolboxPanelItem? SelectedItem
    {
        get => _owner.SelectedItem is { } item && Items.Contains(item) ? item : null;
        set => _owner.SelectGroupItem(this, value);
    }

    internal void UpdateItems(IReadOnlyList<ToolboxPanelItem> items)
    {
        if (_items.SequenceEqual(items)) return;
        _items = items;
        this.RaisePropertyChanged(nameof(Items));
        this.RaisePropertyChanged(nameof(Header));
    }

    internal void NotifySelectionChanged() => this.RaisePropertyChanged(nameof(SelectedItem));
}

public sealed class ToolboxCatalogEntry
{
    public string? DisplayName { get; set; }

    public string? TypeName { get; set; }

    public string? XmlNamespace { get; set; }

    public string? ParentNodeId { get; set; }

    public string? CommandId { get; set; }

    public List<string>? CommandArguments { get; set; }

    /// <summary>Identifies the assembly group for a custom catalog entry.</summary>
    public string? AssemblyName { get; set; }

    /// <summary>Declares the origin of a custom entry. Project entries use actual assembly metadata.</summary>
    public bool? IsStandardControl { get; set; }
}

public sealed partial class ToolboxPanelViewModel : ReactiveObject, IDisposable
{
    private readonly ICommands _commands;
    private readonly ISettings _settings;
    private readonly IControlCatalog? _catalog;
    private readonly IDesignerHost? _designer;
    private readonly IEditorServices? _editor;
    private readonly ISequencer _uiScheduler;
    private readonly ToolboxFilterOption _allLibraries = new(null, UiStrings.AllLibraries);
    private readonly ToolboxFilterOption _allCategories = new(null, UiStrings.AllCategories);
    private ToolboxPanelItem? _selectedItem;
    private ToolboxFilterOption? _selectedLibrary;
    private ToolboxFilterOption? _selectedCategory;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isLoading;
    private bool _isCancelling;
    private bool _hasLoadError;
    private bool _isProgressIndeterminate = true;
    private double _loadProgress;
    private string? _loadErrorDetails;
    private bool _lastIncludeProjectControls = true;
    private bool _isLoadStatusVisible;
    private readonly SerialDisposable _statusDismissal = new();
    private readonly Dictionary<string, ToolboxAssemblyGroup> _assemblyGroups = new(StringComparer.Ordinal);
    private bool _updatingGroups;
    private bool _updatingFilters;
    private bool _disposed;
    private int _reloadVersion;
    private CancellationTokenSource? _loadCancellation;
    private const string CatalogSettingsKey = "toolbox.catalog";
    internal const string DefaultInsertCommandId = "toolbox.insertSelected";

    public ToolboxPanelViewModel(ICommands commands, ISettings settings,
        IControlCatalog? catalog = null, ISequencer? uiScheduler = null,
        IWorkspaceInfo? workspaceInfo = null, IExtensionStorage? storage = null, IDesignerHost? designer = null,
        IEditorServices? editor = null)
    {
        _commands = commands;
        _settings = settings;
        _catalog = catalog;
        _designer = designer;
        _editor = editor;
        _uiScheduler = uiScheduler ?? RxSchedulers.MainThreadScheduler;
        StatusDismissalScheduler = _uiScheduler;
        _workspaceInfo = workspaceInfo;
        _preferenceStorage = storage;
        _preferencesReady = storage is null;
        if (_workspaceInfo is not null) _workspaceInfo.WorkspaceChanged += OnPreferencesWorkspaceChanged;
        if (_designer is not null) _designer.ActiveDocumentChanged += OnInsertDocumentChanged;
        if (_editor is not null) _editor.ActiveDocumentChanged += OnEditorDocumentChanged;

        ReplaceItems(TryLoadCatalogFromSettings());
        StatusText = catalog is null ? UiStrings.ControlCatalogUnavailable : UiStrings.ControlCatalogNotLoaded;

        // Inserting depends only on the selection and the designer document, not on catalog loads or preferences.
        IObservable<bool> canInsert = this.WhenAnyValue(x => x.SelectedItem, x => x.HasInsertTarget,
            (item, hasTarget) => item is not null && hasTarget);
        InsertSelectedCommand = ReactiveCommand.CreateFromTask(InsertSelectedAsync, canInsert);
        RetryLoadCommand = ReactiveCommand.CreateFromTask(
            () => ReloadAsync(includeProjectControls: _lastIncludeProjectControls),
            this.WhenAnyValue(x => x.HasLoadError, x => x.IsLoading, (error, loading) => error && !loading));
        CancelLoadCommand = ReactiveCommand.Create(CancelLoad,
            this.WhenAnyValue(x => x.IsLoading, x => x.IsCancelling, x => x.HasLoadError,
                (loading, cancelling, error) => (loading && !cancelling) || error));
        DismissLoadStatusCommand = ReactiveCommand.Create(() =>
        {
            _statusDismissal.Disposable = null;
            IsLoadStatusVisible = false;
        }, this.WhenAnyValue(x => x.HasLoadTask).Select(active => !active));
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            SearchText = string.Empty;
            SelectedLibrary = _allLibraries;
            SelectedCategory = _allCategories;
            SetAllAssembliesIncluded(true);
        });
        ShowAllAssembliesCommand = ReactiveCommand.Create(() => SetAllAssembliesIncluded(true),
            this.WhenAnyValue(x => x.CanEditAssemblyVisibility));
        HideAllAssembliesCommand = ReactiveCommand.Create(() => SetAllAssembliesIncluded(false),
            this.WhenAnyValue(x => x.CanEditAssemblyVisibility));
        CollapseAllCommand = ReactiveCommand.Create(() => SetAllGroupsExpanded(false),
            this.WhenAnyValue(x => x.CanEditCollapsedAssemblies));
        ExpandAllCommand = ReactiveCommand.Create(() => SetAllGroupsExpanded(true),
            this.WhenAnyValue(x => x.CanEditCollapsedAssemblies));
        if (_catalog is not null)
        {
            _catalog.Changed += OnCatalogChanged;
        }
    }

    public ObservableCollection<ToolboxPanelItem> Items { get; } = new();

    /// <summary>Gets the controls that pass the active filters, in catalog order.</summary>
    public IReadOnlyList<ToolboxPanelItem> VisibleItems { get; private set; } = Array.Empty<ToolboxPanelItem>();

    /// <summary>Gets the visible assemblies, each containing its alphabetically sorted controls.</summary>
    public ObservableCollection<ToolboxAssemblyGroup> AssemblyGroups { get; } = new();

    /// <summary>Gets available library filters.</summary>
    public ObservableCollection<ToolboxFilterOption> Libraries { get; } = new();

    /// <summary>Gets available category filters.</summary>
    public ObservableCollection<ToolboxFilterOption> Categories { get; } = new();

    /// <summary>Gets or sets the name, namespace, library or category search.</summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            this.RaiseAndSetIfChanged(ref _searchText, value);
            ApplyFilter();
        }
    }

    /// <summary>Gets or sets the library filter.</summary>
    public ToolboxFilterOption? SelectedLibrary
    {
        get => _selectedLibrary;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedLibrary, value);
            ApplyFilter();
        }
    }

    /// <summary>Gets or sets the category filter.</summary>
    public ToolboxFilterOption? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedCategory, value);
            ApplyFilter();
        }
    }

    /// <summary>Gets whether control discovery is running.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(NoMatches));
            this.RaisePropertyChanged(nameof(HasLoadTask));
            this.RaisePropertyChanged(nameof(IsLoadingProjectControls));
            NotifyPreferenceAvailability();
        }
    }

    /// <summary>Gets whether the running load reads the project assemblies in addition to the standard controls.</summary>
    public bool IsLoadingProjectControls => IsLoading && _lastIncludeProjectControls;

    /// <summary>Gets whether the running search has yet to confirm its cancellation.</summary>
    public bool IsCancelling
    {
        get => _isCancelling;
        private set => this.RaiseAndSetIfChanged(ref _isCancelling, value);
    }

    /// <summary>Gets whether retry or cancel has to be offered.</summary>
    public bool HasLoadError
    {
        get => _hasLoadError;
        private set
        {
            this.RaiseAndSetIfChanged(ref _hasLoadError, value);
            this.RaisePropertyChanged(nameof(HasLoadTask));
        }
    }

    public bool HasLoadTask => IsLoading || HasLoadError;

    /// <summary>Shows the running task or its briefly visible result.</summary>
    public bool IsLoadStatusVisible
    {
        get => _isLoadStatusVisible;
        private set => this.RaiseAndSetIfChanged(ref _isLoadStatusVisible, value);
    }

    /// <summary>Holds the full error message for the tooltip.</summary>
    public string? LoadErrorDetails
    {
        get => _loadErrorDetails;
        private set => this.RaiseAndSetIfChanged(ref _loadErrorDetails, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => this.RaiseAndSetIfChanged(ref _isProgressIndeterminate, value);
    }

    public double LoadProgress
    {
        get => _loadProgress;
        private set => this.RaiseAndSetIfChanged(ref _loadProgress, value);
    }

    /// <summary>Gets discovery progress and actionable loading errors.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>Gets whether the active filters exclude every available control.</summary>
    public bool NoMatches => !IsLoading && Items.Count > 0 && VisibleItems.Count == 0;

    public ToolboxPanelItem? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (ReferenceEquals(_selectedItem, value))
            {
                return;
            }

            this.RaiseAndSetIfChanged(ref _selectedItem, value);
            foreach (ToolboxAssemblyGroup group in _assemblyGroups.Values)
            {
                group.NotifySelectionChanged();
            }
        }
    }

    internal void SelectGroupItem(ToolboxAssemblyGroup group, ToolboxPanelItem? item)
    {
        if (_updatingGroups) return;
        if (item is not null && group.Items.Contains(item))
        {
            SelectedItem = item;
        }
        else if (item is null && SelectedItem is { } selected && group.Items.Contains(selected))
        {
            SelectedItem = null;
        }
    }

    public ReactiveCommand<RxVoid, RxVoid> InsertSelectedCommand { get; }

    /// <summary>Repeats the most recently failed search.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RetryLoadCommand { get; }

    /// <summary>Hides the result of a finished task.</summary>
    public ReactiveCommand<RxVoid, RxVoid> DismissLoadStatusCommand { get; }

    /// <summary>Cancels the active catalog request.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelLoadCommand { get; }

    /// <summary>Restores the unfiltered catalog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearFiltersCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CollapseAllCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> ExpandAllCommand { get; }

    private void SetAllGroupsExpanded(bool expanded)
    {
        if (!CanEditCollapsedAssemblies) return;
        bool changed = false;
        foreach (ToolboxAssemblyGroup group in AssemblyGroups)
        {
            group.ApplyExpandedState(expanded);
            changed |= expanded ? _collapsedAssemblies.Remove(group.AssemblyName) : _collapsedAssemblies.Add(group.AssemblyName);
        }
        if (changed) SaveCollapsedAssemblies();
    }

    /// <summary>
    /// Inserting and dragging need the active designer document, the same document that the "hasDesignerDocument"
    /// command condition requires.
    /// </summary>
    public bool HasInsertTarget => IsActiveDesignerDocument(_designer, _editor);

    /// <summary>
    /// Returns whether the designer host's document is the active editor document. While a text editor is active, the
    /// designer host still reports a designer document in the background; that document is no insert target. A host
    /// without editor services has no other document in front.
    /// </summary>
    internal static bool IsActiveDesignerDocument(IDesignerHost? designer, IEditorServices? editor)
    {
        string? path = designer?.ActiveDocumentPath;
        if (string.IsNullOrWhiteSpace(path)) return false;
        return editor is null || XamlVisualEditor.Core.FileSystemPathComparison.Equals(editor.ActiveDocument?.FilePath, path);
    }

    private void OnInsertDocumentChanged(object? sender, DesignerDocumentChangedEventArgs args) =>
        _ = UpdateUiAsync(() => this.RaisePropertyChanged(nameof(HasInsertTarget)));

    private void OnEditorDocumentChanged(object? sender, EditorActiveDocumentChangedEventArgs args) =>
        _ = UpdateUiAsync(() => this.RaisePropertyChanged(nameof(HasInsertTarget)));

    private async Task InsertSelectedAsync()
    {
        ToolboxPanelItem? item = SelectedItem;
        if (item is null || !HasInsertTarget)
        {
            return;
        }

        try
        {
            object?[] commandArgs = item.CommandArguments.Cast<object?>().ToArray();
            await _commands.ExecuteAsync(item.CommandId, commandArgs, CancellationToken.None);
        }
        catch (Exception error)
        {
            StatusText = string.Format(UiStrings.CannotInsertControl, item.DisplayName, error.Message);
        }
    }

    /// <summary>Gets the most recently started load of <see cref="LoadCatalogAsync"/>.</summary>
    internal Task PendingCatalogLoad { get; private set; } = Task.CompletedTask;

    /// <summary>
    /// Publishes the standard controls first and then adds the project controls, so a running project scan does not
    /// hide the standard controls.
    /// </summary>
    public Task LoadCatalogAsync(CancellationToken cancellationToken = default) =>
        PendingCatalogLoad = LoadCatalogCoreAsync(cancellationToken);

    private async Task LoadCatalogCoreAsync(CancellationToken cancellationToken)
    {
        bool published = await ReloadCoreAsync(cancellationToken, includeProjectControls: false, forceRefresh: false)
            .ConfigureAwait(false);
        // A cancelled, failed or superseded base load ends the sequence. The catalog cache is keyed by the assembly
        // files, so the project load reads changed assemblies without forcing a refresh.
        if (published && _catalog is { HasProjectAssemblies: true })
        {
            await ReloadCoreAsync(cancellationToken, includeProjectControls: true, forceRefresh: false).ConfigureAwait(false);
        }
    }

    /// <summary>Refreshes the catalog while ignoring results superseded by another workspace.</summary>
    public Task ReloadAsync(CancellationToken cancellationToken = default, bool includeProjectControls = true,
        bool forceRefresh = true) => ReloadCoreAsync(cancellationToken, includeProjectControls, forceRefresh);

    /// <summary>Returns whether this request published its snapshot.</summary>
    private async Task<bool> ReloadCoreAsync(CancellationToken cancellationToken, bool includeProjectControls,
        bool forceRefresh)
    {
        if (_disposed)
        {
            return false;
        }
        int version = Interlocked.Increment(ref _reloadVersion);
        bool published = false;
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version)) return;
                _loadCancellation?.Cancel();
                _loadCancellation = request;
                _statusDismissal.Disposable = null;
                IsLoadStatusVisible = true;
                _lastIncludeProjectControls = includeProjectControls;
                this.RaisePropertyChanged(nameof(IsLoadingProjectControls));
                HasLoadError = false;
                LoadErrorDetails = null;
                IsCancelling = false;
                LoadProgress = 0;
                IsProgressIndeterminate = true;
                IsLoading = true;
                StatusText = includeProjectControls ? UiStrings.LoadingProjectControls : UiStrings.LoadingStandardControls;
            }).ConfigureAwait(false);
            if (!IsCurrent(version)) return false;
            request.Token.ThrowIfCancellationRequested();
            ControlCatalogSnapshot snapshot = _catalog is null
                ? new ControlCatalogSnapshot(Array.Empty<ControlCatalogItem>(), Array.Empty<string>())
                : await _catalog.GetControlsAsync(request.Token, new CatalogProgress(this, version, request.Token),
                    includeProjectControls, forceRefresh).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version) || request.IsCancellationRequested) return;
                IEnumerable<ToolboxPanelItem> discovered = snapshot.Controls.Select(control =>
                    new ToolboxPanelItem(control.DisplayName, DefaultInsertCommandId,
                        new[] { control.TypeName, control.XmlNamespace }, control.IsStandardControl,
                        control.FullName, control.AssemblyName, control.Category, control.AssemblyVersion));
                ReplaceItems(discovered.Concat(TryLoadCatalogFromSettings()));
                StatusText = _catalog is null ? UiStrings.ControlCatalogUnavailable
                    : string.Format(UiStrings.ControlsAvailable, Items.Count);
                if (snapshot.Diagnostics.Count > 0)
                {
                    StatusText += Environment.NewLine + string.Join(Environment.NewLine, snapshot.Diagnostics);
                }
                published = true;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            await UpdateUiAsync(() => { if (IsCurrent(version)) StatusText = UiStrings.ControlDiscoveryCanceled; }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version)) return;
                LoadErrorDetails = error.ToString();
                StatusText = error is IOException or UnauthorizedAccessException
                    ? UiStrings.ToolboxFileReadFailed : UiStrings.ToolboxTaskFailed;
                HasLoadError = true;
            }).ConfigureAwait(false);
        }
        finally
        {
            await UpdateUiAsync(() =>
            {
                if (ReferenceEquals(_loadCancellation, request)) _loadCancellation = null;
                if (IsCurrent(version))
                {
                    IsLoading = false;
                    IsCancelling = false;
                    if (request.IsCancellationRequested && !HasLoadError) StatusText = UiStrings.ControlDiscoveryCanceled;
                    if (!HasLoadError) ScheduleStatusDismissal();
                }
            }).ConfigureAwait(false);
        }
        return published;
    }

    private bool IsCurrent(int version) => !_disposed && version == Volatile.Read(ref _reloadVersion);

    private Task UpdateUiAsync(Action action) => Signal.Start(action, _uiScheduler).ToTask();

    private void OnCatalogChanged(object? sender, EventArgs args) =>
        _ = ReloadAsync(includeProjectControls: _catalog?.HasProjectAssemblies ?? false, forceRefresh: false);

    private void CancelLoad()
    {
        if (IsLoading)
        {
            IsCancelling = true;
            StatusText = UiStrings.ControlDiscoveryCancelling;
            _loadCancellation?.Cancel();
        }
        else
        {
            HasLoadError = false;
            LoadErrorDetails = null;
            StatusText = UiStrings.ControlDiscoveryCanceled;
            ScheduleStatusDismissal();
        }
    }

    /// <summary>Gets or sets how long the status of a completed load stays visible.</summary>
    internal TimeSpan LoadStatusDismissDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the scheduler whose clock times the status dismissal; it runs its work on the UI thread.</summary>
    internal ISequencer StatusDismissalScheduler { get; set; }

    private void ScheduleStatusDismissal()
    {
        _statusDismissal.Disposable = StatusDismissalScheduler.Schedule(LoadStatusDismissDelay, () =>
        {
            if (!_disposed && !HasLoadTask) IsLoadStatusVisible = false;
        });
    }

    private sealed class CatalogProgress(ToolboxPanelViewModel owner, int version, CancellationToken token)
        : IProgress<ControlCatalogProgress>
    {
        public void Report(ControlCatalogProgress value) => owner._uiScheduler.Schedule(() =>
        {
            if (!owner.IsCurrent(version) || !owner.IsLoading || token.IsCancellationRequested) return;
            owner.IsProgressIndeterminate = value.TotalAssemblies == 0 || string.IsNullOrEmpty(value.AssemblyName);
            owner.LoadProgress = value.TotalAssemblies == 0 ? 0 : 100.0 * value.CompletedAssemblies / value.TotalAssemblies;
            owner.StatusText = string.IsNullOrEmpty(value.AssemblyName) ? UiStrings.ClassifyingProjectControls
                : string.Format(UiStrings.ReadingControlAssembly, value.CompletedAssemblies + 1, value.TotalAssemblies, value.AssemblyName);
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        FlushPreferenceWrites();
        DisposeAssemblyPreferences();
        Interlocked.Increment(ref _reloadVersion);
        if (_catalog is not null) _catalog.Changed -= OnCatalogChanged;
        if (_designer is not null) _designer.ActiveDocumentChanged -= OnInsertDocumentChanged;
        if (_editor is not null) _editor.ActiveDocumentChanged -= OnEditorDocumentChanged;
        _loadCancellation?.Cancel();
        _statusDismissal.Dispose();
        InsertSelectedCommand.Dispose();
        RetryLoadCommand.Dispose();
        DismissLoadStatusCommand.Dispose();
        CancelLoadCommand.Dispose();
        ClearFiltersCommand.Dispose();
        ShowAllAssembliesCommand.Dispose();
        HideAllAssembliesCommand.Dispose();
        CollapseAllCommand.Dispose();
        ExpandAllCommand.Dispose();
    }

    private void ReplaceItems(IEnumerable<ToolboxPanelItem> items)
    {
        string? library = SelectedLibrary?.Value;
        string? category = SelectedCategory?.Value;
        _updatingFilters = true;
        try
        {
            Items.Clear();
            foreach (ToolboxPanelItem item in items
                .DistinctBy(item => (item.AssemblyName, item.FullName, item.DisplayName, item.CommandId, string.Join("\0", item.CommandArguments)))
                .OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(item => item.AssemblyName, StringComparer.Ordinal))
            {
                Items.Add(item);
            }
            Libraries.Clear();
            Libraries.Add(_allLibraries);
            foreach (var assembly in Items.Where(item => !string.IsNullOrEmpty(item.AssemblyName))
                .GroupBy(item => item.AssemblyName, StringComparer.Ordinal)
                .OrderByDescending(group => group.All(item => item.IsStandardControl))
                .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                Libraries.Add(new ToolboxFilterOption(assembly.Key, assembly.Key));
            }
            Categories.Clear();
            Categories.Add(_allCategories);
            foreach (string name in Items.Select(item => item.Category).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(new ToolboxFilterOption(name, UiLabels.Category(name)));
            }
            SelectedLibrary = Libraries.FirstOrDefault(option => option.Value == library) ?? _allLibraries;
            SelectedCategory = Categories.FirstOrDefault(option => option.Value == category) ?? _allCategories;
        }
        finally
        {
            _updatingFilters = false;
        }
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_updatingFilters) return;
        RefreshAssemblyChoices();
        ToolboxPanelItem? selected = SelectedItem;
        string[] terms = (SearchText ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _updatingGroups = true;
        try
        {
            ToolboxPanelItem[] visibleItems = Items.Where(item => IsAssemblyIncluded(item.AssemblyName)
                && (SelectedLibrary?.Value is not { } library || item.AssemblyName == library)
                && (SelectedCategory?.Value is not { } category || item.Category == category)
                && terms.All(term => item.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.AssemblyName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.Category.Contains(term, StringComparison.OrdinalIgnoreCase))).ToArray();
            VisibleItems = visibleItems;
            List<ToolboxAssemblyGroup> visibleGroups = new();
            foreach (IGrouping<string, ToolboxPanelItem> assembly in visibleItems.GroupBy(item => item.AssemblyName, StringComparer.Ordinal))
            {
                if (!_assemblyGroups.TryGetValue(assembly.Key, out ToolboxAssemblyGroup? group))
                {
                    group = new ToolboxAssemblyGroup(this, assembly.Key);
                    _assemblyGroups.Add(assembly.Key, group);
                }

                group.UpdateItems(assembly.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(item => item.FullName, StringComparer.Ordinal).ToArray());
                visibleGroups.Add(group);
            }

            visibleGroups = visibleGroups.OrderByDescending(group => group.IsStandardAssembly)
                .ThenByDescending(group => group.IsFavorite)
                .ThenBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.AssemblyName, StringComparer.Ordinal).ToList();
            foreach (ToolboxAssemblyGroup hidden in _assemblyGroups.Values.Except(visibleGroups))
            {
                hidden.UpdateItems(Array.Empty<ToolboxPanelItem>());
            }

            SynchronizeGroups(AssemblyGroups, visibleGroups);

            // A selection the filter removed stays empty, so Insert never targets a control the user did not choose.
            SelectedItem = selected is null ? null : visibleItems.FirstOrDefault(item =>
                item.AssemblyName == selected.AssemblyName && item.FullName == selected.FullName
                && item.DisplayName == selected.DisplayName && item.CommandId == selected.CommandId
                && item.CommandArguments.SequenceEqual(selected.CommandArguments));
            foreach (ToolboxAssemblyGroup group in AssemblyGroups) group.NotifySelectionChanged();
        }
        finally
        {
            _updatingGroups = false;
        }

        this.RaisePropertyChanged(nameof(NoMatches));
    }

    private List<ToolboxPanelItem> TryLoadCatalogFromSettings()
    {
        List<ToolboxCatalogEntry>? typedCatalog = _settings.Get<List<ToolboxCatalogEntry>>(CatalogSettingsKey);
        if (typedCatalog is not null)
        {
            return MapEntries(typedCatalog);
        }

        string? jsonCatalog = _settings.Get<string>(CatalogSettingsKey);
        if (string.IsNullOrWhiteSpace(jsonCatalog))
        {
            return new List<ToolboxPanelItem>();
        }

        try
        {
            List<ToolboxCatalogEntry>? parsed = JsonSerializer.Deserialize<List<ToolboxCatalogEntry>>(jsonCatalog);
            if (parsed is null)
            {
                return new List<ToolboxPanelItem>();
            }

            return MapEntries(parsed);
        }
        catch
        {
            return new List<ToolboxPanelItem>();
        }
    }

    private static List<ToolboxPanelItem> MapEntries(IEnumerable<ToolboxCatalogEntry> entries)
    {
        List<ToolboxPanelItem> result = new();
        foreach (ToolboxCatalogEntry entry in entries)
        {
            if (entry.CommandArguments is { Count: > 0 })
            {
                string commandId = string.IsNullOrWhiteSpace(entry.CommandId)
                    ? DefaultInsertCommandId
                    : entry.CommandId;
                string displayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                    ? commandId
                    : entry.DisplayName;

                result.Add(new ToolboxPanelItem(displayName, commandId, entry.CommandArguments, entry.IsStandardControl,
                    assemblyName: entry.AssemblyName));
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.TypeName) || string.IsNullOrWhiteSpace(entry.XmlNamespace))
            {
                continue;
            }

            List<string> args = new() { entry.TypeName, entry.XmlNamespace };
            if (!string.IsNullOrWhiteSpace(entry.ParentNodeId))
            {
                args.Add(entry.ParentNodeId);
            }

            string derivedCommandId = string.IsNullOrWhiteSpace(entry.CommandId)
                ? DefaultInsertCommandId
                : entry.CommandId;
            string derivedDisplayName = string.IsNullOrWhiteSpace(entry.DisplayName)
                ? entry.TypeName
                : entry.DisplayName;

            result.Add(new ToolboxPanelItem(derivedDisplayName, derivedCommandId, args, entry.IsStandardControl,
                assemblyName: entry.AssemblyName));
        }

        return result;
    }

}
