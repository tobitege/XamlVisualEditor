using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Shell-backed designer host implementation for extensions.</summary>
public sealed partial class ShellDesignerHost : IDesignerHost, IDesignerDocumentNotifications, IDisposable
{
    private readonly MainWindowViewModel _mainViewModel;
    private readonly CompositeDisposable _disposables = new();
    private readonly SerialDisposable _selectionSubscription = new();
    private readonly object _stateGate = new();
    private DesignerDocumentViewModel? _currentDocument;
    private string? _lastActiveDocumentPath;

    public ShellDesignerHost(MainWindowViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;

        IObservable<DesignerDocumentViewModel?> activeDocuments = _mainViewModel
            .WhenAnyValue(
                x => x.ActiveDesignerDocument,
                x => x.ActiveDocument,
                ResolveCurrentDocument)
            .DistinctUntilChanged();
        _disposables.Add(activeDocuments.Subscribe(PublishActiveDocument));
        _disposables.Add(Observable.Switch(activeDocuments.Select(document => document is null
                ? Observable.Empty<DesignerDocumentChangedEventArgs>()
                : document.SyncEngine.SyncEvents.Select(_ => new DesignerDocumentChangedEventArgs(document.FilePath)))
            )
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(args => DocumentChanged?.Invoke(this, args)));

        _disposables.Add(_selectionSubscription);
    }

    public string? ActiveDocumentPath
    {
        get
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                return GetActiveDocument()?.FilePath ?? GetCachedActiveDocumentPath();
            }

