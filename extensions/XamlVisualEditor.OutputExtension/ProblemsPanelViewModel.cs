using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Data.Core;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.OutputExtension;

public sealed class DiagnosticsChannelViewModel
{
    public DiagnosticsChannelViewModel(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    public string Id { get; }
    public string DisplayName { get; }
}

internal readonly record struct DiagnosticIdentity(string FilePath, LanguageTextRange Range,
    DiagnosticSeverity Severity, string? Code, string? Source, string Message)
{
    public static DiagnosticIdentity From(LanguageDiagnostic diagnostic) => new(
        diagnostic.FilePath, diagnostic.Range, diagnostic.Severity, diagnostic.Code,
        string.IsNullOrWhiteSpace(diagnostic.Source) ? null : diagnostic.Source, diagnostic.Message);
}

public sealed class DiagnosticEntryViewModel
{
    public DiagnosticEntryViewModel(LanguageDiagnostic diagnostic, DateTimeOffset firstSeen)
    {
        Identity = DiagnosticIdentity.From(diagnostic);
        FirstSeen = firstSeen;
        Severity = diagnostic.Severity;
        Message = diagnostic.Message;
        FilePath = diagnostic.FilePath;
        Line = diagnostic.Range.Start.Line;
        Column = diagnostic.Range.Start.Column;
        Code = diagnostic.Code;
        Source = diagnostic.Source;
        Range = diagnostic.Range;
    }

    public DiagnosticSeverity Severity { get; }
    internal DiagnosticIdentity Identity { get; }
    public DateTimeOffset FirstSeen { get; }
    public string FirstSeenTime => FirstSeen.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    public string FirstSeenDetails => FirstSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture);
    public string Tooltip => string.Format(UiStrings.DiagnosticFirstSeen, (object?)(Message), (object?)(Environment.NewLine), (object?)(FirstSeenDetails));
    public string Message { get; }
    public string Summary => Message.ReplaceLineEndings(" ");
    public string FilePath { get; }
    public string Location => string.IsNullOrWhiteSpace(FilePath) ? "Workspace" : $"{Path.GetFileName(FilePath)}:{Line}:{Column}";
    public int Line { get; }
    public int Column { get; }
    public string? Code { get; }
    public string? Source { get; }
    public LanguageTextRange Range { get; }
}

public sealed class ProblemsGroupViewModel : ReactiveObject
{
    private bool _isExpanded = true;

    public ProblemsGroupViewModel(string key, string title)
    {
        Key = key;
        Title = title;
    }

    public string Key { get; }
    public string Title { get; }
    public ObservableCollection<DiagnosticEntryViewModel> Items { get; } = new();

    public int Count => Items.Count;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
}

public sealed class ProblemsPanelViewModel : ReactiveObject, IDisposable
{
    private const string AllChannelsId = "all";
    private readonly IDiagnosticsService _diagnostics;
    private readonly IEditorServices _editor;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<DiagnosticIdentity, DateTimeOffset> _firstSeen = new();
    private readonly CompositeDisposable _disposables = new();
    private readonly CompositeDisposable _groupDisposables = new();
    private readonly ObservableCollection<DiagnosticsChannelViewModel> _channels = new();
    private readonly ObservableCollection<DiagnosticEntryViewModel> _entries = new();
    private readonly ObservableCollection<object> _rootItems = new();
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DiagnosticsDocumentSnapshot> _latestSnapshots = Array.Empty<DiagnosticsDocumentSnapshot>();
    private DiagnosticsChannelViewModel? _selectedChannel;
    private DiagnosticEntryViewModel? _selectedDiagnostic;
    private object? _selectedRow;
    private object? _selectedItem;
    private string? _searchText;
    private string _selectedSeverity = "All";
    private string _selectedGroupBy = "File";
    private bool _showFirstSeen;
    private CancellationTokenSource? _loadCts;
    private readonly DataGridTemplateColumnDefinition _firstSeenColumn;

    private static readonly ClrPropertyInfo NodeItemProperty = new(
        nameof(HierarchicalNode.Item), target => ((HierarchicalNode)target).Item, null, typeof(object));

