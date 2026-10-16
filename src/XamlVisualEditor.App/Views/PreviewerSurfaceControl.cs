using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Viewport;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using ProtocolPixelFormat = Avalonia.Remote.Protocol.Viewport.PixelFormat;
using SurfacePixelFormat = Avalonia.Platform.PixelFormat;

namespace XamlVisualEditor.App.Views;

public sealed partial class PreviewerSurfaceControl : Control
{
    private static readonly IPen AlignmentGuidePen = new Pen(Brushes.Magenta, 1, DashStyle.Dash);
    private static readonly IPen ManualGuidePen = new Pen(new SolidColorBrush(Color.Parse("#3FA9F5")), 1, DashStyle.Dash);
    private readonly MarginPaddingAdorner _marginPaddingAdorner = new();
    private readonly SpacingGuideAdorner _spacingGuideAdorner = new();

    public static readonly StyledProperty<PreviewerTcpSession?> SessionProperty =
        AvaloniaProperty.Register<PreviewerSurfaceControl, PreviewerTcpSession?>(nameof(Session));

    public static readonly StyledProperty<DesignerDocumentViewModel?> DocumentProperty =
        AvaloniaProperty.Register<PreviewerSurfaceControl, DesignerDocumentViewModel?>(nameof(Document));

    public static readonly DirectProperty<PreviewerSurfaceControl, string?> StatusTextProperty =
        AvaloniaProperty.RegisterDirect<PreviewerSurfaceControl, string?>(nameof(StatusText), control => control.StatusText);

    private string? _statusText;

    private DesignerDocumentViewModel? _document;
    private DesignerHostSnapshot? _designTree;
    private DesignerHostSnapshot? _geometryTree;
    private string? _pendingGeometryText;
    private double? _naturalWidth;
    private double? _naturalHeight;
    private readonly Dictionary<int, DesignItem> _hostItems = new();
    private Action<DesignerHostSnapshot?>? _treeHandler;
    private bool _updatingSelection;
    private LayoutDropIndicator? _toolboxTarget;
    private PointerMoveState? _pointerMove;

    private sealed class PointerMoveState
    {
        public required IPointer Pointer { get; init; }
        public required DesignerHostSnapshot Tree { get; init; }
        public required DesignItem[] Selection { get; init; }
        public required int PrimaryInstance { get; init; }
        public required string SourceText { get; init; }
        public required Point Start { get; init; }
        public Point Current { get; set; }
        public bool Moved { get; set; }
        public PreviewerResizeEdges ResizeEdges { get; init; }
        public bool IsMarquee { get; init; }
        public bool Additive { get; init; }
        public int? ScopeInstance { get; init; }
    }

    private PreviewerTcpSession? _session;
    private FrameMessage? _lastFrame;
    private WriteableBitmap? _bitmap;
    private byte[]? _rowBuffer;
    private bool _frameDirty;
    private bool _isAttached;
    private bool _isConnected;
    private bool _hasError;
    private TopLevel? _topLevel;
    private Action<IAvaloniaRemoteTransportConnection?>? _connectionHandler;
    private Action<FrameMessage>? _frameHandler;
    private Action<PreviewerErrorInfo?>? _errorHandler;

    public PreviewerTcpSession? Session
    {
        get => GetValue(SessionProperty);
        set => SetValue(SessionProperty, value);
    }

    public DesignerDocumentViewModel? Document
    {
        get => GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    public PreviewerSurfaceControl()
    {
        Focusable = true;
        Avalonia.Input.DragDrop.SetAllowDrop(this, true);
        AddHandler(Avalonia.Input.DragDrop.DragOverEvent, OnToolboxDragOver);
        AddHandler(Avalonia.Input.DragDrop.DropEvent, OnToolboxDrop);
        AddHandler(Avalonia.Input.DragDrop.DragLeaveEvent, (_, _) => { _toolboxTarget = null; InvalidateVisual(); });
    }

    static PreviewerSurfaceControl()
    {
        SessionProperty.Changed.AddClassHandler<PreviewerSurfaceControl>(
            (control, args) => control.OnSessionChanged(args));
        DocumentProperty.Changed.AddClassHandler<PreviewerSurfaceControl>(
            (control, args) => { if (control._isAttached) control.AttachDocument(args.NewValue as DesignerDocumentViewModel); });
    }

    protected override Size MeasureOverride(Size availableSize) => _session is null && _lastFrame is null
        ? new Size(Math.Min(availableSize.Width, 480), Math.Min(availableSize.Height, 40))
        : base.MeasureOverride(availableSize);

    public override void Render(DrawingContext context)
    {
        if (!HasFrame)
        {
            base.Render(context);
            return;
        }

        if (_bitmap is null
            || _bitmap.PixelSize.Width != _lastFrame.Width
            || _bitmap.PixelSize.Height != _lastFrame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(_lastFrame.Width, _lastFrame.Height),
                new Vector(96, 96),
                SurfacePixelFormat.Bgra8888,
                AlphaFormat.Premul);
            _frameDirty = true;
        }

        if (_frameDirty)
        {
            using ILockedFramebuffer buffer = _bitmap.Lock();
            CopyFrameToBuffer(_lastFrame, buffer);
            _frameDirty = false;
        }

        Rect sourceRect = new Rect(0, 0, _bitmap.Size.Width, _bitmap.Size.Height);
        Rect destRect = new Rect(0, 0, Bounds.Width, Bounds.Height);
        context.DrawImage(_bitmap, sourceRect, destRect);
        DrawDesignerGuides(context);
        DrawSelection(context);
        using (context.PushClip(new Rect(Bounds.Size)))
        using (context.PushTransform(Matrix.CreateScale(PreviewZoom, PreviewZoom)))
            DrawMeasure(context);
    }