            return GetCachedActiveDocumentPath();
        }
    }

    public event EventHandler<DesignerDocumentChangedEventArgs>? ActiveDocumentChanged;

    public event EventHandler<DesignerSelectionChangedEventArgs>? SelectionChanged;
    public event EventHandler<DesignerDocumentChangedEventArgs>? DocumentChanged;

    public async Task<IReadOnlyList<DesignerNodeSummary>> GetSelectedNodesAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetSelectedNodesCore();
        }

        return await Dispatcher.UIThread.InvokeAsync(GetSelectedNodesCore, DispatcherPriority.Normal);
    }

    public async Task<IReadOnlyList<DesignerNodeSummary>> GetVisualTreeAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetCurrentTree(includeContentPreview: false);
        }

        return await Dispatcher.UIThread.InvokeAsync(() => GetCurrentTree(includeContentPreview: false), DispatcherPriority.Normal);
    }

    public async Task<IReadOnlyList<DesignerNodeSummary>> GetLogicalTreeAsync(CancellationToken cancellationToken)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetCurrentTree(includeContentPreview: true);
        }

        return await Dispatcher.UIThread.InvokeAsync(() => GetCurrentTree(includeContentPreview: true), DispatcherPriority.Normal);
    }

    public async Task<IReadOnlyList<DesignerPropertyInfo>> GetPropertiesAsync(string nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetPropertiesCore(nodeId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => GetPropertiesCore(nodeId),
            DispatcherPriority.Normal);
    }

    public async Task<IReadOnlyList<DesignerEventInfo>> GetEventsAsync(string nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return GetEventsCore(nodeId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => GetEventsCore(nodeId),
            DispatcherPriority.Normal);
    }

    public async Task<bool> SetPropertyAsync(string nodeId, string propertyName, string? value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return await SetPropertyCoreAsync(nodeId, propertyName, value, cancellationToken);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => SetPropertyCoreAsync(nodeId, propertyName, value, cancellationToken),
            DispatcherPriority.Background);
    }

    public async Task<string?> InsertElementAsync(
        string typeName,
        string xmlNamespace,
        string? parentNodeId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return InsertElementCore(typeName, xmlNamespace, parentNodeId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => InsertElementCore(typeName, xmlNamespace, parentNodeId),
            DispatcherPriority.Background);
    }

    public async Task<bool> DeleteNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return DeleteNodeCore(nodeId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => DeleteNodeCore(nodeId),
            DispatcherPriority.Background);
    }

    public async Task<string?> WrapNodeAsync(
        string nodeId,
        string wrapperTypeName,
        string wrapperXmlNamespace,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return WrapNodeCore(nodeId, wrapperTypeName, wrapperXmlNamespace);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => WrapNodeCore(nodeId, wrapperTypeName, wrapperXmlNamespace),
            DispatcherPriority.Background);
    }

    public async Task<bool> SelectNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return SelectNodeCore(nodeId);
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () => SelectNodeCore(nodeId),
            DispatcherPriority.Background);
    }

    public Task<bool> RevealNodeAsync(string nodeId, CancellationToken cancellationToken)
    {
        return SelectNodeAsync(nodeId, cancellationToken);
    }

    public IDesignerTransaction BeginTransaction(string name)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        return new ShellDesignerTransaction(document, name);
    }

    public void Dispose()
    {
        _disposables.Dispose();
    }

    private IReadOnlyList<DesignerPropertyInfo> GetPropertiesCore(string nodeId)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed)
        {
            return Array.Empty<DesignerPropertyInfo>();
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return Array.Empty<DesignerPropertyInfo>();
        }

        if (document.NodeMap.FindById(id) is MutableAstObjectNode node)
        {
            return AddPropertyTypeSemantics(document, node, BuildPropertyInfo(document, node));
        }

        if (TryFindDesignItem(document, id, out IDesignItem? designItem) && designItem is not null)
        {
            return BuildPropertyInfo(designItem);
        }

        return Array.Empty<DesignerPropertyInfo>();
    }

    private IReadOnlyList<DesignerEventInfo> GetEventsCore(string nodeId)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed)
        {
            return Array.Empty<DesignerEventInfo>();
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return Array.Empty<DesignerEventInfo>();
        }

        if (document.NodeMap.FindById(id) is not MutableAstObjectNode node)
        {
            return Array.Empty<DesignerEventInfo>();
        }

        return BuildEventInfo(document, node);
    }

    private async Task<bool> SetPropertyCoreAsync(
        string nodeId, string propertyName, string? value, CancellationToken cancellationToken)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed || !document.SyncEngine.CanEditAst)
        {
            return false;
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return false;
        }

        if (document.NodeMap.FindById(id) is MutableAstObjectNode node)
        {
            TypeMetadata? type = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName);
            if (propertyName == "x:Name" || propertyName == "Name" && type?.IsControl == true
                || DesignerNameEditor.FindNameProperty(node, document.MetadataService)?.PropertyName == propertyName)
            {
                return await _mainViewModel.RenameDesignerControlAsync(document, node, value, cancellationToken);
            }

            if (GetEventsCore(nodeId).Any(item => string.Equals(item.Name, propertyName, StringComparison.Ordinal)))
            {
                return await _mainViewModel.SetDesignerEventHandlerAsync(document, node, propertyName, value, cancellationToken);
            }
            PropertyMetadata? property = type is null ? null : document.MetadataService?.GetProperties(type)
                .FirstOrDefault(candidate => candidate.Name == propertyName);
            if (property is not null) PropertyValueValidator.Validate(property, value);
            using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction($"Set {propertyName}", SyncSource.PropertyEditor);
            string localName = FindLocalProperty(document, node, propertyName)?.PropertyName ?? propertyName;
            node.SetPropertyValue(localName, value);
            edit.Commit();

            return true;
        }

        if (TryFindDesignItem(document, id, out IDesignItem? designItem) && designItem is not null)
        {
            using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction($"Set {propertyName}", SyncSource.PropertyEditor);
            designItem.SetProperty(propertyName, value);
            edit.Commit();

            return true;
        }

        return false;
    }

    private string? InsertElementCore(
        string typeName,
        string xmlNamespace,
        string? parentNodeId)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(xmlNamespace))
        {
            return null;
        }

        DesignerDocumentViewModel? document = GetActiveDocument();
        MutableAstDocument? currentDocument = document?.SyncEngine.CurrentDocument;
        if (document is null || document.IsDisposed || currentDocument?.Root is null || !document.SyncEngine.CanEditAst)
        {
            return null;
        }

        MutableAstObjectNode inserted = new()
        {
            TypeName = typeName,
            XmlNamespace = xmlNamespace
        };

        LayoutEditService editor = new(document.MetadataService, document.DesignSurface.AutoGenerateAutomationIds);
        MutableAstObjectNode? parent;
        if (!string.IsNullOrWhiteSpace(parentNodeId))
        {
            parent = Guid.TryParse(parentNodeId, out Guid targetId)
                ? document.NodeMap.FindById(targetId) as MutableAstObjectNode : null;
            if (parent is null) throw new InvalidOperationException(UiStrings.DesignerInsertionTargetGone);
        }
        else
        {
            parent = editor.FindInsertionParent(currentDocument.Root, ResolveParentNode(document, null), inserted);
        }
        if (parent is null) throw new InvalidOperationException(UiStrings.DesignerSelectLayoutTarget);

        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction($"Insert {typeName}");
        if (!editor.TryInsert(parent, inserted, out string? error)) throw new InvalidOperationException(error);
        edit.Commit();
        document.SetSelectedNode(inserted.Id, SyncSource.DesignSurface);

        return inserted.Id.ToString("D");
    }

    private bool DeleteNodeCore(string nodeId)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        MutableAstDocument? currentDocument = document?.SyncEngine.CurrentDocument;
        if (document is null || document.IsDisposed || currentDocument is null || !document.SyncEngine.CanEditAst)
        {
            return false;
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return false;
        }

        if (document.NodeMap.FindById(id) is not MutableAstObjectNode node)
        {
            return false;
        }

        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction($"Delete {node.TypeName}");
        if (!new LayoutEditService(document.MetadataService).TryRemove(node, out _)) return false;
        if (document.SelectedNodeId == id)
        {
            document.SetSelectedNode(null, SyncSource.DesignSurface);
        }

        edit.Commit();
        return true;
    }

    private string? WrapNodeCore(
        string nodeId,
        string wrapperTypeName,
        string wrapperXmlNamespace)
    {
        if (string.IsNullOrWhiteSpace(wrapperTypeName) || string.IsNullOrWhiteSpace(wrapperXmlNamespace))
        {
            return null;
        }

        DesignerDocumentViewModel? document = GetActiveDocument();
        MutableAstDocument? currentDocument = document?.SyncEngine.CurrentDocument;
        if (document is null || document.IsDisposed || currentDocument is null || !document.SyncEngine.CanEditAst)
        {
            return null;
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return null;
        }

        if (document.NodeMap.FindById(id) is not MutableAstObjectNode node)
        {
            return null;
        }

        MutableAstObjectNode wrapper = new()
        {
            TypeName = wrapperTypeName,
            XmlNamespace = wrapperXmlNamespace
        };

        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction($"Wrap with {wrapperTypeName}");
        if (!new LayoutEditService(document.MetadataService, document.DesignSurface.AutoGenerateAutomationIds)
            .TryWrap(node, wrapper, out string? error))
            throw new InvalidOperationException(error);

        edit.Commit();
        document.SetSelectedNode(wrapper.Id, SyncSource.DesignSurface);

        return wrapper.Id.ToString("D");
    }

    private bool SelectNodeCore(string nodeId)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed)
        {
            return false;
        }

        if (!Guid.TryParse(nodeId, out Guid id))
        {
            return false;
        }

        if (document.NodeMap.FindById(id) is not MutableAstObjectNode)
        {
            return false;
        }

        document.SetSelectedNode(id, SyncSource.TreeView);
        return true;
    }

    private IReadOnlyList<DesignerNodeSummary> GetSelectedNodesCore()
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed)
        {
            return Array.Empty<DesignerNodeSummary>();
        }

        return BuildSelectedNodes(document);
    }

    private IReadOnlyList<DesignerNodeSummary> GetCurrentTree(bool includeContentPreview)
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed)
        {
            return Array.Empty<DesignerNodeSummary>();
        }

        List<DesignerNodeSummary> results = new();
        MutableAstObjectNode? root = document.SyncEngine.CurrentDocument?.Root;
        if (root is not null)
        {
            Flatten(root, null, results, includeContentPreview);
            return results;
        }

        if (document.DesignSurface.RootItem is IDesignItem surfaceRoot)
        {
            Flatten(surfaceRoot, null, results, includeContentPreview);
        }

        return results;
    }

    private void HookSelection(DesignerDocumentViewModel? document)
    {
        CompositeDisposable subscription = new();
        if (document is not null && !document.IsDisposed)
        {
            IDisposable selectedNodeSubscription = document.WhenAnyValue(x => x.SelectedNodeId)
                .WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RaiseSelectionChanged());
            subscription.Add(selectedNodeSubscription);

            IDisposable syncSubscription = document.SyncEngine.SyncEvents
                .WitnessOn(RxSchedulers.MainThreadScheduler)
                .Subscribe(_ => RaiseSelectionChanged());
            subscription.Add(syncSubscription);

            var designSurfaceSelection = document.DesignSurface.Selection;

            void OnSelectionChanged(IReadOnlyList<IDesignItem> items)
            {
                RaiseSelectionChanged(items);
            }

            designSurfaceSelection.SelectionChanged += OnSelectionChanged;
            subscription.Add(Disposable.Create(() => designSurfaceSelection.SelectionChanged -= OnSelectionChanged));

            if (!ReferenceEquals(designSurfaceSelection, document.SelectionManager))
            {
                document.SelectionManager.SelectionChanged += OnSelectionChanged;
                subscription.Add(Disposable.Create(() => document.SelectionManager.SelectionChanged -= OnSelectionChanged));
            }
        }

        _selectionSubscription.Disposable = subscription;
    }

    private void RaiseSelectionChanged(IReadOnlyList<IDesignItem>? selectionItems = null)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = Dispatcher.UIThread.InvokeAsync(() => RaiseSelectionChanged(selectionItems), DispatcherPriority.Normal);
            return;
        }

        DesignerDocumentViewModel? document = GetActiveDocument();
        IReadOnlyList<DesignerNodeSummary> selectedNodes = document is null || document.IsDisposed
            ? Array.Empty<DesignerNodeSummary>()
            : BuildSelectedNodes(document, selectionItems);
        SelectionChanged?.Invoke(this, new DesignerSelectionChangedEventArgs(selectedNodes));
    }

    private void PublishActiveDocument(DesignerDocumentViewModel? document)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            _ = Dispatcher.UIThread.InvokeAsync(() => PublishActiveDocument(document), DispatcherPriority.Normal);
            return;
        }

        DesignerDocumentViewModel? cached = GetCachedDocument();
        if (ReferenceEquals(cached, document))
        {
            return;
        }

        UpdateCachedDocument(document);
        ActiveDocumentChanged?.Invoke(this, new DesignerDocumentChangedEventArgs(document?.FilePath));
        HookSelection(document);
        RaiseSelectionChanged();
    }

    private static IReadOnlyList<DesignerPropertyInfo> BuildPropertyInfo(
        DesignerDocumentViewModel document,
        MutableAstObjectNode node)
    {
        List<DesignerPropertyInfo> results = new();
        HashSet<string> propertyNames = new(StringComparer.OrdinalIgnoreCase);

        ITypeMetadataService? metadataService = document.MetadataService;
        TypeMetadata? meta = metadataService?.GetType(node.XmlNamespace, node.TypeName);
        if (metadataService is not null && meta is not null)
        {
            foreach (PropertyMetadata prop in metadataService.GetProperties(meta))
            {
                MutableAstPropertyNode? local = FindLocalProperty(document, node, prop.Name);
                string? value = (local?.Value as MutableAstTextNode)?.Text;
                IReadOnlyList<string>? enumOptions = prop.ClrType is { IsEnum: true }
                    ? Enum.GetNames(prop.ClrType)
                    : null;

                string? defaultValue = prop.DefaultValue is null
                    ? null
                    : Convert.ToString(prop.DefaultValue, CultureInfo.InvariantCulture);

                results.Add(new DesignerPropertyInfo(
                    prop.Name,
                    prop.TypeFullName,
                    value,
                    prop.IsReadOnly,
                    string.IsNullOrWhiteSpace(prop.Category) || prop.Category == "Misc"
                        ? CategorizeProperty(prop.Name, prop.DeclaringTypeName) : prop.Category,
                    prop.Description,
                    defaultValue,
                    prop.IsAttached,
                    prop.OwnerType,
                    enumOptions) { HasLocalValue = local is not null });
                propertyNames.Add(prop.Name);
                if (local is not null) propertyNames.Add(local.PropertyName);
            }
        }

        foreach (MutableAstPropertyNode property in node.Properties)
        {
            if (propertyNames.Contains(property.PropertyName))
            {
                continue;
            }

            string? value = (property.Value as MutableAstTextNode)?.Text;
            results.Add(new DesignerPropertyInfo(
                property.PropertyName,
                "string",
                value,
                IsReadOnly: false,
                Category: CategorizeProperty(property.PropertyName),
                Description: null,
                DefaultValue: null,
                IsAttached: false,
                OwnerType: null,
                EnumOptions: null) { HasLocalValue = true });
            propertyNames.Add(property.PropertyName);
        }

        results.Sort(CompareProperties);
        return results;
    }

    private static IReadOnlyList<DesignerPropertyInfo> BuildPropertyInfo(IDesignItem designItem)
    {
        List<DesignerPropertyInfo> results = new();
        foreach (IPropertyDescriptor descriptor in designItem.Properties)
        {
            string? defaultValue = descriptor.DefaultValue is null
                ? null
                : Convert.ToString(descriptor.DefaultValue, CultureInfo.InvariantCulture);

            IReadOnlyList<string>? enumOptions = descriptor.PropertyType.IsEnum
                ? Enum.GetNames(descriptor.PropertyType)
                : null;

            results.Add(new DesignerPropertyInfo(
                descriptor.Name,
                descriptor.PropertyType.FullName ?? descriptor.PropertyType.Name,
                designItem.GetProperty(descriptor.Name),
                descriptor.IsReadOnly,
                string.IsNullOrWhiteSpace(descriptor.Category) || descriptor.Category == "Misc"
                    ? CategorizeProperty(descriptor.Name) : descriptor.Category,
                descriptor.Description,
                defaultValue,
                IsAttached: descriptor.Name.Contains('.'),
                OwnerType: null,
                enumOptions));
        }

        results.Sort(CompareProperties);
        return results;
    }

    private static IReadOnlyList<DesignerEventInfo> BuildEventInfo(
        DesignerDocumentViewModel document,
        MutableAstObjectNode node)
    {
        ITypeMetadataService? metadataService = document.MetadataService;
        TypeMetadata? meta = metadataService?.GetType(node.XmlNamespace, node.TypeName);
        if (metadataService is null || meta is null)
        {
            return Array.Empty<DesignerEventInfo>();
        }

        List<DesignerEventInfo> results = new();
        foreach (EventMetadata evt in metadataService.GetEvents(meta))
        {
            results.Add(new DesignerEventInfo(
                evt.Name,
                node.GetPropertyValue(evt.Name),
                evt.Description));
        }

        results.Sort(CompareEvents);
        return results;
    }

    private static int CompareProperties(DesignerPropertyInfo left, DesignerPropertyInfo right)
    {
        int category = string.Compare(
            left.Category ?? string.Empty,
            right.Category ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
        if (category != 0)
        {
            return category;
        }

        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static int CompareEvents(DesignerEventInfo left, DesignerEventInfo right)
    {
        return string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static MutableAstObjectNode? ResolveParentNode(DesignerDocumentViewModel document, string? parentNodeId)
    {
        if (!string.IsNullOrWhiteSpace(parentNodeId)
            && Guid.TryParse(parentNodeId, out Guid explicitParentId)
            && document.NodeMap.FindById(explicitParentId) is MutableAstObjectNode explicitParent)
        {
            return explicitParent;
        }

        if (document.SelectedNodeId is Guid selectedId
            && document.NodeMap.FindById(selectedId) is MutableAstObjectNode selectedParent)
        {
            return selectedParent;
        }

        return null;
    }

    private static void Flatten(
        MutableAstObjectNode node,
        string? parentNodeId,
        List<DesignerNodeSummary> output,
        bool includeContentPreview)
    {
        string? displayName = GetDisplayName(
            node.GetPropertyValue("x:Name") ?? node.GetPropertyValue("Name"),
            includeContentPreview
                ? node.GetPropertyValue("Content") ?? node.GetPropertyValue("Text")
                : null);

        output.Add(new DesignerNodeSummary(
            node.Id.ToString("D"),
            node.TypeName,
            displayName,
            parentNodeId,
            node.Children.Count));

        string nodeId = node.Id.ToString("D");
        foreach (MutableAstNode child in node.Children)
        {
            if (child is MutableAstObjectNode objectNode)
            {
                Flatten(objectNode, nodeId, output, includeContentPreview);
            }
        }
    }

    private static void Flatten(
        IDesignItem node,
        string? parentNodeId,
        List<DesignerNodeSummary> output,
        bool includeContentPreview)
    {
        string nodeId = node.AstNodeId.ToString("D");
        string? displayName = GetDisplayName(
            node.GetProperty("x:Name") ?? node.GetProperty("Name"),
            includeContentPreview
                ? node.GetProperty("Content") ?? node.GetProperty("Text")
                : null);

        output.Add(new DesignerNodeSummary(
            nodeId,
            node.TypeName,
            displayName,
            parentNodeId,
            node.Children.Count));

        foreach (IDesignItem child in node.Children)
        {
            Flatten(child, nodeId, output, includeContentPreview);
        }
    }

    private static IReadOnlyList<DesignerNodeSummary> BuildSelectedNodes(
        DesignerDocumentViewModel document,
        IReadOnlyList<IDesignItem>? selectionItems = null)
    {
        List<DesignerNodeSummary> nodes = new();
        IReadOnlyList<IDesignItem> selectedItems = selectionItems is { Count: > 0 }
            ? selectionItems
            : document.DesignSurface.Selection.SelectedItems;
        if (selectedItems.Count == 0 && !ReferenceEquals(document.SelectionManager, document.DesignSurface.Selection))
        {
            selectedItems = document.SelectionManager.SelectedItems;
        }
        if (selectedItems.Count > 0)
        {
            foreach (IDesignItem selected in selectedItems)
            {
                string? parentNodeId = selected.Parent?.AstNodeId.ToString("D");

                nodes.Add(new DesignerNodeSummary(
                    selected.AstNodeId.ToString("D"),
                    selected.TypeName,
                    selected.GetProperty("x:Name") ?? selected.GetProperty("Name"),
                    parentNodeId,
                    selected.Children.Count));
            }
        }
        else if (document.SelectedNodeId is Guid nodeId
                 && document.NodeMap.FindById(nodeId) is MutableAstObjectNode selectedNode)
        {
            nodes.Add(CreateSummary(selectedNode));
        }
        else if (document.SyncEngine.CurrentDocument?.Root is MutableAstObjectNode rootNode)
        {
            nodes.Add(CreateSummary(rootNode));
        }

        return nodes;
    }

    private static bool TryFindDesignItem(DesignerDocumentViewModel document, Guid nodeId, out IDesignItem? designItem)
    {
        if (document.DesignSurface.ItemMap.TryGetValue(nodeId, out XamlVisualEditor.Designer.Core.DesignItem? mapped))
        {
            designItem = mapped;
            return true;
        }

        foreach (IDesignItem selected in document.DesignSurface.Selection.SelectedItems)
        {
            if (selected.AstNodeId == nodeId)
            {
                designItem = selected;
                return true;
            }
        }

        if (document.DesignSurface.Selection.PrimarySelection is IDesignItem primary && primary.AstNodeId == nodeId)
        {
            designItem = primary;
            return true;
        }

        designItem = null;
        return false;
    }

    private DesignerDocumentViewModel? GetActiveDocument()
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            DesignerDocumentViewModel? resolved = ResolveCurrentDocument(
                _mainViewModel.ActiveDesignerDocument,
                _mainViewModel.ActiveDocument);
            if (resolved is not null && !resolved.IsDisposed)
            {
                UpdateCachedDocument(resolved);
                return resolved;
            }

            resolved = ResolveFromMainWindowDataContext();
            if (resolved is not null && !resolved.IsDisposed)
            {
                UpdateCachedDocument(resolved);
                return resolved;
            }
        }

        DesignerDocumentViewModel? cached = GetCachedDocument();
        return cached is not { IsDisposed: false } ? null : cached;
    }

    private DesignerDocumentViewModel? ResolveCurrentDocument(
        DesignerDocumentViewModel? activeDesignerDocument,
        IEditorDocumentViewModel? activeDocument)
    {
        if (activeDesignerDocument is not null && !activeDesignerDocument.IsDisposed)
        {
            return activeDesignerDocument;
        }

        if (activeDocument is DesignerDocumentViewModel activeDesigner && !activeDesigner.IsDisposed)
        {
            return activeDesigner;
        }

        return _mainViewModel.Documents
            .OfType<DesignerDocumentViewModel>()
            .FirstOrDefault(d => !d.IsDisposed);
    }

    private DesignerDocumentViewModel? GetCachedDocument()
    {
        lock (_stateGate)
        {
            return _currentDocument;
        }
    }

    private string? GetCachedActiveDocumentPath()
    {
        lock (_stateGate)
        {
            return _lastActiveDocumentPath;
        }
    }

    private void UpdateCachedDocument(DesignerDocumentViewModel? document)
    {
        lock (_stateGate)
        {
            _currentDocument = document;
            _lastActiveDocumentPath = document?.FilePath;
        }
    }

    private DesignerDocumentViewModel? ResolveFromMainWindowDataContext()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            return null;
        }

        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return null;
        }

        if (desktop.MainWindow?.DataContext is not MainWindowViewModel windowVm)
        {
            return null;
        }

        if (windowVm.ActiveDesignerDocument is { IsDisposed: false } activeDesigner)
        {
            return activeDesigner;
        }

        if (windowVm.ActiveDocument is DesignerDocumentViewModel activeDocument && !activeDocument.IsDisposed)
        {
            return activeDocument;
        }

        return windowVm.Documents
            .OfType<DesignerDocumentViewModel>()
            .FirstOrDefault(d => !d.IsDisposed);
    }

    private static DesignerNodeSummary CreateSummary(MutableAstObjectNode node)
    {
        string? parentNodeId = node.Parent is MutableAstObjectNode parent ? parent.Id.ToString("D") : null;
        return new DesignerNodeSummary(
            node.Id.ToString("D"),
            node.TypeName,
            node.GetPropertyValue("x:Name") ?? node.GetPropertyValue("Name"),
            parentNodeId,
            node.Children.Count);
    }

    private static string? GetDisplayName(string? name, string? contentPreview)
    {
        if (string.IsNullOrWhiteSpace(contentPreview))
        {
            return name;
        }

        string normalized = contentPreview.Trim();
        if (normalized.Length > 30)
        {
            normalized = normalized[..30] + "...";
        }

        return string.IsNullOrWhiteSpace(name)
            ? $"\"{normalized}\""
            : $"{name} \"{normalized}\"";
    }

    private static string CategorizeProperty(string propertyName, string? declaringTypeName = null)
    {
        return propertyName switch
        {
            "Width" or "Height" or "MinWidth" or "MinHeight" or "MaxWidth" or "MaxHeight"
                or "Margin" or "Padding" or "HorizontalAlignment" or "VerticalAlignment"
                or "Row" or "Column" or "RowSpan" or "ColumnSpan"
                or "DockPanel.Dock" or "Canvas.Left" or "Canvas.Top"
                or "Canvas.Right" or "Canvas.Bottom"
                => "Layout",

            "Background" or "Foreground" or "BorderBrush" or "BorderThickness"
                or "CornerRadius" or "Opacity" or "IsVisible" or "ClipToBounds"
                or "RenderTransform" or "RenderTransformOrigin"
                => "Appearance",

            "FontFamily" or "FontSize" or "FontWeight" or "FontStyle"
                or "TextAlignment" or "TextWrapping" or "TextDecorations"
                or "TextTrimming"
                => "Text",

            "Name" or "x:Name" or "Classes" or "Tag" or "DataContext"
                => "Miscellaneous",

            _ => declaringTypeName ?? "Common"
        };
    }

    private sealed class ShellDesignerTransaction : IDesignerTransaction
    {
        private readonly DocumentEditTransaction? _transaction;

        public ShellDesignerTransaction(DesignerDocumentViewModel? document, string name)
        {
            if (document?.SyncEngine.CanEditAst == true)
            {
                _transaction = document.SyncEngine.BeginEditTransaction(
                    string.IsNullOrWhiteSpace(name) ? "Designer Transaction" : name);
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _transaction?.Commit();
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            _transaction?.Rollback();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _transaction?.Dispose();
        }
    }
}
