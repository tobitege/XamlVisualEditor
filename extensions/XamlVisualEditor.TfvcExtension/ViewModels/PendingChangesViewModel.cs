using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.DataGridHierarchical;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>A work item that is linked to the next check-in.</summary>
/// <param name="Id">Work item ID.</param>
/// <param name="Label">Display text.</param>
public sealed record TfvcWorkItemViewModel(int Id, string Label);

/// <summary>View model of the pending changes panel: comment, related work items, included and excluded changes.</summary>
public sealed partial class PendingChangesViewModel : ReactiveObject, IDisposable
{
    private readonly TfvcSession _session;
    private readonly TfvcAutoCheckOutService _autoCheckOut;
    private readonly IEditorServices _editor;
    private readonly IWindow _window;
    private readonly IDialogHost? _dialogHost;
    private readonly ISystemIconService? _icons;
    private readonly Func<string, Task>? _showHistory;
    private bool _isDisposed;

    /// <summary>Creates the view model.</summary>
    public PendingChangesViewModel(
        TfvcSession session,
        TfvcAutoCheckOutService autoCheckOut,
        IEditorServices editor,
        IWindow window,
        IDialogHost? dialogHost,
        ISystemIconService? icons,
        Func<string, Task>? showHistory = null,
        TfvcChangesetDetailsViewModel? changesetDetails = null)
    {
        _showHistory = showHistory;
        _session = session;
        _autoCheckOut = autoCheckOut;
        _editor = editor;
        _window = window;
        _dialogHost = dialogHost;
        _icons = icons;
        Connection = new TfvcConnectionViewModel(session, "pendingChanges");
        ChangesetDetails = changesetDetails ?? new TfvcChangesetDetailsViewModel(session);

        IncludedModel = CreateModel(IncludedRoots);
        ExcludedModel = CreateModel(ExcludedRoots);

        IObservable<bool> hasIncluded = this.WhenAnyValue(x => x.IncludedCount, x => x.IsWorking, (count, working) => count > 0 && !working);
        IObservable<bool> hasAny = this.WhenAnyValue(x => x.IncludedCount, x => x.ExcludedCount, x => x.IsWorking,
            (included, excluded, working) => included + excluded > 0 && !working);
        IObservable<PendingChangeNodeViewModel?> selectedNode = this.WhenAnyValue(x => x.SelectedNode);
        IObservable<bool> hasSelection = selectedNode.CombineLatest(this.WhenAnyValue(x => x.IsWorking), (node, working) => node is not null && !working);
        IObservable<bool> hasSelectedFile = selectedNode.Select(node => node?.Change?.LocalPath is not null && !node.IsFolder);

        RefreshCommand = ReactiveCommand.CreateFromTask(ct => _session.RefreshPendingChangesAsync(ct));
        CheckInCommand = ReactiveCommand.CreateFromTask(CheckInAsync, hasIncluded);
        ShelveCommand = ReactiveCommand.CreateFromTask(ShelveAsync, hasIncluded);
        UndoSelectedCommand = ReactiveCommand.CreateFromTask(() => UndoAsync(SelectedNode?.GetChanges()), hasSelection);
        UndoAllCommand = ReactiveCommand.CreateFromTask(() => UndoAsync(_session.PendingChanges), hasAny);
        ExcludeSelectedCommand = ReactiveCommand.CreateFromTask(
            ct => SetExcludedAsync(SelectedNode?.GetChanges(), true, ct),
            this.WhenAnyValue(x => x.SelectedNode, x => x.IsSelectedNodeExcluded, (node, excluded) => node is not null && !excluded));
        IncludeSelectedCommand = ReactiveCommand.CreateFromTask(
            ct => SetExcludedAsync(SelectedNode?.GetChanges(), false, ct),
            this.WhenAnyValue(x => x.SelectedNode, x => x.IsSelectedNodeExcluded, (node, excluded) => node is not null && excluded));
        ExcludeAllCommand = ReactiveCommand.CreateFromTask(ct => SetExcludedAsync(_session.PendingChanges, true, ct));
        IncludeAllCommand = ReactiveCommand.CreateFromTask(ct => SetExcludedAsync(_session.PendingChanges, false, ct));
        OpenSelectedCommand = ReactiveCommand.CreateFromTask(OpenSelectedAsync, hasSelectedFile);
        CompareSelectedCommand = ReactiveCommand.CreateFromTask(CompareSelectedAsync, hasSelectedFile);
        CloseDiffCommand = ReactiveCommand.Create(() => { IsDiffVisible = false; });
        AddWorkItemCommand = ReactiveCommand.CreateFromTask(AddWorkItemAsync, this.WhenAnyValue(x => x.WorkItemIdText, text => TryParseWorkItemId(text, out _)));
        RemoveWorkItemCommand = ReactiveCommand.Create<TfvcWorkItemViewModel>(item => WorkItems.Remove(item));
        AddActiveDocumentCommand = ReactiveCommand.CreateFromTask(AddActiveDocumentAsync);
        IObservable<bool> hasNode = selectedNode.Select(node => node is not null);
        CopyNameCommand = ReactiveCommand.CreateFromTask(() => CopyAsync(GetLastSegment(SelectedNode?.FullPath)), hasNode);
        CopyPathCommand = ReactiveCommand.CreateFromTask(() => CopyAsync(SelectedNode?.FullPath), hasNode);
        ShowHistoryCommand = ReactiveCommand.CreateFromTask(
            () => SelectedNode is { } node && _showHistory is not null ? _showHistory(node.Change?.ServerPath ?? node.FullPath) : Task.CompletedTask,
            hasNode);

        this.WhenAnyValue(x => x.SelectedIncludedRow)
            .Select(ToNode)
            .Where(node => node is not null)
            .Subscribe(node => SelectNode(node, excluded: false));
        this.WhenAnyValue(x => x.SelectedExcludedRow)
            .Select(ToNode)
            .Where(node => node is not null)
            .Subscribe(node => SelectNode(node, excluded: true));

        _session.PendingChangesChanged += OnPendingChangesChanged;
        _session.StateChanged += OnSessionStateChanged;
        RebuildTrees();
    }

