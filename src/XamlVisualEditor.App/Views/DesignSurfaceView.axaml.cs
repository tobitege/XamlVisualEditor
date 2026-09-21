using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Serilog;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.ComponentModel;
using System.Reactive;
using System.Windows.Input;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;

using static XamlVisualEditor.Designer.Core.AlignmentSnapping;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Code-behind for the design surface view.
/// Wires the DesignSurfaceViewModel.RebuildRequested event to re-create the
/// control tree from the current AST using ControlFactory.
/// Handles drag-and-drop from the toolbox to create new controls.
/// Defers rebuild until the canvas panel is attached to the visual tree.
/// </summary>
public sealed partial class DesignSurfaceView : UserControl
{
    private DesignSurfaceViewModel? _currentVm;
    private bool _isLoaded;
    private bool _rebuildPending;
    private bool _rebuildScheduled;
    private Panel? _canvas;
    private Control? _rootControl;
    private Control? _zoomSurface;
    private TopLevel? _topLevel;
    private const double MinZoom = DesignSurfaceViewModel.MinimumZoom;
    private const double MaxZoom = DesignSurfaceViewModel.MaximumZoom;
    private DesignAdornerLayer? _adornerLayer;
    private ScrollViewer? _scrollViewer;
    private Border? _surfaceBorder;
    private int _appliedWorkspaceThemeVersion = -1;
    private Control? _workspaceThemeTarget;
    private readonly List<IStyle> _workspaceThemeStyles = new();
    private readonly List<IResourceProvider> _workspaceThemeResources = new();
    private readonly List<Avalonia.Controls.Templates.IDataTemplate> _workspaceDataTemplates = new();
    private RulerControl? _horizontalRuler;
    private RulerControl? _verticalRuler;
    private IDesignItem? _hoveredItem;
    private DragState? _dragState;
    private MarqueeState? _marqueeState;
    private Guid? _marqueeScopeId;
    private IPointer? _selectionPointer;
    private Guid[] _selectionBeforePointerDown = Array.Empty<Guid>();
    private bool _isSpaceDown;
    private bool _isPanning;
    private Point _panStart;
    private Vector _panStartOffset;
    private readonly SurfaceDropHandler _surfaceDropHandler = new();
    private NotifyCollectionChangedEventHandler? _guideChangedHandler;
    private readonly ContextMenu _surfaceContextMenu;
    private readonly MenuItem _openDefinitionMenuItem;

