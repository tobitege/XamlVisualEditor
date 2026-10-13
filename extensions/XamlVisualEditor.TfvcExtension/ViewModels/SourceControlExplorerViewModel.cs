using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.DataGridFiltering;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Controls.DataGridSearching;
using Avalonia.Controls.DataGridSorting;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>View model of the source control explorer: server tree of the collection with workspace state.</summary>
public sealed partial class SourceControlExplorerViewModel : ReactiveObject, IDisposable
{
    private const string ROOT_PATH = "$/";

    private readonly TfvcSession _session;
    private readonly TfvcAutoCheckOutService _autoCheckOut;
    private readonly IEditorServices _editor;
    private readonly IDialogHost? _dialogHost;
    private readonly ISystemIconService? _icons;
    private string? _loadedCollectionUrl;
    private string? _loadingRootCollectionUrl;
    private SourceControlItemViewModel? _treeRoot;
    private readonly Func<string, Task>? _showHistory;
    private readonly IDisposable _searchSubscription;
    private bool _isDisposed;

    /// <summary>Creates the view model.</summary>
    public SourceControlExplorerViewModel(
        TfvcSession session,
        TfvcAutoCheckOutService autoCheckOut,
        IEditorServices editor,
        IDialogHost? dialogHost,
        ISystemIconService? icons,
        Func<string, Task>? showHistory = null)
    {
        _showHistory = showHistory;
        _session = session;
        _autoCheckOut = autoCheckOut;
        _editor = editor;
        _dialogHost = dialogHost;
        _icons = icons;
        Connection = new TfvcConnectionViewModel(session, "sourceControlExplorer");

        HierarchicalOptions options = new()
        {
            ChildrenSelector = item => ((SourceControlItemViewModel)item).Children,
            IsLeafSelector = item => !((SourceControlItemViewModel)item).HasChildren,
            IsExpandedSelector = item => ((SourceControlItemViewModel)item).IsExpanded,
            IsExpandedSetter = (item, value) => OnExpandedChanged((SourceControlItemViewModel)item, value)
        };
        Model = new HierarchicalModel(options);
        Model.SetRoots(Roots);
        SortingModel = new SortingModel();
        FilteringModel = new FilteringModel();
        SearchModel = new SearchModel
        {
            HighlightMode = SearchHighlightMode.TextAndCell,
            HighlightCurrent = true,
            WrapNavigation = true,
            UpdateSelectionOnNavigate = true
        };

        this.WhenAnyValue(x => x.SelectedRow)
            .Select(row => row switch
            {
                HierarchicalNode node => node.Item as SourceControlItemViewModel,
                SourceControlItemViewModel item => item,
                _ => null
            })
            .Subscribe(item =>
            {
                SelectedItem = item is { IsPlaceholder: false } ? item : null;
                if (SelectedItem is not null)
                {
                    PathText = SelectedItem.ServerPath;
                }
            });

        _searchSubscription = this.WhenAnyValue(x => x.SearchText)
            .Throttle(TimeSpan.FromMilliseconds(150))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplySearch);

        IObservable<SourceControlItemViewModel?> selected = this.WhenAnyValue(x => x.SelectedItem);
        IObservable<bool> notWorking = this.WhenAnyValue(x => x.IsWorking).Select(working => !working);
        IObservable<bool> canOpen = selected.Select(item => item is { IsFolder: false, IsDownloaded: true });
        IObservable<bool> canGet = selected.CombineLatest(notWorking, (item, idle) => idle && item?.LocalPath is not null);
        IObservable<bool> canCheckOut = selected.CombineLatest(notWorking, (item, idle) => idle && item is { IsDownloaded: true });
        IObservable<bool> canUndo = selected.CombineLatest(notWorking, (item, idle) => idle && item is not null && HasPendingChangeBelow(item.ServerPath));