    /// <summary>
    /// Gets the state of the preview for the status strip below it; <see langword="null"/> while the frame is current.
    /// </summary>
    public string? StatusText
    {
        get => _statusText;
        private set => SetAndRaise(StatusTextProperty, ref _statusText, value);
    }

    [MemberNotNullWhen(true, nameof(_lastFrame))]
    private bool HasFrame => _lastFrame is { Width: > 0, Height: > 0 };

    private void UpdateStatusText() => StatusText = !HasFrame
        ? _document?.Diagnostics.FirstOrDefault(diagnostic => diagnostic.Code == "XVE_PREVIEW_START")?.Message
            ?? (_hasError ? UiStrings.PreviewStatusUnavailable
                : _document?.PreviewerState == PreviewerState.Canceled ? UiStrings.PreviewerStartCanceled
                : UiStrings.PreviewStatusWaiting)
        : _hasError ? UiStrings.PreviewStatusOutOfDate
        : !_isConnected ? UiStrings.PreviewStatusDisconnected
        : null;

    protected override void ArrangeCore(Rect finalRect)
    {
        base.ArrangeCore(finalRect);
        UpdateViewportFromBounds();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _topLevel = TopLevel.GetTopLevel(this);
        if (_topLevel is not null) _topLevel.ScalingChanged += OnScalingChanged;
        AttachSession(Session);
        AttachDocument(Document);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        if (_topLevel is not null) _topLevel.ScalingChanged -= OnScalingChanged;
        _topLevel = null;
        AttachSession(null);
        AttachDocument(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSessionChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_isAttached) AttachSession(args.NewValue as PreviewerTcpSession);
    }

