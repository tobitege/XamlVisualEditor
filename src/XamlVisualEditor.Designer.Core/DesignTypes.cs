using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.Core;

public sealed record DesignerLayoutAction(string Title, string Description, ReactiveCommand<Unit, Unit> Command)
{
    public Avalonia.Media.Geometry? Icon { get; init; }
}

/// <summary>
/// Concrete implementation of <see cref="IDesignItem"/> that bridges
/// an AST node to its instantiated Avalonia control.
/// </summary>
public sealed partial class DesignItem : ReactiveObject, IDesignItem
{
    private readonly MutableAstObjectNode _astNode;
    private readonly List<DesignItem> _children = new();
    private readonly List<IPropertyDescriptor> _properties = new();

    /// <summary>
    /// Creates a new design item for the specified AST node.
    /// </summary>
    public DesignItem(MutableAstObjectNode astNode, Control? visualElement = null)
    {
        _astNode = astNode;
        VisualElement = visualElement;
    }

    /// <inheritdoc />
    public Guid AstNodeId => _astNode.Id;

    /// <summary>
    /// Gets the underlying AST node.
    /// </summary>
    public MutableAstObjectNode AstNode => _astNode;

    /// <inheritdoc />
    public string TypeName => _astNode.TypeName;

    /// <summary>
    /// Gets or sets the instantiated Avalonia control.
    /// </summary>
    [Reactive]
    public partial Control? VisualElement { get; set; }

    /// <summary>Identifies one live instance in an external preview.</summary>
    public int? HostInstanceId { get; init; }

    /// <summary>Measured bounds in the external host's viewport coordinate space.</summary>
    public Rect? HostBounds { get; init; }

    /// <inheritdoc />
    public IDesignItem? Parent { get; internal set; }

    /// <inheritdoc />
    public IReadOnlyList<IDesignItem> Children => _children;

    /// <inheritdoc />
    public IReadOnlyList<IPropertyDescriptor> Properties => _properties;

    /// <summary>
    /// Gets the bounds of the visual element relative to its parent.
    /// For surface-relative bounds, use <see cref="GetBoundsRelativeTo"/>.
    /// </summary>
    public Rect Bounds => VisualElement?.Bounds ?? HostBounds ?? default;

    /// <summary>
    /// Gets the bounds of the visual element transformed to the coordinate space
    /// of the specified ancestor control (typically the design surface root).
    /// Falls back to <see cref="Bounds"/> if transformation is not possible.
    /// </summary>
    public Rect GetBoundsRelativeTo(Control? ancestor)
    {
        if (VisualElement is null || ancestor is null)
        {
            return Bounds;
        }

        if (VisualElement.TransformToVisual(ancestor) is not { } transform)
        {
            return Bounds;
        }

        return new Rect(VisualElement.Bounds.Size).TransformToAABB(transform);
    }

    /// <summary>
    /// Gets whether this item is currently selected.
    /// </summary>
    [Reactive]
    public partial bool IsSelected { get; set; }

    /// <inheritdoc />
    public void SetProperty(string name, string? value)
    {
        _astNode.SetPropertyValue(name, value);
    }

    /// <inheritdoc />
    public string? GetProperty(string name)
    {
        return _astNode.GetPropertyValue(name);
    }

    /// <summary>
    /// Adds a child design item.
    /// </summary>
    public void AddChild(DesignItem child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>Inserts a child at its logical content position.</summary>
    public void InsertChild(int index, DesignItem child)
    {
        child.Parent = this;
        _children.Insert(index, child);
    }

    /// <summary>
    /// Removes a child design item.
    /// </summary>
    public bool RemoveChild(DesignItem child)
    {
        child.Parent = null;
        return _children.Remove(child);
    }

    /// <summary>
    /// Sets the property descriptors for this item.
    /// </summary>
    public void SetProperties(IEnumerable<IPropertyDescriptor> descriptors)
    {
        _properties.Clear();
        _properties.AddRange(descriptors);
    }
}

/// <summary>
/// Manages the selection state on the design surface.
/// </summary>
public sealed partial class SelectionManager : ReactiveObject
{
    private readonly List<IDesignItem> _selectedItems = new();
    private int _suppressSelectionChanged;