    /// <summary>Gets the connection settings and workspace selection.</summary>
    public TfvcConnectionViewModel Connection { get; }

    /// <summary>Gets the changeset details that replace the pending changes while they are active.</summary>
    public TfvcChangesetDetailsViewModel ChangesetDetails { get; }

    /// <summary>Gets the roots of the included changes tree.</summary>
    public ObservableCollection<PendingChangeNodeViewModel> IncludedRoots { get; } = new();

    /// <summary>Gets the roots of the excluded changes tree.</summary>
    public ObservableCollection<PendingChangeNodeViewModel> ExcludedRoots { get; } = new();

    /// <summary>Gets the hierarchical model of the included changes.</summary>
    public HierarchicalModel IncludedModel { get; }

    /// <summary>Gets the hierarchical model of the excluded changes.</summary>
    public HierarchicalModel ExcludedModel { get; }

    /// <summary>Gets the work items that are linked to the next check-in.</summary>
    public ObservableCollection<TfvcWorkItemViewModel> WorkItems { get; } = new();

    /// <summary>Gets the lines of the displayed difference.</summary>
    public ObservableCollection<TfvcDiffLine> DiffLines { get; } = new();

    /// <summary>Reloads the pending changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Checks in the included changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CheckInCommand { get; }

    /// <summary>Shelves the included changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShelveCommand { get; }

    /// <summary>Undoes the selected changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UndoSelectedCommand { get; }

    /// <summary>Undoes all pending changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> UndoAllCommand { get; }

    /// <summary>Moves the selected changes to the excluded list.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExcludeSelectedCommand { get; }

    /// <summary>Moves the selected changes to the included list.</summary>
    public ReactiveCommand<RxVoid, RxVoid> IncludeSelectedCommand { get; }

    /// <summary>Excludes all changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ExcludeAllCommand { get; }

    /// <summary>Includes all changes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> IncludeAllCommand { get; }

    /// <summary>Opens the selected file in the editor.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenSelectedCommand { get; }

    /// <summary>Compares the selected file with its workspace version.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CompareSelectedCommand { get; }

    /// <summary>Hides the difference view.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CloseDiffCommand { get; }

    /// <summary>Adds the entered work item.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddWorkItemCommand { get; }

    /// <summary>Removes a related work item.</summary>
    public ReactiveCommand<TfvcWorkItemViewModel, RxVoid> RemoveWorkItemCommand { get; }

    /// <summary>Adds the active editor document to version control.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AddActiveDocumentCommand { get; }

    /// <summary>Shows the history of the selected item.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowHistoryCommand { get; }

    /// <summary>Copies the name of the selected item to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyNameCommand { get; }

    /// <summary>Copies the local path of the selected item, or its server path when it is not mapped, to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyPathCommand { get; }

    /// <summary>Asks the view to place text on the clipboard.</summary>
    public Interaction<string, RxVoid> CopyToClipboard { get; } = new();

    /// <summary>Gets or sets the check-in comment.</summary>
    [Reactive]
    public partial string? Comment { get; set; }