    private void AttachSession(PreviewerTcpSession? session)
    {
        if (ReferenceEquals(_session, session)) return;
        ReleasePreviewInput();
        if (_session is { } oldSession)
        {
            oldSession.ConnectionChanged -= _connectionHandler;
            oldSession.FrameReceived -= _frameHandler;
            oldSession.ErrorChanged -= _errorHandler;
            oldSession.DesignTreeChanged -= _treeHandler;
        }

        _connectionHandler = null;
        _frameHandler = null;
        _errorHandler = null;
        _treeHandler = null;
        _session = session;
        ResetFrameState();
        if (session is not null)
        {
            _connectionHandler = connection => OnConnectionChanged(session, connection);
            _frameHandler = frame => OnFrameReceived(session, frame);
            _errorHandler = error => OnErrorChanged(session, error);
            _treeHandler = tree => Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(_session, session) && ReferenceEquals(session.LastDesignTree, tree)) ApplyDesignTree(tree);
            });
            session.ConnectionChanged += _connectionHandler;
            session.FrameReceived += _frameHandler;
            session.ErrorChanged += _errorHandler;
            session.DesignTreeChanged += _treeHandler;
        }

        _hasError = _session?.LastErrorInfo is not null;
        _lastFrame = _session?.LastFrame;
        _frameDirty = _lastFrame is not null;
        UpdateConnectionState(_session?.Connection);
        UpdateViewportFromBounds();
        ApplyDesignTree(_session?.LastDesignTree);
        UpdateStatusText();
        InvalidateVisual();
    }

    private void AttachDocument(DesignerDocumentViewModel? document)
    {
        ReleasePreviewInput();
        CancelPointerMove();
        _geometryTree = null;
        _pendingGeometryText = null;
        if (_document is { } previous)
        {
            previous.PropertyChanged -= OnDocumentPropertyChanged;
            previous.DesignSurface.PropertyChanged -= OnSurfacePropertyChanged;
            previous.DesignSurface.Selection.SelectionChanged -= OnSelectionChanged;
            previous.DesignSurface.HorizontalGuides.CollectionChanged -= OnGuidesChanged;
            previous.DesignSurface.VerticalGuides.CollectionChanged -= OnGuidesChanged;
        }
        _document = document;
        if (document is not null)
        {
            document.PropertyChanged += OnDocumentPropertyChanged;
            document.DesignSurface.PropertyChanged += OnSurfacePropertyChanged;
            document.DesignSurface.Selection.SelectionChanged += OnSelectionChanged;
            document.DesignSurface.HorizontalGuides.CollectionChanged += OnGuidesChanged;
            document.DesignSurface.VerticalGuides.CollectionChanged += OnGuidesChanged;
            if (document.UseExternalPreviewer) document.DesignSurface.RequestRebuild();
        }
        ApplyDesignTree(_session?.LastDesignTree);
        UpdateStatusText();
    }

    private void OnDocumentPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(DesignerDocumentViewModel.UseExternalPreviewer))
        {
            _document?.DesignSurface.RequestRebuild();
            ApplyDesignTree(_session?.LastDesignTree);
        }
        if (args.PropertyName is nameof(DesignerDocumentViewModel.Diagnostics) or nameof(DesignerDocumentViewModel.PreviewerState))
            UpdateStatusText();
        if (!_updatingSelection) InvalidateVisual();
    }

    private void OnSurfacePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(DesignSurfaceViewModel.Zoom) or nameof(DesignSurfaceViewModel.CanvasGap))
            UpdateScaledBounds();
        if (args.PropertyName is nameof(DesignSurfaceViewModel.ShowAlignmentGuides) or nameof(DesignSurfaceViewModel.SnapLinesEnabled)
            or nameof(DesignSurfaceViewModel.SnapToGrid) or nameof(DesignSurfaceViewModel.GridSnapSize)
            or nameof(DesignSurfaceViewModel.ShowGrid) or nameof(DesignSurfaceViewModel.ShowGuides)
            or nameof(DesignSurfaceViewModel.SnapToGuides) or nameof(DesignSurfaceViewModel.ShowSpacingGuides)
            or nameof(DesignSurfaceViewModel.ShowMarginPadding) or nameof(DesignSurfaceViewModel.IsMeasuring)) InvalidateVisual();
        if (args.PropertyName == nameof(DesignSurfaceViewModel.IsEditMode))
        {
            ReleasePreviewInput();
            CancelPointerMove();
            InvalidateVisual();
        }
    }

    public Rect? FitToViewport(Size viewport, bool selection)
    {
        if (_document is null || _geometryTree is not { Width: > 0, Height: > 0 } tree) return null;
        Rect target = new(0, 0, tree.Width, tree.Height);
        if (selection)
        {
            var ids = _document.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().Select(item => item.HostInstanceId).ToHashSet();
            Rect? selected = null;
            foreach (DesignerHostNode node in tree.Nodes.Where(node => ids.Contains(node.InstanceId) && node.IsVisible && node.Width > 0 && node.Height > 0))
            {
                Rect bounds = new(node.X, node.Y, node.Width, node.Height);
                selected = selected is { } existing ? existing.Union(bounds) : bounds;
            }
            if (selected is not { } selectionBounds) return null;
            target = selectionBounds;
        }
        double gap = Math.Clamp(_document.DesignSurface.CanvasGap, 0, 64);
        double width = viewport.Width - gap * 2;
        double height = viewport.Height - gap * 2;
        if (width <= 0 || height <= 0) return null;
        _document.DesignSurface.Zoom = DesignSurfaceViewModel.FitZoom(Math.Min(width / target.Width, height / target.Height));
        UpdateScaledBounds();
        return target;
    }

    private double PreviewZoom => Math.Clamp(_document?.DesignSurface.Zoom ?? 1,
        DesignSurfaceViewModel.MinimumZoom, DesignSurfaceViewModel.MaximumZoom);

    private void UpdateScaledBounds()
    {
        if (_document is null) return;
        double zoom = PreviewZoom;
        if (_naturalWidth is { } width) Width = width * zoom;
        if (_naturalHeight is { } height) Height = height * zoom;
        Margin = new Thickness(Math.Clamp(_document.DesignSurface.CanvasGap, 0, 64));
        UpdateViewportFromBounds();
        InvalidateMeasure();
        InvalidateVisual();
    }

    private void OnSelectionChanged(IReadOnlyList<IDesignItem> items)
    {
        if (!_updatingSelection) InvalidateVisual();
    }

    private void OnGuidesChanged(object? sender, NotifyCollectionChangedEventArgs args) => InvalidateVisual();

    private void DrawDesignerGuides(DrawingContext context)
    {
        if (_document?.DesignSurface is not { IsEditMode: true } surface) return;
        double zoom = PreviewZoom;
        using (context.PushClip(new Rect(Bounds.Size)))
        {
            if (surface.ShowGrid)
                using (context.PushTransform(Matrix.CreateScale(zoom, zoom)))
                    DesignGridLayer.Draw(context, new Size(Bounds.Width / zoom, Bounds.Height / zoom), surface.GridSnapSize, zoom);
            if (!surface.ShowGuides) return;
            foreach (double x in surface.VerticalGuides)
                if (double.IsFinite(x)) context.DrawLine(ManualGuidePen, new Point(x * zoom, 0), new Point(x * zoom, Bounds.Height));
            foreach (double y in surface.HorizontalGuides)
                if (double.IsFinite(y)) context.DrawLine(ManualGuidePen, new Point(0, y * zoom), new Point(Bounds.Width, y * zoom));
        }
    }

    private void ApplyDesignTree(DesignerHostSnapshot? tree)
    {
        _toolboxTarget = null;
        if (_designTree is { } previousTree && previousTree.RenderId != tree?.RenderId) ReleasePreviewInput();
        if (_pointerMove is { } moving && tree?.RenderId != moving.Tree.RenderId) CancelPointerMove();
        _designTree = tree;
        if (tree is not null)
        {
            _naturalWidth = tree.Width;
            _naturalHeight = tree.Height;
            UpdateScaledBounds();
        }
        else UpdateFrameSize(_lastFrame);
        if (tree is not null) _geometryTree = tree;
        _hostItems.Clear();
        if (_document is not { IsDisposed: false, UseExternalPreviewer: true } document || tree is null)
        {
            InvalidateVisual();
            return;
        }
        DesignItem[] previousSelection = document.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().ToArray();
        IReadOnlyList<Guid>? requested = document.DesignSurface.TakeRequestedSelection();
        Dictionary<Guid, DesignItem> sourceItems = new();
        DesignItem? rootItem = null;
        foreach (DesignerHostNode node in tree.Nodes)
        {
            if (document.NodeMap.FindById(node.NodeId) is not MutableAstObjectNode ast) continue;
            DesignItem item = new(ast)
            {
                HostInstanceId = node.InstanceId,
                HostBounds = new Rect(node.X, node.Y, node.Width, node.Height)
            };
            _hostItems[node.InstanceId] = item;
            sourceItems.TryAdd(node.NodeId, item);
            if (node.ParentInstanceId is { } parent && _hostItems.TryGetValue(parent, out DesignItem? owner)) owner.AddChild(item);
            else rootItem ??= item;
        }
        document.DesignSurface.SetDesignTree(rootItem, sourceItems, new Dictionary<Control, DesignItem>());
        List<IDesignItem> selected = new();
        if (requested is not null)
        {
            foreach (Guid id in requested)
                if (sourceItems.TryGetValue(id, out DesignItem? requestedItem) && !selected.Contains(requestedItem)) selected.Add(requestedItem);
        }
        else foreach (DesignItem old in previousSelection)
        {
            DesignItem? current = old.HostInstanceId is { } instance && _hostItems.TryGetValue(instance, out DesignItem? match)
                && match.AstNodeId == old.AstNodeId ? match : sourceItems.GetValueOrDefault(old.AstNodeId);
            if (current is not null && !selected.Contains(current)) selected.Add(current);
        }
        if (requested is null && document.SelectedNodeId is { } selectedId && sourceItems.TryGetValue(selectedId, out DesignItem? primary)
            && selected.All(item => item.AstNodeId != selectedId))
        {
            selected.Clear();
            selected.Add(primary);
        }
        document.DesignSurface.SelectDesignItemsFromSync(selected);
        InvalidateVisual();
    }

    private bool CanSelect => _document is { IsDisposed: false, UseExternalPreviewer: true } document
        && document.SyncEngine.CanEditAst && document.DesignSurface.IsEditMode
        && _isConnected && !_hasError && _lastFrame is not null
        && _designTree is { Width: > 0, Height: > 0 } tree
        && ReferenceEquals(_session?.LastDesignTree, tree) && Bounds.Width > 0 && Bounds.Height > 0;

    private bool CanEditGeometry => CanSelect || _document is { IsDisposed: false, UseExternalPreviewer: true } document
        && document.SyncEngine.CanEditAst && document.DesignSurface.IsEditMode && _isConnected && !_hasError
        && _geometryTree is not null && _pendingGeometryText is not null
        && document.SyncEngine.CurrentText == _pendingGeometryText;

    private (ToolboxDragData Item, Point Position)? ToolboxDrag(DragEventArgs args)
    {
        if (!CanSelect || !ToolboxDragData.TryParse(args.DataTransfer.TryGetValue(DesignerDataFormats.ToolboxItem), out ToolboxDragData? payload)) return null;
        Point point = args.GetPosition(this);
        return (payload!, new Point(point.X * _designTree!.Width / Bounds.Width, point.Y * _designTree.Height / Bounds.Height));
    }

    private void OnToolboxDragOver(object? sender, DragEventArgs args)
    {
        var data = ToolboxDrag(args);
        _toolboxTarget = data is { } value ? new PreviewerGeometryEditor(_document!, _designTree!)
            .PreviewInsertion(value.Item.TypeName, value.Item.XmlNamespace, value.Position) : null;
        args.DragEffects = _toolboxTarget is null ? DragDropEffects.None : DragDropEffects.Copy;
        args.Handled = true;
        InvalidateVisual();
    }

    private void OnToolboxDrop(object? sender, DragEventArgs args)
    {
        var data = ToolboxDrag(args);
        _toolboxTarget = null;
        if (data is { } value)
        {
            try
            {
                new PreviewerGeometryEditor(_document!, _designTree!).InsertControl(value.Item.TypeName, value.Item.XmlNamespace, value.Position);
                _document!.SetDesignerInteractionError(null);
                args.DragEffects = DragDropEffects.Copy;
            }
            catch (InvalidOperationException error)
            {
                _document!.SetDesignerInteractionError(error);
                args.DragEffects = DragDropEffects.None;
            }
        }
        else args.DragEffects = DragDropEffects.None;
        args.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs args)
    {
        base.OnPointerPressed(args);
        if (TryStartMeasure(args)) return;
        if (ForwardPreviewPress(args)) return;
        if (!CanSelect || !args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Focus();
        Point position = args.GetPosition(this);
        Point hostPosition = new(position.X * _designTree!.Width / Bounds.Width, position.Y * _designTree.Height / Bounds.Height);
        PreviewerResizeEdges handleEdges = HitResizeHandle(position);
        if (handleEdges != PreviewerResizeEdges.None && _document!.DesignSurface.Selection.PrimarySelection is DesignItem primary)
        {
            _pointerMove = new PointerMoveState
            {
                Pointer = args.Pointer, Tree = _designTree, Selection = new[] { primary }, PrimaryInstance = primary.HostInstanceId!.Value,
                SourceText = _document.SyncEngine.GetCurrentText(), Start = hostPosition, Current = hostPosition, ResizeEdges = handleEdges
            };
            args.Pointer.Capture(this);
            args.Handled = true;
            return;
        }
        // A press on the border of a selected control moves the selection, like a press inside the control.
        if (args.KeyModifiers == KeyModifiers.None && HitSelectionBorder(position) is { } borderNode)
        {
            _pointerMove = new PointerMoveState
            {
                Pointer = args.Pointer, Tree = _designTree, Selection = _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().ToArray(),
                PrimaryInstance = borderNode.InstanceId, SourceText = _document.SyncEngine.GetCurrentText(), Start = hostPosition, Current = hostPosition
            };
            args.Pointer.Capture(this);
            args.Handled = true;
            return;
        }
        DesignerHostNode? hit = _designTree.Nodes.Reverse().FirstOrDefault(node => node.IsVisible
            && _hostItems.ContainsKey(node.InstanceId) && new Rect(node.X, node.Y, node.Width, node.Height).Contains(hostPosition)
            && (node.Clip is not { } clip || new Rect(clip.X, clip.Y, clip.Width, clip.Height).Contains(hostPosition)));
        bool toggle = (args.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        bool extend = toggle || args.KeyModifiers.HasFlag(KeyModifiers.Shift);
        DesignItem[] beforeSelection = _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().ToArray();
        bool keepSelection = hit is not null && _document!.DesignSurface.Selection.SelectedItems.Any(item => item.AstNodeId == hit.NodeId);
        List<DesignItem> selection = extend || keepSelection ? _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().ToList() : new();
        if (hit is not null)
        {
            DesignItem item = _hostItems[hit.InstanceId];
            int existing = selection.FindIndex(selected => selected.AstNodeId == item.AstNodeId);
            if (existing < 0) selection.Add(item);
            else if (toggle && selection[existing].HostInstanceId == item.HostInstanceId) selection.RemoveAt(existing);
            else selection[existing] = item;
        }
        SelectFromInput(selection);
        bool marquee = !args.KeyModifiers.HasFlag(KeyModifiers.Alt)
            && (hit is null || hit.Layout?.Kind is "Canvas" or "Grid" or "Stack" or "Panel" or "Dock");
        if (marquee)
        {
            _pointerMove = new PointerMoveState
            {
                Pointer = args.Pointer, Tree = _designTree, Selection = beforeSelection, PrimaryInstance = hit?.InstanceId ?? 0,
                SourceText = _document.SyncEngine.GetCurrentText(), Start = hostPosition, Current = hostPosition,
                IsMarquee = true, Additive = extend,
                ScopeInstance = hit?.InstanceId ?? _designTree.Nodes.FirstOrDefault(node => node.ParentInstanceId is null)?.InstanceId
            };
            args.Pointer.Capture(this);
        }
        else if (!extend && hit is { ParentInstanceId: not null, Layout: not null })
        {
            _pointerMove = new PointerMoveState
            {
                Pointer = args.Pointer, Tree = _designTree, Selection = selection.ToArray(), PrimaryInstance = hit.InstanceId,
                SourceText = _document!.SyncEngine.GetCurrentText(), Start = hostPosition, Current = hostPosition
            };
            args.Pointer.Capture(this);
        }
        args.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs args)
    {
        base.OnPointerMoved(args);
        if (UpdateMeasure(args)) return;
        if (ForwardPreviewMove(args))
        {
            Cursor = null;
            return;
        }
        if (_pointerMove is not { } state)
        {
            if (_designTree is not null) UpdateChromeCursor(args.GetPosition(this));
            return;
        }
        if (_document?.SyncEngine.GetCurrentText() != state.SourceText || !CanSelect)
        {
            CancelPointerMove();
            return;
        }
        Point position = args.GetPosition(this);
        state.Current = new Point(position.X * state.Tree.Width / Bounds.Width, position.Y * state.Tree.Height / Bounds.Height);
        Vector distance = state.Current - state.Start;
        bool wasMoved = state.Moved;
        state.Moved |= Math.Abs(distance.X * Bounds.Width / state.Tree.Width) >= 3
            || Math.Abs(distance.Y * Bounds.Height / state.Tree.Height) >= 3;
        // The strip still holds the bounds before the move; the ghost bounds follow with the next render.
        if (!wasMoved && state.Moved && !state.IsMarquee) Readout?.BeginChange();
        InvalidateVisual();
        args.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs args)
    {
        base.OnPointerReleased(args);
        if (EndMeasure(args)) return;
        if (ForwardPreviewRelease(args)) return;
        PointerMoveState? state = _pointerMove;
        if (state is null) return;
        _pointerMove = null;
        state.Pointer.Capture(null);
        Readout?.EndChange();
        if (state.Moved && CanSelect && _document!.SyncEngine.GetCurrentText() == state.SourceText)
        {
            try
            {
                if (state.IsMarquee) CommitMarquee(state);
                else
                {
                    PreviewerGeometryEditor editor = new(_document, state.Tree);
                    if (state.ResizeEdges != PreviewerResizeEdges.None)
                        editor.ResizePointer(state.Selection[0], state.Current - state.Start, state.ResizeEdges);
                    else editor.MovePointer(state.Selection, state.PrimaryInstance, state.Current - state.Start, state.Current);
                }
                _document.SetDesignerInteractionError(null);
            }
            catch (InvalidOperationException error) { _document.SetDesignerInteractionError(error); }
        }
        InvalidateVisual();
        args.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs args)
    {
        base.OnPointerCaptureLost(args);
        _measureAnchor = null;
        if (_previewPointer is not null) ReleasePreviewInput();
        CancelPointerMove();
    }

    private void CancelPointerMove()
    {
        PointerMoveState? state = _pointerMove;
        _pointerMove = null;
        state?.Pointer.Capture(null);
        if (state is not null) Readout?.EndChange();
        if (state is { IsMarquee: true, Moved: true } && _document is { UseExternalPreviewer: true }
            && _document.SyncEngine.GetCurrentText() == state.SourceText)
            SelectFromInput(state.Selection);
        InvalidateVisual();
    }

    private void CommitMarquee(PointerMoveState state)
    {
        Rect rectangle = MarqueeBounds(state);
        List<DesignItem> selected = state.Additive ? state.Selection.ToList() : new();
        foreach (DesignerHostNode node in state.Tree.Nodes)
        {
            if (!node.IsVisible || node.ParentInstanceId != state.ScopeInstance
                || !_hostItems.TryGetValue(node.InstanceId, out DesignItem? item)) continue;
            Rect bounds = new(node.X, node.Y, node.Width, node.Height);
            if (node.Clip is { } clip) bounds = bounds.Intersect(new Rect(clip.X, clip.Y, clip.Width, clip.Height));
            if (bounds.Width > 0 && bounds.Height > 0 && bounds.Intersects(rectangle)
                && selected.All(existing => existing.AstNodeId != item.AstNodeId)) selected.Add(item);
        }
        SelectFromInput(selected);
    }

    private static Rect MarqueeBounds(PointerMoveState state) => new(
        Math.Min(state.Start.X, state.Current.X), Math.Min(state.Start.Y, state.Current.Y),
        Math.Abs(state.Current.X - state.Start.X), Math.Abs(state.Current.Y - state.Start.Y));

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.Handled || KeepsKeyOutsidePreview(args)) return;
        if (ForwardPreviewKey(args, true)) return;
        if (_pointerMove is not null)
        {
            if (args.Key == Key.Escape) CancelPointerMove();
            args.Handled = true;
            return;
        }
        if (_document is { IsDisposed: false } document && document.DesignSurface.IsEditMode)
        {
            System.Windows.Input.ICommand? editCommand = args.KeyModifiers == KeyModifiers.Control ? args.Key switch
            {
                Key.X => document.DesignSurface.CutCommand,
                Key.C => document.DesignSurface.CopyCommand,
                Key.V => document.DesignSurface.PasteCommand,
                Key.D => document.DesignSurface.DuplicateCommand,
                Key.A => document.DesignSurface.SelectAllCommand,
                _ => null
            } : args.Key == Key.Delete && args.KeyModifiers == KeyModifiers.None ? document.DesignSurface.DeleteSelectedCommand : null;
            if (editCommand is not null)
            {
                document.SetSelectedNode(document.SelectedNodeId, SyncSource.DesignSurface);
                args.Handled = true;
                if (editCommand.CanExecute(null)) editCommand.Execute(null);
                return;
            }
        }
        if (CanEditGeometry && args.Key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            double step = args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
            double dx = args.Key == Key.Left ? -step : args.Key == Key.Right ? step : 0;
            double dy = args.Key == Key.Up ? -step : args.Key == Key.Down ? step : 0;
            try
            {
                PreviewerGeometryEditor editor = new(_document!, _geometryTree!);
                DesignItem[] selection = _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>().ToArray();
                if (args.KeyModifiers.HasFlag(KeyModifiers.Control)) editor.Resize(selection, dx, dy);
                else editor.Nudge(selection, dx, dy);
                _pendingGeometryText = _document.SyncEngine.CurrentText;
                _document.SetDesignerInteractionError(null);
            }
            catch (InvalidOperationException error) { _document!.SetDesignerInteractionError(error); }
            args.Handled = true;
            return;
        }
        if (!CanSelect || args.KeyModifiers.HasFlag(KeyModifiers.Control) || args.KeyModifiers.HasFlag(KeyModifiers.Meta)) return;
        if (args.Key == Key.Escape)
        {
            DesignItem? parent = (_document!.DesignSurface.Selection.PrimarySelection as DesignItem)?.Parent as DesignItem;
            SelectFromInput(parent is null ? Array.Empty<DesignItem>() : new[] { parent });
            args.Handled = true;
        }
        else if (args.Key == Key.Tab)
        {
            DesignItem[] items = _designTree!.Nodes.Where(node => node.IsVisible && node.Width > 0 && node.Height > 0)
                .Where(node => _hostItems.ContainsKey(node.InstanceId)).Select(node => _hostItems[node.InstanceId]).ToArray();
            if (items.Length == 0) return;
            int index = Array.IndexOf(items, _document!.DesignSurface.Selection.PrimarySelection as DesignItem);
            int next = (index + (args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1) + items.Length) % items.Length;
            SelectFromInput(new[] { items[next] });
            args.Handled = true;
        }
    }

    private void SelectFromInput(IEnumerable<DesignItem> items)
    {
        if (_document is null) return;
        _updatingSelection = true;
        try
        {
            _document.DesignSurface.SelectDesignItemsFromSync(items);
            _document.SetSelectedNode(_document.DesignSurface.Selection.PrimarySelection?.AstNodeId, SyncSource.DesignSurface);
        }
        finally { _updatingSelection = false; }
        InvalidateVisual();
    }

    private static void DrawDropIndicator(DrawingContext context, LayoutDropIndicator indicator, double scaleX, double scaleY)
    {
        Pen pen = new(Brushes.DeepSkyBlue, 2);
        if (indicator.LineStart is { } start && indicator.LineEnd is { } end)
            context.DrawLine(pen, new Point(start.X * scaleX, start.Y * scaleY), new Point(end.X * scaleX, end.Y * scaleY));
        else
        {
            Rect bounds = indicator.Bounds;
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(30, 0, 191, 255)), pen,
                new Rect(bounds.X * scaleX, bounds.Y * scaleY, bounds.Width * scaleX, bounds.Height * scaleY));
        }
    }

    private void DrawSelection(DrawingContext context)
    {
        if (!CanSelect)
        {
            if (Readout is not null) ReportSelection(null, 0);
            return;
        }
        double scaleX = Bounds.Width / _designTree!.Width;
        double scaleY = Bounds.Height / _designTree.Height;
        PreviewerMovePreview? movePreview = null;
        if (_pointerMove is { Moved: true, IsMarquee: false, ResizeEdges: PreviewerResizeEdges.None } drag)
        {
            try
            {
                movePreview = new PreviewerGeometryEditor(_document!, drag.Tree)
                    .PreviewMove(drag.Selection, drag.PrimaryInstance, drag.Current - drag.Start, drag.Current);
                if (movePreview.TargetBounds is { } dropBounds)
                    context.DrawRectangle(null, new Pen(movePreview.Error is null ? Brushes.DeepSkyBlue : Brushes.OrangeRed, 2),
                        new Rect(dropBounds.X * scaleX, dropBounds.Y * scaleY, dropBounds.Width * scaleX, dropBounds.Height * scaleY));
                if (movePreview.DropIndicator is { } insertion) DrawDropIndicator(context, insertion, scaleX, scaleY);
                if (movePreview.SnapLines is { } lines)
                    foreach (PreviewerSnapLine line in lines)
                    {
                        using (context.PushClip(new Rect(line.Clip.X * scaleX, line.Clip.Y * scaleY,
                            line.Clip.Width * scaleX, line.Clip.Height * scaleY)))
                            context.DrawLine(AlignmentGuidePen, new Point(line.Start.X * scaleX, line.Start.Y * scaleY),
                                new Point(line.End.X * scaleX, line.End.Y * scaleY));
                    }
            }
            catch (InvalidOperationException) { }
        }
        if (_toolboxTarget is { } target)
            DrawDropIndicator(context, target, scaleX, scaleY);
        Rect? selectedBounds = null;
        HashSet<int> selectedInstances = new();
        foreach (DesignItem item in _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>())
        {
            DesignerHostNode? node = _designTree.Nodes.FirstOrDefault(node => node.InstanceId == item.HostInstanceId && node.NodeId == item.AstNodeId);
            if (node is null || !node.IsVisible || node.Width <= 0 || node.Height <= 0) continue;
            selectedInstances.Add(node.InstanceId);
            Rect rectangle = new(node.X * scaleX, node.Y * scaleY, node.Width * scaleX, node.Height * scaleY);
            bool isGhost = false;
            if (_pointerMove is { Moved: true, IsMarquee: false } moving)
            {
                Vector delta = moving.Current - moving.Start;
                if (moving.ResizeEdges != PreviewerResizeEdges.None && node.InstanceId == moving.PrimaryInstance)
                {
                    try
                    {
                        Rect resized = new PreviewerGeometryEditor(_document, moving.Tree)
                            .PreviewResize(moving.Selection[0], delta, moving.ResizeEdges);
                        rectangle = new Rect(resized.X * scaleX, resized.Y * scaleY, resized.Width * scaleX, resized.Height * scaleY);
                        isGhost = true;
                    }
                    catch (InvalidOperationException) { }
                }
                else if (moving.ResizeEdges == PreviewerResizeEdges.None)
                {
                    delta = movePreview?.Delta ?? delta;
                    rectangle = rectangle.Translate(new Vector(delta.X * scaleX, delta.Y * scaleY));
                    isGhost = true;
                }
            }
            selectedBounds = selectedBounds is { } current ? current.Union(rectangle) : rectangle;
            if (_pointerMove is null && _document.DesignSurface.ShowMarginPadding)
                DrawMarginPadding(context, node, scaleX, scaleY);
            Rect clip = !isGhost && node.Clip is { } bounds ? new Rect(bounds.X * scaleX, bounds.Y * scaleY, bounds.Width * scaleX, bounds.Height * scaleY)
                : new Rect(Bounds.Size);
            using (context.PushClip(clip)) context.DrawRectangle(null, new Pen(Brushes.DeepSkyBlue, 2), rectangle);
            if (_pointerMove is null && ReferenceEquals(item, _document.DesignSurface.Selection.PrimarySelection))
                foreach (var handle in ResizeHandles(node))
                    context.DrawRectangle(Brushes.White, new Pen(Brushes.DeepSkyBlue, 1),
                        new Rect(handle.Position.X - 3, handle.Position.Y - 3, 6, 6));
        }
        ReportSelection(selectedBounds is { } shown
            ? new Rect(shown.X / scaleX, shown.Y / scaleY, shown.Width / scaleX, shown.Height / scaleY) : null,
            selectedInstances.Count);
        if (_document.DesignSurface.ShowSpacingGuides && selectedBounds is { } selection)
        {
            Rect logical = new(selection.X / scaleX, selection.Y / scaleY, selection.Width / scaleX, selection.Height / scaleY);
            IReadOnlyList<SpacingGuide> guides = SpacingGuideAdorner.Calculate(logical, _designTree.Nodes
                .Where(node => node.IsVisible && !selectedInstances.Contains(node.InstanceId))
                .Select(node => new Rect(node.X, node.Y, node.Width, node.Height)));
            using (context.PushClip(new Rect(Bounds.Size)))
                _spacingGuideAdorner.Render(context, guides.Select(guide => guide with
                {
                    Start = new Point(guide.Start.X * scaleX, guide.Start.Y * scaleY),
                    End = new Point(guide.End.X * scaleX, guide.End.Y * scaleY)
                }).ToArray());
        }
        if (_pointerMove is { IsMarquee: true, Moved: true } marquee)
        {
            Rect rectangle = MarqueeBounds(marquee);
            rectangle = new Rect(rectangle.X * scaleX, rectangle.Y * scaleY, rectangle.Width * scaleX, rectangle.Height * scaleY);
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 0, 191, 255)), new Pen(Brushes.DeepSkyBlue, 1), rectangle);
        }
    }

    private void DrawMarginPadding(DrawingContext context, DesignerHostNode node, double scaleX, double scaleY)
    {
        if (node.Layout is not { } layout) return;
        DesignerHostNode? parent = _designTree!.Nodes.FirstOrDefault(candidate => candidate.InstanceId == node.ParentInstanceId);
        static Matrix Transform(DesignerHostLayout value) => new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);
        Matrix scale = Matrix.CreateScale(scaleX, scaleY);
        Rect clip = parent?.Clip is { } bounds
            ? new Rect(bounds.X * scaleX, bounds.Y * scaleY, bounds.Width * scaleX, bounds.Height * scaleY)
            : new Rect(Bounds.Size);
        using (context.PushClip(clip))
        {
            Rect marginBounds = parent?.Layout is not null
                ? new Rect(layout.X, layout.Y, layout.Width, layout.Height) : new Rect(0, 0, layout.Width, layout.Height);
            using (context.PushTransform(Transform(parent?.Layout ?? layout) * scale))
                _marginPaddingAdorner.RenderMargin(context, marginBounds,
                    new Thickness(layout.MarginLeft, layout.MarginTop, layout.MarginRight, layout.MarginBottom));
            using (context.PushTransform(Transform(layout) * scale))
                _marginPaddingAdorner.RenderPadding(context, new Rect(0, 0, layout.Width, layout.Height),
                    new Thickness(layout.PaddingLeft, layout.PaddingTop, layout.PaddingRight, layout.PaddingBottom));
        }
    }

    private IEnumerable<(Point Position, PreviewerResizeEdges Edges)> ResizeHandles(DesignerHostNode node)
    {
        if (node.Layout is not { } layout || _designTree is null || node.Width <= 0 || node.Height <= 0) yield break;
        var handles = new[]
        {
            (0d, 0d, PreviewerResizeEdges.Left | PreviewerResizeEdges.Top),
            (0.5, 0d, PreviewerResizeEdges.Top), (1d, 0d, PreviewerResizeEdges.Right | PreviewerResizeEdges.Top),
            (0d, 0.5, PreviewerResizeEdges.Left), (1d, 0.5, PreviewerResizeEdges.Right),
            (0d, 1d, PreviewerResizeEdges.Left | PreviewerResizeEdges.Bottom),
            (0.5, 1d, PreviewerResizeEdges.Bottom), (1d, 1d, PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom)
        };
        foreach (var handle in handles)
        {
            if (node.ParentInstanceId is null && (handle.Item3.HasFlag(PreviewerResizeEdges.Left) || handle.Item3.HasFlag(PreviewerResizeEdges.Top))) continue;
            Point hostPoint = PreviewerGeometryEditor.TransformPoint(layout, new Point(layout.Width * handle.Item1, layout.Height * handle.Item2));
            if (node.Clip is { } clip && !new Rect(clip.X, clip.Y, clip.Width, clip.Height).Contains(hostPoint)) continue;
            yield return (new Point(hostPoint.X * Bounds.Width / _designTree.Width, hostPoint.Y * Bounds.Height / _designTree.Height), handle.Item3);
        }
    }

    private void OnFrameReceived(PreviewerTcpSession session, FrameMessage frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_session, session) || !ReferenceEquals(session.LastFrame, frame)) return;
            _lastFrame = frame;
            _frameDirty = true;
            UpdateFrameSize(frame);
            UpdateStatusText();
            InvalidateVisual();
        });
    }

    private void UpdateFrameSize(FrameMessage? frame)
    {
        if (_designTree is not null || frame is not { Width: > 0, Height: > 0 } ||
            !double.IsFinite(frame.DpiX) || frame.DpiX <= 0 ||
            !double.IsFinite(frame.DpiY) || frame.DpiY <= 0) return;

        // Resize notifications contain pixels without DPI; only a frame carries its own scale.
        _naturalWidth = Math.Clamp(frame.Width * 96d / frame.DpiX, 1, 4096);
        _naturalHeight = Math.Clamp(frame.Height * 96d / frame.DpiY, 1, 4096);
        UpdateScaledBounds();
    }

    private void OnConnectionChanged(PreviewerTcpSession session, IAvaloniaRemoteTransportConnection? connection)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_session, session) || !ReferenceEquals(session.Connection, connection)) return;
            if (connection is null) ReleasePreviewInput();
            UpdateConnectionState(connection);
            UpdateStatusText();
            InvalidateVisual();
        });
    }

    private void OnErrorChanged(PreviewerTcpSession session, PreviewerErrorInfo? error)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_session, session) || !Equals(session.LastErrorInfo, error)) return;
            _hasError = error is not null;
            if (_hasError) ReleasePreviewInput();
            UpdateStatusText();
            InvalidateVisual();
        });
    }

    private void ResetFrameState()
    {
        _toolboxTarget = null;
        CancelPointerMove();
        _geometryTree = null;
        _pendingGeometryText = null;
        _naturalWidth = null;
        _naturalHeight = null;
        _lastFrame = null;
        _frameDirty = false;
        _isConnected = false;
        _hasError = false;
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void UpdateConnectionState(IAvaloniaRemoteTransportConnection? connection)
    {
        _isConnected = connection is not null;
    }

    private void UpdateViewportFromBounds()
    {
        if (_session is null)
        {
            return;
        }

        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        double zoom = PreviewZoom;
        double dpi = 96 * (_topLevel?.RenderScaling ?? 1) * zoom;
        _session.UpdateViewport(_naturalWidth ?? Bounds.Width / zoom, _naturalHeight ?? Bounds.Height / zoom, dpi, dpi);
    }

    private void OnScalingChanged(object? sender, EventArgs e) => UpdateViewportFromBounds();

    private void CopyFrameToBuffer(FrameMessage frame, ILockedFramebuffer buffer)
    {
        if (frame.Format == ProtocolPixelFormat.Bgra8888)
        {
            CopyBgra8888(frame, buffer);
            return;
        }

        if (frame.Format == ProtocolPixelFormat.Rgb565)
        {
            CopyRgb565(frame, buffer);
        }
    }

    private static int GetStride(FrameMessage frame, int bytesPerPixel)
        => frame.Stride > 0 ? frame.Stride : frame.Width * bytesPerPixel;

    private static void CopyBgra8888(FrameMessage frame, ILockedFramebuffer buffer)
    {
        int stride = GetStride(frame, 4);
        int lineLen = Math.Min(frame.Width * 4, buffer.RowBytes);
        for (int y = 0; y < frame.Height; y++)
        {
            Marshal.Copy(frame.Data, y * stride, buffer.Address + y * buffer.RowBytes, lineLen);
        }
    }

    private void CopyRgb565(FrameMessage frame, ILockedFramebuffer buffer)
    {
        int stride = GetStride(frame, 2);
        int destRowBytes = Math.Min(frame.Width * 4, buffer.RowBytes);
        EnsureRowBuffer(destRowBytes);
        if (_rowBuffer is null)
        {
            return;
        }

        for (int y = 0; y < frame.Height; y++)
        {
            int srcIndex = y * stride;
            int destIndex = 0;
            for (int x = 0; x < frame.Width; x++)
            {
                byte lo = frame.Data[srcIndex++];
                byte hi = frame.Data[srcIndex++];
                ushort pixel = (ushort)(lo | (hi << 8));

                int r = (pixel >> 11) & 0x1F;
                int g = (pixel >> 5) & 0x3F;
                int b = pixel & 0x1F;

                _rowBuffer[destIndex++] = (byte)((b << 3) | (b >> 2));
                _rowBuffer[destIndex++] = (byte)((g << 2) | (g >> 4));
                _rowBuffer[destIndex++] = (byte)((r << 3) | (r >> 2));
                _rowBuffer[destIndex++] = 255;
            }

            Marshal.Copy(_rowBuffer, 0, buffer.Address + y * buffer.RowBytes, destRowBytes);
        }
    }

    private void EnsureRowBuffer(int requiredBytes)
    {
        if (_rowBuffer is not null && _rowBuffer.Length >= requiredBytes)
        {
            return;
        }

        _rowBuffer = new byte[requiredBytes];
    }
}