    public ProblemsPanelViewModel(IDiagnosticsService diagnostics, IEditorServices editor, TimeProvider? timeProvider = null)
    {
        _diagnostics = diagnostics;
        _editor = editor;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _firstSeenColumn = CreateTemplateColumn("FirstSeen", UiStrings.Since, "ProblemsFirstSeenCell", 80, "Item.FirstSeen");
        _firstSeenColumn.IsVisible = false;
        Columns = new DataGridColumnDefinition[]
        {
            _firstSeenColumn,
            new DataGridHierarchicalColumnDefinition
            {
                ColumnKey = "Message",
                Header = UiStrings.Message,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                MinWidth = 120,
                SortMemberPath = "Item.Message",
                Binding = DataGridBindingDefinition.Create<HierarchicalNode, object>(NodeItemProperty, node => node.Item),
                CellTemplateKey = "ProblemsMessageCell"
            },
            CreateTemplateColumn("Severity", UiStrings.Severity, "ProblemsSeverityCell", 120),
            CreateTemplateColumn("Location", UiStrings.Location, "ProblemsLocationCell", 150),
            CreateTemplateColumn("Source", UiStrings.Source, "ProblemsSourceCell", 90)
        };

        ChannelsView = new DataGridCollectionView(_channels);

        var options = new HierarchicalOptions
        {
            ChildrenSelector = item => item is ProblemsGroupViewModel group ? group.Items : null,
            IsLeafSelector = item => item is DiagnosticEntryViewModel,
            IsExpandedSelector = item => item is ProblemsGroupViewModel group && group.IsExpanded,
            IsExpandedSetter = (item, value) =>
            {
                if (item is ProblemsGroupViewModel group)
                {
                    group.IsExpanded = value;
                }
            },
            VirtualizeChildren = true
        };

        Model = new HierarchicalModel(options);
        Model.SetRoots(_rootItems);

        _disposables.Add(this.WhenAnyValue(x => x.SearchText)
            .Subscribe(_ => RebuildGroups()));
        _disposables.Add(this.WhenAnyValue(x => x.SelectedSeverity)
            .Skip(1)
            .Subscribe(_ => RebuildGroups()));
        _disposables.Add(this.WhenAnyValue(x => x.SelectedGroupBy)
            .Skip(1)
            .Subscribe(_ => RebuildGroups()));

        _disposables.Add(this.WhenAnyValue(x => x.SelectedChannel)
            .Subscribe(channel =>
            {
                if (channel is null)
                {
                    ClearDiagnostics();
                    return;
                }

                _ = LoadChannelAsync(channel);
            }));

        _disposables.Add(this.WhenAnyValue(x => x.SelectedRow)
            .Subscribe(row =>
            {
                SelectedItem = row is HierarchicalNode node ? node.Item : row;
                SelectedDiagnostic = SelectedItem as DiagnosticEntryViewModel;
            }));

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);

        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedDiagnostic)
            .Select(entry => entry is not null);
        IObservable<bool> canNavigate = this.WhenAnyValue(x => x.SelectedDiagnostic)
            .Select(entry => entry is not null && !string.IsNullOrWhiteSpace(entry.FilePath));
        OpenLocationCommand = ReactiveCommand.CreateFromTask(OpenSelectedAsync, canNavigate);

        ClearCommand = ReactiveCommand.Create(ClearDiagnostics);
        CopySelectedCommand = ReactiveCommand.CreateFromTask(CopySelectedAsync, hasSelection);

        IObservable<bool> hasEntries = Observable.FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => _entries.CollectionChanged += h,
                h => _entries.CollectionChanged -= h)
            .Select(_ => _entries.Count > 0)
            .StartWith(_entries.Count > 0);
        CopyAllCommand = ReactiveCommand.CreateFromTask(CopyAllAsync, hasEntries);

        ClearFiltersCommand = ReactiveCommand.Create(() =>
        {
            SearchText = null;
            SelectedSeverity = "All";
            SelectedGroupBy = "File";
        });
    }

    public DataGridCollectionView ChannelsView { get; }

    public HierarchicalModel Model { get; }

    /// <summary>Gets the diagnostics grid columns; the cell templates are resources of the problems view.</summary>
    public IReadOnlyList<DataGridColumnDefinition> Columns { get; }

    public Interaction<string, Unit> CopyToClipboardInteraction { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    public ReactiveCommand<Unit, Unit> OpenLocationCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearCommand { get; }

    public ReactiveCommand<Unit, Unit> CopySelectedCommand { get; }

    public ReactiveCommand<Unit, Unit> CopyAllCommand { get; }

    public ReactiveCommand<Unit, Unit> ClearFiltersCommand { get; }

    public IReadOnlyList<string> SeverityOptions { get; } = new[] { "All", "Error", "Warning", "Info" };

    public IReadOnlyList<string> GroupByOptions { get; } = new[] { "None", "File", "Channel" };

    public DiagnosticsChannelViewModel? SelectedChannel
    {
        get => _selectedChannel;
        set => this.RaiseAndSetIfChanged(ref _selectedChannel, value);
    }

    public object? SelectedRow
    {
        get => _selectedRow;
        set => this.RaiseAndSetIfChanged(ref _selectedRow, value);
    }

    public object? SelectedItem
    {
        get => _selectedItem;
        private set => this.RaiseAndSetIfChanged(ref _selectedItem, value);
    }

    public DiagnosticEntryViewModel? SelectedDiagnostic
    {
        get => _selectedDiagnostic;
        private set => this.RaiseAndSetIfChanged(ref _selectedDiagnostic, value);
    }

    public string SelectedSeverity
    {
        get => _selectedSeverity;
        set => this.RaiseAndSetIfChanged(ref _selectedSeverity, value);
    }

    public string SelectedGroupBy
    {
        get => _selectedGroupBy;
        set => this.RaiseAndSetIfChanged(ref _selectedGroupBy, value);
    }

    public string? SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public bool ShowFirstSeen
    {
        get => _showFirstSeen;
        set
        {
            this.RaiseAndSetIfChanged(ref _showFirstSeen, value);
            _firstSeenColumn.IsVisible = value;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await RefreshChannelsAsync(cancellationToken);
    }

    public void HandleChannelsChanged(IReadOnlyList<DiagnosticsChannelInfo> channels)
    {
        string? selectedId = SelectedChannel?.Id;
        _channels.Clear();

        _channels.Add(new DiagnosticsChannelViewModel(AllChannelsId, UiStrings.FilterAll));
        foreach (DiagnosticsChannelInfo channel in channels)
        {
            _channels.Add(new DiagnosticsChannelViewModel(channel.Id, channel.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(selectedId))
        {
            SelectedChannel = FindChannel(selectedId) ?? _channels[0];
        }
        else if (_channels.Count > 0)
        {
            SelectedChannel = _channels[0];
        }
        else
        {
            SelectedChannel = null;
            ClearDiagnostics();
        }
    }

    public void HandleSnapshotsPublished(IReadOnlyList<DiagnosticsDocumentSnapshot> snapshots)
    {
        _loadCts?.Cancel();
        _latestSnapshots = snapshots;
        UpdateEntriesFromSnapshots();
    }

    public async Task<bool> NavigateToRelativeAsync(int delta, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delta == 0)
        {
            return false;
        }

        List<DiagnosticEntryViewModel> filtered = GetFilteredEntries()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.FilePath)).ToList();
        if (filtered.Count == 0)
        {
            return false;
        }

        int direction = delta > 0 ? 1 : -1;
        int currentIndex = SelectedDiagnostic is null
            ? (direction > 0 ? -1 : 0)
            : filtered.IndexOf(SelectedDiagnostic);
        if (currentIndex < 0)
        {
            currentIndex = direction > 0 ? -1 : 0;
        }

        int nextIndex = currentIndex;
        int steps = Math.Abs(delta);
        for (int i = 0; i < steps; i++)
        {
            nextIndex = direction > 0
                ? (nextIndex + 1) % filtered.Count
                : (nextIndex - 1 + filtered.Count) % filtered.Count;
        }

        DiagnosticEntryViewModel next = filtered[nextIndex];
        if (!SelectEntry(next)) return false;

        LanguageLocation location = new()
        {
            FilePath = next.FilePath,
            Range = next.Range
        };

        return await _editor.OpenDiagnosticLocationAsync(location, cancellationToken).ConfigureAwait(false);
    }

    private static DataGridTemplateColumnDefinition CreateTemplateColumn(
        string key,
        string header,
        string cellTemplateKey,
        double width,
        string? sortMemberPath = null)
    {
        return new DataGridTemplateColumnDefinition
        {
            ColumnKey = key,
            Header = header,
            Width = new DataGridLength(width),
            SortMemberPath = sortMemberPath,
            CellTemplateKey = cellTemplateKey
        };
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _disposables.Dispose();
        _groupDisposables.Dispose();
    }

    private async Task RefreshAsync()
    {
        await RefreshChannelsAsync(CancellationToken.None);
        if (SelectedChannel is not null)
        {
            await LoadChannelAsync(SelectedChannel);
        }
    }

    private async Task RefreshChannelsAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DiagnosticsChannelInfo> channels = await _diagnostics.GetChannelsAsync(cancellationToken);
        HandleChannelsChanged(channels);
    }

    private async Task LoadChannelAsync(DiagnosticsChannelViewModel channel)
    {
        CancellationTokenSource? previous = _loadCts;
        CancellationTokenSource current = new();
        _loadCts = current;
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken token = current.Token;
        string? channelId = string.Equals(channel.Id, AllChannelsId, StringComparison.OrdinalIgnoreCase)
            ? null
            : channel.Id;

        try
        {
            IReadOnlyList<DiagnosticsDocumentSnapshot> snapshots = await _diagnostics.GetDiagnosticsSnapshotAsync(
                new DiagnosticsQuery(null, channelId), token);
            if (token.IsCancellationRequested || !ReferenceEquals(_loadCts, current)) return;
            _latestSnapshots = snapshots;
            UpdateEntriesFromSnapshots(channelId);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private void UpdateEntriesFromSnapshots(string? channelId = null)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        HashSet<DiagnosticIdentity> present = new();
        _entries.Clear();
        foreach (DiagnosticsDocumentSnapshot snapshot in _latestSnapshots)
        {
            foreach (LanguageDiagnostic diagnostic in snapshot.Diagnostics)
            {
                DiagnosticIdentity identity = DiagnosticIdentity.From(diagnostic);
                present.Add(identity);
                if (!_firstSeen.TryGetValue(identity, out DateTimeOffset firstSeen))
                {
                    firstSeen = now;
                    _firstSeen.Add(identity, firstSeen);
                }
                _entries.Add(new DiagnosticEntryViewModel(diagnostic, firstSeen));
            }
        }

        foreach (DiagnosticIdentity identity in _firstSeen.Keys.Where(identity =>
            !present.Contains(identity) && (channelId is null ||
                string.Equals(GetChannelId(identity.Source), channelId, StringComparison.OrdinalIgnoreCase))).ToArray())
        {
            _firstSeen.Remove(identity);
        }

        RebuildGroups();
    }

    private void ClearDiagnostics()
    {
        _loadCts?.Cancel();
        _latestSnapshots = Array.Empty<DiagnosticsDocumentSnapshot>();
        _groupDisposables.Clear();
        _entries.Clear();
        _rootItems.Clear();
        Model.Refresh();
        SelectedDiagnostic = null;
        SelectedRow = null;
    }

    private void RebuildGroups()
    {
        DiagnosticIdentity? selectedKey = SelectedDiagnostic?.Identity;
        _groupDisposables.Clear();
        _rootItems.Clear();

        List<DiagnosticEntryViewModel> filtered = GetFilteredEntries();
        if (filtered.Count == 0)
        {
            Model.Refresh();
            SelectedDiagnostic = null;
            SelectedRow = null;
            return;
        }

        switch (SelectedGroupBy)
        {
            case "None":
                foreach (DiagnosticEntryViewModel entry in filtered)
                {
                    _rootItems.Add(entry);
                }
                break;
            case "Channel":
                BuildChannelGroups(filtered);
                break;
            default:
                BuildFileGroups(filtered);
                break;
        }

        Model.Refresh();

        SelectedRow = null;
        if (selectedKey is DiagnosticIdentity identity)
        {
            DiagnosticEntryViewModel? found = FindEntryByKey(_rootItems, identity);
            if (found is not null)
            {
                SelectEntry(found);
            }
        }
    }

    private bool SelectEntry(DiagnosticEntryViewModel entry)
    {
        ProblemsGroupViewModel? group = _rootItems.OfType<ProblemsGroupViewModel>()
            .FirstOrDefault(candidate => candidate.Items.Contains(entry));
        if (group is not null) Model.Expand(new[] { group });
        if (!Model.TryExpandToItem(entry, out HierarchicalNode? node)) return false;
        SelectedRow = node;
        return true;
    }

    private void BuildFileGroups(List<DiagnosticEntryViewModel> filtered)
    {
        AddGroups(filtered, entry => $"file:{entry.FilePath}",
            entry => string.IsNullOrWhiteSpace(entry.FilePath) ? UiStrings.Workspace : Path.GetFileName(entry.FilePath));
    }

    private void BuildChannelGroups(List<DiagnosticEntryViewModel> filtered)
    {
        AddGroups(filtered, entry => $"channel:{GetChannelId(entry.Source)}", entry => GetChannelId(entry.Source));
    }

    // A group joins the tree only after it holds all its entries: the hierarchical model updates its flattened
    // rows for every entry added to an attached group, which makes large result sets take quadratic time.
    private void AddGroups(
        List<DiagnosticEntryViewModel> filtered,
        Func<DiagnosticEntryViewModel, string> getKey,
        Func<DiagnosticEntryViewModel, string> getTitle)
    {
        Dictionary<string, ProblemsGroupViewModel> groups = new(StringComparer.OrdinalIgnoreCase);
        List<ProblemsGroupViewModel> ordered = new();
        foreach (DiagnosticEntryViewModel entry in filtered)
        {
            string key = getKey(entry);
            if (!groups.TryGetValue(key, out ProblemsGroupViewModel? group))
            {
                group = new ProblemsGroupViewModel(key, getTitle(entry))
                {
                    IsExpanded = !_collapsedGroups.Contains(key)
                };
                TrackGroupExpansion(group);
                groups[key] = group;
                ordered.Add(group);
            }

            group.Items.Add(entry);
        }

        foreach (ProblemsGroupViewModel group in ordered)
        {
            _rootItems.Add(group);
        }
    }

    private void TrackGroupExpansion(ProblemsGroupViewModel group)
    {
        IDisposable subscription = group.WhenAnyValue(x => x.IsExpanded)
            .Subscribe(isExpanded => UpdateExpanded(group.Key, isExpanded));
        _groupDisposables.Add(subscription);
    }

    private void UpdateExpanded(string key, bool isExpanded)
    {
        if (isExpanded)
        {
            _collapsedGroups.Remove(key);
        }
        else
        {
            _collapsedGroups.Add(key);
        }
    }

    private List<DiagnosticEntryViewModel> GetFilteredEntries()
    {
        List<DiagnosticEntryViewModel> filtered = new();
        string? search = string.IsNullOrWhiteSpace(SearchText) ? null : SearchText.Trim();
        string? channelId = SelectedChannel?.Id;
        bool filterChannel = !string.IsNullOrWhiteSpace(channelId)
            && !string.Equals(channelId, AllChannelsId, StringComparison.OrdinalIgnoreCase);

        foreach (DiagnosticEntryViewModel entry in _entries)
        {
            if (filterChannel && !string.Equals(GetChannelId(entry.Source), channelId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsSeverityMatch(entry.Severity, SelectedSeverity))
            {
                continue;
            }

            if (search is not null && !MatchesSearch(entry, search))
            {
                continue;
            }

            filtered.Add(entry);
        }

        return filtered;
    }

    private async Task OpenSelectedAsync()
    {
        if (SelectedDiagnostic is null || string.IsNullOrWhiteSpace(SelectedDiagnostic.FilePath))
        {
            return;
        }

        LanguageLocation location = new()
        {
            FilePath = SelectedDiagnostic.FilePath,
            Range = SelectedDiagnostic.Range
        };

        await _editor.OpenDiagnosticLocationAsync(location, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CopySelectedAsync()
    {
        if (SelectedDiagnostic is null)
        {
            return;
        }

        await CopyToClipboardInteraction.Handle(FormatEntry(SelectedDiagnostic)).ToTask().ConfigureAwait(false);
    }

    private async Task CopyAllAsync()
    {
        List<DiagnosticEntryViewModel> filtered = GetFilteredEntries();
        if (filtered.Count == 0)
        {
            return;
        }

        StringBuilder builder = new();
        foreach (DiagnosticEntryViewModel entry in filtered)
        {
            builder.AppendLine(FormatEntry(entry));
        }

        await CopyToClipboardInteraction.Handle(builder.ToString()).ToTask().ConfigureAwait(false);
    }

    private static bool IsSeverityMatch(DiagnosticSeverity severity, string filter)
    {
        return filter switch
        {
            "Error" => severity == DiagnosticSeverity.Error,
            "Warning" => severity == DiagnosticSeverity.Warning,
            "Info" => severity == DiagnosticSeverity.Info,
            _ => true
        };
    }

    private static bool MatchesSearch(DiagnosticEntryViewModel entry, string search)
    {
        return entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase)
            || entry.FilePath.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (entry.Code?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (entry.Source?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetChannelId(string? source)
    {
        return string.IsNullOrWhiteSpace(source) ? "default" : source;
    }

    private static DiagnosticEntryViewModel? FindEntryByKey(IEnumerable<object> roots, DiagnosticIdentity key)
    {
        foreach (object root in roots)
        {
            if (root is DiagnosticEntryViewModel entry && entry.Identity == key)
            {
                return entry;
            }

            if (root is ProblemsGroupViewModel group)
            {
                foreach (DiagnosticEntryViewModel child in group.Items)
                {
                    if (child.Identity == key)
                    {
                        return child;
                    }
                }
            }
        }

        return null;
    }

    private static string FormatEntry(DiagnosticEntryViewModel entry)
    {
        return $"[{entry.Severity}] {entry.Message} ({entry.FilePath}:{entry.Line}:{entry.Column})";
    }

    private DiagnosticsChannelViewModel? FindChannel(string id)
    {
        foreach (DiagnosticsChannelViewModel channel in _channels)
        {
            if (string.Equals(channel.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return channel;
            }
        }

        return null;
    }
}