    /// <summary>Gets or sets the entered work item ID.</summary>
    [Reactive]
    public partial string? WorkItemIdText { get; set; }

    /// <summary>Gets or sets the selected row of the included changes grid.</summary>
    [Reactive]
    public partial object? SelectedIncludedRow { get; set; }

    /// <summary>Gets or sets the selected row of the excluded changes grid.</summary>
    [Reactive]
    public partial object? SelectedExcludedRow { get; set; }

    /// <summary>Gets the node the item commands operate on.</summary>
    [Reactive]
    public partial PendingChangeNodeViewModel? SelectedNode { get; private set; }

    /// <summary>Gets whether the selected node belongs to the excluded changes.</summary>
    [Reactive]
    public partial bool IsSelectedNodeExcluded { get; private set; }

    /// <summary>Gets the number of included changes.</summary>
    [Reactive]
    public partial int IncludedCount { get; private set; }

    /// <summary>Gets the number of excluded changes.</summary>
    [Reactive]
    public partial int ExcludedCount { get; private set; }

    /// <summary>Gets the header of the included changes section.</summary>
    [Reactive]
    public partial string IncludedHeader { get; private set; } = string.Empty;

    /// <summary>Gets the header of the excluded changes section.</summary>
    [Reactive]
    public partial string ExcludedHeader { get; private set; } = string.Empty;

    /// <summary>Gets whether there are no pending changes.</summary>
    [Reactive]
    public partial bool IsEmpty { get; private set; } = true;

    /// <summary>Gets whether a TFVC operation of this panel is running.</summary>
    [Reactive]
    public partial bool IsWorking { get; private set; }

    /// <summary>Gets the result or error message of the last operation.</summary>
    [Reactive]
    public partial string? OperationMessage { get; private set; }

    /// <summary>Gets or sets whether the difference view is visible.</summary>
    [Reactive]
    public partial bool IsDiffVisible { get; set; }

    /// <summary>Gets the title of the difference view.</summary>
    [Reactive]
    public partial string? DiffTitle { get; private set; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _session.PendingChangesChanged -= OnPendingChangesChanged;
        _session.StateChanged -= OnSessionStateChanged;
        Connection.Dispose();
    }

