using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using ReactiveUI;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Designer.DragDrop;

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
        string? category = null)
    {
        DisplayName = displayName;
        CommandId = commandId;
        CommandArguments = commandArgs;
        IsStandardControl = isStandardControl ?? false;
        FullName = fullName ?? commandArgs.FirstOrDefault() ?? displayName;
        AssemblyName = assemblyName ?? string.Empty;
        Category = category ?? "Custom";
    }

    public string DisplayName { get; }

    public string CommandId { get; }

    public IReadOnlyList<string> CommandArguments { get; }

    /// <inheritdoc />
    public bool CanDrag => CommandId == "toolbox.insertSelected" && CommandArguments.Count >= 2;

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

    /// <summary>Gets the library-defined toolbox category.</summary>
    public string Category { get; }

    /// <summary>Gets the library and category displayed below the control name.</summary>
    public string Subtitle => string.IsNullOrEmpty(AssemblyName) ? Category : AssemblyName + " / " + Category;

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

    internal ToolboxAssemblyGroup(ToolboxPanelViewModel owner, string assemblyName)
    {
        _owner = owner;
        AssemblyName = assemblyName;
    }

    public string AssemblyName { get; }
    public string DisplayName => string.IsNullOrEmpty(AssemblyName) ? "Custom entries" : AssemblyName;
    public string Header => $"{DisplayName} ({Items.Count})";
    public IReadOnlyList<ToolboxPanelItem> Items => _items;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }

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

public sealed class ToolboxPanelViewModel : ReactiveObject, IDisposable
{
    private readonly ICommands _commands;
    private readonly ISettings _settings;
    private readonly IControlCatalog? _catalog;
    private readonly IScheduler _uiScheduler;
    private readonly ToolboxFilterOption _allLibraries = new(null, "All libraries");
    private readonly ToolboxFilterOption _allCategories = new(null, "All categories");
    private ToolboxPanelItem? _selectedItem;
    private ToolboxFilterOption? _selectedLibrary;
    private ToolboxFilterOption? _selectedCategory;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isLoading;
    private readonly Dictionary<string, ToolboxAssemblyGroup> _assemblyGroups = new(StringComparer.Ordinal);
    private bool _updatingGroups;
    private bool _updatingFilters;
    private bool _disposed;
    private int _reloadVersion;
    private CancellationTokenSource? _loadCancellation;
    private const string CatalogSettingsKey = "toolbox.catalog";
    private const string DefaultInsertCommandId = "toolbox.insertSelected";