        RefreshCommand = ReactiveCommand.CreateFromTask(RefreshAsync);
        NavigateCommand = ReactiveCommand.CreateFromTask(ct => NavigateToAsync(PathText, ct));
        OpenCommand = ReactiveCommand.CreateFromTask(OpenSelectedAsync, canOpen);
        GetLatestCommand = ReactiveCommand.CreateFromTask(GetLatestAsync, canGet);
        CheckOutCommand = ReactiveCommand.CreateFromTask(CheckOutAsync, canCheckOut);
        UndoCommand = ReactiveCommand.CreateFromTask(UndoAsync, canUndo);
        AddItemsToFolderCommand = ReactiveCommand.CreateFromTask(
            AddItemsToFolderAsync,
            selected.CombineLatest(notWorking, (item, idle) => idle && _dialogHost is not null
                && item is { IsFolder: true, LocalPath: { } folder } && Directory.Exists(folder)));
        LocateActiveDocumentCommand = ReactiveCommand.CreateFromTask(LocateActiveDocumentAsync);
        FocusSelectedCommand = ReactiveCommand.CreateFromTask(
            ct => SetFocusAsync(SelectedItem?.ServerPath, ct),
            this.WhenAnyValue(x => x.SelectedItem, x => x.FocusedPath, (item, focus) =>
                item is { IsFolder: true } && item.ServerPath != ROOT_PATH
                && !string.Equals(item.ServerPath, focus, StringComparison.OrdinalIgnoreCase)));
        IObservable<bool> hasItem = selected.Select(item => item is not null);
        CopyNameCommand = ReactiveCommand.CreateFromTask(() => CopyAsync(SelectedItem?.Name), hasItem);
        CopyPathCommand = ReactiveCommand.CreateFromTask(() => CopyAsync(SelectedItem?.LocalPath ?? SelectedItem?.ServerPath), hasItem);
        ShowHistoryCommand = ReactiveCommand.CreateFromTask(
            () => SelectedItem is { } item && _showHistory is not null ? _showHistory(item.ServerPath) : Task.CompletedTask,
            hasItem);
        ClearFocusCommand = ReactiveCommand.CreateFromTask(
            ct => SetFocusAsync(null, ct),
            this.WhenAnyValue(x => x.FocusedPath).Select(focus => focus is not null));

