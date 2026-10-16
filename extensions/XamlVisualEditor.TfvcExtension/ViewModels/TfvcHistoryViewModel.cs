using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.DataGridSorting;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>A changeset row of the history panel.</summary>
public sealed class TfvcChangesetRowViewModel
{
    /// <summary>Creates the row.</summary>
    public TfvcChangesetRowViewModel(TfvcChangesetInfo changeset)
    {
        Changeset = changeset;
        DateText = changeset.CreatedDate?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? string.Empty;
        CommentLine = FirstLine(changeset.Comment);
    }

    /// <summary>Gets the changeset.</summary>
    public TfvcChangesetInfo Changeset { get; }

    /// <summary>Gets the changeset number.</summary>
    public int Id => Changeset.Id;

    /// <summary>Gets the author.</summary>
    public string Author => Changeset.Author ?? string.Empty;

    /// <summary>Gets the check-in date; the sort key of the date column.</summary>
    public DateTimeOffset? Date => Changeset.CreatedDate;

    /// <summary>Gets the formatted check-in date.</summary>
    public string DateText { get; }

    /// <summary>Gets the full comment.</summary>
    public string Comment => Changeset.Comment ?? string.Empty;

    /// <summary>Gets the first line of the comment.</summary>
    public string CommentLine { get; }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        int index = text.IndexOfAny(['\r', '\n']);
        return index < 0 ? text : text.Substring(0, index);
    }
}

/// <summary>A label row of the history panel.</summary>
public sealed class TfvcLabelRowViewModel
{
    /// <summary>Creates the row.</summary>
    public TfvcLabelRowViewModel(TfvcLabelInfo label)
    {
        Label = label;
        DateText = label.ModifiedDate?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? string.Empty;
    }

    /// <summary>Gets the label.</summary>
    public TfvcLabelInfo Label { get; }

    /// <summary>Gets the label name.</summary>
    public string Name => Label.Name;

    /// <summary>Gets the owner.</summary>
    public string Owner => Label.Owner ?? string.Empty;

    /// <summary>Gets the modification date; the sort key of the date column.</summary>
    public DateTimeOffset? Date => Label.ModifiedDate;

    /// <summary>Gets the formatted modification date.</summary>
    public string DateText { get; }

    /// <summary>Gets the label comment.</summary>
    public string Description => Label.Description ?? string.Empty;

    /// <summary>Gets the label scope.</summary>
    public string Scope => Label.Scope ?? string.Empty;
}

/// <summary>A changed item of the selected changeset.</summary>
/// <param name="ServerPath">Server path of the item.</param>
/// <param name="ChangeText">Displayed change types.</param>
public sealed record TfvcChangeRowViewModel(string ServerPath, string ChangeText);

/// <summary>View model of the history panel: changesets and labels of one item, with a comment and an author filter.</summary>
public sealed partial class TfvcHistoryViewModel : ReactiveObject
{
    /// <summary>Number of changesets loaded per page.</summary>
    public const int PAGE_SIZE = 50;

    /// <summary>Maximum number of changesets scanned by one comment search.</summary>
    public const int SEARCH_LIMIT = 5000;

    /// <summary>Sort member path of the changeset column.</summary>
    public const string CHANGESET_SORT_PATH = nameof(TfvcChangesetRowViewModel.Id);

    private readonly TfvcSession _session;
    private readonly Func<TfvcChangesetInfo, Task>? _showChangesetDetails;
    private readonly HashSet<string> _knownAuthors = new(StringComparer.CurrentCultureIgnoreCase);
    private string? _appliedAuthor;
    private int _loadedCount;
    private int _requestVersion;

    /// <summary>Creates the view model.</summary>
    /// <param name="session">TFVC session.</param>
    /// <param name="showChangesetDetails">Shows the details of a changeset; null hides the command.</param>
    public TfvcHistoryViewModel(TfvcSession session, Func<TfvcChangesetInfo, Task>? showChangesetDetails = null)
    {
        _session = session;
        _showChangesetDetails = showChangesetDetails;
        SortingModel = new SortingModel();
        SortingModel.SetOrUpdate(new SortingDescriptor(
            CHANGESET_SORT_PATH,
            ListSortDirection.Descending,
            CHANGESET_SORT_PATH,
            null,
            CultureInfo.CurrentCulture));

        IObservable<bool> hasItem = this.WhenAnyValue(x => x.ServerPath, x => x.IsBusy, (path, busy) => !string.IsNullOrWhiteSpace(path) && !busy);
        RefreshCommand = ReactiveCommand.CreateFromTask(ReloadAsync, hasItem);
        LoadMoreCommand = ReactiveCommand.CreateFromTask(LoadMoreAsync, this.WhenAnyValue(x => x.HasMore, x => x.IsBusy, (more, busy) => more && !busy));
        SearchCommand = ReactiveCommand.CreateFromTask(SearchAsync, hasItem);
        ClearSearchCommand = ReactiveCommand.CreateFromTask(async ct =>
        {
            SearchText = null;
            AuthorText = null;
            _appliedAuthor = null;
            await ReloadAsync(ct);
        }, hasItem);
        NavigateCommand = ReactiveCommand.CreateFromTask(ct => ShowAsync(LocationText, ct));
        ShowChangesetDetailsCommand = ReactiveCommand.CreateFromTask(
            () => SelectedChangeset is { } row && _showChangesetDetails is not null ? _showChangesetDetails(row.Changeset) : Task.CompletedTask,
            this.WhenAnyValue(x => x.SelectedChangeset).Select(row => row is not null && showChangesetDetails is not null));

        this.WhenAnyValue(x => x.SelectedChangeset)
            .Subscribe(row => _ = LoadChangesAsync(row));
    }