    public DesignSurfaceView()
    {
        InitializeComponent();
        Focusable = true;
        AddHandler(PointerPressedEvent, (_, args) =>
        {
            if (_currentVm?.IsEditMode != true) return;
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _selectionPointer = args.Pointer;
                _selectionBeforePointerDown = _currentVm.Selection.SelectedItems.OfType<DesignItem>()
                    .Select(item => item.AstNodeId).ToArray();
            }
            Focus();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        _openDefinitionMenuItem = new MenuItem { Header = UiStrings.OpenDefinition };
        _openDefinitionMenuItem.Click += OnOpenDefinitionClick;
        _surfaceContextMenu = new ContextMenu();
        _surfaceContextMenu.Items.Add(_openDefinitionMenuItem);

        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        LayoutUpdated += (_, _) => UpdateRulerOffsets();

        // Wire drag-and-drop handlers (listen even if handled by inner controls)
        AddHandler(
            DragDrop.DragOverEvent,
            OnDragOver,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        AddHandler(
            DragDrop.DropEvent,
            OnDrop,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => _adornerLayer?.UpdateDropTarget(null, null));

        AddHandler(
            KeyDownEvent,
            OnKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        AddHandler(
            KeyUpEvent,
            OnKeyUp,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);

        AddHandler(
            PointerWheelChangedEvent,
            OnPointerWheelChanged,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);

        DetachedFromLogicalTree += OnDetachedFromLogicalTree;
    }

    private void OnDetachedFromLogicalTree(object? sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        CancelDragState();
        if (_rootControl is not null) _rootControl.LayoutUpdated -= OnRuntimeLayoutUpdated;
        if (_topLevel is null)
        {
            return;
        }

        _topLevel.RemoveHandler(KeyDownEvent, OnKeyDown);
        _topLevel.RemoveHandler(KeyUpEvent, OnKeyUp);
        _topLevel = null;
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isLoaded = true;

        _canvas = this.FindControl<Panel>("DesignCanvas");
        _zoomSurface = this.FindControl<Control>("ZoomSurface");
        _adornerLayer = this.FindControl<DesignAdornerLayer>("AdornerLayer");
        _scrollViewer = this.FindControl<ScrollViewer>("SurfaceScrollViewer");
        _surfaceBorder = this.FindControl<Border>("SurfaceBorder");
        _horizontalRuler = this.FindControl<RulerControl>("HorizontalRuler");
        _verticalRuler = this.FindControl<RulerControl>("VerticalRuler");
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnKeyDown,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        _topLevel?.AddHandler(KeyUpEvent, OnKeyUp,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        if (_canvas is not null)
        {
            if (_canvas.Background is null) _canvas.SetCurrentValue(Panel.BackgroundProperty, Avalonia.Media.Brushes.Transparent);
            _canvas.PointerPressed += OnCanvasPointerPressed;
            _canvas.PointerMoved += OnCanvasPointerMoved;
            _canvas.PointerReleased += OnCanvasPointerReleased;
            _canvas.PointerCaptureLost += OnCanvasPointerCaptureLost;
            _canvas.ContextMenu = _surfaceContextMenu;
        }

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollChanged += OnSurfaceScrollChanged;
            _scrollViewer.SizeChanged += OnSurfaceSizeChanged;
            UpdateSurfaceMargin();
            UpdateRulerOffsets();
        }

        if (_adornerLayer is not null && _canvas is not null)
        {
            _adornerLayer.SetSurfaceRoot(GetSurfaceRoot());
        }

        // If a rebuild was requested before we were loaded, do it now
        if (_rebuildPending)
        {
            _rebuildPending = false;
            ScheduleRebuild();
        }
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // Unsubscribe from previous VM
        if (_currentVm is not null)
        {
            _currentVm.IsRebuilding = false;
            _currentVm.RebuildRequested -= OnRebuildRequested;
            _currentVm.Selection.SelectionChanged -= OnSelectionChanged;
            _currentVm.PropertyChanged -= OnDesignSurfacePropertyChanged;
            DetachGuideHandlers(_currentVm);
        }

        _currentVm = DataContext as DesignSurfaceViewModel;

        _surfaceContextMenu.Items.Clear();
        AddEditMenuItem("Cut", _currentVm?.CutCommand, Key.X);
        AddEditMenuItem("Copy", _currentVm?.CopyCommand, Key.C);
        AddEditMenuItem("Paste", _currentVm?.PasteCommand, Key.V);
        AddEditMenuItem("Duplicate", _currentVm?.DuplicateCommand, Key.D);
        _surfaceContextMenu.Items.Add(new MenuItem
        {
            Header = UiStrings.Delete, Command = _currentVm?.DeleteSelectedCommand,
            InputGesture = new KeyGesture(Key.Delete)
        });
        AddEditMenuItem(UiStrings.SelectAll, _currentVm?.SelectAllCommand, Key.A);
        MenuItem layoutMenu = new() { Header = UiStrings.Layout };
        if (_currentVm is not null)
        {
            foreach (DesignerLayoutAction action in _currentVm.LayoutActions)
            {
                MenuItem item = new() { Header = action.Title, Command = action.Command };
                ToolTip.SetTip(item, action.Description);
                layoutMenu.Items.Add(item);
            }
        }
        _surfaceContextMenu.Items.Add(layoutMenu);
        _surfaceContextMenu.Items.Add(new Separator());
        _surfaceContextMenu.Items.Add(_openDefinitionMenuItem);

        if (_currentVm is not null)
        {
            _currentVm.RebuildRequested += OnRebuildRequested;
            _currentVm.Selection.SelectionChanged += OnSelectionChanged;
            _currentVm.PropertyChanged += OnDesignSurfacePropertyChanged;
            AttachGuideHandlers(_currentVm);
            ApplyAdornerVisibility();
            UpdateGuideLines();
            UpdateRulerSelectionBounds();

            // Trigger an initial rebuild if we're already loaded
            if (_isLoaded)
            {
                ScheduleRebuild();
            }
            else
            {
                _rebuildPending = true;
            }
        }

        UpdateNavigateMenuState();
    }

    private void AddEditMenuItem(string header, ICommand? command, Key key)
    {
        _surfaceContextMenu.Items.Add(new MenuItem
        {
            Header = header, Command = command, InputGesture = new KeyGesture(key, KeyModifiers.Control)
        });
    }

    private void OnSelectionChanged(IReadOnlyList<IDesignItem> selected)
    {
        Guid? selectedId = null;
        if (_currentVm?.Selection.PrimarySelection is DesignItem primary)
        {
            selectedId = primary.AstNodeId;
        }

        if (_currentVm?.IsSelectionSyncing != true)
        {
            UpdateSelectedNode(selectedId);
        }

        if (_adornerLayer is null)
        {
            return;
        }

        _adornerLayer.UpdateSelection(selected);
        UpdateRulerSelectionBounds();
        UpdateSpacingGuidesForSelection();
        UpdateNavigateMenuState();
    }

    private void OnDesignSurfacePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DesignSurfaceViewModel.IsEditMode))
        {
            ApplyEditMode();
            return;
        }

        if (e.PropertyName == nameof(DesignSurfaceViewModel.ShowMarginPadding) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.ShowAlignmentGuides) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.ShowGuides) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.ShowSpacingGuides))
        {
            ApplyAdornerVisibility();
            UpdateGuideLines();
            UpdateSpacingGuidesForSelection();
            return;
        }

        if (e.PropertyName == nameof(DesignSurfaceViewModel.Zoom) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.CanvasWidth) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.CanvasHeight) ||
            e.PropertyName == nameof(DesignSurfaceViewModel.CanvasGap))
        {
            UpdateSurfaceMargin();
            UpdateRulerOffsets();
            UpdateRulerSelectionBounds();
            RefreshSelectionAdorners();
        }
    }

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        UpdateSurfaceMargin();
        UpdateRulerOffsets();
        RefreshSelectionAdorners();
    }

    private void AttachGuideHandlers(DesignSurfaceViewModel vm)
    {
        _guideChangedHandler ??= (_, _) => UpdateGuideLines();
        vm.HorizontalGuides.CollectionChanged += _guideChangedHandler;
        vm.VerticalGuides.CollectionChanged += _guideChangedHandler;
    }

    private void DetachGuideHandlers(DesignSurfaceViewModel vm)
    {
        if (_guideChangedHandler is null)
        {
            return;
        }

        vm.HorizontalGuides.CollectionChanged -= _guideChangedHandler;
        vm.VerticalGuides.CollectionChanged -= _guideChangedHandler;
    }

    private void ApplyAdornerVisibility()
    {
        if (_adornerLayer is null || _currentVm is null)
        {
            return;
        }

        _adornerLayer.ShowMarginPadding = _currentVm.ShowMarginPadding;
        _adornerLayer.ShowAlignmentGuides = _currentVm.ShowAlignmentGuides;
        _adornerLayer.ShowGuides = _currentVm.ShowGuides;
        _adornerLayer.ShowSpacingGuides = _currentVm.ShowSpacingGuides;
    }

    private void UpdateGuideLines()
    {
        if (_adornerLayer is null || _currentVm is null)
        {
            return;
        }

        if (_currentVm.ShowGuides)
        {
            _adornerLayer.UpdateGuideLines(_currentVm.HorizontalGuides, _currentVm.VerticalGuides);
        }
        else
        {
            _adornerLayer.UpdateGuideLines(Array.Empty<double>(), Array.Empty<double>());
        }
    }

    private void OnSurfaceScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        UpdateRulerOffsets();
        RefreshSelectionAdorners();
    }

    private void UpdateRulerOffsets()
    {
        Control? surfaceRoot = GetSurfaceRoot();
        if (surfaceRoot?.TranslatePoint(default, this) is not Point surfaceOrigin)
        {
            return;
        }

        if (_horizontalRuler is not null &&
            _horizontalRuler.TranslatePoint(default, this) is Point horizontalOrigin)
        {
            _horizontalRuler.Offset = horizontalOrigin.X - surfaceOrigin.X;
        }

        if (_verticalRuler is not null &&
            _verticalRuler.TranslatePoint(default, this) is Point verticalOrigin)
        {
            _verticalRuler.Offset = verticalOrigin.Y - surfaceOrigin.Y;
        }
    }

    private Point GetCanvasOriginInScrollViewer()
    {
        if (_scrollViewer is null)
        {
            return default;
        }

        Control? surfaceRoot = GetSurfaceRoot();
        Point? origin = surfaceRoot?.TranslatePoint(new Point(0, 0), _scrollViewer);
        return origin ?? default;
    }

    private void UpdateRulerSelectionBounds()
    {
        if (_canvas is null)
        {
            return;
        }

        Rect? selectionBounds = GetSelectionBounds();
        if (_horizontalRuler is not null)
        {
            _horizontalRuler.SelectionStart = selectionBounds?.Left;
            _horizontalRuler.SelectionEnd = selectionBounds?.Right;
        }

        if (_verticalRuler is not null)
        {
            _verticalRuler.SelectionStart = selectionBounds?.Top;
            _verticalRuler.SelectionEnd = selectionBounds?.Bottom;
        }
    }

    private Rect? GetSelectionBounds()
    {
        if (_currentVm is null || _currentVm.Selection.SelectedItems.Count == 0)
        {
            return null;
        }

        Control? surfaceRoot = GetSurfaceRoot();
        Rect? bounds = null;
        foreach (IDesignItem item in _currentVm.Selection.SelectedItems)
        {
            if (item is not DesignItem designItem)
            {
                continue;
            }

            Rect itemBounds = designItem.GetBoundsRelativeTo(surfaceRoot);
            bounds = bounds is null ? itemBounds : bounds.Value.Union(itemBounds);
        }

        return bounds;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || (!IsKeyboardFocusWithin && _dragState is null && _marqueeState is null)) return;
        if (e.Key == Key.Escape && _dragState is not null)
        {
            CancelDragState();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Space && _currentVm?.IsEditMode == true)
        {
            _isSpaceDown = true;
            UpdatePanCursor();
            e.Handled = true;
            return;
        }

        if (_currentVm is null)
        {
            return;
        }

        if (HandleZoomShortcuts(e))
        {
            return;
        }

        if (!_currentVm.IsEditMode)
        {
            return;
        }

        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            ICommand navigation = e.KeyModifiers == KeyModifiers.Shift
                ? _currentVm.SelectPreviousControlCommand : _currentVm.SelectNextControlCommand;
            navigation.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            if (_marqueeState is not null)
            {
                _marqueeState = null;
                _selectionPointer?.Capture(null);
                _adornerLayer?.ClearAll();
                _currentVm.SelectNodesFromSync(_selectionBeforePointerDown);
                UpdateSelectedNode((_currentVm.Selection.PrimarySelection as DesignItem)?.AstNodeId);
            }
            else ((ICommand)_currentVm.SelectParentControlCommand).Execute(null);
            e.Handled = true;
            return;
        }

        ICommand? editCommand = e.KeyModifiers == KeyModifiers.Control ? e.Key switch
        {
            Key.X => _currentVm.CutCommand,
            Key.C => _currentVm.CopyCommand,
            Key.V => _currentVm.PasteCommand,
            Key.D => _currentVm.DuplicateCommand,
            Key.A => _currentVm.SelectAllCommand,
            _ => null
        } : e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None ? _currentVm.DeleteSelectedCommand : null;
        if (editCommand is not null)
        {
            e.Handled = true;
            if (editCommand.CanExecute(null)) editCommand.Execute(null);
            return;
        }

        if (_currentVm.Selection.PrimarySelection is not DesignItem primary)
        {
            return;
        }

        bool resize = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
        double step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;

        switch (e.Key)
        {
            case Key.Left:
                if (resize)
                {
                    ResizeSelection(primary, -step, 0);
                }
                else
                {
                    NudgeSelection(-step, 0);
                }
                e.Handled = true;
                break;
            case Key.Right:
                if (resize)
                {
                    ResizeSelection(primary, step, 0);
                }
                else
                {
                    NudgeSelection(step, 0);
                }
                e.Handled = true;
                break;
            case Key.Up:
                if (resize)
                {
                    ResizeSelection(primary, 0, -step);
                }
                else
                {
                    NudgeSelection(0, -step);
                }
                e.Handled = true;
                break;
            case Key.Down:
                if (resize)
                {
                    ResizeSelection(primary, 0, step);
                }
                else
                {
                    NudgeSelection(0, step);
                }
                e.Handled = true;
                break;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !_isSpaceDown)
        {
            return;
        }

        _isSpaceDown = false;
        UpdatePanCursor();
        if (_isPanning)
        {
            EndPan();
        }
        e.Handled = true;
    }

    private bool HandleZoomShortcuts(KeyEventArgs e)
    {
        if (_currentVm is null || _scrollViewer is null)
        {
            return false;
        }

        bool zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (zoomModifier)
        {
            Point center = GetViewportCenter();
            if (e.Key is Key.OemPlus or Key.Add)
            {
                if (TryGetCanvasFocus(center, out Point canvasFocus))
                {
                    ApplyZoom(DesignSurfaceViewModel.StepZoom(_currentVm.Zoom, 1), center, canvasFocus);
                }
                else
                {
                    ApplyZoom(DesignSurfaceViewModel.StepZoom(_currentVm.Zoom, 1), center);
                }
                e.Handled = true;
                return true;
            }

            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                if (TryGetCanvasFocus(center, out Point canvasFocus))
                {
                    ApplyZoom(DesignSurfaceViewModel.StepZoom(_currentVm.Zoom, -1), center, canvasFocus);
                }
                else
                {
                    ApplyZoom(DesignSurfaceViewModel.StepZoom(_currentVm.Zoom, -1), center);
                }
                e.Handled = true;
                return true;
            }
        }

        if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            if (e.Key == Key.D1)
            {
                ZoomToFit();
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.D2)
            {
                ZoomToSelection();
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.D0)
            {
                Point center = GetViewportCenter();
                if (TryGetCanvasFocus(center, out Point canvasFocus))
                {
                    ApplyZoom(1.0, center, canvasFocus);
                }
                else
                {
                    ApplyZoom(1.0, center);
                }
                e.Handled = true;
                return true;
            }
        }

        return false;
    }

    private void OnZoomToFitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ZoomToFit();
    }

    private void OnZoomToSelectionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ZoomToSelection();
    }

    private void ZoomToSelection()
    {
        Rect? selectionBounds = GetSelectionBounds();
        if (selectionBounds is null || selectionBounds.Value.Width <= 0 || selectionBounds.Value.Height <= 0)
        {
            ZoomToFit();
            return;
        }

        ZoomToBounds(selectionBounds.Value);
    }

    private void ZoomToFit()
    {
        if (_currentVm is null)
        {
            return;
        }

        Rect bounds = new(0, 0, _currentVm.CanvasWidth, _currentVm.CanvasHeight);
        ZoomToBounds(bounds);
    }

    private void ZoomToBounds(Rect bounds)
    {
        if (_currentVm is null || _scrollViewer is null)
        {
            return;
        }

        Size viewport = _scrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        double padding = Math.Clamp(_currentVm.CanvasGap, 0, 64);
        Thickness border = _surfaceBorder?.BorderThickness ?? default;
        double width = Math.Max(1, bounds.Width);
        double height = Math.Max(1, bounds.Height);
        double zoomX = (viewport.Width - padding * 2 - border.Left - border.Right) / width;
        double zoomY = (viewport.Height - padding * 2 - border.Top - border.Bottom) / height;
        double targetZoom = ClampZoom(Math.Min(zoomX, zoomY));
        _currentVm.Zoom = targetZoom;
        UpdateSurfaceMargin();

        Point center = bounds.Center;
        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer is null)
            {
                return;
            }

            _scrollViewer.UpdateLayout();
            Point origin = GetCanvasOriginInScrollViewer();
            Size currentViewport = _scrollViewer.Viewport;
            _scrollViewer.Offset += new Vector(
                origin.X + center.X * targetZoom - currentViewport.Width / 2,
                origin.Y + center.Y * targetZoom - currentViewport.Height / 2);
        }, DispatcherPriority.Render);
    }

    private void ApplyZoom(double newZoom, Point focusInScrollViewer)
    {
        ApplyZoom(newZoom, focusInScrollViewer, null);
    }

    private void ApplyZoom(double newZoom, Point focusInScrollViewer, Point? canvasFocus)
    {
        if (_currentVm is null || _scrollViewer is null || _canvas is null)
        {
            return;
        }

        double clamped = ClampZoom(newZoom);
        double oldZoom = _currentVm.Zoom;
        if (Math.Abs(oldZoom - clamped) < 0.0001 || oldZoom <= 0)
        {
            return;
        }

        Vector focus = new(focusInScrollViewer.X, focusInScrollViewer.Y);
        Vector offset = _scrollViewer.Offset;
        double scale = clamped / oldZoom;

        _currentVm.Zoom = clamped;
        UpdateSurfaceMargin();

        Dispatcher.UIThread.Post(() =>
        {
            if (_scrollViewer is null)
            {
                return;
            }

            if (canvasFocus.HasValue)
            {
                _scrollViewer.UpdateLayout();
                Point logical = canvasFocus.Value;
                Point origin = GetCanvasOriginInScrollViewer();
                _scrollViewer.Offset += new Vector(
                    origin.X + logical.X * clamped - focus.X,
                    origin.Y + logical.Y * clamped - focus.Y);
            }
            else
            {
                _scrollViewer.Offset = (offset + focus) * scale - focus;
            }
        }, DispatcherPriority.Render);
    }

    private void UpdateSurfaceMargin()
    {
        if (_currentVm is null || _scrollViewer is null || _surfaceBorder is null)
        {
            return;
        }

        Size viewport = _scrollViewer.Viewport;
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            return;
        }

        double gap = Math.Clamp(_currentVm.CanvasGap, 0, 64);
        _surfaceBorder.Margin = new Thickness(gap);
    }

    private bool TryGetCanvasFocus(Point focusInScrollViewer, out Point canvasFocus)
    {
        canvasFocus = default;
        if (_scrollViewer is null || _currentVm is null)
        {
            return false;
        }

        Point origin = GetCanvasOriginInScrollViewer();
        double zoom = Math.Max(0.01, _currentVm.Zoom);
        double logicalX = (focusInScrollViewer.X - origin.X) / zoom;
        double logicalY = (focusInScrollViewer.Y - origin.Y) / zoom;
        canvasFocus = new Point(logicalX, logicalY);
        return true;
    }

    // Intentionally left without coordinate helpers; use e.GetPosition(target)
    // for consistent hit testing across split layouts.

    private Control? GetSurfaceRoot()
    {
        return _canvas;
    }

    private Point GetViewportCenter()
    {
        if (_scrollViewer is null)
        {
            return default;
        }

        return new Point(_scrollViewer.Viewport.Width / 2, _scrollViewer.Viewport.Height / 2);
    }

    private static double ClampZoom(double zoom)
    {
        return Math.Clamp(zoom, MinZoom, MaxZoom);
    }

    private void OnRebuildRequested()
    {
        if (!_isLoaded)
        {
            _rebuildPending = true;
            return;
        }

        // Ensure we run on the UI thread (sync events may come from background)
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(ScheduleRebuild);
            return;
        }

        ScheduleRebuild();
    }

    private void ScheduleRebuild()
    {
        if (_currentVm is null)
        {
            return;
        }

        _currentVm.RebuildStatus = UiStrings.BuildingDesignSurface;
        _currentVm.IsRebuilding = true;
        if (_rebuildScheduled)
        {
            return;
        }

        _rebuildScheduled = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rebuildScheduled = false;
            ExecuteRebuild();
        }, DispatcherPriority.Background);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        ToolboxDropPlan? plan = ResolveToolboxDrop(e);
        e.DragEffects = plan is null ? DragDropEffects.None : DragDropEffects.Copy;
        _adornerLayer?.UpdateDropIndicator(GetToolboxDropIndicator(plan));
        e.Handled = true;
    }

    private LayoutDropIndicator? GetToolboxDropIndicator(ToolboxDropPlan? plan)
    {
        if (plan?.Target?.VisualElement is not { } visual || _canvas is null
            || visual.TransformToVisual(_canvas) is not { } transform) return null;
        LayoutDropIndicator indicator = visual switch
        {
            Grid grid => LayoutDropGeometry.GridCell(grid.Bounds.Size,
                grid.RowDefinitions.Select(row => row.ActualHeight).ToArray(),
                grid.ColumnDefinitions.Select(column => column.ActualWidth).ToArray(),
                grid.RowSpacing, grid.ColumnSpacing, plan.Placement?.Row ?? 0, plan.Placement?.Column ?? 0),
            StackPanel stack => LayoutDropGeometry.StackInsertion(stack.Bounds.Size,
                stack.Children.Where(child => _currentVm!.ControlMap.ContainsKey(child)).Select(child => (Rect?)child.Bounds).ToArray(),
                plan.Index ?? 0, stack.Orientation == Orientation.Horizontal, stack.Spacing),
            _ => new LayoutDropIndicator(new Rect(visual.Bounds.Size))
        };
        return indicator.Transform(transform);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        ToolboxDropPlan? plan = ResolveToolboxDrop(e);
        DesignerDocumentViewModel? document = FindDocumentViewModel();
        _adornerLayer?.UpdateDropTarget(null, null);
        if (plan is null || document?.SyncEngine.CanEditAst != true) return;
        using XamlVisualEditor.Sync.DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction(UiStrings.InsertPrefix + plan.Node.TypeName);
        if (!new LayoutEditService(document.MetadataService, document.DesignSurface.AutoGenerateAutomationIds)
            .TryInsert(plan.Parent, plan.Node, out _, plan.Index, plan.Placement)) return;
        edit.Commit();
        document.SetSelectedNode(plan.Node.Id, SyncSource.DesignSurface);
        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private ToolboxDropPlan? ResolveToolboxDrop(DragEventArgs args)
    {
        DesignerDocumentViewModel? document = FindDocumentViewModel();
        if (_currentVm?.IsEditMode != true || _canvas is null || _rootControl is null
            || document?.SyncEngine.CanEditAst != true || document.SyncEngine.CurrentDocument?.Root is not { } root
            || !ToolboxDragData.TryParse(args.DataTransfer.TryGetValue(DesignerDataFormats.ToolboxItem), out ToolboxDragData? payload)) return null;
        MutableAstObjectNode node = new() { TypeName = payload!.TypeName, XmlNamespace = payload.XmlNamespace };
        Control? hit = ControlFactory.HitTest(_rootControl, args.GetPosition(_rootControl));
        DesignItem? hovered = FindDesignItem(hit);
        LayoutEditService editor = new(document.MetadataService);
        MutableAstObjectNode? parent = hovered is not null && editor.CanInsert(hovered.AstNode, node, out _)
            ? hovered.AstNode : editor.FindInsertionParent(root, hovered?.AstNode, node);
        if (parent is null) return null;
        DesignItem? target = _currentVm.ControlMap.Values.FirstOrDefault(candidate => candidate.AstNodeId == parent.Id);
        Control? visual = target?.VisualElement;
        Point position = args.GetPosition(visual ?? _canvas);
        LayoutPlacement? placement = null;
        int? insertion = null;
        if (visual is Canvas) placement = new LayoutPlacement(position.X, position.Y);
        if (visual is Grid grid) placement = new LayoutPlacement(
            Row: LayoutDropGeometry.TrackIndex(grid.RowDefinitions.Select(row => row.ActualHeight).ToArray(), grid.RowSpacing, position.Y),
            Column: LayoutDropGeometry.TrackIndex(grid.ColumnDefinitions.Select(column => column.ActualWidth).ToArray(), grid.ColumnSpacing, position.X));
        if (visual is StackPanel stack)
        {
            insertion = LayoutDropGeometry.StackIndex(stack.Children.Where(child => _currentVm.ControlMap.ContainsKey(child))
                .Select(child => (Rect?)child.Bounds).ToArray(), stack.Orientation == Orientation.Horizontal, position);
        }
        return new ToolboxDropPlan(parent, node, placement, insertion, target);
    }

    private sealed record ToolboxDropPlan(MutableAstObjectNode Parent, MutableAstObjectNode Node,
        LayoutPlacement? Placement, int? Index, DesignItem? Target);

    private void ExecuteRebuild()
    {
        DesignSurfaceViewModel? viewModel = _currentVm;
        try
        {
            ExecuteRebuildCore();
        }
        finally
        {
            if (viewModel is not null)
            {
                viewModel.IsRebuilding = false;
            }
        }
    }

    private void ExecuteRebuildCore()
    {
        Panel? canvas = _canvas ?? this.FindControl<Panel>("DesignCanvas");
        if (canvas is null)
        {
            return;
        }

        _zoomSurface ??= this.FindControl<Control>("ZoomSurface");

        // Walk up to find the DesignerDocumentViewModel that owns the SyncEngine & ControlFactory
        DesignerDocumentViewModel? docVm = FindDocumentViewModel();
        if (docVm is null)
        {
            return;
        }

        if (docVm.UseExternalPreviewer)
        {
            if (_rootControl is not null) _rootControl.LayoutUpdated -= OnRuntimeLayoutUpdated;
            canvas.Children.Clear();
            if (_runtimeRenderedDocument?.Root is Window ownedWindow) ownedWindow.Close();
            _rootControl = null;
            _runtimeRenderedDocument = null;
            _runtimeDocumentOwner = null;
            if (_currentVm is { } surface)
            {
                if (surface.ControlMap.Count > 0)
                    surface.SetDesignTree(null, new Dictionary<Guid, DesignItem>(), new Dictionary<Control, DesignItem>());
                if (surface.Selection.SelectedItems.OfType<DesignItem>().Any(item => item.VisualElement is not null))
                    surface.ClearSelectionFromSync();
            }
            return;
        }

        List<Guid>? selectedIds = null;
        if (_currentVm is not null)
        {
            selectedIds = _currentVm.Selection.SelectedItems
                .OfType<DesignItem>()
                .Select(item => item.AstNodeId)
                .ToList();
        }

        MutableAstDocument? doc = docVm.SyncEngine.CurrentDocument;
        if (doc?.Root is null)
        {
            ShowRuntimeRenderFailure(canvas, docVm, new InvalidOperationException(UiStrings.NoValidXamlRoot));
            return;
        }

        RuntimeDesignRenderResult renderedDocument;
        Control tree;
        try
        {
            renderedDocument = RenderRuntimeDocument(docVm, doc);
            tree = CreateRuntimeSurface(renderedDocument, docVm.MetadataService);
        }
        catch (Exception exception)
        {
            ShowRuntimeRenderFailure(canvas, docVm, exception);
            return;
        }

        if (_rootControl is not null) _rootControl.LayoutUpdated -= OnRuntimeLayoutUpdated;
        canvas.Children.Clear();
        _runtimeRenderFailed = false;
        canvas.Children.Add(tree);
        _rootControl = tree;
        _runtimeRenderedDocument = renderedDocument;
        _runtimeDocumentOwner = docVm;
        docVm.SetRuntimeRenderDiagnostics(renderedDocument.Diagnostics);
        SetPreviewStatus(renderedDocument.Diagnostics.Count == 0 ? null :
            $"Preview incomplete: {renderedDocument.Diagnostics.Count} control(s) unavailable. See Problems.");
        UpdateCanvasSizeFromRoot(docVm, doc.Root);
        tree.UpdateLayout();

        ApplyEditMode();

        if (_adornerLayer is not null)
        {
            _adornerLayer.SetSurfaceRoot(GetSurfaceRoot());
            _adornerLayer.UpdateSelection(_currentVm?.Selection.SelectedItems ?? Array.Empty<IDesignItem>());
            ApplyAdornerVisibility();
            UpdateGuideLines();
        }

        UpdateRulerSelectionBounds();

        // Build design item maps for selection sync
        Dictionary<Guid, DesignItem> itemMap = new();
        Dictionary<Control, DesignItem> controlMap = new();
        DesignItem rootItem = BuildDesignItemTree(doc.Root, tree, renderedDocument.GetControlNodeIds(tree), itemMap, controlMap);
        _currentVm?.SetDesignTree(rootItem, itemMap, controlMap);
        tree.LayoutUpdated += OnRuntimeLayoutUpdated;

        if (_currentVm is not null)
        {
            Guid? selectedId = docVm.SelectedNodeId;
            IReadOnlyList<Guid>? requested = _currentVm.TakeRequestedSelection();
            if (requested is not null)
            {
                _currentVm.SelectNodesFromSync(requested);
            }
            else if (docVm.SelectionSource != SyncSource.CodeEditor && selectedId is { } primaryId
                && selectedIds is { Count: > 1 } && selectedIds.Contains(primaryId))
            {
                _currentVm.SelectNodesFromSync(selectedIds);
            }
            else if (selectedId is not null)
            {
                _currentVm.SelectByAstNodeIdFromSync(selectedId.Value);
            }
            else if (selectedIds is { Count: > 0 })
            {
                _currentVm.SelectNodesFromSync(selectedIds);
            }
        }
    }

    private void ApplyEditMode()
    {
        if (_currentVm is null || _rootControl is null)
        {
            return;
        }

        if (_runtimeRenderFailed)
        {
            SetPreviewHitTest(_rootControl, false);
            if (_canvas is not null) _canvas.ContextMenu = null;
            return;
        }

        bool isEditMode = _currentVm.IsEditMode;
        bool interactive = !isEditMode;
        SetPreviewHitTest(_rootControl, interactive);

        if (_canvas is not null)
        {
            _canvas.ContextMenu = isEditMode ? _surfaceContextMenu : null;
        }

        if (interactive)
        {
            _dragState = null;
            _marqueeState = null;
            _hoveredItem = null;
            _isPanning = false;
            _isSpaceDown = false;
            _adornerLayer?.ClearAll();
            _currentVm.Selection.ClearSelection();
            UpdateSelectedNode(null);
            UpdatePanCursor();
        }
    }

    private void UpdateCanvasSizeFromRoot(DesignerDocumentViewModel docVm, MutableAstObjectNode root)
    {
        if (_currentVm is null)
        {
            return;
        }

        double? designWidth = GetNumericProperty(root, "DesignWidth");
        double? designHeight = GetNumericProperty(root, "DesignHeight");

        if (!designWidth.HasValue || !designHeight.HasValue)
        {
            TryGetDesignSizeFromText(docVm.SyncEngine.CurrentText, ref designWidth, ref designHeight);
        }

        if (!designWidth.HasValue)
        {
            designWidth = GetNumericProperty(root, "Width");
        }

        if (!designHeight.HasValue)
        {
            designHeight = GetNumericProperty(root, "Height");
        }

        _currentVm.CanvasWidth = designWidth ?? 800;
        _currentVm.CanvasHeight = designHeight ?? 600;
    }

    private static void TryGetDesignSizeFromText(string? text, ref double? designWidth, ref double? designHeight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (!designWidth.HasValue)
        {
            designWidth = TryMatchDesignDimension(text, "DesignWidth");
        }

        if (!designHeight.HasValue)
        {
            designHeight = TryMatchDesignDimension(text, "DesignHeight");
        }
    }

    private static double? TryMatchDesignDimension(string text, string propertyName)
    {
        Regex regex = new($"\\b(?:\\w+:)?{propertyName}\\s*=\\s*\"(?<value>[0-9]+(?:\\.[0-9]+)?)\"", RegexOptions.IgnoreCase);
        Match match = regex.Match(text);
        if (!match.Success)
        {
            return null;
        }

        string value = match.Groups["value"].Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double result)
            ? result
            : null;
    }

    private static double? GetNumericProperty(MutableAstObjectNode node, string propertyName)
    {
        foreach (MutableAstPropertyNode prop in node.Properties)
        {
            if (!IsPropertyNameMatch(prop.PropertyName, propertyName))
            {
                continue;
            }

            if (prop.Value is MutableAstTextNode textNode &&
                double.TryParse(textNode.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsPropertyNameMatch(string propertyName, string target)
    {
        if (string.Equals(propertyName, target, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        int colonIndex = propertyName.IndexOf(':');
        if (colonIndex >= 0 && colonIndex < propertyName.Length - 1)
        {
            string suffix = propertyName[(colonIndex + 1)..];
            return string.Equals(suffix, target, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_currentVm is null || _rootControl is null || _canvas is null)
        {
            return;
        }

        if (TryStartPan(e))
        {
            Focus();
            e.Handled = true;
            return;
        }

        if (!_currentVm.IsEditMode || FindDocumentViewModel()?.SyncEngine.CanEditAst != true)
        {
            return;
        }

        Focus();
        PointerPoint pointInfo = e.GetCurrentPoint(_canvas);
        if (!pointInfo.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (_adornerLayer is not null)
        {
            ResizeHandle? handle = _adornerLayer.HitTestResizeHandles(e.GetPosition(_adornerLayer));
            if (handle is not null && _currentVm.Selection.PrimarySelection is DesignItem primary)
            {
                StartResize(primary, handle, e.GetPosition(_canvas), e.Pointer);
                e.Pointer.Capture(_canvas);
                e.Handled = true;
                return;
            }
        }

        Point canvasPoint = e.GetPosition(_canvas);
        Point rootPoint = e.GetPosition(_rootControl);
        Control? hit = ControlFactory.HitTest(_rootControl, rootPoint);
        DesignItem? item = FindDesignItem(hit);
        Control? mappedHit = item?.VisualElement;
        if (item is not null)
        {
            ApplySelection(item, e.KeyModifiers);
            UpdateSelectedNode(item.AstNodeId);
            if (e.ClickCount == 2)
            {
                DesignerDocumentViewModel? docVm = FindDocumentViewModel();
                if (docVm is not null)
                {
                    _ = docVm.NavigateToDefinitionAsync();
                }

                e.Handled = true;
                return;
            }
            if (mappedHit is Panel && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
            {
                StartMarquee(canvasPoint, e.KeyModifiers, item.AstNodeId);
                e.Pointer.Capture(_canvas);
                e.Handled = true;
                return;
            }
            StartDrag(item, canvasPoint, e.Pointer);
            e.Pointer.Capture(_canvas);
            e.Handled = true;
            return;
        }

        _currentVm.Selection.ClearSelection();
        UpdateSelectedNode(null);
        StartMarquee(canvasPoint, e.KeyModifiers);
        e.Pointer.Capture(_canvas);
    }

    private void OnOpenDefinitionClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        DesignerDocumentViewModel? docVm = FindDocumentViewModel();
        if (docVm is null)
        {
            return;
        }

        _ = docVm.NavigateToDefinitionAsync();
    }

    private void UpdateNavigateMenuState()
    {
        if (_openDefinitionMenuItem is null)
        {
            return;
        }

        DesignerDocumentViewModel? docVm = FindDocumentViewModel();
        _openDefinitionMenuItem.IsEnabled = docVm is not null
            && ((ICommand)docVm.NavigateToDefinitionCommand).CanExecute(null);
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_currentVm is null || _rootControl is null || _canvas is null)
        {
            return;
        }

        if (_isPanning)
        {
            UpdatePan(e);
            return;
        }

        if (!_currentVm.IsEditMode)
        {
            return;
        }

        if (_dragState is not null)
        {
            UpdateDragState(e.GetPosition(_canvas));
            return;
        }

        if (_marqueeState is not null)
        {
            UpdateMarquee(e.GetPosition(_canvas));
            return;
        }

        UpdateHover(e.GetPosition(_rootControl));
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_currentVm is null || _canvas is null)
        {
            return;
        }

        if (_isPanning)
        {
            EndPan();
            e.Pointer.Capture(null);
            return;
        }

        if (_dragState is null)
        {
            if (_marqueeState is not null)
            {
                CommitMarqueeSelection();
                _marqueeState = null;
                e.Pointer.Capture(null);
            }
            return;
        }

        CommitDragState();
        _dragState = null;
        e.Pointer.Capture(null);
    }

    private void OnCanvasPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isPanning)
        {
            EndPan();
            return;
        }

        if (_dragState is null)
        {
            if (_marqueeState is not null)
            {
                CommitMarqueeSelection();
                _marqueeState = null;
            }
            return;
        }

        CancelDragState();
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || e.Delta.Y == 0 || _currentVm is null || _scrollViewer is null)
        {
            return;
        }

        bool zoomModifier = e.KeyModifiers.HasFlag(KeyModifiers.Control)
            || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (!zoomModifier)
        {
            return;
        }

        double targetZoom = DesignSurfaceViewModel.StepZoom(_currentVm.Zoom, Math.Sign(e.Delta.Y));
        Point focus = e.GetPosition(_scrollViewer);
        if (TryGetCanvasFocus(focus, out Point canvasFocus))
        {
            ApplyZoom(targetZoom, focus, canvasFocus);
        }
        else
        {
            ApplyZoom(targetZoom, focus);
        }
        e.Handled = true;
    }

    private bool TryStartPan(PointerPressedEventArgs e)
    {
        if (_scrollViewer is null)
        {
            return false;
        }

        PointerPoint pointInfo = e.GetCurrentPoint(_scrollViewer);
        bool middle = pointInfo.Properties.IsMiddleButtonPressed;
        bool spaceDrag = _isSpaceDown && pointInfo.Properties.IsLeftButtonPressed;
        if (!middle && !spaceDrag)
        {
            return false;
        }

        _isPanning = true;
        _panStart = e.GetPosition(_scrollViewer);
        _panStartOffset = _scrollViewer.Offset;
        e.Pointer.Capture(_canvas);
        UpdatePanCursor();
        return true;
    }

    private void UpdatePan(PointerEventArgs e)
    {
        if (_scrollViewer is null)
        {
            return;
        }

        Point current = e.GetPosition(_scrollViewer);
        Vector delta = current - _panStart;
        _scrollViewer.Offset = new Vector(
            _panStartOffset.X - delta.X,
            _panStartOffset.Y - delta.Y);
        UpdateRulerOffsets();
        RefreshSelectionAdorners();
    }

    private void EndPan()
    {
        _isPanning = false;
        UpdatePanCursor();
    }

    private void UpdatePanCursor()
    {
        if (_canvas is null)
        {
            return;
        }

        if (_isPanning || _isSpaceDown)
        {
            _canvas.Cursor = new Cursor(StandardCursorType.Hand);
        }
        else
        {
            _canvas.Cursor = null;
        }
    }

    private void ApplySelection(DesignItem item, KeyModifiers modifiers)
    {
        bool toggle = modifiers.HasFlag(KeyModifiers.Control) || modifiers.HasFlag(KeyModifiers.Meta);
        bool add = modifiers.HasFlag(KeyModifiers.Shift);

        if (toggle)
        {
            _currentVm?.Selection.ToggleSelection(item);
            return;
        }

        if (!add && _currentVm?.Selection.SelectedItems.Any(selected => selected.AstNodeId == item.AstNodeId) == true)
            return;
        _currentVm?.Selection.Select(item, addToSelection: add);
    }

    private void UpdateSelectedNode(Guid? nodeId)
    {
        DesignerDocumentViewModel? docVm = FindDocumentViewModel();
        if (docVm is not null)
        {
            docVm.SetSelectedNode(nodeId, SyncSource.DesignSurface);
        }
    }

    private void StartMarquee(Point startPoint, KeyModifiers modifiers, Guid? scopeId = null)
    {
        _marqueeScopeId = scopeId ?? FindDocumentViewModel()?.SyncEngine.CurrentDocument?.Root?.Id;
        _marqueeState = new MarqueeState
        {
            StartPoint = startPoint,
            CurrentPoint = startPoint,
            Additive = modifiers.HasFlag(KeyModifiers.Shift)
        };

        _adornerLayer?.UpdateSelectionBox(new Rect(startPoint, startPoint));
    }

    private void UpdateMarquee(Point currentPoint)
    {
        if (_marqueeState is null)
        {
            return;
        }

        _marqueeState.CurrentPoint = currentPoint;
        Rect box = GetMarqueeRect(_marqueeState.StartPoint, _marqueeState.CurrentPoint);
        _adornerLayer?.UpdateSelectionBox(box);
    }

    private void CommitMarqueeSelection()
    {
        if (_marqueeState is null || _currentVm is null || _canvas is null)
        {
            return;
        }

        Rect box = GetMarqueeRect(_marqueeState.StartPoint, _marqueeState.CurrentPoint);
        if (box.Width < 4 && box.Height < 4)
        {
            _adornerLayer?.UpdateSelectionBox(null);
            return;
        }

        List<Guid> selected = _marqueeState.Additive ? _selectionBeforePointerDown.ToList() : new();
        foreach (DesignItem item in _currentVm.ItemMap.Values)
        {
            if (LayoutEditService.GetParent(item.AstNode)?.Id != _marqueeScopeId) continue;
            Rect bounds = item.GetBoundsRelativeTo(_canvas);
            if (bounds.Intersects(box))
            {
                selected.Add(item.AstNodeId);
            }
        }
        _currentVm.SelectNodesFromSync(selected);
        UpdateSelectedNode((_currentVm.Selection.PrimarySelection as DesignItem)?.AstNodeId);
        _adornerLayer?.UpdateSelectionBox(null);
    }

    private static Rect GetMarqueeRect(Point start, Point end)
    {
        double x = Math.Min(start.X, end.X);
        double y = Math.Min(start.Y, end.Y);
        double w = Math.Abs(end.X - start.X);
        double h = Math.Abs(end.Y - start.Y);
        return new Rect(x, y, w, h);
    }

    private void NudgeSelection(double dx, double dy)
    {
        if (_currentVm is null)
        {
            return;
        }

        DesignerDocumentViewModel? document = FindDocumentViewModel();
        if (document?.SyncEngine.CanEditAst != true) return;
        using XamlVisualEditor.Sync.DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction(UiStrings.MoveSelection);
        bool changed = false;
        foreach (IDesignItem item in _currentVm.Selection.SelectedItems)
        {
            if (item is DesignItem designItem)
            {
                changed |= NudgeItem(designItem, dx, dy);
            }
        }

        if (changed)
        {
            edit.Commit();
        }
    }

    private bool NudgeItem(DesignItem item, double dx, double dy)
    {
        if (item.VisualElement?.Parent is Canvas)
        {
            double left = GetAttachedDouble(item, "Canvas.Left") + dx;
            double top = GetAttachedDouble(item, "Canvas.Top") + dy;
            SetAttachedDouble(item, "Canvas.Left", left);
            SetAttachedDouble(item, "Canvas.Top", top);
            return true;
        }

        if (item.VisualElement?.Parent is Grid grid)
        {
            int row = GetAttachedInt(item, "Grid.Row");
            int column = GetAttachedInt(item, "Grid.Column");
            if (dy < 0)
            {
                row = Math.Max(0, row - 1);
            }
            else if (dy > 0)
            {
                row = Math.Min(Math.Max(1, grid.RowDefinitions.Count) - 1, row + 1);
            }

            if (dx < 0)
            {
                column = Math.Max(0, column - 1);
            }
            else if (dx > 0)
            {
                column = Math.Min(Math.Max(1, grid.ColumnDefinitions.Count) - 1, column + 1);
            }

            SetAttachedInt(item, "Grid.Row", row);
            SetAttachedInt(item, "Grid.Column", column);
            return true;
        }

        if (item.VisualElement?.Parent is DockPanel)
        {
            Avalonia.Controls.Dock dock = GetDockFromDirection(dx, dy);
            SetAttachedString(item, "DockPanel.Dock", dock.ToString());
            return true;
        }

        if (item.VisualElement?.Parent is StackPanel stack)
        {
            bool vertical = stack.Orientation == Orientation.Vertical;
            int offset = vertical ? Math.Sign(dy) : Math.Sign(dx);
            return MoveSibling(item, offset);
        }

        if (item.VisualElement?.Parent is WrapPanel or UniformGrid)
        {
            int offset = Math.Sign(dx != 0 ? dx : dy);
            return MoveSibling(item, offset);
        }

        if (item.VisualElement?.Parent is Panel)
        {
            Thickness margin = GetThickness(item, "Margin");
            Thickness updated = new(
                margin.Left + dx,
                margin.Top + dy,
                margin.Right,
                margin.Bottom);
            SetThickness(item, "Margin", updated);
            return true;
        }

        return false;
    }

    private static Avalonia.Controls.Dock GetDockFromDirection(double dx, double dy)
    {
        if (Math.Abs(dx) > Math.Abs(dy))
        {
            return dx < 0 ? Avalonia.Controls.Dock.Left : Avalonia.Controls.Dock.Right;
        }

        return dy < 0 ? Avalonia.Controls.Dock.Top : Avalonia.Controls.Dock.Bottom;
    }

    private bool MoveSibling(DesignItem item, int offset)
    {
        if (offset == 0)
        {
            return false;
        }

        MutableAstObjectNode? parent = LayoutEditService.GetParent(item.AstNode);
        if (parent is null)
        {
            return false;
        }

        LayoutEditService editor = new(FindDocumentViewModel()?.MetadataService);
        List<MutableAstObjectNode> siblings = editor.GetChildren(parent).OfType<MutableAstObjectNode>().ToList();
        int index = siblings.IndexOf(item.AstNode);
        if (index < 0)
        {
            return false;
        }

        int nextIndex = Math.Clamp(index + offset, 0, siblings.Count - 1);
        if (nextIndex == index)
        {
            return false;
        }

        return editor.TryMoveRelative(item.AstNode, siblings[nextIndex],
            offset < 0 ? DropPosition.Before : DropPosition.After, out _);
    }

    private void ResizeSelection(DesignItem primary, double deltaWidth, double deltaHeight)
    {
        DesignerDocumentViewModel? document = FindDocumentViewModel();
        if (document?.SyncEngine.CanEditAst != true) return;
        using XamlVisualEditor.Sync.DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction(UiStrings.ResizeSelection);
        double width = GetPropertyDouble(primary, "Width");
        double height = GetPropertyDouble(primary, "Height");

        width = Math.Max(1, width + deltaWidth);
        height = Math.Max(1, height + deltaHeight);

        SetPropertyDouble(primary, "Width", width);
        SetPropertyDouble(primary, "Height", height);

        edit.Commit();
    }

    private DesignItem? FindDesignItem(Control? hit)
    {
        if (_currentVm is null) return null;
        for (Visual? visual = hit; visual is not null; visual = visual.GetVisualParent())
        {
            if (visual is Control control && _currentVm.ControlMap.TryGetValue(control, out DesignItem? item)) return item;
            if (ReferenceEquals(visual, _rootControl)) break;
        }
        return null;
    }

    private void UpdateHover(Point surfacePoint)
    {
        if (_currentVm is null)
        {
            return;
        }

        Control? hit = ControlFactory.HitTest(_rootControl!, surfacePoint);
        IDesignItem? hover = FindDesignItem(hit);

        if (!ReferenceEquals(_hoveredItem, hover))
        {
            _hoveredItem = hover;
            _adornerLayer?.UpdateHover(_hoveredItem);
        }
    }

    private void RefreshSelectionAdorners()
    {
        if (_adornerLayer is null || _currentVm is null)
        {
            return;
        }

        _adornerLayer.UpdateSelection(_currentVm.Selection.SelectedItems);
        UpdateRulerSelectionBounds();
        UpdateSpacingGuidesForSelection();
    }

    private void StartDrag(DesignItem item, Point startPoint, IPointer pointer)
    {
        DragState state = new()
        {
            Item = item,
            Pointer = pointer,
            StartPoint = startPoint,
            LastPoint = startPoint,
            Mode = GetDragMode(item),
            StartBounds = GetAlignmentBounds(item),
            StartCanvasLeft = GetAttachedDouble(item, "Canvas.Left"),
            StartCanvasTop = GetAttachedDouble(item, "Canvas.Top")
        };

        if (state.Mode == DragMode.Canvas && _currentVm is not null)
        {
            foreach (IDesignItem selection in _currentVm.Selection.SelectedItems)
            {
                if (selection is DesignItem selectedItem && selectedItem.VisualElement?.Parent is Canvas)
                {
                    double left = GetAttachedDouble(selectedItem, "Canvas.Left");
                    double top = GetAttachedDouble(selectedItem, "Canvas.Top");
                    state.StartPositions[selectedItem.AstNodeId] = new Point(left, top);
                }
            }

            if (!state.StartPositions.ContainsKey(item.AstNodeId))
            {
                state.StartPositions[item.AstNodeId] = new Point(state.StartCanvasLeft, state.StartCanvasTop);
            }
        }

        _dragState = state;
    }

    private void StartResize(DesignItem item, ResizeHandle handle, Point startPoint, IPointer pointer)
    {
        _dragState = new DragState
        {
            Item = item,
            Pointer = pointer,
            StartPoint = startPoint,
            LastPoint = startPoint,
            Mode = DragMode.Resize,
            ResizeDirection = handle.Direction,
            StartBounds = item.GetBoundsRelativeTo(GetSurfaceRoot()),
            ResizeStartSize = item.VisualElement?.Bounds.Size ?? item.Bounds.Size,
            ResizeToParent = _canvas is not null && item.VisualElement?.Parent is Visual parent
                ? _canvas.TransformToVisual(parent) : null,
            StartCanvasLeft = GetAttachedDouble(item, "Canvas.Left"),
            StartCanvasTop = GetAttachedDouble(item, "Canvas.Top")
        };
    }

    private void UpdateDragState(Point currentPoint)
    {
        if (_dragState?.Transaction?.IsCompleted == true)
        {
            CancelDragState();
            return;
        }
        if (_dragState is null || _currentVm is null)
        {
            return;
        }

        Vector delta = currentPoint - _dragState.StartPoint;
        if (!_dragState.HasMoved && delta.Length < 3)
        {
            return;
        }

        if (!_dragState.HasMoved)
        {
            _dragState.Transaction = FindDocumentViewModel()?.SyncEngine.BeginEditTransaction(
                _dragState.Mode == DragMode.Resize ? UiStrings.ResizeSelection : UiStrings.MoveSelection);
        }
        _dragState.HasMoved = true;
        _dragState.LastPoint = currentPoint;

        switch (_dragState.Mode)
        {
            case DragMode.Canvas:
                UpdateCanvasMove(_dragState, currentPoint);
                break;
            case DragMode.Grid:
                UpdateGridMove(_dragState, currentPoint);
                break;
            case DragMode.Dock:
                UpdateDockMove(_dragState, currentPoint);
                break;
            case DragMode.Reorder:
                UpdateReorderDrag(_dragState, currentPoint);
                break;
            case DragMode.Resize:
                UpdateResize(_dragState, currentPoint);
                break;
        }
    }

    private void CommitDragState()
    {
        if (_dragState is null || _currentVm is null)
        {
            return;
        }

        DesignerDocumentViewModel? docVm = FindDocumentViewModel();
        if (docVm is null)
        {
            CancelDragState();
            return;
        }

        DragState state = _dragState;
        bool commit = true;
        if (state.Mode == DragMode.Reorder && state.DropTarget is DesignItem target && state.DropPosition.HasValue)
        {
            IEnumerable<DesignItem> sources = state.ReorderItems ?? new[] { state.Item };
            if (state.DropPosition == DropPosition.After) sources = sources.Reverse();
            bool moved = sources.All(item => _surfaceDropHandler.Move(item, target, state.DropPosition.Value, docVm.MetadataService));
            commit = moved;
            if (moved && docVm.SyncEngine.CurrentDocument is not null)
            {
                docVm.SyncEngine.NotifyAstChanged(docVm.SyncEngine.CurrentDocument, SyncSource.DesignSurface);
            }
        }
        else if (state.HasMoved && docVm.SyncEngine.CurrentDocument is not null)
        {
            docVm.SyncEngine.NotifyAstChanged(docVm.SyncEngine.CurrentDocument, SyncSource.DesignSurface);
        }

        if (commit) state.Transaction?.Commit();
        else state.Transaction?.Dispose();

        _adornerLayer?.UpdateDropTarget(null, null);
        _adornerLayer?.UpdateSnapLines(Array.Empty<double>(), Array.Empty<double>());
        _adornerLayer?.UpdateSpacingGuides(Array.Empty<SpacingGuide>());
        RefreshSelectionAdorners();
    }

    private void CancelDragState()
    {
        DragState? state = _dragState;
        _dragState = null;
        state?.Transaction?.Rollback();
        state?.Pointer?.Capture(null);
        _adornerLayer?.UpdateDropTarget(null, null);
        _adornerLayer?.UpdateSnapLines(Array.Empty<double>(), Array.Empty<double>());
        _adornerLayer?.UpdateSpacingGuides(Array.Empty<SpacingGuide>());
        RefreshSelectionAdorners();
    }

    private void UpdateCanvasMove(DragState state, Point currentPoint)
    {
        if (_currentVm is null)
        {
            return;
        }

        Vector delta = ToCanvasParentDelta(state, currentPoint - state.StartPoint);
        double left = state.StartCanvasLeft + delta.X;
        double top = state.StartCanvasTop + delta.Y;
        Point rawPosition = new(left, top);
        if (_currentVm.SnapToGrid)
        {
            left = Snap(left, _currentVm.GridSnapSize);
            top = Snap(top, _currentVm.GridSnapSize);
        }

        IReadOnlyList<double> snapHorizontal = Array.Empty<double>();
        IReadOnlyList<double> snapVertical = Array.Empty<double>();

        if (_currentVm.ShowAlignmentGuides || _currentVm.SnapLinesEnabled)
        {
            (left, top, snapHorizontal, snapVertical) = ApplySnapLines(state, left, top, _currentVm.SnapLinesEnabled, rawPosition);
        }

        if (_currentVm.ShowGuides || _currentVm.SnapToGuides)
        {
            (left, top) = ApplyGuideSnaps(state, left, top, _currentVm.SnapToGuides, rawPosition);
        }

        double dx = left - state.StartCanvasLeft;
        double dy = top - state.StartCanvasTop;

        if (state.StartPositions.Count > 0)
        {
            foreach ((Guid id, Point start) in state.StartPositions)
            {
                if (_currentVm.ItemMap.TryGetValue(id, out DesignItem? selectedItem) && selectedItem is not null)
                {
                    SetAttachedDouble(selectedItem, "Canvas.Left", start.X + dx);
                    SetAttachedDouble(selectedItem, "Canvas.Top", start.Y + dy);
                }
            }
        }
        else
        {
            SetAttachedDouble(state.Item, "Canvas.Left", left);
            SetAttachedDouble(state.Item, "Canvas.Top", top);
        }

        _adornerLayer?.UpdateSnapLines(snapHorizontal, snapVertical);
        UpdateSpacingGuidesForDrag(GetCanvasMoveBounds(state, left, top));
        RefreshSelectionAdorners();
    }

    private void UpdateGridMove(DragState state, Point currentPoint)
    {
        if (state.Item.VisualElement?.Parent is not Grid grid)
        {
            return;
        }

        Point inGrid = TranslateFromCanvas(grid, currentPoint);
        int row = LayoutDropGeometry.TrackIndex(grid.RowDefinitions.Select(definition => definition.ActualHeight).ToArray(), grid.RowSpacing, inGrid.Y);
        int column = LayoutDropGeometry.TrackIndex(grid.ColumnDefinitions.Select(definition => definition.ActualWidth).ToArray(), grid.ColumnSpacing, inGrid.X);

        SetAttachedInt(state.Item, "Grid.Row", row);
        SetAttachedInt(state.Item, "Grid.Column", column);
        RefreshSelectionAdorners();
    }

    private void UpdateDockMove(DragState state, Point currentPoint)
    {
        if (state.Item.VisualElement?.Parent is not DockPanel dockPanel)
        {
            return;
        }

        Point local = TranslateFromCanvas(dockPanel, currentPoint);
        Rect bounds = dockPanel.Bounds;
        double edge = Math.Min(bounds.Width, bounds.Height) * 0.2;

          Avalonia.Controls.Dock dock = Avalonia.Controls.Dock.Left;
        if (local.Y <= edge)
        {
             dock = Avalonia.Controls.Dock.Top;
        }
        else if (local.Y >= bounds.Height - edge)
        {
             dock = Avalonia.Controls.Dock.Bottom;
        }
        else if (local.X >= bounds.Width - edge)
        {
             dock = Avalonia.Controls.Dock.Right;
        }

        SetAttachedString(state.Item, "DockPanel.Dock", dock.ToString());
        RefreshSelectionAdorners();
    }

    private void UpdateReorderDrag(DragState state, Point currentPoint)
    {
        if (_currentVm is null || _canvas is null || _rootControl is null)
        {
            return;
        }

        state.DropTarget = null;
        state.DropPosition = null;
        state.ReorderItems = null;
        _adornerLayer?.UpdateDropTarget(null, null);
        Point rootPoint = TranslateToRoot(currentPoint);
        Control? hit = ControlFactory.HitTest(_rootControl, rootPoint);
        StackPanel? stack = hit is StackPanel direct && _currentVm.ControlMap.ContainsKey(direct) ? direct
            : hit?.GetVisualAncestors().OfType<StackPanel>().FirstOrDefault(panel => _currentVm.ControlMap.ContainsKey(panel));
        if (stack is not null && _currentVm.ControlMap.TryGetValue(stack, out DesignItem? stackItem)
            && _canvas.TranslatePoint(currentPoint, stack) is { } positionInStack
            && stack.TransformToVisual(_canvas) is { } transform)
        {
            DesignItem[] sources = _currentVm.Selection.SelectedItems.OfType<DesignItem>().ToArray();
            if (!sources.Any(item => item.AstNodeId == state.Item.AstNodeId)) sources = new[] { state.Item };
            if (sources.Length > 1)
            {
                if (state.Item.VisualElement?.Parent is not Panel sourcePanel
                    || sources.Any(item => !ReferenceEquals(item.VisualElement?.Parent, sourcePanel))) return;
                sources = sources.OrderBy(item => sourcePanel.Children.IndexOf(item.VisualElement!)).ToArray();
            }
            HashSet<Guid> movingIds = new(sources.Select(item => item.AstNodeId));
            Control[] remaining = stack.Children.Where(child => _currentVm.ControlMap.TryGetValue(child, out DesignItem? item)
                && !movingIds.Contains(item.AstNodeId)).ToArray();
            Rect?[] remainingBounds = remaining.Select(child => (Rect?)child.Bounds).ToArray();
            bool horizontal = stack.Orientation == Orientation.Horizontal;
            int index = LayoutDropGeometry.StackIndex(remainingBounds, horizontal, positionInStack);
            state.ReorderItems = sources;
            state.DropTarget = remaining.Length == 0 ? stackItem : _currentVm.ControlMap[remaining[Math.Min(index, remaining.Length - 1)]];
            state.DropPosition = remaining.Length == 0 ? DropPosition.Inside
                : index < remaining.Length ? DropPosition.Before : DropPosition.After;
            _adornerLayer?.UpdateDropIndicator(LayoutDropGeometry.StackInsertion(stack.Bounds.Size,
                remainingBounds, index, horizontal, stack.Spacing).Transform(transform));
            return;
        }

        DesignItem? target = FindDesignItem(hit);
        if (target is null || ReferenceEquals(target, state.Item)) return;

        DropPosition position = ComputeDropPosition(target, currentPoint);
        state.DropTarget = target;
        state.DropPosition = position;

        Rect bounds = target.GetBoundsRelativeTo(GetSurfaceRoot());
        _adornerLayer?.UpdateDropTarget(bounds, position);
    }

    private void UpdateResize(DragState state, Point currentPoint)
    {
        if (_currentVm is null || state.ResizeDirection is null)
        {
            return;
        }

        Vector delta = currentPoint - state.StartPoint;
        Rect bounds = state.StartBounds;
        double left = bounds.Left;
        double top = bounds.Top;
        double width = bounds.Width;
        double height = bounds.Height;
        double scaleX = bounds.Width > 0 && state.ResizeStartSize.Width > 0 ? bounds.Width / state.ResizeStartSize.Width : 1;
        double scaleY = bounds.Height > 0 && state.ResizeStartSize.Height > 0 ? bounds.Height / state.ResizeStartSize.Height : 1;
        bool resizeLeft = state.ResizeDirection.Value is ResizeDirection.Left or ResizeDirection.TopLeft or ResizeDirection.BottomLeft;
        bool resizeTop = state.ResizeDirection.Value is ResizeDirection.Top or ResizeDirection.TopLeft or ResizeDirection.TopRight;

        switch (state.ResizeDirection.Value)
        {
            case ResizeDirection.Left:
                left += delta.X;
                width -= delta.X;
                break;
            case ResizeDirection.Right:
                width += delta.X;
                break;
            case ResizeDirection.Top:
                top += delta.Y;
                height -= delta.Y;
                break;
            case ResizeDirection.Bottom:
                height += delta.Y;
                break;
            case ResizeDirection.TopLeft:
                left += delta.X;
                width -= delta.X;
                top += delta.Y;
                height -= delta.Y;
                break;
            case ResizeDirection.TopRight:
                width += delta.X;
                top += delta.Y;
                height -= delta.Y;
                break;
            case ResizeDirection.BottomLeft:
                left += delta.X;
                width -= delta.X;
                height += delta.Y;
                break;
            case ResizeDirection.BottomRight:
                width += delta.X;
                height += delta.Y;
                break;
        }

        width = Math.Max(scaleX, width);
        height = Math.Max(scaleY, height);

        if (_currentVm.SnapToGrid)
        {
            width = Math.Max(1, Snap(width / scaleX, _currentVm.GridSnapSize)) * scaleX;
            height = Math.Max(1, Snap(height / scaleY, _currentVm.GridSnapSize)) * scaleY;
        }
        if (resizeLeft) left = bounds.Right - width;
        if (resizeTop) top = bounds.Bottom - height;

        if (_currentVm.SnapToGuides)
        {
            (left, top, width, height) = ApplyGuideSnapsToResize(state, left, top, width, height);
        }

        width = Math.Max(scaleX, width);
        height = Math.Max(scaleY, height);
        if (resizeLeft) left = bounds.Right - width;
        if (resizeTop) top = bounds.Bottom - height;
        SetPropertyDouble(state.Item, "Width", width / scaleX);
        SetPropertyDouble(state.Item, "Height", height / scaleY);

        if (state.Item.VisualElement?.Parent is Canvas)
        {
            Vector positionDelta = new(left - bounds.Left, top - bounds.Top);
            if (state.ResizeToParent is { } toParent)
                positionDelta = new Vector(positionDelta.X * toParent.M11 + positionDelta.Y * toParent.M21,
                    positionDelta.X * toParent.M12 + positionDelta.Y * toParent.M22);
            SetAttachedDouble(state.Item, "Canvas.Left", state.StartCanvasLeft + positionDelta.X);
            SetAttachedDouble(state.Item, "Canvas.Top", state.StartCanvasTop + positionDelta.Y);
        }

        UpdateSpacingGuidesForDrag(new Rect(left, top, width, height));
        RefreshSelectionAdorners();
    }

    private DropPosition ComputeDropPosition(DesignItem target, Point surfacePoint)
    {
        Rect bounds = target.GetBoundsRelativeTo(GetSurfaceRoot());
        double inset = Math.Min(bounds.Width, bounds.Height) * 0.25;

        if (surfacePoint.Y <= bounds.Top + inset || surfacePoint.X <= bounds.Left + inset)
        {
            return DropPosition.Before;
        }

        if (surfacePoint.Y >= bounds.Bottom - inset || surfacePoint.X >= bounds.Right - inset)
        {
            return DropPosition.After;
        }

        return DropPosition.Inside;
    }

    private DragMode GetDragMode(DesignItem item)
    {
        Control? parent = item.VisualElement?.Parent as Control;
        return parent switch
        {
            Canvas => DragMode.Canvas,
            Grid => DragMode.Grid,
            DockPanel => DragMode.Dock,
            StackPanel => DragMode.Reorder,
            WrapPanel => DragMode.Reorder,
            UniformGrid => DragMode.Reorder,
            Panel => DragMode.Reorder,
            _ => DragMode.Reorder
        };
    }

    private void SetPreviewHitTest(Control root, bool interactive)
    {
        root.IsHitTestVisible = interactive;

        switch (root)
        {
            case Panel panel:
                foreach (Control child in panel.Children.OfType<Control>())
                {
                    SetPreviewHitTest(child, interactive);
                }
                break;
            case Decorator decorator when decorator.Child is Control child:
                SetPreviewHitTest(child, interactive);
                break;
            case ContentControl contentControl when contentControl.Content is Control contentChild:
                SetPreviewHitTest(contentChild, interactive);
                break;
            case ItemsControl itemsControl when itemsControl.ItemsSource is IEnumerable items:
                foreach (object? item in items)
                {
                    if (item is Control itemControl)
                    {
                        SetPreviewHitTest(itemControl, interactive);
                    }
                }
                break;
        }
    }

    private static double Snap(double value, double gridSize)
    {
        if (gridSize <= 0)
        {
            return value;
        }

        return Math.Round(value / gridSize) * gridSize;
    }

    private (double left, double top, IReadOnlyList<double> snapHorizontal, IReadOnlyList<double> snapVertical)
        ApplySnapLines(DragState state, double left, double top, bool applySnap, Point rawPosition)
    {
        if (_currentVm is null || _canvas is null)
        {
            return (left, top, Array.Empty<double>(), Array.Empty<double>());
        }

        List<double> candidateX = new();
        List<double> candidateY = new();
        Control? parent = state.Item.VisualElement?.Parent as Control;

        foreach (DesignItem item in _currentVm.ItemMap.Values)
        {
            if (ReferenceEquals(item, state.Item))
            {
                continue;
            }

            if (state.StartPositions.ContainsKey(item.AstNodeId))
            {
                continue;
            }

            if (item.VisualElement is not { } visual || !visual.IsVisible) continue;
            if (parent is not null && !ReferenceEquals(visual, parent) && !ReferenceEquals(visual.Parent, parent)) continue;
            Rect bounds = GetAlignmentBounds(item);
            candidateX.Add(bounds.Left);
            candidateX.Add(bounds.Center.X);
            candidateX.Add(bounds.Right);
            candidateY.Add(bounds.Top);
            candidateY.Add(bounds.Center.Y);
            candidateY.Add(bounds.Bottom);
        }

        return SnapCanvasLines(state, left, top, candidateX, candidateY, applySnap, rawPosition);
    }

    private Rect GetAlignmentBounds(DesignItem item) =>
        item.VisualElement is { } visual && _canvas is not null && visual.TransformToVisual(_canvas) is { } transform
            ? new Rect(visual.Bounds.Size).TransformToAABB(transform) : item.GetBoundsRelativeTo(_canvas);

    private Vector ToCanvasParentDelta(DragState state, Vector delta)
    {
        if (state.Item.VisualElement?.Parent is not Control parent) return delta;
        return TranslateFromCanvas(parent, new Point(delta.X, delta.Y)) - TranslateFromCanvas(parent, default);
    }

    private Rect GetCanvasMoveBounds(DragState state, double left, double top)
    {
        Vector delta = new(left - state.StartCanvasLeft, top - state.StartCanvasTop);
        if (state.Item.VisualElement?.Parent is Control parent && _canvas is not null
            && parent.TranslatePoint(default, _canvas) is { } origin
            && parent.TranslatePoint(new Point(delta.X, delta.Y), _canvas) is { } moved)
            delta = moved - origin;
        return state.StartBounds.Translate(delta);
    }

    private (double left, double top, IReadOnlyList<double> horizontal, IReadOnlyList<double> vertical)
        SnapCanvasLines(DragState state, double left, double top, IReadOnlyList<double> candidateX,
            IReadOnlyList<double> candidateY, bool applySnap, Point rawPosition)
    {
        Rect current = GetCanvasMoveBounds(state, left, top);
        Rect proposed = applySnap ? GetCanvasMoveBounds(state, rawPosition.X, rawPosition.Y) : current;
        double threshold = 6.0 / Math.Max(0.1, _currentVm?.Zoom ?? 1);
        (double dx, double? x) = FindSnapOffset(new[] { proposed.Left, proposed.Center.X, proposed.Right }, candidateX, threshold);
        (double dy, double? y) = FindSnapOffset(new[] { proposed.Top, proposed.Center.Y, proposed.Bottom }, candidateY, threshold);
        if (applySnap)
        {
            Vector correction = ToCanvasParentDelta(state, new Vector(
                x.HasValue ? proposed.Left + dx - current.Left : 0,
                y.HasValue ? proposed.Top + dy - current.Top : 0));
            left += correction.X;
            top += correction.Y;
        }
        return (left, top, y is { } horizontal ? new[] { horizontal } : Array.Empty<double>(),
            x is { } vertical ? new[] { vertical } : Array.Empty<double>());
    }

    private (double left, double top) ApplyGuideSnaps(DragState state, double left, double top, bool applySnap, Point rawPosition)
    {
        if (_currentVm is null)
        {
            return (left, top);
        }
        var snapped = SnapCanvasLines(state, left, top, _currentVm.VerticalGuides, _currentVm.HorizontalGuides, applySnap, rawPosition);
        return (snapped.left, snapped.top);
    }

    private (double left, double top, double width, double height) ApplyGuideSnapsToResize(
        DragState state,
        double left,
        double top,
        double width,
        double height)
    {
        if (_currentVm is null || state.ResizeDirection is null)
        {
            return (left, top, width, height);
        }

        const double threshold = 6.0;
        ResizeDirection direction = state.ResizeDirection.Value;

        if (_currentVm.VerticalGuides.Count > 0)
        {
            if (direction is ResizeDirection.Left or ResizeDirection.TopLeft or ResizeDirection.BottomLeft)
            {
                (double offset, _) = FindSnapOffset(new[] { left }, _currentVm.VerticalGuides, threshold);
                if (Math.Abs(offset) > 0)
                {
                    left += offset;
                    width -= offset;
                }
            }
            else if (direction is ResizeDirection.Right or ResizeDirection.TopRight or ResizeDirection.BottomRight)
            {
                double right = left + width;
                (double offset, _) = FindSnapOffset(new[] { right }, _currentVm.VerticalGuides, threshold);
                if (Math.Abs(offset) > 0)
                {
                    width += offset;
                }
            }
        }

        if (_currentVm.HorizontalGuides.Count > 0)
        {
            if (direction is ResizeDirection.Top or ResizeDirection.TopLeft or ResizeDirection.TopRight)
            {
                (double offset, _) = FindSnapOffset(new[] { top }, _currentVm.HorizontalGuides, threshold);
                if (Math.Abs(offset) > 0)
                {
                    top += offset;
                    height -= offset;
                }
            }
            else if (direction is ResizeDirection.Bottom or ResizeDirection.BottomLeft or ResizeDirection.BottomRight)
            {
                double bottom = top + height;
                (double offset, _) = FindSnapOffset(new[] { bottom }, _currentVm.HorizontalGuides, threshold);
                if (Math.Abs(offset) > 0)
                {
                    height += offset;
                }
            }
        }

        width = Math.Max(1, width);
        height = Math.Max(1, height);

        return (left, top, width, height);
    }

    private void UpdateSpacingGuidesForSelection()
    {
        if (_currentVm is null || _adornerLayer is null)
        {
            return;
        }

        if (!_currentVm.ShowSpacingGuides)
        {
            _adornerLayer.UpdateSpacingGuides(Array.Empty<SpacingGuide>());
            return;
        }

        Rect? selectionBounds = GetSelectionBounds();
        if (selectionBounds is null)
        {
            _adornerLayer.UpdateSpacingGuides(Array.Empty<SpacingGuide>());
            return;
        }

        _adornerLayer.UpdateSpacingGuides(BuildSpacingGuides(selectionBounds.Value));
    }

    private void UpdateSpacingGuidesForDrag(Rect selectionBounds)
    {
        if (_currentVm is null || _adornerLayer is null)
        {
            return;
        }

        if (!_currentVm.ShowSpacingGuides)
        {
            _adornerLayer.UpdateSpacingGuides(Array.Empty<SpacingGuide>());
            return;
        }

        _adornerLayer.UpdateSpacingGuides(BuildSpacingGuides(selectionBounds));
    }

    private IReadOnlyList<SpacingGuide> BuildSpacingGuides(Rect selectionBounds)
    {
        if (_currentVm is null || _canvas is null) return Array.Empty<SpacingGuide>();
        return SpacingGuideAdorner.Calculate(selectionBounds, _currentVm.ItemMap.Values
            .Where(item => !_currentVm.Selection.SelectedItems.Contains(item))
            .Select(item => item.GetBoundsRelativeTo(GetSurfaceRoot())));
    }

    private static double GetAttachedDouble(DesignItem item, string propertyName)
    {
        string? raw = item.AstNode.GetPropertyValue(propertyName);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        if (item.VisualElement is Control control)
        {
            return propertyName switch
            {
                "Canvas.Left" => double.IsNaN(Canvas.GetLeft(control)) ? 0 : Canvas.GetLeft(control),
                "Canvas.Top" => double.IsNaN(Canvas.GetTop(control)) ? 0 : Canvas.GetTop(control),
                _ => 0
            };
        }

        return 0;
    }

    private static int GetAttachedInt(DesignItem item, string propertyName)
    {
        string? raw = item.AstNode.GetPropertyValue(propertyName);
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        if (item.VisualElement is Control control)
        {
            return propertyName switch
            {
                "Grid.Row" => Grid.GetRow(control),
                "Grid.Column" => Grid.GetColumn(control),
                _ => 0
            };
        }

        return 0;
    }

    private static void SetAttachedDouble(DesignItem item, string propertyName, double value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        item.AstNode.SetPropertyValue(propertyName, text);

        if (item.VisualElement is Control control)
        {
            switch (propertyName)
            {
                case "Canvas.Left":
                    Canvas.SetLeft(control, value);
                    break;
                case "Canvas.Top":
                    Canvas.SetTop(control, value);
                    break;
            }
        }
    }

    private static void SetAttachedInt(DesignItem item, string propertyName, int value)
    {
        item.AstNode.SetPropertyValue(propertyName, value.ToString(CultureInfo.InvariantCulture));

        if (item.VisualElement is Control control)
        {
            switch (propertyName)
            {
                case "Grid.Row":
                    Grid.SetRow(control, value);
                    break;
                case "Grid.Column":
                    Grid.SetColumn(control, value);
                    break;
            }
        }
    }

    private static void SetAttachedString(DesignItem item, string propertyName, string value)
    {
        item.AstNode.SetPropertyValue(propertyName, value);

        if (item.VisualElement is Control control && propertyName == "DockPanel.Dock" && Enum.TryParse(value, out Avalonia.Controls.Dock dock))
        {
            DockPanel.SetDock(control, dock);
        }
    }

    private static void SetPropertyDouble(DesignItem item, string propertyName, double value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        item.AstNode.SetPropertyValue(propertyName, text);

        if (item.VisualElement is Control control)
        {
            switch (propertyName)
            {
                case "Width":
                    control.Width = value;
                    break;
                case "Height":
                    control.Height = value;
                    break;
            }
        }
    }

    private static double GetPropertyDouble(DesignItem item, string propertyName)
    {
        string? raw = item.AstNode.GetPropertyValue(propertyName);
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return value;
        }

        if (item.VisualElement is Control control)
        {
            return propertyName switch
            {
                "Width" => double.IsNaN(control.Width) ? control.Bounds.Width : control.Width,
                "Height" => double.IsNaN(control.Height) ? control.Bounds.Height : control.Height,
                _ => 0
            };
        }

        return 0;
    }

    private static Thickness GetThickness(DesignItem item, string propertyName)
    {
        string? raw = item.AstNode.GetPropertyValue(propertyName);
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string[] parts = raw.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 1 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double uniform))
            {
                return new Thickness(uniform);
            }
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double h) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                return new Thickness(h, v, h, v);
            }
            if (parts.Length == 4 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double l) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double t) &&
                double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double r) &&
                double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double b))
            {
                return new Thickness(l, t, r, b);
            }
        }

        if (item.VisualElement is Control control)
        {
            return control.Margin;
        }

        return default;
    }

    private static void SetThickness(DesignItem item, string propertyName, Thickness thickness)
    {
        string text = string.Join(",",
            thickness.Left.ToString(CultureInfo.InvariantCulture),
            thickness.Top.ToString(CultureInfo.InvariantCulture),
            thickness.Right.ToString(CultureInfo.InvariantCulture),
            thickness.Bottom.ToString(CultureInfo.InvariantCulture));

        item.AstNode.SetPropertyValue(propertyName, text);

        if (item.VisualElement is Control control && propertyName == "Margin")
        {
            control.Margin = thickness;
        }
    }

    private Point TranslateFromCanvas(Control target, Point canvasPoint)
    {
        return _canvas?.TranslatePoint(canvasPoint, target) ?? canvasPoint;
    }

    private Point TranslateToRoot(Point canvasPoint)
    {
        if (_canvas is null || _rootControl is null)
        {
            return canvasPoint;
        }

        return _canvas.TranslatePoint(canvasPoint, _rootControl) ?? canvasPoint;
    }

    private sealed class DragState
    {
        public required DesignItem Item { get; init; }
        public IPointer? Pointer { get; init; }
        public XamlVisualEditor.Sync.DocumentEditTransaction? Transaction { get; set; }
        public required Point StartPoint { get; init; }
        public required Rect StartBounds { get; init; }
        public required DragMode Mode { get; init; }
        public Point LastPoint { get; set; }
        public bool HasMoved { get; set; }
        public ResizeDirection? ResizeDirection { get; init; }
        public Size ResizeStartSize { get; init; }
        public Matrix? ResizeToParent { get; init; }
        public double StartCanvasLeft { get; init; }
        public double StartCanvasTop { get; init; }
        public DesignItem? DropTarget { get; set; }
        public DropPosition? DropPosition { get; set; }
        public IReadOnlyList<DesignItem>? ReorderItems { get; set; }
        public Dictionary<Guid, Point> StartPositions { get; } = new();
    }

    private sealed class MarqueeState
    {
        public required Point StartPoint { get; init; }
        public required Point CurrentPoint { get; set; }
        public bool Additive { get; init; }
    }

    private enum DragMode
    {
        Canvas,
        Grid,
        Dock,
        Reorder,
        Resize
    }

    private static DesignItem BuildDesignItemTree(
        MutableAstObjectNode astNode,
        Control control,
        IReadOnlyDictionary<Control, Guid> identities,
        Dictionary<Guid, DesignItem> itemMap,
        Dictionary<Control, DesignItem> controlMap)
    {
        Dictionary<Guid, MutableAstObjectNode> nodes = new();
        Stack<MutableAstNode> pending = new();
        pending.Push(astNode);
        while (pending.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstPropertyNode property && property.Value is { } value)
            {
                pending.Push(value);
                continue;
            }
            if (node is not MutableAstObjectNode element) continue;
            nodes[element.Id] = element;
            foreach (MutableAstPropertyNode member in element.Properties) pending.Push(member);
            foreach (MutableAstNode child in element.Children) pending.Push(child);
        }

        foreach (var identity in identities)
        {
            if (!nodes.TryGetValue(identity.Value, out MutableAstObjectNode? node)) continue;
            DesignItem item = new(node, identity.Key);
            controlMap[identity.Key] = item;
            itemMap.TryAdd(node.Id, item);
        }
        if (!controlMap.TryGetValue(control, out DesignItem? rootItem))
        {
            rootItem = new DesignItem(astNode, control);
            controlMap[control] = rootItem;
            itemMap[astNode.Id] = rootItem;
        }

        foreach (var pair in controlMap)
        {
            if (ReferenceEquals(pair.Value, rootItem)) continue;
            DesignItem? parent = null;
            foreach (Control ancestor in pair.Key.GetVisualAncestors().OfType<Control>())
            {
                if (controlMap.TryGetValue(ancestor, out parent)) break;
            }
            for (MutableAstNode? ancestor = pair.Value.AstNode.Parent; parent is null && ancestor is not null; ancestor = ancestor.Parent)
                itemMap.TryGetValue(ancestor.Id, out parent);
            (parent ?? rootItem).AddChild(pair.Value);
        }
        return rootItem;
    }

    // Window style lookup starts at the Window, not at the editor canvas hosting its content.
    private void ApplyWorkspaceThemes(Control target, XamlVisualEditor.Core.Interfaces.ITypeMetadataService? metadata)
    {
        if (ReferenceEquals(_workspaceThemeTarget, target)
            && _appliedWorkspaceThemeVersion == WorkspaceDesignThemeRegistry.Version)
        {
            return;
        }

        foreach (var template in _workspaceDataTemplates)
            _workspaceThemeTarget?.DataTemplates.Remove(template);
        _workspaceDataTemplates.Clear();

        foreach (IStyle style in _workspaceThemeStyles)
        {
            _workspaceThemeTarget?.Styles.Remove(style);
        }

        foreach (IResourceProvider provider in _workspaceThemeResources)
        {
            _workspaceThemeTarget?.Resources.MergedDictionaries.Remove(provider);
        }

        _workspaceThemeStyles.Clear();
        _workspaceThemeResources.Clear();
        _workspaceThemeTarget = target;

        int resourceIndex = 0;
        foreach (Func<IResourceProvider?> factory in WorkspaceDesignThemeRegistry.ResourceFactories)
        {
            if (factory() is { } provider)
            {
                target.Resources.MergedDictionaries.Insert(resourceIndex++, provider);
                _workspaceThemeResources.Add(provider);
            }
        }

        int styleIndex = 0;
        foreach (Func<IStyle?> factory in WorkspaceDesignThemeRegistry.StyleFactories)
        {
            if (factory() is { } style)
            {
                target.Styles.Insert(styleIndex++, style);
                _workspaceThemeStyles.Add(style);
                foreach (var template in WorkspaceDesignThemeLoader.GetApplicationDataTemplates(style))
                {
                    target.DataTemplates.Add(template);
                    _workspaceDataTemplates.Add(template);
                }
            }
        }

        if (metadata is not null)
        {
            int libraryStyleIndex = 0;
            foreach (IStyle style in LibraryDesignThemeLoader.CreateStyles(target, metadata,
                         message => Log.Information("{DesignThemeDiagnostic}", message)))
            {
                target.Styles.Insert(libraryStyleIndex++, style);
                _workspaceThemeStyles.Add(style);
            }
        }

        _appliedWorkspaceThemeVersion = WorkspaceDesignThemeRegistry.Version;
    }

    private DesignerDocumentViewModel? FindDocumentViewModel()
    {
        if (DataContext is DesignerDocumentViewModel selfVm)
        {
            return selfVm;
        }

        if (DataContext is DesignerDocument selfDoc)
        {
            return selfDoc.DocumentViewModel;
        }

        foreach (var ancestor in this.GetVisualAncestors())
        {
            if (ancestor is Control control)
            {
                if (control.DataContext is DesignerDocumentViewModel dvm)
                {
                    return dvm;
                }

                if (control.DataContext is DesignerDocument dockDoc)
                {
                    return dockDoc.DocumentViewModel;
                }
            }
        }

        return null;
    }
}