    /// <summary>Rebuilds both trees from the pending changes of the session.</summary>
    public void RebuildTrees()
    {
        List<TfvcPendingChange> included = new();
        List<TfvcPendingChange> excluded = new();
        foreach (TfvcPendingChange change in _session.PendingChanges)
        {
            (_session.IsExcluded(change) ? excluded : included).Add(change);
        }

        ReplaceRoots(IncludedRoots, PendingChangeTreeBuilder.Build(included, GetIcon));
        ReplaceRoots(ExcludedRoots, PendingChangeTreeBuilder.Build(excluded, GetIcon));
        IncludedModel.SetRoots(IncludedRoots);
        ExcludedModel.SetRoots(ExcludedRoots);
        IncludedModel.Refresh();
        ExcludedModel.Refresh();
        IncludedCount = included.Count;
        ExcludedCount = excluded.Count;
        IncludedHeader = string.Format(CultureInfo.CurrentCulture, TfvcStrings.IncludedChangesFormat, included.Count);
        ExcludedHeader = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ExcludedChangesFormat, excluded.Count);
        IsEmpty = included.Count + excluded.Count == 0;
        SelectedNode = null;
        IsSelectedNodeExcluded = false;
    }

    private static HierarchicalModel CreateModel(ObservableCollection<PendingChangeNodeViewModel> roots)
    {
        HierarchicalOptions options = new()
        {
            ChildrenSelector = item => ((PendingChangeNodeViewModel)item).Children,
            IsLeafSelector = item => ((PendingChangeNodeViewModel)item).Children.Count == 0,
            IsExpandedSelector = item => ((PendingChangeNodeViewModel)item).IsExpanded,
            IsExpandedSetter = (item, value) => ((PendingChangeNodeViewModel)item).IsExpanded = value
        };
        HierarchicalModel model = new(options);
        model.SetRoots(roots);
        return model;
    }

    private static PendingChangeNodeViewModel? ToNode(object? row)
    {
        return row switch
        {
            HierarchicalNode node => node.Item as PendingChangeNodeViewModel,
            PendingChangeNodeViewModel viewModel => viewModel,
            _ => null
        };
    }

    private static void ReplaceRoots(ObservableCollection<PendingChangeNodeViewModel> target, IReadOnlyList<PendingChangeNodeViewModel> roots)
    {
        target.Clear();
        foreach (PendingChangeNodeViewModel root in roots)
        {
            target.Add(root);
        }
    }

    private static bool TryParseWorkItemId(string? text, out int id)
    {
        return int.TryParse(text?.Trim().TrimStart('#'), NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    private async Task CopyAsync(string? text)
    {
        if (!string.IsNullOrEmpty(text))
        {
            await CopyToClipboard.Handle(text);
        }
    }

    private static string? GetLastSegment(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string trimmed = path.TrimEnd('\\', '/');
        int index = trimmed.LastIndexOfAny(['\\', '/']);
        return index >= 0 ? trimmed.Substring(index + 1) : trimmed;
    }

    private void SelectNode(PendingChangeNodeViewModel? node, bool excluded)
    {
        SelectedNode = node;
        IsSelectedNodeExcluded = excluded;
    }

    private object? GetIcon(string path, bool isFolder)
    {
        return _icons?.GetIcon(path, isFolder);
    }

    private IReadOnlyList<TfvcPendingChange> GetIncludedChanges()
    {
        List<TfvcPendingChange> included = new();
        foreach (TfvcPendingChange change in _session.PendingChanges)
        {
            if (!_session.IsExcluded(change))
            {
                included.Add(change);
            }
        }

        return included;
    }

    private async Task CheckInAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        string? collectionUrl = _session.Settings.CollectionUrl;
        IReadOnlyList<TfvcPendingChange> included = GetIncludedChanges();
        if (workspace is null || collectionUrl is null || included.Count == 0)
        {
            return;
        }

        string comment = Comment?.Trim() ?? string.Empty;
        if (comment.Length == 0)
        {
            OperationMessage = TfvcStrings.CommentRequired;
            return;
        }

        await RunOperationAsync(async () =>
        {
            TfCommandResult result;
            try
            {
                result = await _session.Client.CheckInAsync(workspace, comment, ToServerPaths(included), cancellationToken);
            }
            catch (TfCommandLineTooLongException) when (_session.ExcludedServerPaths.Count == 0)
            {
                // Without exclusions, checking in the whole workspace is the same change set.
                result = await _session.Client.CheckInAsync(workspace, comment, Array.Empty<string>(), cancellationToken);
            }

            int? changesetId = TfvcOutputParser.TryParseChangesetId(result.CombinedOutput);
            List<string> messages = new()
            {
                changesetId is null
                    ? TfvcStrings.CheckedIn
                    : string.Format(CultureInfo.CurrentCulture, TfvcStrings.CheckedInFormat, changesetId.Value)
            };

            if (changesetId is not null)
            {
                foreach (TfvcWorkItemViewModel workItem in WorkItems)
                {
                    try
                    {
                        await _session.Server.LinkChangesetAsync(collectionUrl, workItem.Id, changesetId.Value, cancellationToken);
                    }
                    catch (TfvcServerException ex)
                    {
                        messages.Add(string.Format(CultureInfo.CurrentCulture, TfvcStrings.WorkItemLinkFailedFormat, workItem.Id, ex.Message));
                    }
                }
            }

            Comment = null;
            WorkItems.Clear();
            OperationMessage = string.Join(Environment.NewLine, messages);
        }, cancellationToken);
    }

    private async Task ShelveAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        IReadOnlyList<TfvcPendingChange> included = GetIncludedChanges();
        if (workspace is null || included.Count == 0)
        {
            return;
        }

        string? name = await _window.ShowInputBoxAsync(
            new InputBoxOptions(TfvcStrings.Shelve, TfvcStrings.ShelveNamePrompt, null),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        string shelvesetName = name.Trim();
        await RunOperationAsync(async () =>
        {
            IReadOnlyList<string> items = _session.ExcludedServerPaths.Count == 0 ? Array.Empty<string>() : ToServerPaths(included);
            await _session.Client.ShelveAsync(workspace, shelvesetName, Comment?.Trim(), items, cancellationToken);
            OperationMessage = string.Format(CultureInfo.CurrentCulture, TfvcStrings.ShelvedFormat, shelvesetName);
        }, cancellationToken);
    }

    private async Task UndoAsync(IReadOnlyList<TfvcPendingChange>? changes)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        if (workspace is null || changes is null || changes.Count == 0)
        {
            return;
        }

        if (!await ConfirmAsync(
            TfvcStrings.Undo,
            string.Format(CultureInfo.CurrentCulture, TfvcStrings.UndoConfirmMessageFormat, changes.Count),
            TfvcStrings.UndoConfirmAction))
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            await _session.Client.UndoAsync(workspace, ToServerPaths(changes), recursive: false, CancellationToken.None);
            OperationMessage = null;
        }, CancellationToken.None);
    }

    private async Task SetExcludedAsync(IReadOnlyList<TfvcPendingChange>? changes, bool excluded, CancellationToken cancellationToken)
    {
        if (changes is null || changes.Count == 0)
        {
            return;
        }

        await _session.SetExcludedAsync(ToServerPaths(changes), excluded, cancellationToken);
    }

    private async Task OpenSelectedAsync(CancellationToken cancellationToken)
    {
        string? localPath = SelectedNode?.Change?.LocalPath;
        if (localPath is not null && File.Exists(localPath))
        {
            await _editor.OpenDocumentAsync(localPath, EditorDocumentOpenBehavior.DocumentOnly, cancellationToken);
        }
    }

    private async Task CompareSelectedAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        string? localPath = SelectedNode?.Change?.LocalPath;
        if (workspace is null || localPath is null)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            TfCommandResult result = await _session.Client.DifferenceAsync(workspace, localPath, cancellationToken);
            DiffLines.Clear();
            foreach (TfvcDiffLine line in TfvcDiffLine.Parse(result.StandardOutput))
            {
                DiffLines.Add(line);
            }

            DiffTitle = string.Format(CultureInfo.CurrentCulture, TfvcStrings.DiffTitleFormat, Path.GetFileName(localPath));
            IsDiffVisible = true;
        }, cancellationToken, refreshAfterwards: false);
    }

    private async Task AddWorkItemAsync(CancellationToken cancellationToken)
    {
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (collectionUrl is null || !TryParseWorkItemId(WorkItemIdText, out int id))
        {
            return;
        }

        foreach (TfvcWorkItemViewModel existing in WorkItems)
        {
            if (existing.Id == id)
            {
                WorkItemIdText = null;
                return;
            }
        }

        await RunOperationAsync(async () =>
        {
            TfvcWorkItemInfo? info = await _session.Server.GetWorkItemAsync(collectionUrl, id, cancellationToken);
            if (info is null)
            {
                OperationMessage = string.Format(CultureInfo.CurrentCulture, TfvcStrings.WorkItemNotFoundFormat, id);
                return;
            }

            WorkItems.Add(new TfvcWorkItemViewModel(
                id,
                string.Format(CultureInfo.CurrentCulture, TfvcStrings.WorkItemLabelFormat, info.WorkItemType, id, info.Title)));
            WorkItemIdText = null;
            OperationMessage = null;
        }, cancellationToken, refreshAfterwards: false);
    }

    private async Task AddActiveDocumentAsync(CancellationToken cancellationToken)
    {
        string? path = _editor.ActiveDocument?.FilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        TfvcFileState state = await _autoCheckOut.GetStateAsync(path, cancellationToken);
        TfvcWorkspace? workspace = state.Workspace;
        if (state.Kind != TfvcFileStateKind.NotVersioned || workspace is null)
        {
            return;
        }

        await RunOperationAsync(async () =>
        {
            await _session.Client.AddAsync(workspace, [path], cancellationToken);
            _autoCheckOut.Invalidate(path);
            OperationMessage = null;
        }, cancellationToken);
    }

    // Each operation replaces the result or error message of the previous one.
    private async Task RunOperationAsync(Func<Task> operation, CancellationToken cancellationToken, bool refreshAfterwards = true)
    {
        IsWorking = true;
        OperationMessage = null;
        try
        {
            await operation();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OperationMessage = TfvcDisplayText.DescribeError(ex);
        }
        finally
        {
            IsWorking = false;
        }

        if (refreshAfterwards)
        {
            await _session.RefreshPendingChangesAsync(cancellationToken);
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string action)
    {
        if (_dialogHost is null)
        {
            return false;
        }

        bool? result = await _dialogHost.ShowDialogAsync<bool>(
            ConfirmationDialogRequest.DialogId,
            new ConfirmationDialogRequest(title, message, action, IsDestructive: true),
            CancellationToken.None);
        return result == true;
    }

    private static IReadOnlyList<string> ToServerPaths(IReadOnlyList<TfvcPendingChange> changes)
    {
        string[] paths = new string[changes.Count];
        for (int i = 0; i < changes.Count; i++)
        {
            paths[i] = changes[i].ServerPath;
        }

        return paths;
    }

    private void OnPendingChangesChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (!_isDisposed)
            {
                RebuildTrees();
            }
        });
    }

    private void OnSessionStateChanged(object? sender, EventArgs e)
    {
        RxSchedulers.MainThreadScheduler.Schedule(() =>
        {
            if (!_isDisposed && !_session.IsConnected)
            {
                RebuildTrees();
            }
        });
    }
}