    /// <summary>
    /// Gets the currently selected items.
    /// </summary>
    public IReadOnlyList<IDesignItem> SelectedItems => _selectedItems;

    /// <summary>
    /// Gets the primary (first) selected item.
    /// </summary>
    [Reactive]
    public partial IDesignItem? PrimarySelection { get; private set; }

    /// <summary>
    /// Fires when the selection changes.
    /// </summary>
    public event Action<IReadOnlyList<IDesignItem>>? SelectionChanged;

    /// <summary>
    /// Suppresses selection changed notifications until the returned scope is disposed.
    /// </summary>
    public IDisposable SuppressSelectionChanged()
    {
        _suppressSelectionChanged++;
        return new SelectionChangedScope(this);
    }

    /// <summary>
    /// Selects a single item, optionally adding to the selection.
    /// </summary>
    public void Select(IDesignItem item, bool addToSelection = false)
    {
        if (!addToSelection)
        {
            ClearSelectionInternal();
        }

        if (!_selectedItems.Contains(item))
        {
            _selectedItems.Add(item);
            if (item is DesignItem di)
            {
                di.IsSelected = true;
            }
        }

        PrimarySelection = _selectedItems.Count > 0 ? _selectedItems[0] : null;
        RaiseSelectionChanged();
    }

    /// <summary>Replaces the selected items and publishes their complete state once.</summary>
    public void SetSelection(IEnumerable<IDesignItem> items)
    {
        IDesignItem[] selected = items.Distinct().ToArray();
        ClearSelectionInternal();
        foreach (IDesignItem item in selected)
        {
            _selectedItems.Add(item);
            if (item is DesignItem designItem) designItem.IsSelected = true;
        }
        PrimarySelection = _selectedItems.Count > 0 ? _selectedItems[0] : null;
        RaiseSelectionChanged();
    }

    /// <summary>
    /// Clears the selection.
    /// </summary>
    public void ClearSelection()
    {
        ClearSelectionInternal();
        PrimarySelection = null;
        RaiseSelectionChanged();
    }

    /// <summary>
    /// Toggles selection of an item.
    /// </summary>
    public void ToggleSelection(IDesignItem item)
    {
        if (_selectedItems.Contains(item))
        {
            _selectedItems.Remove(item);
            if (item is DesignItem di)
            {
                di.IsSelected = false;
            }
        }
        else
        {
            _selectedItems.Add(item);
            if (item is DesignItem di)
            {
                di.IsSelected = true;
            }
        }

        PrimarySelection = _selectedItems.Count > 0 ? _selectedItems[0] : null;
        RaiseSelectionChanged();
    }

    private void RaiseSelectionChanged()
    {
        if (_suppressSelectionChanged == 0)
        {
            SelectionChanged?.Invoke(_selectedItems);
        }
    }

    private void ClearSelectionInternal()
    {
        foreach (IDesignItem item in _selectedItems)
        {
            if (item is DesignItem di)
            {
                di.IsSelected = false;
            }
        }
        _selectedItems.Clear();
    }

    private sealed partial class SelectionChangedScope : IDisposable
    {
        private SelectionManager? _owner;

        public SelectionChangedScope(SelectionManager owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner._suppressSelectionChanged = Math.Max(0, _owner._suppressSelectionChanged - 1);
            _owner = null;
        }
    }
}

/// <summary>
/// ViewModel for the design surface.
/// </summary>
public sealed partial class DesignSurfaceViewModel : ReactiveObject
{
    private int _selectionSyncDepth;
    /// <summary>
    /// Gets the selection manager.
    /// </summary>
    public SelectionManager Selection { get; } = new();

    /// <summary>
    /// Gets or sets the root design item.
    /// </summary>
    [Reactive]
    public partial DesignItem? RootItem { get; set; }

    private IReadOnlyDictionary<Guid, DesignItem> _itemMap = new Dictionary<Guid, DesignItem>();
    private IReadOnlyDictionary<Control, DesignItem> _controlMap = new Dictionary<Control, DesignItem>();