    /// <summary>Gets the changesets of the item.</summary>
    public ObservableCollection<TfvcChangesetRowViewModel> Changesets { get; } = new();

    /// <summary>Gets the sorting of the changesets grid; newest changeset first by default.</summary>
    public SortingModel SortingModel { get; }

    /// <summary>Gets the authors of the loaded changesets, offered as filter suggestions.</summary>
    public ObservableCollection<string> KnownAuthors { get; } = new();

    /// <summary>Gets the labels that cover the item.</summary>
    public ObservableCollection<TfvcLabelRowViewModel> Labels { get; } = new();

    /// <summary>Gets the changed items of the selected changeset.</summary>
    public ObservableCollection<TfvcChangeRowViewModel> Changes { get; } = new();

    /// <summary>Reloads the history.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Loads the next page of changesets.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadMoreCommand { get; }

    /// <summary>Searches the check-in comments.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SearchCommand { get; }

    /// <summary>Clears the search and shows the complete history again.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearSearchCommand { get; }

    /// <summary>Shows the history of the entered location.</summary>
    public ReactiveCommand<RxVoid, RxVoid> NavigateCommand { get; }

    /// <summary>Shows the changed items and the comment of the selected changeset in the changes panel.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowChangesetDetailsCommand { get; }

    /// <summary>Gets the server path of the item.</summary>
    [Reactive]
    public partial string? ServerPath { get; private set; }

    /// <summary>Gets or sets the displayed location, the local path when mapped.</summary>
    [Reactive]
    public partial string? LocationText { get; set; }

    /// <summary>Gets or sets the comment search text.</summary>
    [Reactive]
    public partial string? SearchText { get; set; }

    /// <summary>Gets or sets the author filter: account or display name as known to the server.</summary>
    [Reactive]
    public partial string? AuthorText { get; set; }

    /// <summary>Gets or sets the selected changeset.</summary>
    [Reactive]
    public partial TfvcChangesetRowViewModel? SelectedChangeset { get; set; }

    /// <summary>Gets whether more changesets can be loaded.</summary>
    [Reactive]
    public partial bool HasMore { get; private set; }

    /// <summary>Gets whether a request is running.</summary>
    [Reactive]
    public partial bool IsBusy { get; private set; }

    /// <summary>Gets the status or error message.</summary>
    [Reactive]
    public partial string? Message { get; private set; }

    /// <summary>Shows the history of a server path or of a mapped local path.</summary>
    public async Task ShowAsync(string? path, CancellationToken cancellationToken)
    {
        string? serverPath = ResolveServerPath(path);
        if (serverPath is null)
        {
            return;
        }

        ServerPath = serverPath;
        LocationText = _session.CurrentWorkspace?.TryGetLocalPath(serverPath) ?? serverPath;
        SearchText = null;
        _appliedAuthor = NormalizeFilter(AuthorText);
        await ReloadAsync(cancellationToken);
    }

    /// <summary>Replaces a displayed changeset after its comment was revised.</summary>
    public void UpdateChangeset(TfvcChangesetInfo changeset)
    {
        for (int i = 0; i < Changesets.Count; i++)
        {
            if (Changesets[i].Id != changeset.Id)
            {
                continue;
            }

            bool wasSelected = ReferenceEquals(Changesets[i], SelectedChangeset);
            TfvcChangesetRowViewModel row = new(changeset);
            Changesets[i] = row;
            if (wasSelected)
            {
                SelectedChangeset = row;
            }

            return;
        }
    }

    private static string? NormalizeFilter(string? text)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private string? ResolveServerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string trimmed = path.Trim();
        if (trimmed.StartsWith('$'))
        {
            return TfvcWorkspace.NormalizeServerPath(trimmed);
        }

        return _session.FindWorkspaceForLocalPath(trimmed)?.TryGetServerPath(trimmed);
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        int version = Interlocked.Increment(ref _requestVersion);
        string? serverPath = ServerPath;
        string? collectionUrl = _session.Settings.CollectionUrl;
        Changesets.Clear();
        Labels.Clear();
        Changes.Clear();
        _loadedCount = 0;
        HasMore = false;
        if (serverPath is null || collectionUrl is null || ReportOffline())
        {
            return;
        }