        _session.StateChanged += OnSessionStateChanged;
        _session.PendingChangesChanged += OnPendingChangesChanged;
        RxSchedulers.MainThreadScheduler.Schedule(() => _ = EnsureRootAsync());
    }

    /// <summary>Gets the connection settings and workspace selection.</summary>
    public TfvcConnectionViewModel Connection { get; }

    /// <summary>Gets the root items.</summary>
    public ObservableCollection<SourceControlItemViewModel> Roots { get; } = new();

    /// <summary>Gets the hierarchical model of the server tree.</summary>
    public HierarchicalModel Model { get; }

    /// <summary>Gets the sorting model of the grid.</summary>
    public SortingModel SortingModel { get; }

    /// <summary>Gets the filtering model of the grid.</summary>
    public FilteringModel FilteringModel { get; }

    /// <summary>Gets the search model of the grid.</summary>
    public SearchModel SearchModel { get; }

    /// <summary>Reloads the tree.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Navigates to the entered server path.</summary>
    public ReactiveCommand<RxVoid, RxVoid> NavigateCommand { get; }

    /// <summary>Opens the selected file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    /// <summary>Gets the latest version of the selected item.</summary>
    public ReactiveCommand<RxVoid, RxVoid> GetLatestCommand { get; }

    /// <summary>Checks out the selected file, or every item of the selected folder and its subfolders.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CheckOutCommand { get; }

    /// <summary>Undoes the pending changes of the selected item.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UndoCommand { get; }

    /// <summary>Adds unversioned items of the selected mapped folder to version control.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddItemsToFolderCommand { get; }

    /// <summary>Selects the active editor document in the tree.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LocateActiveDocumentCommand { get; }

    /// <summary>Shows the history of the selected item.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowHistoryCommand { get; }

    /// <summary>Copies the name of the selected item to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyNameCommand { get; }

    /// <summary>Copies the local path of the selected item, or its server path when it is not mapped, to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyPathCommand { get; }

    /// <summary>Asks the view to place text on the clipboard.</summary>
    public Interaction<string, RxVoid> CopyToClipboard { get; } = new();

    /// <summary>Shows only the selected folder and its descendants.</summary>
    public ReactiveCommand<RxVoid, RxVoid> FocusSelectedCommand { get; }

    /// <summary>Shows the whole collection again.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearFocusCommand { get; }

    /// <summary>Gets the server path of the focused folder, or null when the whole collection is shown.</summary>
    [Reactive]
    public partial string? FocusedPath { get; private set; }

    /// <summary>Gets or sets the selected grid row.</summary>
    [Reactive]
    public partial object? SelectedRow { get; set; }

    /// <summary>Gets the selected item.</summary>
    [Reactive]
    public partial SourceControlItemViewModel? SelectedItem { get; private set; }

    /// <summary>Gets or sets the server path of the navigation box.</summary>
    [Reactive]
    public partial string? PathText { get; set; }

    /// <summary>Gets or sets the search text.</summary>
    [Reactive]
    public partial string? SearchText { get; set; }

    /// <summary>Gets whether an operation of this panel is running.</summary>
    [Reactive]
    public partial bool IsWorking { get; private set; }

    /// <summary>Gets the result or error message of the last operation.</summary>
    [Reactive]
    public partial string? OperationMessage { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _session.StateChanged -= OnSessionStateChanged;
        _session.PendingChangesChanged -= OnPendingChangesChanged;
        _searchSubscription.Dispose();
        Connection.Dispose();
    }

    /// <summary>Expands the tree along a server path and selects its item.</summary>
    public async Task NavigateToAsync(string? serverPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverPath) || !serverPath.Trim().StartsWith('$'))
        {
            return;
        }

        await EnsureRootAsync();
        if (Roots.Count == 0)
        {
            return;
        }

        string target = TfvcWorkspace.NormalizeServerPath(serverPath);
        if (FocusedPath is not null && !TfvcWorkspace.IsSameOrChildServerPath(target, FocusedPath))
        {
            await SetFocusAsync(null, cancellationToken);
        }

        List<SourceControlItemViewModel> ancestors = new();
        SourceControlItemViewModel current = await ResolvePathAsync(Roots[0], target, ancestors, cancellationToken);
        Model.Refresh();
        foreach (SourceControlItemViewModel ancestor in ancestors)
        {
            HierarchicalNode? ancestorNode = Model.FindNode(ancestor);
            if (ancestorNode is not null && !ancestorNode.IsExpanded)
            {
                Model.Expand(ancestorNode);
            }
        }

        SelectedRow = (object?)Model.FindNode(current) ?? current;
    }

    /// <summary>Shows only the given folder and its descendants; null shows the whole collection again.</summary>
    public async Task SetFocusAsync(string? serverPath, CancellationToken cancellationToken)
    {
        string? normalized = string.IsNullOrWhiteSpace(serverPath) || TfvcWorkspace.NormalizeServerPath(serverPath) == ROOT_PATH
            ? null
            : TfvcWorkspace.NormalizeServerPath(serverPath);
        string? previousFocus = FocusedPath;
        FocusedPath = normalized;
        await _session.SaveExplorerFocusAsync(normalized, cancellationToken);
        await ApplyFocusAsync(cancellationToken);
        if (normalized is null && previousFocus is not null)
        {
            await NavigateToAsync(previousFocus, cancellationToken);
        }
    }

    private async Task ApplyFocusAsync(CancellationToken cancellationToken)
    {
        if (_treeRoot is null)
        {
            return;
        }

        SourceControlItemViewModel displayed = _treeRoot;
        if (FocusedPath is not null)
        {
            // A loaded tree resolves the focus in place; otherwise only the focused folder is read instead of
            // every folder above it.
            SourceControlItemViewModel? start = Roots.Count == 1 && Roots[0].IsLoaded && TfvcWorkspace.IsSameOrChildServerPath(FocusedPath, Roots[0].ServerPath)
                ? Roots[0]
                : _treeRoot.IsLoaded ? _treeRoot : null;
            SourceControlItemViewModel? resolved = start is not null
                ? await ResolvePathAsync(start, FocusedPath, new List<SourceControlItemViewModel>(), cancellationToken)
                : await LoadFolderAsync(FocusedPath, cancellationToken);
            if (resolved is not null && resolved.IsFolder && string.Equals(resolved.ServerPath, FocusedPath, StringComparison.OrdinalIgnoreCase))
            {
                displayed = resolved;
                displayed.IsExpanded = true;
            }
            else
            {
                FocusedPath = null;
            }
        }

        await LoadChildrenAsync(displayed, cancellationToken);

        if (Roots.Count != 1 || !ReferenceEquals(Roots[0], displayed))
        {
            Roots.Clear();
            Roots.Add(displayed);
            Model.SetRoots(Roots);
        }

        Model.Refresh();
    }

    private async Task<SourceControlItemViewModel?> LoadFolderAsync(string serverPath, CancellationToken cancellationToken)
    {
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (collectionUrl is null)
        {
            return null;
        }

        try
        {
            TfvcServerItem? item = await _session.Server.GetItemAsync(collectionUrl, serverPath, cancellationToken);
            if (item is null)
            {
                return null;
            }

            SourceControlItemViewModel folder = new(item, GetIcon(GetIconPath(item), item.IsFolder));
            UpdateWorkspaceState(folder);
            return folder;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OperationMessage = TfvcDisplayText.DescribeError(ex);
            return null;
        }
    }

    // Walks from a start item towards a server path, loading children on the way; returns the deepest item reached.
    private async Task<SourceControlItemViewModel> ResolvePathAsync(
        SourceControlItemViewModel start,
        string target,
        List<SourceControlItemViewModel> ancestors,
        CancellationToken cancellationToken)
    {
        SourceControlItemViewModel current = start;
        while (!string.Equals(current.ServerPath, target, StringComparison.OrdinalIgnoreCase))
        {
            await LoadChildrenAsync(current, cancellationToken);
            current.IsExpanded = true;
            ancestors.Add(current);
            SourceControlItemViewModel? next = null;
            foreach (SourceControlItemViewModel child in current.Children)
            {
                if (TfvcWorkspace.IsSameOrChildServerPath(target, child.ServerPath))
                {
                    next = child;
                    break;
                }
            }

            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    private async Task EnsureRootAsync()
    {
        string? collectionUrl = _session.IsConnected ? _session.Settings.CollectionUrl : null;
        if (_isDisposed
            || (collectionUrl is not null && string.Equals(collectionUrl, _loadingRootCollectionUrl, StringComparison.OrdinalIgnoreCase))
            || (string.Equals(collectionUrl, _loadedCollectionUrl, StringComparison.OrdinalIgnoreCase) && (Roots.Count > 0 || collectionUrl is null)))
        {
            return;
        }

        _loadedCollectionUrl = collectionUrl;
        _treeRoot = null;
        Roots.Clear();
        if (collectionUrl is not null)
        {
            // Session state changes arrive while the root loads; they must not start a second load of the same tree.
            _loadingRootCollectionUrl = collectionUrl;
            try
            {
                SourceControlItemViewModel root = new(new TfvcServerItem(ROOT_PATH, true, 0, null), GetIcon(ROOT_PATH, true));
                _treeRoot = root;
                UpdateWorkspaceState(root);
                root.IsExpanded = true;
                FocusedPath ??= await _session.LoadExplorerFocusAsync(CancellationToken.None);
                await ApplyFocusAsync(CancellationToken.None);
            }
            finally
            {
                if (string.Equals(collectionUrl, _loadingRootCollectionUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _loadingRootCollectionUrl = null;
                }
            }

            return;
        }

        Model.SetRoots(Roots);
        Model.Refresh();
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceControlItemViewModel root in Roots)
        {
            CollectExpanded(root, expanded);
        }

        string? selectedPath = SelectedItem?.ServerPath;
        _loadedCollectionUrl = null;
        await EnsureRootAsync();
        foreach (SourceControlItemViewModel root in Roots)
        {
            await RestoreExpandedAsync(root, expanded, cancellationToken);
        }

        Model.Refresh();
        if (selectedPath is not null)
        {
            await NavigateToAsync(selectedPath, cancellationToken);
        }
    }

    private async Task RestoreExpandedAsync(SourceControlItemViewModel item, HashSet<string> expanded, CancellationToken cancellationToken)
    {
        if (!item.IsFolder || !expanded.Contains(item.ServerPath))
        {
            return;
        }

        await LoadChildrenAsync(item, cancellationToken);
        item.IsExpanded = true;
        foreach (SourceControlItemViewModel child in item.Children)
        {
            await RestoreExpandedAsync(child, expanded, cancellationToken);
        }
    }

    private static void CollectExpanded(SourceControlItemViewModel item, HashSet<string> expanded)
    {
        if (!item.IsExpanded)
        {
            return;
        }

        expanded.Add(item.ServerPath);
        foreach (SourceControlItemViewModel child in item.Children)
        {
            CollectExpanded(child, expanded);
        }
    }

    private void OnExpandedChanged(SourceControlItemViewModel item, bool isExpanded)
    {
        item.IsExpanded = isExpanded;
        if (isExpanded && !item.IsLoaded)
        {
            _ = LoadChildrenAsync(item, CancellationToken.None);
        }
    }

    private async Task LoadChildrenAsync(SourceControlItemViewModel item, CancellationToken cancellationToken)
    {
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (item.IsLoaded || !item.IsFolder || collectionUrl is null)
        {
            return;
        }

        item.IsLoaded = true;
        item.Children.Clear();
        item.Children.Add(new SourceControlItemViewModel(new TfvcServerItem(TfvcWorkspace.CombineServerPath(item.ServerPath, "."), false, 0, null), null, isPlaceholder: true));
        RefreshRowsBelow(item);
        try
        {
            IReadOnlyList<TfvcServerItem> children = await _session.Server.GetChildrenAsync(collectionUrl, item.ServerPath, cancellationToken);
            item.Children.Clear();
            foreach (TfvcServerItem child in children)
            {
                SourceControlItemViewModel childViewModel = new(child, GetIcon(GetIconPath(child), child.IsFolder));
                UpdateWorkspaceState(childViewModel);
                item.Children.Add(childViewModel);
            }

            item.HasChildren = item.Children.Count > 0;
            OperationMessage = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            item.IsLoaded = false;
            item.Children.Clear();
            OperationMessage = TfvcDisplayText.DescribeError(ex);
        }

        RefreshRowsBelow(item);
    }

    // Loading a folder replaces only its own child rows: a refresh of the whole model rebuilds every row, and the
    // grid then shows the top of the tree again instead of the folder the user just expanded.
    private void RefreshRowsBelow(SourceControlItemViewModel item)
    {
        if (Model.FindNode(item) is { } node)
        {
            Model.Refresh(node);
        }
        else
        {
            Model.Refresh();
        }
    }

    private string GetIconPath(TfvcServerItem item)
    {
        return _session.CurrentWorkspace?.TryGetLocalPath(item.ServerPath) ?? item.Name;
    }

    private object? GetIcon(string path, bool isFolder) => _icons?.GetIcon(path, isFolder);

    private void UpdateWorkspaceState(SourceControlItemViewModel item)
    {
        TfvcPendingChange? change = _session.FindPendingChangeByServerPath(item.ServerPath);
        item.UpdateWorkspaceState(
            _session.CurrentWorkspace,
            change,
            change is null ? string.Empty : TfvcDisplayText.FormatChangeTypes(change.ChangeTypes));
    }

    // A focused folder that was loaded on its own is not part of the tree below the collection root.
    private IEnumerable<SourceControlItemViewModel> GetStateRoots()
    {
        if (_treeRoot is not null)
        {
            yield return _treeRoot;
        }

        foreach (SourceControlItemViewModel root in Roots)
        {
            if (!ReferenceEquals(root, _treeRoot))
            {
                yield return root;
            }
        }
    }

    private void UpdateWorkspaceStateRecursive(IEnumerable<SourceControlItemViewModel> items)
    {
        foreach (SourceControlItemViewModel item in items)
        {
            UpdateWorkspaceState(item);
            UpdateWorkspaceStateRecursive(item.Children);
        }
    }

    private bool HasPendingChangeBelow(string serverPath)
    {
        foreach (TfvcPendingChange change in _session.PendingChanges)
        {
            if (TfvcWorkspace.IsSameOrChildServerPath(change.ServerPath, TfvcWorkspace.NormalizeServerPath(serverPath)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task OpenSelectedAsync(CancellationToken cancellationToken)
    {
        string? localPath = SelectedItem?.LocalPath;
        if (localPath is not null && File.Exists(localPath))
        {
            await _editor.OpenDocumentAsync(localPath, EditorDocumentOpenBehavior.DocumentOnly, cancellationToken);
        }
    }

    private async Task GetLatestAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        SourceControlItemViewModel? item = SelectedItem;
        if (workspace is null || item?.LocalPath is null)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            await _session.Client.GetLatestAsync(workspace, item.ServerPath, item.IsFolder, cancellationToken);
            item.UpdateWorkspaceState(workspace, _session.FindPendingChangeByServerPath(item.ServerPath), item.PendingChangeText);
            UpdateWorkspaceStateRecursive(item.Children);
        });
    }

    private async Task CheckOutAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        SourceControlItemViewModel? item = SelectedItem;
        string? localPath = item?.LocalPath;
        if (workspace is null || item is null || localPath is null)
        {
            return;
        }

        // A folder checks out every file below it, which can be a large pending change.
        if (item.IsFolder)
        {
            if (_dialogHost is null)
            {
                return;
            }

            bool? confirmed = await _dialogHost.ShowDialogAsync<bool>(
                ConfirmationDialogRequest.DIALOG_ID,
                new ConfirmationDialogRequest(
                    TfvcStrings.CheckOut,
                    string.Format(CultureInfo.CurrentCulture, TfvcStrings.CheckOutFolderConfirmMessageFormat, item.ServerPath),
                    TfvcStrings.CheckOut,
                    IsDestructive: false),
                cancellationToken);
            if (confirmed != true)
            {
                return;
            }
        }

        await RunOperationAsync(async () =>
        {
            await _session.Client.CheckOutAsync(workspace, [localPath], item.IsFolder, cancellationToken);
            if (item.IsFolder)
            {
                _autoCheckOut.InvalidateFolder(localPath);
            }
            else
            {
                _autoCheckOut.Invalidate(localPath);
            }
        });
    }

    private async Task UndoAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        SourceControlItemViewModel? item = SelectedItem;
        if (workspace is null || item is null || _dialogHost is null)
        {
            return;
        }

        int count = 0;
        foreach (TfvcPendingChange change in _session.PendingChanges)
        {
            if (TfvcWorkspace.IsSameOrChildServerPath(change.ServerPath, item.ServerPath))
            {
                count++;
            }
        }

        bool? confirmed = await _dialogHost.ShowDialogAsync<bool>(
            ConfirmationDialogRequest.DIALOG_ID,
            new ConfirmationDialogRequest(
                TfvcStrings.Undo,
                string.Format(CultureInfo.CurrentCulture, TfvcStrings.UndoConfirmMessageFormat, count),
                TfvcStrings.UndoConfirmAction,
                IsDestructive: true),
            cancellationToken);
        if (confirmed != true)
        {
            return;
        }

        await RunOperationAsync(() => _session.Client.UndoAsync(workspace, [item.ServerPath], item.IsFolder, cancellationToken));
    }

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            await CopyToClipboard.Handle(text);
        }
    }

    private async Task LocateActiveDocumentAsync(CancellationToken cancellationToken)
    {
        string? path = _editor.ActiveDocument?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string? serverPath = _session.FindWorkspaceForLocalPath(path)?.TryGetServerPath(path);
        if (serverPath is not null)
        {
            PathText = serverPath;
            await NavigateToAsync(serverPath, cancellationToken);
        }
    }

    private async Task AddItemsToFolderAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        string? localPath = SelectedItem is { IsFolder: true } item ? item.LocalPath : null;
        if (workspace is null || localPath is null || _dialogHost is null || !Directory.Exists(localPath))
        {
            return;
        }

        TfvcAddItemsScanner scanner = new(_session, workspace, localPath);
        TfvcAddItemsDialogViewModel? dialog = null;
        await RunOperationAsync(async () =>
        {
            await scanner.ScanAsync(cancellationToken);
            TfvcAddItemsDialogViewModel loaded = new(scanner, GetIcon);
            await loaded.ShowFolderAsync(scanner.RootFolder, cancellationToken);
            dialog = loaded;
        });
        if (dialog is null)
        {
            return;
        }

        IReadOnlyList<TfvcAddEntry>? selected = await _dialogHost.ShowDialogAsync<IReadOnlyList<TfvcAddEntry>>(
            TfvcAddItemsDialogViewModel.DIALOG_ID,
            dialog,
            cancellationToken);
        if (selected is not { Count: > 0 })
        {
            return;
        }

        // A selected folder is added with its content, so items below it are not added a second time.
        List<string> folders = selected.Where(entry => entry.IsFolder).Select(entry => entry.LocalPath).ToList();
        folders.RemoveAll(folder => folders.Any(other => IsBelow(folder, other)));
        List<string> files = selected
            .Where(entry => !entry.IsFolder && !folders.Any(folder => IsBelow(entry.LocalPath, folder)))
            .Select(entry => entry.LocalPath)
            .ToList();
        await RunOperationAsync(async () =>
        {
            if (folders.Count > 0)
            {
                await _session.Client.AddFoldersAsync(workspace, folders, cancellationToken);
            }

            if (files.Count > 0)
            {
                await _session.Client.AddAsync(workspace, files, cancellationToken);
            }
        });
    }

    private static bool IsBelow(string path, string folder) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private async Task RunOperationAsync(Func<Task> operation)
    {
        IsWorking = true;
        try
        {
            await operation();
            OperationMessage = null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OperationMessage = TfvcDisplayText.DescribeError(ex);
        }
        finally
        {
            IsWorking = false;
        }

        await _session.RefreshPendingChangesAsync(CancellationToken.None);
    }

    private void ApplySearch(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            SearchModel.Clear();
            return;
        }

        SearchModel.SetOrUpdate(new SearchDescriptor(
            text.Trim(),
            matchMode: SearchMatchMode.Contains,
            termMode: SearchTermCombineMode.Any,
            scope: SearchScope.VisibleColumns,
            comparison: StringComparison.OrdinalIgnoreCase));
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (_isDisposed || _session.IsBusy)
            {
                return;
            }

            _ = EnsureRootAsync();
            UpdateWorkspaceStateRecursive(GetStateRoots());
        });
    }

    private void OnPendingChangesChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (!_isDisposed)
            {
                UpdateWorkspaceStateRecursive(GetStateRoots());
            }
        });
    }
}
