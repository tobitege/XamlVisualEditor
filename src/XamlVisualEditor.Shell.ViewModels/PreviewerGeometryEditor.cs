using System.Globalization;
using Avalonia;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using static XamlVisualEditor.Designer.Core.LayoutDropGeometry;

namespace XamlVisualEditor.Shell.ViewModels;

[Flags]
public enum PreviewerResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

/// <summary>Describes the drag translation and the prospective container, including rejected drops.</summary>
public sealed record PreviewerMovePreview(Vector Delta, Rect? TargetBounds = null, string? Error = null,
    IReadOnlyList<PreviewerSnapLine>? SnapLines = null, LayoutDropIndicator? DropIndicator = null);

/// <summary>Describes an alignment line and its visible container in viewport coordinates.</summary>
public sealed record PreviewerSnapLine(Point Start, Point End, Rect Clip);

/// <summary>Applies measured layout edits without constructing project controls in the editor process.</summary>
public sealed class PreviewerGeometryEditor(DesignerDocumentViewModel document, DesignerHostSnapshot snapshot)
{
    private static readonly string[] PlacementProperties =
    {
        "Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom", "Grid.Row", "Grid.Column",
        "Grid.RowSpan", "Grid.ColumnSpan", "DockPanel.Dock"
    };

    /// <summary>Previews the measured target cell or insertion line without changing the document.</summary>
    public LayoutDropIndicator? PreviewInsertion(string typeName, string xmlNamespace, Point viewportPoint)
    {
        try
        {
            InsertionPlan plan = PlanInsertion(typeName, xmlNamespace, viewportPoint);
            return PlacementIndicator(plan.Parent, plan.View, viewportPoint, plan.Placement, plan.Index);
        }
        catch (InvalidOperationException) { return null; }
    }

    public Guid InsertControl(string typeName, string xmlNamespace, Point viewportPoint)
    {
        if (!document.SyncEngine.CanEditAst) throw new InvalidOperationException("Correct the XAML errors before inserting controls.");
        InsertionPlan plan = PlanInsertion(typeName, xmlNamespace, viewportPoint);
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Insert " + typeName);
        LayoutEditService editor = new(document.MetadataService, document.DesignSurface.AutoGenerateAutomationIds);
        if (!editor.TryInsert(plan.Parent, plan.Node, out string? error, plan.Index, plan.Placement))
            throw new InvalidOperationException(error);
        edit.Commit();
        document.SetSelectedNode(plan.Node.Id, SyncSource.DesignSurface);
        document.DesignSurface.RequestSelection(new[] { plan.Node.Id });
        return plan.Node.Id;
    }

    private InsertionPlan PlanInsertion(string typeName, string xmlNamespace, Point viewportPoint)
    {
        if (string.IsNullOrWhiteSpace(typeName) || string.IsNullOrWhiteSpace(xmlNamespace)
            || document.SyncEngine.CurrentDocument?.Root is not { } root)
            throw new InvalidOperationException("Choose a control from the loaded toolbox.");
        MutableAstObjectNode node = new() { TypeName = typeName, XmlNamespace = xmlNamespace };
        LayoutEditService editor = new(document.MetadataService);
        DesignerHostNode? hit = snapshot.Nodes.Reverse().FirstOrDefault(candidate => candidate.IsVisible
            && new Rect(candidate.X, candidate.Y, candidate.Width, candidate.Height).Contains(viewportPoint)
            && (candidate.Clip is not { } clip || new Rect(clip.X, clip.Y, clip.Width, clip.Height).Contains(viewportPoint)));
        MutableAstObjectNode? hovered = hit is null ? null : document.NodeMap.FindById(hit.NodeId) as MutableAstObjectNode;
        MutableAstObjectNode? parent = hovered is not null && editor.CanInsert(hovered, node, out _)
            ? hovered : editor.FindInsertionParent(root, hovered, node);
        if (parent is null) throw new InvalidOperationException("Drop on a layout panel or an empty content slot.");
        DesignerHostNode? view = hit;
        while (view is not null && view.NodeId != parent.Id)
        {
            int? parentInstance = view.ParentInstanceId;
            view = snapshot.Nodes.FirstOrDefault(candidate => candidate.InstanceId == parentInstance);
        }
        view ??= snapshot.Nodes.FirstOrDefault(candidate => candidate.NodeId == parent.Id);
        if (view?.Layout is not { } layout) throw new InvalidOperationException("The insertion container has not been measured yet.");
        Vector position = ToLocalVector(layout, new Vector(viewportPoint.X - layout.M31, viewportPoint.Y - layout.M32));
        LayoutPlacement? placement = layout.Kind == "Canvas" ? new(position.X, position.Y)
            : layout.Kind == "Grid" ? new(Row: TrackIndex(layout.RowHeights, layout.RowSpacing, position.Y),
                Column: TrackIndex(layout.ColumnWidths, layout.ColumnSpacing, position.X)) : null;
        int? index = null;
        if (layout.Kind == "Stack")
        {
            index = 0;
            foreach (MutableAstObjectNode child in editor.GetChildren(parent).OfType<MutableAstObjectNode>())
            {
                DesignerHostLayout? measured = snapshot.Nodes.FirstOrDefault(candidate => candidate.NodeId == child.Id
                    && candidate.ParentInstanceId == view.InstanceId)?.Layout;
                if (measured is not null && (layout.IsHorizontal ? position.X < measured.X + measured.Width / 2
                    : position.Y < measured.Y + measured.Height / 2)) break;
                index++;
            }
        }
        return new InsertionPlan(parent, node, view, placement, index);
    }

    private sealed record InsertionPlan(MutableAstObjectNode Parent, MutableAstObjectNode Node,
        DesignerHostNode View, LayoutPlacement? Placement, int? Index);

    private LayoutDropIndicator PlacementIndicator(MutableAstObjectNode parent, DesignerHostNode view, Point pointer,
        LayoutPlacement? placement = null, int? index = null, ISet<Guid>? excluded = null, int rowSpan = 1, int columnSpan = 1)
    {
        DesignerHostLayout layout = view.Layout!;
        Vector position = ToLocalVector(layout, new Vector(pointer.X - layout.M31, pointer.Y - layout.M32));
        LayoutDropIndicator indicator;
        if (layout.Kind == "Grid")
            indicator = GridCell(new Size(layout.Width, layout.Height), layout.RowHeights, layout.ColumnWidths,
                layout.RowSpacing, layout.ColumnSpacing,
                placement?.Row ?? TrackIndex(layout.RowHeights, layout.RowSpacing, position.Y),
                placement?.Column ?? TrackIndex(layout.ColumnWidths, layout.ColumnSpacing, position.X), rowSpan, columnSpan);
        else if (layout.Kind == "Stack")
        {
            Rect?[] children = new LayoutEditService(document.MetadataService).GetChildren(parent).OfType<MutableAstObjectNode>()
                .Where(child => excluded?.Contains(child.Id) != true)
                .Select(child => snapshot.Nodes.FirstOrDefault(node => node.NodeId == child.Id && node.ParentInstanceId == view.InstanceId)?.Layout)
                .Select(child => child is null ? (Rect?)null : new Rect(child.X, child.Y, child.Width, child.Height)).ToArray();
            indicator = StackInsertion(new Size(layout.Width, layout.Height), children,
                index ?? StackIndex(children, layout.IsHorizontal, new Point(position.X, position.Y)), layout.IsHorizontal, layout.Spacing);
        }
        else return new(new Rect(view.X, view.Y, view.Width, view.Height));
        return indicator.Transform(new Matrix(layout.M11, layout.M12, layout.M21, layout.M22, layout.M31, layout.M32));
    }