    public ToolboxPanelViewModel(ICommands commands, ISettings settings,
        IControlCatalog? catalog = null, IScheduler? uiScheduler = null)
    {
        _commands = commands;
        _settings = settings;
        _catalog = catalog;
        _uiScheduler = uiScheduler ?? RxSchedulers.MainThreadScheduler;

        ReplaceItems(TryLoadCatalogFromSettings());
        StatusText = catalog is null ? "No project control catalog is available in this host." : "Control catalog not loaded yet.";

        IObservable<bool> canInsert = this.WhenAnyValue(x => x.SelectedItem, x => x.IsLoading,
            (item, loading) => item is not null && !loading);
        InsertSelectedCommand = ReactiveCommand.CreateFromTask(InsertSelectedAsync, canInsert);
        ReloadCommand = ReactiveCommand.CreateFromTask(() => ReloadAsync(),
            this.WhenAnyValue(x => x.IsLoading).Select(loading => !loading));
        CancelLoadCommand = ReactiveCommand.Create(() => { _loadCancellation?.Cancel(); },
            this.WhenAnyValue(x => x.IsLoading));
        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            SearchText = string.Empty;
            SelectedLibrary = _allLibraries;
            SelectedCategory = _allCategories;
        });
        if (_catalog is not null)
        {
            _catalog.Changed += OnCatalogChanged;
        }
    }

    public ObservableCollection<ToolboxPanelItem> Items { get; } = new();

    /// <summary>Gets standard controls in alphabetical display order.</summary>
    public ObservableCollection<ToolboxPanelItem> StandardItems { get; } = new();

    /// <summary>Gets third-party controls in alphabetical display order.</summary>
    public ObservableCollection<ToolboxPanelItem> ThirdPartyItems { get; } = new();

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
        }
    }

    /// <summary>Gets discovery progress and actionable loading errors.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    /// <summary>Gets whether the active filters exclude every available control.</summary>
    public bool NoMatches => !IsLoading && Items.Count > 0 && StandardItems.Count + ThirdPartyItems.Count == 0;

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

    public ReactiveCommand<Unit, Unit> InsertSelectedCommand { get; }

    /// <summary>Reloads controls from the active project.</summary>
    public ReactiveCommand<Unit, Unit> ReloadCommand { get; }

    /// <summary>Cancels the active catalog request.</summary>
    public ReactiveCommand<Unit, Unit> CancelLoadCommand { get; }

    /// <summary>Restores the unfiltered catalog.</summary>
    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    private async Task InsertSelectedAsync()
    {
        ToolboxPanelItem? item = SelectedItem;
        if (item is null)
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
            StatusText = $"Cannot insert {item.DisplayName}: {error.Message}";
        }
    }

    /// <summary>Refreshes the catalog while ignoring results superseded by another workspace.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }
        int version = Interlocked.Increment(ref _reloadVersion);
        using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version)) return;
                _loadCancellation?.Cancel();
                _loadCancellation = request;
                IsLoading = true;
                StatusText = "Loading project controls...";
            }).ConfigureAwait(false);
            if (!IsCurrent(version)) return;
            request.Token.ThrowIfCancellationRequested();
            ControlCatalogSnapshot snapshot = _catalog is null
                ? new ControlCatalogSnapshot(Array.Empty<ControlCatalogItem>(), Array.Empty<string>())
                : await _catalog.GetControlsAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version)) return;
                IEnumerable<ToolboxPanelItem> discovered = snapshot.Controls.Select(control =>
                    new ToolboxPanelItem(control.DisplayName, DefaultInsertCommandId,
                        new[] { control.TypeName, control.XmlNamespace }, control.IsStandardControl,
                        control.FullName, control.AssemblyName, control.Category));
                ReplaceItems(discovered.Concat(TryLoadCatalogFromSettings()));
                StatusText = _catalog is null ? "No project control catalog is available in this host."
                    : $"{Items.Count} controls available.";
                if (snapshot.Diagnostics.Count > 0)
                {
                    StatusText += Environment.NewLine + string.Join(Environment.NewLine, snapshot.Diagnostics);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            await UpdateUiAsync(() => { if (IsCurrent(version)) StatusText = "Control discovery cancelled."; }).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await UpdateUiAsync(() =>
            {
                if (!IsCurrent(version)) return;
                ReplaceItems(Array.Empty<ToolboxPanelItem>());
                StatusText = "Cannot load project controls: " + error.Message;
            }).ConfigureAwait(false);
        }
        finally
        {
            await UpdateUiAsync(() =>
            {
                if (ReferenceEquals(_loadCancellation, request)) _loadCancellation = null;
                if (IsCurrent(version)) IsLoading = false;
            }).ConfigureAwait(false);
        }
    }

    private bool IsCurrent(int version) => !_disposed && version == Volatile.Read(ref _reloadVersion);

    private Task UpdateUiAsync(Action action) => Observable.Start(action, _uiScheduler).ToTask();

    private void OnCatalogChanged(object? sender, EventArgs args) => _ = ReloadAsync();

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Interlocked.Increment(ref _reloadVersion);
        if (_catalog is not null) _catalog.Changed -= OnCatalogChanged;
        _loadCancellation?.Cancel();
        InsertSelectedCommand.Dispose();
        ReloadCommand.Dispose();
        CancelLoadCommand.Dispose();
        ClearFiltersCommand.Dispose();
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
            foreach (string name in Items.Select(item => item.AssemblyName).Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            {
                Libraries.Add(new ToolboxFilterOption(name, name));
            }
            Categories.Clear();
            Categories.Add(_allCategories);
            foreach (string name in Items.Select(item => item.Category).Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase))
            {
                Categories.Add(new ToolboxFilterOption(name, name));
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
        ToolboxPanelItem? selected = SelectedItem;
        string[] terms = (SearchText ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        _updatingGroups = true;
        try
        {
            StandardItems.Clear();
            ThirdPartyItems.Clear();
            foreach (ToolboxPanelItem item in Items)
            {
                if (SelectedLibrary?.Value is { } library && item.AssemblyName != library) continue;
                if (SelectedCategory?.Value is { } category && item.Category != category) continue;
                if (!terms.All(term => item.DisplayName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.FullName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.AssemblyName.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || item.Category.Contains(term, StringComparison.OrdinalIgnoreCase))) continue;
                (item.IsStandardControl ? StandardItems : ThirdPartyItems).Add(item);
            }

            HashSet<string> availableAssemblies = Items.Select(item => item.AssemblyName).ToHashSet(StringComparer.Ordinal);
            foreach (string obsolete in _assemblyGroups.Keys.Where(key => !availableAssemblies.Contains(key)).ToArray())
            {
                _assemblyGroups.Remove(obsolete);
            }

            ToolboxPanelItem[] visibleItems = StandardItems.Concat(ThirdPartyItems).ToArray();
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

            visibleGroups = visibleGroups.OrderBy(group => group.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(group => group.AssemblyName, StringComparer.Ordinal).ToList();
            foreach (ToolboxAssemblyGroup hidden in _assemblyGroups.Values.Except(visibleGroups))
            {
                hidden.UpdateItems(Array.Empty<ToolboxPanelItem>());
            }

            if (!AssemblyGroups.SequenceEqual(visibleGroups))
            {
                AssemblyGroups.Clear();
                foreach (ToolboxAssemblyGroup group in visibleGroups) AssemblyGroups.Add(group);
            }

            SelectedItem = visibleItems.FirstOrDefault(item => selected is not null
                && item.AssemblyName == selected.AssemblyName && item.FullName == selected.FullName
                && item.DisplayName == selected.DisplayName && item.CommandId == selected.CommandId
                && item.CommandArguments.SequenceEqual(selected.CommandArguments))
                ?? StandardItems.FirstOrDefault() ?? ThirdPartyItems.FirstOrDefault();
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