    /// <summary>
    /// Gets the design items indexed by AST node id.
    /// </summary>
    public IReadOnlyDictionary<Guid, DesignItem> ItemMap => _itemMap;

    /// <summary>
    /// Gets the design items indexed by control instance.
    /// </summary>
    public IReadOnlyDictionary<Control, DesignItem> ControlMap => _controlMap;

    /// <summary>
    /// Gets whether selection changes are being applied from sync.
    /// </summary>
    public bool IsSelectionSyncing => _selectionSyncDepth > 0;

    /// <summary>
    /// Gets or sets the zoom level (1.0 = 100%).
    /// </summary>
    public double Zoom
    {
        get => _zoom;
        set => this.RaiseAndSetIfChanged(ref _zoom, Math.Clamp(double.IsNaN(value) ? 1 : value, MinimumZoom, MaximumZoom));
    }

    public const double MinimumZoom = 0.25;
    public const double MaximumZoom = 3.0;
    public const double ZoomStep = 0.25;
    private double _zoom = 1.0;

    public static double StepZoom(double zoom, int direction)
    {
        zoom = Math.Clamp(double.IsNaN(zoom) ? 1 : zoom, MinimumZoom, MaximumZoom);
        double tick = Math.Round(zoom / ZoomStep, 8);
        double next = direction > 0 ? Math.Floor(tick) + 1 : direction < 0 ? Math.Ceiling(tick) - 1 : tick;
        return Math.Clamp(next * ZoomStep, MinimumZoom, MaximumZoom);
    }

    /// <summary>
    /// Gets or sets whether snap lines are enabled.
    /// </summary>
    [Reactive]
    public partial bool SnapLinesEnabled { get; set; } = true;