    public void Arrange(IReadOnlyList<DesignItem> selection, DesignerLayoutOperation operation)
    {
        if (!document.SyncEngine.CanEditAst) throw new InvalidOperationException("Correct the XAML errors before arranging controls.");
        var nodes = Resolve(selection);
        if (nodes.Length < 2) throw new InvalidOperationException("Select at least two controls.");
        MutableAstObjectNode? parent = LayoutEditService.GetParent(nodes[0].Node);
        DesignerHostNode? parentView = snapshot.Nodes.FirstOrDefault(node => node.InstanceId == nodes[0].View.ParentInstanceId);
        if (parent is null || parentView?.NodeId != parent.Id || parentView.Layout is not { } parentLayout
            || nodes.Any(entry => !ReferenceEquals(LayoutEditService.GetParent(entry.Node), parent)))
            throw new InvalidOperationException("Arrange controls from one source container at a time.");
        var primary = nodes.FirstOrDefault(entry => entry.Node.Id == document.DesignSurface.Selection.PrimarySelection?.AstNodeId);
        if (primary.Node is null) primary = nodes[0];
        DesignerHostLayout primaryLayout = primary.View.Layout!;
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction(DesignerLayoutEditor.Title(operation));
        if (operation is DesignerLayoutOperation.SameWidth or DesignerLayoutOperation.SameHeight)
        {
            bool horizontal = operation == DesignerLayoutOperation.SameWidth;
            double size = horizontal ? primaryLayout.Width : primaryLayout.Height;
            foreach (var entry in nodes)
            {
                DesignerHostLayout layout = entry.View.Layout!;
                double min = horizontal ? layout.MinWidth : layout.MinHeight;
                double max = (horizontal ? layout.MaxWidth : layout.MaxHeight) ?? double.PositiveInfinity;
                if (size < min || size > max) throw new InvalidOperationException("The primary control's size exceeds a selected control's Min/Max constraint.");
                SetNumber(entry.Node, horizontal ? "Width" : "Height", size);
            }
        }
        else if (operation is DesignerLayoutOperation.DistributeHorizontally or DesignerLayoutOperation.DistributeVertically)
        {
            bool horizontal = operation == DesignerLayoutOperation.DistributeHorizontally;
            LayoutEditService editor = new(document.MetadataService);
            Guid[] children = editor.GetChildren(parent).OfType<MutableAstObjectNode>().Select(node => node.Id).ToArray();
            bool all = children.Length == nodes.Length && children.All(id => nodes.Any(entry => entry.Node.Id == id));
            if (parentLayout.Kind == "Canvas")
            {
                if (nodes.Length < 3) throw new InvalidOperationException("Canvas distribution requires at least three controls.");
                var ordered = nodes.OrderBy(entry => horizontal ? entry.View.Layout!.X : entry.View.Layout!.Y).ToArray();
                double start = horizontal ? ordered[0].View.Layout!.X : ordered[0].View.Layout!.Y;
                DesignerHostLayout last = ordered[^1].View.Layout!;
                double end = horizontal ? last.X + last.Width : last.Y + last.Height;
                double occupied = ordered.Sum(entry => horizontal ? entry.View.Layout!.Width : entry.View.Layout!.Height);
                double gap = (end - start - occupied) / (ordered.Length - 1);
                foreach (var entry in ordered)
                {
                    DesignerHostLayout layout = entry.View.Layout!;
                    MoveCanvasAxis(entry.Node, horizontal ? layout.CanvasLeft : layout.CanvasTop,
                        horizontal ? layout.CanvasRight : layout.CanvasBottom,
                        horizontal ? layout.X - layout.MarginLeft : layout.Y - layout.MarginTop,
                        start - (horizontal ? layout.X : layout.Y),
                        horizontal ? "Canvas.Left" : "Canvas.Top", horizontal ? "Canvas.Right" : "Canvas.Bottom");
                    start += (horizontal ? layout.Width : layout.Height) + gap;
                }
            }
            else if (parentLayout.Kind == "Grid")
            {
                int count = horizontal ? parentLayout.ColumnWidths.Count : parentLayout.RowHeights.Count;
                string definitions = horizontal ? "ColumnDefinitions" : "RowDefinitions";
                if (count == 0 && all && FindProperty(parent, definitions) is null)
                {
                    count = nodes.Length;
                    SetValue(parent, definitions, string.Join(",", Enumerable.Repeat("*", count)));
                }
                if (count < nodes.Length) throw new InvalidOperationException("The Grid needs enough existing rows or columns. Existing definitions are not replaced.");
                var ordered = nodes.OrderBy(entry => horizontal ? entry.View.Layout!.GridColumn : entry.View.Layout!.GridRow)
                    .ThenBy(entry => Array.IndexOf(children, entry.Node.Id)).ToArray();
                for (int index = 0; index < ordered.Length; index++)
                {
                    int cell = (int)Math.Round(index * (count - 1d) / (ordered.Length - 1), MidpointRounding.AwayFromZero);
                    SetNumber(ordered[index].Node, horizontal ? "Grid.Column" : "Grid.Row", cell);
                    SetNumber(ordered[index].Node, horizontal ? "Grid.ColumnSpan" : "Grid.RowSpan", 1);
                }
            }
            else if (parentLayout.Kind == "Stack")
            {
                if (horizontal != parentLayout.IsHorizontal) throw new InvalidOperationException("Choose distribution along the StackPanel's orientation.");
                if (!all) throw new InvalidOperationException("Select all children before changing their shared StackPanel spacing.");
                double available = horizontal ? parentLayout.Width : parentLayout.Height;
                double occupied = nodes.Sum(entry => horizontal ? entry.View.Layout!.DesiredWidth : entry.View.Layout!.DesiredHeight);
                if (available < occupied) throw new InvalidOperationException("The StackPanel is too small to distribute its children without overlap.");
                SetNumber(parent, "Spacing", (available - occupied) / (nodes.Length - 1));
            }
            else throw new InvalidOperationException("Distribution is available for Canvas, Grid and StackPanel containers.");
        }
        else
        {
            bool horizontal = operation is DesignerLayoutOperation.AlignLeft or DesignerLayoutOperation.CenterHorizontally or DesignerLayoutOperation.AlignRight;
            foreach (var entry in nodes)
            {
                DesignerHostLayout layout = entry.View.Layout!;
                double delta = operation switch
                {
                    DesignerLayoutOperation.AlignLeft => primaryLayout.X - layout.X,
                    DesignerLayoutOperation.CenterHorizontally => primaryLayout.X + primaryLayout.Width / 2 - layout.X - layout.Width / 2,
                    DesignerLayoutOperation.AlignRight => primaryLayout.X + primaryLayout.Width - layout.X - layout.Width,
                    DesignerLayoutOperation.AlignTop => primaryLayout.Y - layout.Y,
                    DesignerLayoutOperation.CenterVertically => primaryLayout.Y + primaryLayout.Height / 2 - layout.Y - layout.Height / 2,
                    DesignerLayoutOperation.AlignBottom => primaryLayout.Y + primaryLayout.Height - layout.Y - layout.Height,
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
                if (parentLayout.Kind != "Canvas")
                {
                    if (Math.Abs(delta) < 0.000001) continue;
                    double left = layout.MarginLeft, right = layout.MarginRight;
                    double top = layout.MarginTop, bottom = layout.MarginBottom;
                    if (horizontal) DesignerLayoutEditor.ShiftMargins(ref left, ref right, delta, operation,
                        layout.HorizontalAlignment, parentLayout.Kind == "Stack" && parentLayout.IsHorizontal);
                    else DesignerLayoutEditor.ShiftMargins(ref top, ref bottom, delta, operation,
                        layout.VerticalAlignment, parentLayout.Kind == "Stack" && !parentLayout.IsHorizontal);
                    SetValue(entry.Node, "Margin", DesignerLayoutEditor.FormatMargin(left, top, right, bottom));
                    continue;
                }
                MoveCanvasAxis(entry.Node, horizontal ? layout.CanvasLeft : layout.CanvasTop,
                    horizontal ? layout.CanvasRight : layout.CanvasBottom,
                    horizontal ? layout.X - layout.MarginLeft : layout.Y - layout.MarginTop, delta,
                    horizontal ? "Canvas.Left" : "Canvas.Top", horizontal ? "Canvas.Right" : "Canvas.Bottom");
            }
        }
        edit.Commit();
    }

    public Rect PreviewResize(DesignItem item, Vector viewportDelta, PreviewerResizeEdges edges) =>
        CalculateResize(item, viewportDelta, edges).Bounds;

    /// <summary>Previews a whole selection moving within or between measured containers.</summary>
    public PreviewerMovePreview PreviewMove(IReadOnlyList<DesignItem> selection, int primaryInstanceId,
        Vector viewportDelta, Point viewportPoint)
    {
        var nodes = Resolve(selection);
        var primary = nodes.First(entry => entry.View.InstanceId == primaryInstanceId);
        ReparentPlan? plan = PlanReparent(nodes, viewportPoint);
        DesignerHostNode? sourceParent = snapshot.Nodes.FirstOrDefault(node => node.InstanceId == primary.View.ParentInstanceId);
        if (sourceParent?.Layout is not { } source)
            throw new InvalidOperationException("Wait for current layout measurements from the previewer.");
        Rect? targetBounds = plan is null ? null : new Rect(plan.View.X, plan.View.Y, plan.View.Width, plan.View.Height);
        if (plan?.Error is not null) return new(viewportDelta, targetBounds, plan.Error);
        DesignerHostNode target = plan?.View ?? sourceParent;
        DesignerHostLayout moved = primary.View.Layout!;
        // The indicator marks the cells the drop assigns, so that it shows where the control lands.
        LayoutDropIndicator? indicator = target.Layout?.Kind is "Grid" or "Stack"
            ? PlacementIndicator(plan?.Parent ?? LayoutEditService.GetParent(primary.Node)!, target, viewportPoint,
                target.Layout!.Kind == "Grid" ? GridTarget(target.Layout, moved, viewportPoint) : null,
                excluded: nodes.Select(entry => entry.Node.Id).ToHashSet(),
                rowSpan: moved.GridRowSpan, columnSpan: moved.GridColumnSpan) : null;
        return SnapCanvasSelection(nodes, primary, source, target, viewportDelta, plan is not null)
            with { TargetBounds = targetBounds, DropIndicator = indicator };
    }

    private PreviewerMovePreview SnapCanvasSelection((MutableAstObjectNode Node, DesignerHostNode View)[] nodes,
        (MutableAstObjectNode Node, DesignerHostNode View) primary, DesignerHostLayout source,
        DesignerHostNode targetView, Vector delta, bool reparent)
    {
        if (targetView.Layout is not { Kind: "Canvas" } target) return new(delta);
        Vector rawDelta = delta;
        delta = reparent ? SnapReparentDelta(primary.View, source, target, delta)
            : TransformVector(source, SnapCanvasDelta(primary.Node, primary.View.Layout!, ToLocalVector(source, delta)));
        DesignSurfaceViewModel surface = document.DesignSurface;
        if (!surface.SnapLinesEnabled && !surface.ShowAlignmentGuides) return new(SnapToManualGuides(primary.View, delta));

        List<double> candidateX = new() { 0, target.Width / 2, target.Width };
        List<double> candidateY = new() { 0, target.Height / 2, target.Height };
        HashSet<Guid> selected = nodes.Select(entry => entry.Node.Id).ToHashSet();
        foreach (DesignerHostNode sibling in snapshot.Nodes)
        {
            if (sibling.ParentInstanceId != targetView.InstanceId || selected.Contains(sibling.NodeId)
                || !sibling.IsVisible || sibling.Layout is null || sibling.Width <= 0 || sibling.Height <= 0) continue;
            Rect bounds = ControlBoundsInParent(sibling, target);
            candidateX.Add(bounds.Left);
            candidateX.Add(bounds.Center.X);
            candidateX.Add(bounds.Right);
            candidateY.Add(bounds.Top);
            candidateY.Add(bounds.Center.Y);
            candidateY.Add(bounds.Bottom);
        }

        Point moved = LayoutOrigin(primary.View, source) + delta;
        Vector position = ToLocalVector(target, new Vector(moved.X - target.M31, moved.Y - target.M32));
        Rect originalBounds = ControlBoundsInParent(primary.View, source);
        Rect current = originalBounds.Translate(
            position - new Vector(primary.View.Layout!.X, primary.View.Layout.Y));
        Rect proposed = current;
        if (surface.SnapLinesEnabled)
        {
            Point rawMoved = LayoutOrigin(primary.View, source) + rawDelta;
            Vector rawPosition = ToLocalVector(target, new Vector(rawMoved.X - target.M31, rawMoved.Y - target.M32));
            proposed = originalBounds.Translate(rawPosition - new Vector(primary.View.Layout.X, primary.View.Layout.Y));
        }
        double zoom = Math.Clamp(surface.Zoom, DesignSurfaceViewModel.MinimumZoom, DesignSurfaceViewModel.MaximumZoom);
        double toleranceX = 6 / Math.Max(0.0001, zoom * Math.Sqrt(target.M11 * target.M11 + target.M12 * target.M12));
        double toleranceY = 6 / Math.Max(0.0001, zoom * Math.Sqrt(target.M21 * target.M21 + target.M22 * target.M22));
        (double dx, double? x) = AlignmentSnapping.FindSnapOffset(new[] { proposed.Left, proposed.Center.X, proposed.Right }, candidateX, toleranceX);
        (double dy, double? y) = AlignmentSnapping.FindSnapOffset(new[] { proposed.Top, proposed.Center.Y, proposed.Bottom }, candidateY, toleranceY);
        if (surface.SnapLinesEnabled)
            delta += TransformVector(target, new Vector(x.HasValue ? proposed.Left + dx - current.Left : 0,
                y.HasValue ? proposed.Top + dy - current.Top : 0));

        List<PreviewerSnapLine> lines = new();
        if (surface.ShowAlignmentGuides)
        {
            Rect clip = new(targetView.X, targetView.Y, targetView.Width, targetView.Height);
            if (targetView.Clip is { } visible) clip = clip.Intersect(new Rect(visible.X, visible.Y, visible.Width, visible.Height));
            if (x is { } vertical)
                lines.Add(new(TransformPoint(target, new Point(vertical, 0)), TransformPoint(target, new Point(vertical, target.Height)), clip));
            if (y is { } horizontal)
                lines.Add(new(TransformPoint(target, new Point(0, horizontal)), TransformPoint(target, new Point(target.Width, horizontal)), clip));
        }
        Vector guided = SnapToManualGuides(primary.View, delta);
        return new(guided, SnapLines: guided == delta ? lines : Array.Empty<PreviewerSnapLine>());
    }

    private Vector SnapToManualGuides(DesignerHostNode node, Vector delta)
    {
        DesignSurfaceViewModel surface = document.DesignSurface;
        if (!surface.SnapToGuides) return delta;
        Rect moved = new Rect(node.X, node.Y, node.Width, node.Height).Translate(delta);
        double tolerance = 6 / Math.Clamp(surface.Zoom, DesignSurfaceViewModel.MinimumZoom, DesignSurfaceViewModel.MaximumZoom);
        (double dx, _) = AlignmentSnapping.FindSnapOffset(new[] { moved.Left, moved.Center.X, moved.Right }, surface.VerticalGuides, tolerance);
        (double dy, _) = AlignmentSnapping.FindSnapOffset(new[] { moved.Top, moved.Center.Y, moved.Bottom }, surface.HorizontalGuides, tolerance);
        return delta + new Vector(dx, dy);
    }

    private static Rect ControlBoundsInParent(DesignerHostNode node, DesignerHostLayout parent)
    {
        DesignerHostLayout layout = node.Layout!;
        double left = double.PositiveInfinity, top = double.PositiveInfinity;
        double right = double.NegativeInfinity, bottom = double.NegativeInfinity;
        for (int corner = 0; corner < 4; corner++)
        {
            Point viewport = TransformPoint(layout, new Point((corner & 1) * layout.Width, (corner >> 1) * layout.Height));
            Vector point = ToLocalVector(parent, new Vector(viewport.X - parent.M31, viewport.Y - parent.M32));
            left = Math.Min(left, point.X);
            top = Math.Min(top, point.Y);
            right = Math.Max(right, point.X);
            bottom = Math.Max(bottom, point.Y);
        }
        return new Rect(left, top, right - left, bottom - top);
    }

    private sealed record ReparentPlan(MutableAstObjectNode Parent, DesignerHostNode View, string? Error = null);

    private ReparentPlan? PlanReparent((MutableAstObjectNode Node, DesignerHostNode View)[] nodes, Point position)
    {
        MutableAstObjectNode? sourceParent = LayoutEditService.GetParent(nodes[0].Node);
        if (sourceParent is null) return null;
        if (nodes.Any(entry => !ReferenceEquals(LayoutEditService.GetParent(entry.Node), sourceParent)))
            throw new InvalidOperationException("Drag controls from one source container at a time.");
        HashSet<Guid> selectedIds = nodes.Select(entry => entry.Node.Id).ToHashSet();
        HashSet<int> ignoredInstances = new();
        foreach (DesignerHostNode view in snapshot.Nodes)
            if (selectedIds.Contains(view.NodeId) || view.ParentInstanceId is { } parentId && ignoredInstances.Contains(parentId))
                ignoredInstances.Add(view.InstanceId);
        DesignerHostNode? hit = snapshot.Nodes.Reverse().FirstOrDefault(view => view.IsVisible && !ignoredInstances.Contains(view.InstanceId)
            && new Rect(view.X, view.Y, view.Width, view.Height).Contains(position)
            && (view.Clip is not { } clip || new Rect(clip.X, clip.Y, clip.Width, clip.Height).Contains(position)));
        LayoutEditService editor = new(document.MetadataService);
        while (hit is not null)
        {
            if (hit.NodeId == sourceParent.Id) return null;
            if (hit.Layout is not null && document.NodeMap.FindById(hit.NodeId) is MutableAstObjectNode parent)
            {
                LayoutContainerKind kind = editor.GetKind(parent);
                bool accepts = editor.CanInsert(parent, nodes[0].Node, out string? error);
                if (kind != LayoutContainerKind.None && (accepts || kind != LayoutContainerKind.Content || hit.ParentInstanceId is null))
                {
                    if (!accepts) return new(parent, hit, error);
                    if (nodes.Length > 1 && kind is LayoutContainerKind.Content or LayoutContainerKind.Child)
                        return new(parent, hit, "Choose a panel for a selection containing more than one control.");
                    try
                    {
                        foreach (var entry in nodes)
                        {
                            if (!editor.CanInsert(parent, entry.Node, out error)) throw new InvalidOperationException(error);
                            foreach (string property in PlacementProperties) GetEditableProperty(entry.Node, property);
                        }
                    }
                    catch (InvalidOperationException invalid) { return new(parent, hit, invalid.Message); }
                    return new(parent, hit);
                }
            }
            int? parentInstance = hit.ParentInstanceId;
            hit = snapshot.Nodes.FirstOrDefault(view => view.InstanceId == parentInstance);
        }
        return null;
    }

    private static Point LayoutOrigin(DesignerHostNode node, DesignerHostLayout parent) =>
        TransformPoint(parent, new Point(node.Layout!.X, node.Layout.Y));

    /// <summary>
    /// Finds the first cell of a moved control: the cell under the pointer, clamped so that the control's spans stay
    /// inside the grid. Preview and drop both use it.
    /// </summary>
    private static LayoutPlacement GridTarget(DesignerHostLayout grid, DesignerHostLayout moved, Point viewportPoint)
    {
        Vector anchor = ToLocalVector(grid, new Vector(viewportPoint.X - grid.M31, viewportPoint.Y - grid.M32));
        return new(
            Row: SpanStart(TrackIndex(grid.RowHeights, grid.RowSpacing, anchor.Y), grid.RowHeights.Count, moved.GridRowSpan),
            Column: SpanStart(TrackIndex(grid.ColumnWidths, grid.ColumnSpacing, anchor.X), grid.ColumnWidths.Count, moved.GridColumnSpan));
    }

    private Vector SnapReparentDelta(DesignerHostNode primary, DesignerHostLayout source,
        DesignerHostLayout target, Vector delta)
    {
        if (target.Kind != "Canvas") return delta;
        Point origin = LayoutOrigin(primary, source) + delta;
        Vector position = ToLocalVector(target, new Vector(origin.X - target.M31, origin.Y - target.M32));
        DesignerHostLayout layout = primary.Layout!;
        bool left = layout.CanvasLeft.HasValue || !layout.CanvasRight.HasValue;
        bool top = layout.CanvasTop.HasValue || !layout.CanvasBottom.HasValue;
        double x = left ? position.X - layout.MarginLeft : target.Width - position.X - layout.Width - layout.MarginRight;
        double y = top ? position.Y - layout.MarginTop : target.Height - position.Y - layout.Height - layout.MarginBottom;
        return delta + TransformVector(target, new Vector(left ? Snap(x) - x : x - Snap(x), top ? Snap(y) - y : y - Snap(y)));
    }

    private void Reparent((MutableAstObjectNode Node, DesignerHostNode View)[] nodes, DesignerHostNode primary,
        DesignerHostLayout source, ReparentPlan plan, Vector delta, Point pointer)
    {
        if (plan.Error is not null) throw new InvalidOperationException(plan.Error);
        DesignerHostLayout target = plan.View.Layout!;
        LayoutEditService editor = new(document.MetadataService);
        MutableAstObjectNode sourceParent = LayoutEditService.GetParent(nodes[0].Node)!;
        var ordered = editor.GetChildren(sourceParent).OfType<MutableAstObjectNode>()
            .Where(node => nodes.Any(entry => ReferenceEquals(entry.Node, node)))
            .Select(node => nodes.First(entry => ReferenceEquals(entry.Node, node))).ToArray();
        if (target.Kind == "Canvas")
            delta = SnapCanvasSelection(nodes, nodes.First(entry => entry.View.InstanceId == primary.InstanceId),
                source, plan.View, delta, true).Delta;
        Vector pointerPosition = ToLocalVector(target, new Vector(pointer.X - target.M31, pointer.Y - target.M32));
        Point primaryOrigin = LayoutOrigin(primary, source);
        int? index = null;
        if (target.Kind is "Stack" or "Panel" or "Items")
        {
            index = 0;
            foreach (MutableAstObjectNode child in editor.GetChildren(plan.Parent).OfType<MutableAstObjectNode>())
            {
                DesignerHostLayout? measured = snapshot.Nodes.FirstOrDefault(view => view.NodeId == child.Id
                    && view.ParentInstanceId == plan.View.InstanceId)?.Layout;
                if (measured is not null && (target.IsHorizontal ? pointerPosition.X < measured.X + measured.Width / 2
                    : pointerPosition.Y < measured.Y + measured.Height / 2)) break;
                index++;
            }
        }
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Move selection to " + plan.Parent.TypeName);
        foreach (var entry in ordered)
        {
            DesignerHostLayout layout = entry.View.Layout!;
            Point origin = LayoutOrigin(entry.View, source);
            LayoutPlacement? placement = target.Kind == "Grid" ? GridTarget(target, layout, pointer + (origin - primaryOrigin)) : null;
            if (!editor.TryMove(entry.Node, plan.Parent, out string? error, index, placement))
                throw new InvalidOperationException(error);
            if (index.HasValue) index++;
            if (target.Kind == "Canvas")
            {
                Point moved = origin + delta;
                Vector position = ToLocalVector(target, new Vector(moved.X - target.M31, moved.Y - target.M32));
                bool left = layout.CanvasLeft.HasValue || !layout.CanvasRight.HasValue;
                bool top = layout.CanvasTop.HasValue || !layout.CanvasBottom.HasValue;
                SetNumber(entry.Node, left ? "Canvas.Left" : "Canvas.Right", left ? position.X - layout.MarginLeft
                    : target.Width - position.X - layout.Width - layout.MarginRight);
                SetNumber(entry.Node, top ? "Canvas.Top" : "Canvas.Bottom", top ? position.Y - layout.MarginTop
                    : target.Height - position.Y - layout.Height - layout.MarginBottom);
            }
            else if (target.Kind == "Dock")
            {
                double edge = Math.Min(target.Width, target.Height) * 0.2;
                SetValue(entry.Node, "DockPanel.Dock", pointerPosition.Y <= edge ? "Top"
                    : pointerPosition.Y >= target.Height - edge ? "Bottom"
                    : pointerPosition.X >= target.Width - edge ? "Right" : "Left");
            }
        }
        edit.Commit();
        document.SetSelectedNode(primary.NodeId, SyncSource.DesignSurface);
        document.DesignSurface.RequestSelection(ordered.Select(entry => entry.Node.Id).ToArray());
    }

    private Vector SnapCanvasDelta(MutableAstObjectNode node, DesignerHostLayout layout, Vector delta)
    {
        double left = CanvasPosition(node, layout.CanvasLeft, layout.CanvasRight, layout.X - layout.MarginLeft, "Canvas.Left", "Canvas.Right");
        double top = CanvasPosition(node, layout.CanvasTop, layout.CanvasBottom, layout.Y - layout.MarginTop, "Canvas.Top", "Canvas.Bottom");
        return new Vector(Snap(left + delta.X) - left, Snap(top + delta.Y) - top);
    }

    private static double CanvasPosition(MutableAstObjectNode node, double? leading, double? trailing,
        double measured, string leadingProperty, string trailingProperty) =>
        FindProperty(node, leadingProperty) is null && leading is null && trailing is { } anchor
            ? measured + anchor - CurrentNumber(node, trailingProperty, anchor)
            : CurrentNumber(node, leadingProperty, leading ?? measured);

    private double Snap(double value)
    {
        double size = document.DesignSurface.GridSnapSize;
        return document.DesignSurface.SnapToGrid && double.IsFinite(size) && size > 0
            ? Math.Round(value / size) * size : value;
    }

    public void ResizePointer(DesignItem item, Vector viewportDelta, PreviewerResizeEdges edges)
    {
        if (!document.SyncEngine.CanEditAst) throw new InvalidOperationException("Correct the XAML errors before resizing controls.");
        ResizePlan plan = CalculateResize(item, viewportDelta, edges);
        DesignerHostLayout measured = plan.View.Layout!;
        bool horizontal = plan.Width != measured.Width;
        bool vertical = plan.Height != measured.Height;
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Resize selection");
        if (horizontal) SetNumber(plan.Node, "Width", plan.Width);
        if (vertical) SetNumber(plan.Node, "Height", plan.Height);
        if (plan.Parent?.Layout is { } parent)
        {
            if (parent.Kind == "Canvas")
            {
                if (plan.PositionDelta.X != 0) MoveCanvasAxis(plan.Node, measured.CanvasLeft, measured.CanvasRight,
                    measured.X - measured.MarginLeft, plan.PositionDelta.X, "Canvas.Left", "Canvas.Right");
                if (plan.PositionDelta.Y != 0) MoveCanvasAxis(plan.Node, measured.CanvasTop, measured.CanvasBottom,
                    measured.Y - measured.MarginTop, plan.PositionDelta.Y, "Canvas.Top", "Canvas.Bottom");
            }
            else if (horizontal || vertical)
            {
                double left = measured.MarginLeft, right = measured.MarginRight;
                double top = measured.MarginTop, bottom = measured.MarginBottom;
                if (horizontal) ResizeMargins(ref left, ref right, plan.Width - measured.Width, plan.PositionDelta.X,
                    edges.HasFlag(PreviewerResizeEdges.Left), measured.HorizontalAlignment,
                    parent.Kind == "Stack" && parent.IsHorizontal);
                if (vertical) ResizeMargins(ref top, ref bottom, plan.Height - measured.Height, plan.PositionDelta.Y,
                    edges.HasFlag(PreviewerResizeEdges.Top), measured.VerticalAlignment,
                    parent.Kind == "Stack" && !parent.IsHorizontal);
                if (left != measured.MarginLeft || top != measured.MarginTop || right != measured.MarginRight || bottom != measured.MarginBottom)
                    SetValue(plan.Node, "Margin", string.Join(",", new[] { left, top, right, bottom }
                        .Select(value => value.ToString("0.####", CultureInfo.InvariantCulture))));
            }
        }
        edit.Commit();
    }

    private static void ResizeMargins(ref double leading, ref double trailing, double sizeDelta,
        double positionDelta, bool fromLeading, string alignment, bool stackAxis)
    {
        if (stackAxis || alignment is "Left" or "Top") leading += positionDelta;
        else if (alignment is "Right" or "Bottom") trailing -= positionDelta + sizeDelta;
        else if (fromLeading) leading += 2 * positionDelta + sizeDelta;
        else trailing -= 2 * positionDelta + sizeDelta;
    }

    private ResizePlan CalculateResize(DesignItem item, Vector viewportDelta, PreviewerResizeEdges edges)
    {
        var entry = Resolve(new[] { item })[0];
        DesignerHostLayout measured = entry.View.Layout!;
        DesignerHostNode? parent = snapshot.Nodes.FirstOrDefault(node => node.InstanceId == entry.View.ParentInstanceId);
        bool left = edges.HasFlag(PreviewerResizeEdges.Left), top = edges.HasFlag(PreviewerResizeEdges.Top);
        bool right = edges.HasFlag(PreviewerResizeEdges.Right), bottom = edges.HasFlag(PreviewerResizeEdges.Bottom);
        if (edges == PreviewerResizeEdges.None || left && right || top && bottom)
            throw new InvalidOperationException("Choose one resize handle.");
        if (parent is null && (left || top)) throw new InvalidOperationException("Resize the document root from its right or bottom side.");
        Vector delta = ToLocalVector(measured, viewportDelta);
        double width = left || right ? Limit(Snap(measured.Width + (left ? -delta.X : delta.X)), measured.MinWidth, measured.MaxWidth) : measured.Width;
        double height = top || bottom ? Limit(Snap(measured.Height + (top ? -delta.Y : delta.Y)), measured.MinHeight, measured.MaxHeight) : measured.Height;
        Vector guidedSize = SnapResizeToGuides(measured, new Vector(width, height),
            new Vector(left ? -1 : right ? 1 : 0, top ? -1 : bottom ? 1 : 0));
        if (left || right) width = Limit(guidedSize.X, measured.MinWidth, measured.MaxWidth);
        if (top || bottom) height = Limit(guidedSize.Y, measured.MinHeight, measured.MaxHeight);
        Vector anchorChange = new(left ? width - measured.Width : 0, top ? height - measured.Height : 0);
        Vector viewportShift = -TransformVector(measured, anchorChange);
        Point[] corners = new[] { new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height) }
            .Select(point => TransformPoint(measured, point) + viewportShift).ToArray();
        Rect bounds = new(new Point(corners.Min(point => point.X), corners.Min(point => point.Y)),
            new Point(corners.Max(point => point.X), corners.Max(point => point.Y)));
        Vector originChange = measured.OriginIsRelative
            ? new Vector((width - measured.Width) * measured.OriginX, (height - measured.Height) * measured.OriginY) : default;
        Vector positionDelta = parent?.Layout is { } parentLayout
            ? ToLocalVector(parentLayout, TransformVector(measured, originChange - anchorChange)) - originChange : default;
        return new ResizePlan(entry.Node, entry.View, parent, width, height, positionDelta, bounds);
    }