        await RunAsync(async () =>
        {
            Task<IReadOnlyList<TfvcChangesetInfo>> changesetTask = _session.Server.GetChangesetsAsync(collectionUrl, serverPath, _appliedAuthor, 0, PAGE_SIZE, cancellationToken);
            Task<IReadOnlyList<TfvcLabelInfo>> labelTask = _session.Server.GetLabelsAsync(collectionUrl, serverPath, cancellationToken);
            IReadOnlyList<TfvcChangesetInfo> changesets = await changesetTask;
            IReadOnlyList<TfvcLabelInfo> labels = await labelTask;
            if (version != _requestVersion)
            {
                return;
            }

            AddChangesets(changesets);
            _loadedCount = changesets.Count;
            HasMore = changesets.Count == PAGE_SIZE;
            foreach (TfvcLabelInfo label in labels)
            {
                Labels.Add(new TfvcLabelRowViewModel(label));
            }

            Message = null;
        });
    }

    private async Task LoadMoreAsync(CancellationToken cancellationToken)
    {
        string? serverPath = ServerPath;
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (serverPath is null || collectionUrl is null || ReportOffline())
        {
            return;
        }

        int version = _requestVersion;
        await RunAsync(async () =>
        {
            IReadOnlyList<TfvcChangesetInfo> changesets = await _session.Server.GetChangesetsAsync(collectionUrl, serverPath, _appliedAuthor, _loadedCount, PAGE_SIZE, cancellationToken);
            if (version != _requestVersion)
            {
                return;
            }

            AddChangesets(changesets);
            _loadedCount += changesets.Count;
            HasMore = changesets.Count == PAGE_SIZE;
        });
    }

    // The changesets API filters by author but not by comment, so the comment search scans the history page by page.
    private async Task SearchAsync(CancellationToken cancellationToken)
    {
        _appliedAuthor = NormalizeFilter(AuthorText);
        string? query = NormalizeFilter(SearchText);
        if (query is null)
        {
            await ReloadAsync(cancellationToken);
            return;
        }

        string? serverPath = ServerPath;
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (serverPath is null || collectionUrl is null || ReportOffline())
        {
            return;
        }

        int version = Interlocked.Increment(ref _requestVersion);
        Changesets.Clear();
        Changes.Clear();
        HasMore = false;
        await RunAsync(async () =>
        {
            int scanned = 0;
            int matches = 0;
            while (scanned < SEARCH_LIMIT)
            {
                IReadOnlyList<TfvcChangesetInfo> page = await _session.Server.GetChangesetsAsync(collectionUrl, serverPath, _appliedAuthor, scanned, PAGE_SIZE, cancellationToken);
                if (version != _requestVersion)
                {
                    return;
                }

                foreach (TfvcChangesetInfo changeset in page)
                {
                    if (changeset.Comment?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true)
                    {
                        AddChangeset(changeset);
                        matches++;
                    }
                }

                scanned += page.Count;
                if (page.Count < PAGE_SIZE)
                {
                    break;
                }
            }

            Message = string.Format(CultureInfo.CurrentCulture, TfvcStrings.HistorySearchResultFormat, matches, scanned);
        });
    }

    private async Task LoadChangesAsync(TfvcChangesetRowViewModel? row)
    {
        Changes.Clear();
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (row is null || collectionUrl is null || ReportOffline())
        {
            return;
        }

        try
        {
            IReadOnlyList<TfvcChangesetChange> changes = await _session.Server.GetChangesetChangesAsync(collectionUrl, row.Id, CancellationToken.None);
            if (!ReferenceEquals(row, SelectedChangeset))
            {
                return;
            }

            foreach (TfvcChangesetChange change in changes)
            {
                Changes.Add(new TfvcChangeRowViewModel(
                    change.ServerPath,
                    TfvcDisplayText.FormatChangeTypes(TfvcOutputParser.SplitChangeTypes(change.ChangeType))));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = TfvcDisplayText.DescribeError(ex);
        }
    }

    // Offline no server call is made; the panel shows the offline status instead.
    private bool ReportOffline()
    {
        if (!_session.IsOffline)
        {
            return false;
        }

        Message = TfvcStrings.HistoryOfflineStatus;
        return true;
    }

    private void AddChangesets(IReadOnlyList<TfvcChangesetInfo> changesets)
    {
        foreach (TfvcChangesetInfo changeset in changesets)
        {
            AddChangeset(changeset);
        }
    }

    private void AddChangeset(TfvcChangesetInfo changeset)
    {
        Changesets.Add(new TfvcChangesetRowViewModel(changeset));
        if (!string.IsNullOrWhiteSpace(changeset.Author) && _knownAuthors.Add(changeset.Author))
        {
            int index = 0;
            while (index < KnownAuthors.Count && string.Compare(KnownAuthors[index], changeset.Author, StringComparison.CurrentCultureIgnoreCase) < 0)
            {
                index++;
            }

            KnownAuthors.Insert(index, changeset.Author);
        }
    }

    // Each request replaces the result or error message of the previous one.
    private async Task RunAsync(Func<Task> operation)
    {
        IsBusy = true;
        Message = null;
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = TfvcDisplayText.DescribeError(ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