    /// <summary>
    /// Gets or sets whether alignment guides are visible.
    /// </summary>
    [Reactive]
    public partial bool ShowAlignmentGuides { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the surface is in edit mode (non-interactive preview).
    /// </summary>
    [Reactive]
    public partial bool IsEditMode { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the designer is creating the control tree.
    /// </summary>
    [Reactive]
    public partial bool IsRebuilding { get; set; }

    /// <summary>
    /// Gets or sets the visible phase text for a design-surface rebuild.
    /// </summary>
    [Reactive]
    public partial string RebuildStatus { get; set; } = "Building design surface...";

    /// <summary>
    /// Gets or sets whether the grid is visible.
    /// </summary>
    [Reactive]
    public partial bool ShowGrid { get; set; }

    /// <summary>
    /// Gets or sets whether spacing guides are visible.
    /// </summary>
    [Reactive]
    public partial bool ShowSpacingGuides { get; set; } = true;

    /// <summary>
    /// Gets or sets whether rulers are visible.
    /// </summary>
    [Reactive]
    public partial bool ShowRulers { get; set; } = true;

    /// <summary>
    /// Gets the available theme variants for preview.
    /// </summary>
    public IReadOnlyList<ThemeVariant> ThemeVariants { get; } = new List<ThemeVariant>
    {
        ThemeVariant.Default,
        ThemeVariant.Light,
        ThemeVariant.Dark
    };

    /// <summary>
    /// Gets or sets the selected theme variant for preview.
    /// </summary>
    [Reactive]
    public partial ThemeVariant PreviewThemeVariant { get; set; } = ThemeVariant.Default;

    /// <summary>
    /// Gets or sets whether guide lines are visible.
    /// </summary>
    [Reactive]
    public partial bool ShowGuides { get; set; } = true;

    /// <summary>
    /// Gets or sets whether snapping to guides is enabled.
    /// </summary>
    [Reactive]
    public partial bool SnapToGuides { get; set; } = true;

    /// <summary>
    /// Gets or sets whether margin/padding hints are visible.
    /// </summary>
    [Reactive]
    public partial bool ShowMarginPadding { get; set; }

    /// <summary>
    /// Gets or sets the unscaled editor space around the artboard, in pixels.
    /// </summary>
    [Reactive]
    public partial double CanvasGap { get; set; } = 8.0;

    /// <summary>Assigns unique automation IDs when visual editing creates controls.</summary>
    [Reactive]
    public partial bool AutoGenerateAutomationIds { get; set; }

    /// <summary>
    /// Gets or sets whether snap to grid is enabled.
    /// </summary>
    [Reactive]
    public partial bool SnapToGrid { get; set; } = true;

    /// <summary>
    /// Gets or sets the grid snap size.
    /// </summary>
    [Reactive]
    public partial double GridSnapSize { get; set; } = 8.0;

    /// <summary>
    /// Gets the horizontal guide positions (in surface coordinates).
    /// </summary>
    public ObservableCollection<double> HorizontalGuides { get; } = new();

    /// <summary>
    /// Gets the vertical guide positions (in surface coordinates).
    /// </summary>
    public ObservableCollection<double> VerticalGuides { get; } = new();

    /// <summary>
    /// Gets or sets the canvas width.
    /// </summary>
    [Reactive]
    public partial double CanvasWidth { get; set; } = 800;

    /// <summary>
    /// Gets or sets the canvas height.
    /// </summary>
    [Reactive]
    public partial double CanvasHeight { get; set; } = 600;

    /// <summary>
    /// Gets or sets the design-time background color.
    /// </summary>
    [Reactive]
    public partial string DesignBackground { get; set; } = "White";

    /// <summary>
    /// Command to zoom in.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ZoomInCommand { get; }

    /// <summary>
    /// Command to zoom out.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ZoomOutCommand { get; }

    /// <summary>
    /// Command to reset zoom.
    /// </summary>
    public ReactiveCommand<Unit, Unit> ResetZoomCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectNextControlCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectPreviousControlCommand { get; }
    public ReactiveCommand<Unit, Unit> SelectParentControlCommand { get; }

    /// <summary>
    /// Document commands supplied by the shell's transactional editing path.
    /// </summary>
    public ReactiveCommand<Unit, Unit>? DeleteSelectedCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? CutCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? CopyCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? PasteCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? DuplicateCommand { get; set; }
    public ReactiveCommand<Unit, Unit>? SelectAllCommand { get; set; }
    public ObservableCollection<DesignerLayoutAction> LayoutActions { get; } = new();

    private IReadOnlyList<Guid>? _requestedSelection;

    /// <summary>
    /// Fires when the design surface needs to rebuild (e.g., after AST changes).
    /// </summary>
    public event Action? RebuildRequested;

    public DesignSurfaceViewModel()
    {
        ZoomInCommand = ReactiveCommand.Create(() => { Zoom = StepZoom(Zoom, 1); });
        ZoomOutCommand = ReactiveCommand.Create(() => { Zoom = StepZoom(Zoom, -1); });
        ResetZoomCommand = ReactiveCommand.Create(() => { Zoom = 1.0; });
        SelectNextControlCommand = ReactiveCommand.Create(() => NavigateSelection(backwards: false));
        SelectPreviousControlCommand = ReactiveCommand.Create(() => NavigateSelection(backwards: true));
        SelectParentControlCommand = ReactiveCommand.Create(SelectParentControl);
    }

    /// <summary>
    /// Updates the design tree and lookup maps.
    /// </summary>
    public void SetDesignTree(
        DesignItem? root,
        IReadOnlyDictionary<Guid, DesignItem> itemMap,
        IReadOnlyDictionary<Control, DesignItem> controlMap)
    {
        RootItem = root;
        _itemMap = itemMap;
        _controlMap = controlMap;
    }

    /// <summary>Restores concrete instances without publishing a new user selection.</summary>
    public void SelectDesignItemsFromSync(IEnumerable<IDesignItem> items)
    {
        using (BeginSelectionSync())
        {
            Selection.SetSelection(items);
        }
    }

    /// <summary>
    /// Selects a design item by AST node id if available.
    /// </summary>
    public void SelectByAstNodeId(Guid nodeId)
    {
        if (_itemMap.TryGetValue(nodeId, out DesignItem? item) && item is not null)
        {
            Selection.Select(item);
        }
        else
        {
            Selection.ClearSelection();
        }
    }

    /// <summary>
    /// Selects a design item by AST node id while marking the change as synced.
    /// </summary>
    public void SelectByAstNodeIdFromSync(Guid nodeId)
    {
        using (BeginSelectionSync())
        {
            SelectByAstNodeId(nodeId);
        }
    }

    /// <summary>Restores a selection without treating it as a new user selection.</summary>
    public void SelectNodesFromSync(IEnumerable<Guid> nodeIds)
    {
        using (BeginSelectionSync())
        {
            Selection.ClearSelection();
            foreach (Guid id in nodeIds)
                if (_itemMap.TryGetValue(id, out DesignItem? item)) Selection.Select(item, addToSelection: true);
        }
    }

    /// <summary>Selects newly inserted elements after their controls have been created.</summary>
    public void RequestSelection(IEnumerable<Guid> nodeIds)
    {
        _requestedSelection = nodeIds.Distinct().ToArray();
        RequestRebuild();
    }

    /// <summary>Consumes the pending selection for the next completed surface rebuild.</summary>
    public IReadOnlyList<Guid>? TakeRequestedSelection()
    {
        IReadOnlyList<Guid>? selection = _requestedSelection;
        _requestedSelection = null;
        return selection;
    }

    /// <summary>
    /// Clears the selection while marking the change as synced.
    /// </summary>
    public void ClearSelectionFromSync()
    {
        using (BeginSelectionSync())
        {
            Selection.ClearSelection();
        }
    }

    private IDisposable BeginSelectionSync()
    {
        _selectionSyncDepth++;
        return new SelectionSyncScope(this);
    }

    /// <summary>
    /// Requests a full rebuild of the design surface from the AST.
    /// </summary>
    public void RequestRebuild()
    {
        RebuildRequested?.Invoke();
    }

    private void NavigateSelection(bool backwards)
    {
        MutableAstObjectNode? root = _itemMap.Values.FirstOrDefault(item => item.AstNode.Parent is null)?.AstNode;
        if (root is null) return;
        Dictionary<Guid, DesignItem> visible = new();
        foreach (var pair in _controlMap)
            if (pair.Key.IsEffectivelyVisible) visible.TryAdd(pair.Value.AstNodeId, pair.Value);
        List<DesignItem> ordered = new();
        Stack<MutableAstNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstPropertyNode property && property.Value is { } value) { pending.Push(value); continue; }
            if (node is not MutableAstObjectNode element) continue;
            if (visible.TryGetValue(element.Id, out DesignItem? item)) ordered.Add(item);
            for (int index = element.Children.Count - 1; index >= 0; index--) pending.Push(element.Children[index]);
            for (int index = element.Properties.Count - 1; index >= 0; index--) pending.Push(element.Properties[index]);
        }
        if (ordered.Count == 0) return;
        Guid? selectedId = (Selection.PrimarySelection as DesignItem)?.AstNodeId;
        int selected = ordered.FindIndex(item => item.AstNodeId == selectedId);
        int next = selected < 0 ? (backwards ? ordered.Count - 1 : 0)
            : (selected + (backwards ? -1 : 1) + ordered.Count) % ordered.Count;
        Selection.Select(ordered[next]);
    }

    private void SelectParentControl()
    {
        if (Selection.PrimarySelection is DesignItem selected)
        {
            for (MutableAstNode? parent = selected.AstNode.Parent; parent is not null; parent = parent.Parent)
            {
                if (!_itemMap.TryGetValue(parent.Id, out DesignItem? item)) continue;
                Selection.Select(item);
                return;
            }
        }
        Selection.ClearSelection();
    }

    private sealed partial class SelectionSyncScope : IDisposable
    {
        private DesignSurfaceViewModel? _owner;

        public SelectionSyncScope(DesignSurfaceViewModel owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (_owner is null)
            {
                return;
            }

            _owner._selectionSyncDepth = Math.Max(0, _owner._selectionSyncDepth - 1);
            _owner = null;
        }
    }
}