    private Vector SnapResizeToGuides(DesignerHostLayout measured, Vector size, Vector axes)
    {
        DesignSurfaceViewModel surface = document.DesignSurface;
        if (!surface.SnapToGuides) return size;
        Point handle = TransformPoint(measured, new Point(
            axes.X < 0 ? measured.Width - size.X : axes.X > 0 ? size.X : measured.Width / 2,
            axes.Y < 0 ? measured.Height - size.Y : axes.Y > 0 ? size.Y : measured.Height / 2));
        double tolerance = 6 / Math.Clamp(surface.Zoom, DesignSurfaceViewModel.MinimumZoom, DesignSurfaceViewModel.MaximumZoom);
        if (axes.X != 0 && axes.Y != 0)
        {
            (double dx, _) = AlignmentSnapping.FindSnapOffset(new[] { handle.X }, surface.VerticalGuides, tolerance);
            (double dy, _) = AlignmentSnapping.FindSnapOffset(new[] { handle.Y }, surface.HorizontalGuides, tolerance);
            Vector local = ToLocalVector(measured, new Vector(dx, dy));
            return size + new Vector(local.X * axes.X, local.Y * axes.Y);
        }

        // A side handle has one degree of freedom, even when its control is rotated or skewed.
        Vector direction = TransformVector(measured, axes);
        double length = direction.Length;
        double? change = null;
        if (Math.Abs(direction.X) > 0.000001)
        {
            (double offset, double? guide) = AlignmentSnapping.FindSnapOffset(new[] { handle.X },
                surface.VerticalGuides, tolerance * Math.Abs(direction.X) / length);
            if (guide.HasValue) change = offset / direction.X;
        }
        if (Math.Abs(direction.Y) > 0.000001)
        {
            (double offset, double? guide) = AlignmentSnapping.FindSnapOffset(new[] { handle.Y },
                surface.HorizontalGuides, tolerance * Math.Abs(direction.Y) / length);
            double candidate = offset / direction.Y;
            if (guide.HasValue && (change is null || Math.Abs(candidate) < Math.Abs(change.Value))) change = candidate;
        }
        return change is { } adjustment
            ? size + new Vector(axes.X != 0 ? adjustment : 0, axes.Y != 0 ? adjustment : 0)
            : size;
    }

    public static Point TransformPoint(DesignerHostLayout layout, Point point) =>
        new(point.X * layout.M11 + point.Y * layout.M21 + layout.M31,
            point.X * layout.M12 + point.Y * layout.M22 + layout.M32);

    private static Vector TransformVector(DesignerHostLayout layout, Vector vector) =>
        new(vector.X * layout.M11 + vector.Y * layout.M21, vector.X * layout.M12 + vector.Y * layout.M22);

    private sealed record ResizePlan(MutableAstObjectNode Node, DesignerHostNode View, DesignerHostNode? Parent,
        double Width, double Height, Vector PositionDelta, Rect Bounds);

    public void Nudge(IReadOnlyList<DesignItem> selection, double dx, double dy) => Move(selection, dx, dy, false);

    public void MovePointer(IReadOnlyList<DesignItem> selection, int primaryInstanceId, Vector viewportDelta, Point viewportPoint)
    {
        var nodes = Resolve(selection);
        var primary = nodes.FirstOrDefault(entry => entry.View.InstanceId == primaryInstanceId);
        if (primary.Node is null) throw new InvalidOperationException("The dragged control is no longer selected.");
        MutableAstObjectNode? parent = LayoutEditService.GetParent(primary.Node);
        DesignerHostNode? parentView = snapshot.Nodes.FirstOrDefault(node => node.InstanceId == primary.View.ParentInstanceId);
        DesignerHostLayout? layout = parentView?.Layout;
        if (parent is null || parentView?.NodeId != parent.Id || layout is null
            || nodes.Any(entry => !ReferenceEquals(LayoutEditService.GetParent(entry.Node), parent)))
            throw new InvalidOperationException("Drag controls from one source container at a time.");
        if (PlanReparent(nodes, viewportPoint) is { } reparent)
        {
            Reparent(nodes, primary.View, layout, reparent, viewportDelta, viewportPoint);
            return;
        }
        Vector delta = ToLocalVector(layout, viewportDelta);
        Vector position = ToLocalVector(layout, new Vector(viewportPoint.X - layout.M31, viewportPoint.Y - layout.M32));
        switch (layout.Kind)
        {
            case "Canvas":
                delta = ToLocalVector(layout, SnapCanvasSelection(nodes, primary, layout, parentView, viewportDelta, false).Delta);
                Move(selection, delta.X, delta.Y, false);
                return;
            case "Grid":
                LayoutPlacement cell = GridTarget(layout, primary.View.Layout!, viewportPoint);
                Move(selection, cell.Column!.Value - CurrentNumber(primary.Node, "Grid.Column", primary.View.Layout!.GridColumn),
                    cell.Row!.Value - CurrentNumber(primary.Node, "Grid.Row", primary.View.Layout!.GridRow), true);
                return;
            case "Stack":
            case "Panel":
                LayoutEditService editor = new(document.MetadataService);
                MutableAstObjectNode[] ordered = editor.GetChildren(parent).OfType<MutableAstObjectNode>()
                    .Where(node => nodes.Any(entry => ReferenceEquals(entry.Node, node))).ToArray();
                bool horizontal = layout.IsHorizontal;
                double coordinate = horizontal ? position.X : position.Y;
                MutableAstObjectNode? target = editor.GetChildren(parent).OfType<MutableAstObjectNode>()
                    .Where(node => !ordered.Contains(node)).FirstOrDefault(node =>
                    {
                        DesignerHostLayout? sibling = snapshot.Nodes.FirstOrDefault(view => view.NodeId == node.Id
                            && view.ParentInstanceId == parentView.InstanceId)?.Layout;
                        return sibling is not null && coordinate < (horizontal ? sibling.X + sibling.Width / 2 : sibling.Y + sibling.Height / 2);
                    });
                using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Reorder selection"))
                {
                    foreach (MutableAstObjectNode node in ordered)
                    {
                        bool moved = target is null ? editor.TryMove(node, parent, out string? error)
                            : editor.TryMoveRelative(node, target, DropPosition.Before, out error);
                        if (!moved) throw new InvalidOperationException(error);
                    }
                    edit.Commit();
                }
                return;
            case "Dock":
                double edge = Math.Min(layout.Width, layout.Height) * 0.2;
                string dock = position.Y <= edge ? "Top" : position.Y >= layout.Height - edge ? "Bottom"
                    : position.X >= layout.Width - edge ? "Right" : "Left";
                using (DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Dock selection"))
                {
                    foreach (var entry in nodes) SetValue(entry.Node, "DockPanel.Dock", dock);
                    edit.Commit();
                }
                return;
            default:
                throw new InvalidOperationException("The selected container does not support pointer placement.");
        }
    }

    private static Vector ToLocalVector(DesignerHostLayout layout, Vector vector)
    {
        double determinant = layout.M11 * layout.M22 - layout.M12 * layout.M21;
        if (!double.IsFinite(determinant) || Math.Abs(determinant) < 0.0000001)
            throw new InvalidOperationException("The container transform cannot be inverted.");
        return new Vector((vector.X * layout.M22 - vector.Y * layout.M21) / determinant,
            (vector.Y * layout.M11 - vector.X * layout.M12) / determinant);
    }

    private void Move(IReadOnlyList<DesignItem> selection, double dx, double dy, bool gridCellDelta)
    {
        if (!document.SyncEngine.CanEditAst) throw new InvalidOperationException("Correct the XAML errors before moving controls.");
        var nodes = Resolve(selection);
        LayoutEditService editor = new(document.MetadataService);
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Move selection");
        foreach (var entry in nodes.OrderBy(item => SourceIndex(editor, item.Node) * (dx > 0 || dy > 0 ? -1 : 1)))
        {
            MutableAstObjectNode? parent = LayoutEditService.GetParent(entry.Node);
            DesignerHostNode? parentView = snapshot.Nodes.FirstOrDefault(item => item.InstanceId == entry.View.ParentInstanceId);
            DesignerHostLayout? parentLayout = parentView?.Layout;
            if (parent is null || parentView?.NodeId != parent.Id || parentLayout is null)
                throw new InvalidOperationException("The selected control has no editable source container.");
            DesignerHostLayout measured = entry.View.Layout!;
            switch (parentLayout.Kind)
            {
                case "Canvas":
                    if (dx != 0) MoveCanvasAxis(entry.Node, measured.CanvasLeft, measured.CanvasRight,
                        measured.X - measured.MarginLeft, dx, "Canvas.Left", "Canvas.Right");
                    if (dy != 0) MoveCanvasAxis(entry.Node, measured.CanvasTop, measured.CanvasBottom,
                        measured.Y - measured.MarginTop, dy, "Canvas.Top", "Canvas.Bottom");
                    break;
                case "Grid":
                    if (dx != 0) SetNumber(entry.Node, "Grid.Column", Math.Clamp(CurrentNumber(entry.Node, "Grid.Column", measured.GridColumn) + (gridCellDelta ? dx : Math.Sign(dx)),
                        0, Math.Max(0, Math.Max(1, parentLayout.ColumnWidths.Count) - Math.Max(1, measured.GridColumnSpan))));
                    if (dy != 0) SetNumber(entry.Node, "Grid.Row", Math.Clamp(CurrentNumber(entry.Node, "Grid.Row", measured.GridRow) + (gridCellDelta ? dy : Math.Sign(dy)),
                        0, Math.Max(0, Math.Max(1, parentLayout.RowHeights.Count) - Math.Max(1, measured.GridRowSpan))));
                    break;
                case "Stack":
                case "Panel":
                    int offset = Math.Sign(parentLayout.Kind == "Stack" ? parentLayout.IsHorizontal ? dx : dy : dx != 0 ? dx : dy);
                    if (offset == 0) break;
                    MutableAstObjectNode[] siblings = editor.GetChildren(parent).OfType<MutableAstObjectNode>().ToArray();
                    int index = Array.IndexOf(siblings, entry.Node);
                    int target = Math.Clamp(index + offset, 0, siblings.Length - 1);
                    if (target != index && !editor.TryMoveRelative(entry.Node, siblings[target],
                            offset < 0 ? DropPosition.Before : DropPosition.After, out string? error))
                        throw new InvalidOperationException(error);
                    break;
                case "Dock":
                    SetValue(entry.Node, "DockPanel.Dock", Math.Abs(dx) > Math.Abs(dy)
                        ? dx < 0 ? "Left" : "Right" : dy < 0 ? "Top" : "Bottom");
                    break;
                default:
                    throw new InvalidOperationException("Move the control within a Canvas, Grid or ordered panel.");
            }
        }
        edit.Commit();
    }

    public void Resize(IReadOnlyList<DesignItem> selection, double dw, double dh)
    {
        if (!document.SyncEngine.CanEditAst) throw new InvalidOperationException("Correct the XAML errors before resizing controls.");
        var nodes = Resolve(selection);
        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Resize selection");
        foreach (var entry in nodes)
        {
            DesignerHostLayout measured = entry.View.Layout!;
            if (dw != 0) SetNumber(entry.Node, "Width", Limit(CurrentNumber(entry.Node, "Width", measured.Width) + dw, measured.MinWidth, measured.MaxWidth));
            if (dh != 0) SetNumber(entry.Node, "Height", Limit(CurrentNumber(entry.Node, "Height", measured.Height) + dh, measured.MinHeight, measured.MaxHeight));
        }
        edit.Commit();
    }

    private (MutableAstObjectNode Node, DesignerHostNode View)[] Resolve(IReadOnlyList<DesignItem> selection)
    {
        if (selection.Count == 0) throw new InvalidOperationException("Select a control first.");
        if (selection.Select(item => item.AstNodeId).Distinct().Count() != selection.Count)
            throw new InvalidOperationException("Select source controls rather than repeated instances of the same template element.");
        return selection.Select(item =>
        {
            DesignerHostNode? view = snapshot.Nodes.FirstOrDefault(node => node.InstanceId == item.HostInstanceId && node.NodeId == item.AstNodeId);
            if (view?.Layout is null || document.NodeMap.FindById(item.AstNodeId) is not MutableAstObjectNode node)
                throw new InvalidOperationException("Wait for current layout measurements from the previewer.");
            return (node, view);
        }).ToArray();
    }

    private static int SourceIndex(LayoutEditService editor, MutableAstObjectNode node) =>
        LayoutEditService.GetParent(node) is { } parent ? editor.GetChildren(parent).ToList().IndexOf(node) : 0;

    private static void MoveCanvasAxis(MutableAstObjectNode node, double? leading, double? trailing,
        double measuredPosition, double delta, string leadingProperty, string trailingProperty)
    {
        if (FindProperty(node, leadingProperty) is null && leading is null && trailing is { } anchor)
            SetNumber(node, trailingProperty, CurrentNumber(node, trailingProperty, anchor) - delta);
        else SetNumber(node, leadingProperty, CurrentNumber(node, leadingProperty, leading ?? measuredPosition) + delta);
    }

    private static double Limit(double value, double minimum, double? maximum)
    {
        double min = Math.Max(1, minimum);
        return Math.Clamp(value, min, Math.Max(min, maximum ?? double.PositiveInfinity));
    }

    private static void SetNumber(MutableAstObjectNode node, string name, double value)
    {
        if (!double.IsFinite(value)) throw new InvalidOperationException("The layout value must be finite.");
        SetValue(node, name, value.ToString("0.####", CultureInfo.InvariantCulture));
    }

    private static void SetValue(MutableAstObjectNode node, string name, string value)
    {
        MutableAstPropertyNode? property = GetEditableProperty(node, name);
        node.SetPropertyValue(property?.PropertyName ?? name, value);
    }

    private static MutableAstPropertyNode? GetEditableProperty(MutableAstObjectNode node, string name)
    {
        MutableAstPropertyNode? property = FindProperty(node, name);
        if (property is not null && (property.Value is not MutableAstTextNode text || text.Text.TrimStart().StartsWith('{')))
            throw new InvalidOperationException(name + " contains an expression; edit or reset it before applying a layout change.");
        return property;
    }

    private static MutableAstPropertyNode? FindProperty(MutableAstObjectNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.PropertyName == name
            || property.PropertyName.EndsWith(":" + name, StringComparison.Ordinal));

    private static double CurrentNumber(MutableAstObjectNode node, string name, double measured) =>
        FindProperty(node, name)?.Value is MutableAstTextNode text
        && double.TryParse(text.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
        && double.IsFinite(value) ? value : measured;
}
