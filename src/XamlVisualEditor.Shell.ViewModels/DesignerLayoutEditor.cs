using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

public enum DesignerLayoutOperation
{
    AlignLeft, CenterHorizontally, AlignRight, AlignTop, CenterVertically, AlignBottom,
    DistributeHorizontally, DistributeVertically, SameWidth, SameHeight
}

/// <summary>Plans layout-specific multi-selection changes before committing one document transaction.</summary>
public sealed class DesignerLayoutEditor
{
    // Fluent System Icons 1.1.341, regular 20px; see THIRD-PARTY-NOTICES.md.
    private const string AlignLeftIconPath = "M3.5 2a.5.5 0 0 0-.5.5v15a.5.5 0 0 0 1 0v-15a.5.5 0 0 0-.5-.5M7 11a2 2 0 0 0-2 2v1c0 1.1.9 2 2 2h5a2 2 0 0 0 2-2v-1a2 2 0 0 0-2-2zm-1 2a1 1 0 0 1 1-1h5a1 1 0 0 1 1 1v1a1 1 0 0 1-1 1H7a1 1 0 0 1-1-1zM5 6c0-1.1.9-2 2-2h8a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1z";
    private const string CenterHorizontallyIconPath = "M10.5 18a.5.5 0 0 1-.5-.5V16H8a2 2 0 0 1-2-2v-1c0-1.1.9-2 2-2h2V9H6a2 2 0 0 1-2-2V6c0-1.1.9-2 2-2h4V2.5a.5.5 0 0 1 1 0V4h4a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2h-4v2h2a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2h-2v1.5a.5.5 0 0 1-.5.5M6 5a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h9a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1zm2 7a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h5a1 1 0 0 0 1-1v-1a1 1 0 0 0-1-1z";
    private const string AlignRightIconPath = "M16.5 2c.28 0 .5.22.5.5v15a.5.5 0 0 1-1 0v-15c0-.28.22-.5.5-.5M13 11a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2v-1c0-1.1.9-2 2-2zm1 2a1 1 0 0 0-1-1H8a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h5a1 1 0 0 0 1-1zm1-7a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v1c0 1.1.9 2 2 2h8a2 2 0 0 0 2-2zm-2-1a1 1 0 0 1 1 1v1a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1z";
    private const string AlignTopIconPath = "M2 3.5c0-.28.22-.5.5-.5h15a.5.5 0 0 1 0 1h-15a.5.5 0 0 1-.5-.5M11 7c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v5a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V7a1 1 0 0 0-1-1zM6 5a2 2 0 0 0-2 2v8c0 1.1.9 2 2 2h1a2 2 0 0 0 2-2V7a2 2 0 0 0-2-2zM5 7a1 1 0 0 1 1-1h1a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1z";
    private const string CenterVerticallyIconPath = "M18 9.5a.5.5 0 0 1-.5.5H16v2a2 2 0 0 1-2 2h-1a2 2 0 0 1-2-2v-2H9v4a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-4H2.5a.5.5 0 0 1 0-1H4V5c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v4h2V7c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v2h1.5c.28 0 .5.22.5.5M5 14a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1H6a1 1 0 0 0-1 1zm7-2a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V7a1 1 0 0 0-1-1h-1a1 1 0 0 0-1 1z";
    private const string AlignBottomIconPath = "M2 16.5c0 .28.22.5.5.5h15a.5.5 0 0 0 0-1h-15a.5.5 0 0 0-.5.5m9-3.5c0 1.1.9 2 2 2h1a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-1a2 2 0 0 0-2 2zm2 1a1 1 0 0 1-1-1V8a1 1 0 0 1 1-1h1a1 1 0 0 1 1 1v5a1 1 0 0 1-1 1zm-7 1a2 2 0 0 1-2-2V5c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2zm-1-2a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V5a1 1 0 0 0-1-1H6a1 1 0 0 0-1 1z";
    private const string DistributeHorizontallyIconPath = "M3 16a2 2 0 0 1-2-2V6c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2zm-1-2a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1H3a1 1 0 0 0-1 1zm7 2a2 2 0 0 1-2-2V6c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2zm-1-2a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1H9a1 1 0 0 0-1 1zm7 2a2 2 0 0 1-2-2V6c0-1.1.9-2 2-2h1a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2zm-1-2a1 1 0 0 0 1 1h1a1 1 0 0 0 1-1V6a1 1 0 0 0-1-1h-1a1 1 0 0 0-1 1z";
    private const string DistributeVerticallyIconPath = "M4 3c0-1.1.9-2 2-2h8a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1V3a1 1 0 0 0-1-1z M4 15c0-1.1.9-2 2-2h8a2 2 0 0 1 2 2v1a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v1a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1v-1a1 1 0 0 0-1-1z M6 7a2 2 0 0 0-2 2v1c0 1.1.9 2 2 2h8a2 2 0 0 0 2-2V9a2 2 0 0 0-2-2zM5 9a1 1 0 0 1 1-1h8a1 1 0 0 1 1 1v1a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1z";
    private const string SameWidthIconPath = "m16.37 15-1.18 1.14a.5.5 0 1 0 .7.72l1.88-1.82c.3-.3.3-.78 0-1.08l-1.88-1.82a.5.5 0 1 0-.7.72L16.38 14H12.5a.5.5 0 1 0 0 1zM15 3a2 2 0 0 1 2 2v4.5a.5.5 0 0 1-1 0V5a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v4.5a.5.5 0 0 1-1 0V5c0-1.1.9-2 2-2zM4.81 16.14 3.63 15H7.5a.5.5 0 1 0 0-1H3.63l1.18-1.14a.5.5 0 0 0-.7-.72l-1.88 1.82c-.3.3-.3.79 0 1.08l1.89 1.82a.5.5 0 1 0 .69-.72";
    private const string SameHeightIconPath = "m15 3.63 1.14 1.18a.5.5 0 1 0 .72-.7l-1.82-1.88a.75.75 0 0 0-1.08 0L12.14 4.1a.5.5 0 1 0 .72.7L14 3.63V7.5a.5.5 0 0 0 1 0zM3 5c0-1.1.9-2 2-2h4.5a.5.5 0 0 1 0 1H5a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1h4.5a.5.5 0 0 1 0 1H5a2 2 0 0 1-2-2zm13.14 10.2L15 16.36V12.5a.5.5 0 1 0-1 0v3.87l-1.14-1.18a.5.5 0 1 0-.72.7l1.82 1.88c.3.3.79.3 1.08 0l1.82-1.88a.5.5 0 1 0-.72-.7";

    /// <summary>Fluent grid symbol used by the shared snap-to-grid toggle.</summary>
    public const string SnapToGridIconPath = "M7.5 11c.83 0 1.5.67 1.5 1.5v4c0 .83-.67 1.5-1.5 1.5h-4A1.5 1.5 0 0 1 2 16.5v-4c0-.83.67-1.5 1.5-1.5zm9 0c.83 0 1.5.67 1.5 1.5v4c0 .83-.67 1.5-1.5 1.5h-4a1.5 1.5 0 0 1-1.5-1.5v-4c0-.83.67-1.5 1.5-1.5zm-9 1h-4a.5.5 0 0 0-.5.5v4c0 .28.22.5.5.5h4a.5.5 0 0 0 .5-.5v-4a.5.5 0 0 0-.5-.5m9 0h-4a.5.5 0 0 0-.5.5v4c0 .28.22.5.5.5h4a.5.5 0 0 0 .5-.5v-4a.5.5 0 0 0-.5-.5m-9-10C8.33 2 9 2.67 9 3.5v4C9 8.33 8.33 9 7.5 9h-4A1.5 1.5 0 0 1 2 7.5v-4C2 2.67 2.67 2 3.5 2zm9 0c.83 0 1.5.67 1.5 1.5v4c0 .83-.67 1.5-1.5 1.5h-4A1.5 1.5 0 0 1 11 7.5v-4c0-.83.67-1.5 1.5-1.5zm-9 1h-4a.5.5 0 0 0-.5.5v4c0 .28.22.5.5.5h4a.5.5 0 0 0 .5-.5v-4a.5.5 0 0 0-.5-.5m9 0h-4a.5.5 0 0 0-.5.5v4c0 .28.22.5.5.5h4a.5.5 0 0 0 .5-.5v-4a.5.5 0 0 0-.5-.5";

    private readonly DesignerDocumentViewModel _document;

    public DesignerLayoutEditor(DesignerDocumentViewModel document) => _document = document;

    public static string Title(DesignerLayoutOperation operation) => operation switch
    {
        DesignerLayoutOperation.AlignLeft => UiStrings.AlignLeft,
        DesignerLayoutOperation.CenterHorizontally => UiStrings.CenterHorizontally,
        DesignerLayoutOperation.AlignRight => UiStrings.AlignRight,
        DesignerLayoutOperation.AlignTop => UiStrings.AlignTop,
        DesignerLayoutOperation.CenterVertically => UiStrings.CenterVertically,
        DesignerLayoutOperation.AlignBottom => UiStrings.AlignBottom,
        DesignerLayoutOperation.DistributeHorizontally => UiStrings.DistributeHorizontally,
        DesignerLayoutOperation.DistributeVertically => UiStrings.DistributeVertically,
        DesignerLayoutOperation.SameWidth => UiStrings.SameWidth,
        DesignerLayoutOperation.SameHeight => UiStrings.SameHeight,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static string IconData(DesignerLayoutOperation operation) => operation switch
    {
        DesignerLayoutOperation.AlignLeft => AlignLeftIconPath,
        DesignerLayoutOperation.CenterHorizontally => CenterHorizontallyIconPath,
        DesignerLayoutOperation.AlignRight => AlignRightIconPath,
        DesignerLayoutOperation.AlignTop => AlignTopIconPath,
        DesignerLayoutOperation.CenterVertically => CenterVerticallyIconPath,
        DesignerLayoutOperation.AlignBottom => AlignBottomIconPath,
        DesignerLayoutOperation.DistributeHorizontally => DistributeHorizontallyIconPath,
        DesignerLayoutOperation.DistributeVertically => DistributeVerticallyIconPath,
        DesignerLayoutOperation.SameWidth => SameWidthIconPath,
        DesignerLayoutOperation.SameHeight => SameHeightIconPath,
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public void Apply(DesignerLayoutOperation operation)
    {
        if (_document.IsDisposed || !_document.SyncEngine.CanEditAst)
            throw new InvalidOperationException("Open a valid designer document first.");
        DesignSurfaceViewModel surface = _document.DesignSurface;
        DesignItem[] selection = surface.Selection.SelectedItems.OfType<DesignItem>().ToArray();
        if (selection.Length < 2) throw new InvalidOperationException("Select at least two sibling controls.");
        if (selection.Select(item => item.AstNodeId).Distinct().Count() != selection.Length)
            throw new InvalidOperationException("Select controls from one template instance, not repeated instances of the same source element.");
        MutableAstObjectNode[] nodes = selection.Select(item => _document.NodeMap.FindById(item.AstNodeId))
            .OfType<MutableAstObjectNode>().ToArray();
        if (nodes.Length != selection.Length) throw new InvalidOperationException("The selection changed. Select the controls again.");
        MutableAstObjectNode? parent = LayoutEditService.GetParent(nodes[0]);
        if (parent is null || nodes.Any(node => !ReferenceEquals(LayoutEditService.GetParent(node), parent)))
            throw new InvalidOperationException("Layout actions require controls in the same container.");

        if (_document.UseExternalPreviewer)
        {
            DesignerHostSnapshot snapshot = _document.PreviewerSession?.LastDesignTree
                ?? throw new InvalidOperationException("Wait for current previewer layout measurements.");
            new PreviewerGeometryEditor(_document, snapshot).Arrange(selection, operation);
            return;
        }

        Control[] controls = selection.Select(item => surface.ControlMap.FirstOrDefault(pair => ReferenceEquals(pair.Value, item)).Key).ToArray();
        if (controls.Any(control => control is null)) throw new InvalidOperationException("Render the selected controls before arranging them.");
        Control? parentControl = controls[0].GetVisualAncestors().OfType<Control>().FirstOrDefault(control =>
            surface.ControlMap.TryGetValue(control, out DesignItem? item) && item.AstNodeId == parent.Id);
        if (parentControl is null || controls.Any(control => !control.GetVisualAncestors().Contains(parentControl)))
            throw new InvalidOperationException("Select controls in the same rendered container.");
        Item[] items = nodes.Select((node, index) => new Item(node, controls[index], BoundsInParent(controls[index], parentControl))).ToArray();
        Item primary = items.FirstOrDefault(item => item.Node.Id == (surface.Selection.PrimarySelection as DesignItem)?.AstNodeId) ?? items[0];
        LayoutEditService layout = new(_document.MetadataService);
        LayoutContainerKind kind = layout.GetKind(parent);
        List<Write> writes = new();

        if (operation is DesignerLayoutOperation.SameWidth or DesignerLayoutOperation.SameHeight)
        {
            bool horizontal = operation == DesignerLayoutOperation.SameWidth;
            double size = horizontal ? primary.Control.Bounds.Width : primary.Control.Bounds.Height;
            foreach (Item item in items)
            {
                double min = horizontal ? item.Control.MinWidth : item.Control.MinHeight;
                double max = horizontal ? item.Control.MaxWidth : item.Control.MaxHeight;
                if (size < min || size > max) throw new InvalidOperationException("The primary control's size exceeds a selected control's Min/Max constraint.");
                writes.Add(new(item.Node, horizontal ? "Width" : "Height", Number(size)));
            }
        }
        else if (operation is DesignerLayoutOperation.DistributeHorizontally or DesignerLayoutOperation.DistributeVertically)
        {
            bool horizontal = operation == DesignerLayoutOperation.DistributeHorizontally;
            Distribute(items, parent, parentControl, layout, kind, horizontal, writes);
        }
        else
        {
            foreach (Item item in items)
            {
                (bool horizontal, double delta) = operation switch
                {
                    DesignerLayoutOperation.AlignLeft => (true, primary.Bounds.Left - item.Bounds.Left),
                    DesignerLayoutOperation.CenterHorizontally => (true, primary.Bounds.Center.X - item.Bounds.Center.X),
                    DesignerLayoutOperation.AlignRight => (true, primary.Bounds.Right - item.Bounds.Right),
                    DesignerLayoutOperation.AlignTop => (false, primary.Bounds.Top - item.Bounds.Top),
                    DesignerLayoutOperation.CenterVertically => (false, primary.Bounds.Center.Y - item.Bounds.Center.Y),
                    DesignerLayoutOperation.AlignBottom => (false, primary.Bounds.Bottom - item.Bounds.Bottom),
                    _ => throw new ArgumentOutOfRangeException(nameof(operation))
                };
                if (kind == LayoutContainerKind.Canvas) MoveCanvas(item, horizontal, delta, writes);
                else ShiftMargin(item, horizontal, delta, operation, parentControl, writes);
            }
        }

        foreach (Write write in writes) EnsureLiteral(write.Node, write.Property);
        using DocumentEditTransaction edit = _document.SyncEngine.BeginEditTransaction(Title(operation));
        foreach (Write write in writes) write.Node.SetPropertyValue(write.Property, write.Value);
        edit.Commit();
        surface.RequestSelection(nodes.Select(node => node.Id));
    }

    private static void Distribute(Item[] items, MutableAstObjectNode parent, Control parentControl,
        LayoutEditService layout, LayoutContainerKind kind, bool horizontal, List<Write> writes)
    {
        if (kind == LayoutContainerKind.Canvas)
        {
            if (items.Length < 3) throw new InvalidOperationException("Canvas distribution requires at least three controls.");
            Item[] ordered = items.OrderBy(item => horizontal ? item.Bounds.Left : item.Bounds.Top).ToArray();
            double start = horizontal ? ordered[0].Bounds.Left : ordered[0].Bounds.Top;
            double end = horizontal ? ordered[^1].Bounds.Right : ordered[^1].Bounds.Bottom;
            double occupied = ordered.Sum(item => horizontal ? item.Bounds.Width : item.Bounds.Height);
            double gap = (end - start - occupied) / (ordered.Length - 1);
            double position = start;
            foreach (Item item in ordered)
            {
                MoveCanvas(item, horizontal, position - (horizontal ? item.Bounds.Left : item.Bounds.Top), writes);
                position += (horizontal ? item.Bounds.Width : item.Bounds.Height) + gap;
            }
            return;
        }

        Guid[] childIds = layout.GetChildren(parent).OfType<MutableAstObjectNode>().Select(node => node.Id).ToArray();
        bool allSelected = childIds.Length == items.Length && childIds.All(id => items.Any(item => item.Node.Id == id));
        if (parentControl is Grid grid)
        {
            int count = horizontal ? grid.ColumnDefinitions.Count : grid.RowDefinitions.Count;
            string definitions = horizontal ? "ColumnDefinitions" : "RowDefinitions";
            if (count == 0 && allSelected && !parent.Properties.Any(property => property.PropertyName == definitions))
            {
                count = items.Length;
                writes.Add(new(parent, definitions, string.Join(",", Enumerable.Repeat("*", count))));
            }
            if (count < items.Length) throw new InvalidOperationException($"The Grid needs at least {items.Length} existing {(horizontal ? "columns" : "rows")}. Existing definitions are not replaced.");
            Item[] ordered = items.OrderBy(item => horizontal ? Grid.GetColumn(item.Control) : Grid.GetRow(item.Control))
                .ThenBy(item => Array.IndexOf(childIds, item.Node.Id)).ToArray();
            for (int index = 0; index < ordered.Length; index++)
            {
                int cell = (int)Math.Round(index * (count - 1d) / (ordered.Length - 1), MidpointRounding.AwayFromZero);
                writes.Add(new(ordered[index].Node, horizontal ? "Grid.Column" : "Grid.Row", cell.ToString(CultureInfo.InvariantCulture)));
                writes.Add(new(ordered[index].Node, horizontal ? "Grid.ColumnSpan" : "Grid.RowSpan", "1"));
            }
            return;
        }
        if (parentControl is StackPanel stack)
        {
            if ((stack.Orientation == Orientation.Horizontal) != horizontal)
                throw new InvalidOperationException("Choose distribution along the StackPanel's orientation.");
            if (!allSelected) throw new InvalidOperationException("Select all children before changing their shared StackPanel spacing.");
            double available = horizontal ? stack.Bounds.Width : stack.Bounds.Height;
            double occupied = items.Sum(item => horizontal ? item.Control.DesiredSize.Width : item.Control.DesiredSize.Height);
            if (available < occupied) throw new InvalidOperationException("The StackPanel is too small to distribute its children without overlap.");
            writes.Add(new(parent, "Spacing", Number((available - occupied) / (items.Length - 1))));
            return;
        }
        throw new InvalidOperationException("Distribution is available for Canvas, Grid and StackPanel containers.");
    }

    private static void MoveCanvas(Item item, bool horizontal, double delta, List<Write> writes)
    {
        if (Math.Abs(delta) < 0.000001) return;
        double current = horizontal ? Canvas.GetLeft(item.Control) : Canvas.GetTop(item.Control);
        if (!double.IsFinite(current)) current = horizontal
            ? item.Control.Bounds.Left - item.Control.Margin.Left : item.Control.Bounds.Top - item.Control.Margin.Top;
        writes.Add(new(item.Node, horizontal ? "Canvas.Left" : "Canvas.Top", Number(current + delta)));
        writes.Add(new(item.Node, horizontal ? "Canvas.Right" : "Canvas.Bottom", null));
    }

    private static void ShiftMargin(Item item, bool horizontal, double delta, DesignerLayoutOperation operation,
        Control parentControl, List<Write> writes)
    {
        if (Math.Abs(delta) < 0.000001) return;
        Thickness margin = item.Control.Margin;
        double leading = horizontal ? margin.Left : margin.Top;
        double trailing = horizontal ? margin.Right : margin.Bottom;
        bool stackAxis = parentControl is StackPanel stack && (stack.Orientation == Orientation.Horizontal) == horizontal;
        string alignment = horizontal ? item.Control.HorizontalAlignment.ToString() : item.Control.VerticalAlignment.ToString();
        ShiftMargins(ref leading, ref trailing, delta, operation, alignment, stackAxis);
        writes.Add(new(item.Node, "Margin", horizontal
            ? FormatMargin(leading, margin.Top, trailing, margin.Bottom)
            : FormatMargin(margin.Left, leading, margin.Right, trailing)));
    }

    /// <summary>
    /// Moves a control's measured edge by <paramref name="delta"/> inside a non-Canvas container by adjusting its
    /// margins along one axis, keeping the control's own alignment semantics intact.
    /// </summary>
    internal static void ShiftMargins(ref double leading, ref double trailing, double delta,
        DesignerLayoutOperation operation, string alignment, bool stackAxis)
    {
        if (stackAxis || alignment is "Left" or "Top") leading += delta;
        else if (alignment is "Right" or "Bottom") trailing -= delta;
        else if (alignment is "Center")
        {
            leading += delta;
            trailing -= delta;
        }
        else
        {
            if (operation is not (DesignerLayoutOperation.AlignRight or DesignerLayoutOperation.AlignBottom)) leading += delta;
            if (operation is not (DesignerLayoutOperation.AlignLeft or DesignerLayoutOperation.AlignTop)) trailing -= delta;
        }
    }

    internal static string FormatMargin(double left, double top, double right, double bottom) =>
        string.Join(",", new[] { left, top, right, bottom }.Select(value => value.ToString("0.####", CultureInfo.InvariantCulture)));

    private static Rect BoundsInParent(Control control, Control parent)
    {
        Point[] corners = { default, new(control.Bounds.Width, 0), new(0, control.Bounds.Height), new(control.Bounds.Width, control.Bounds.Height) };
        Point[] points = corners.Select(point => control.TranslatePoint(point, parent)
            ?? throw new InvalidOperationException("The selected control is not attached to the design surface.")).ToArray();
        double left = points.Min(point => point.X);
        double top = points.Min(point => point.Y);
        return new Rect(left, top, points.Max(point => point.X) - left, points.Max(point => point.Y) - top);
    }

    private static void EnsureLiteral(MutableAstObjectNode node, string name)
    {
        MutableAstPropertyNode? property = node.FindProperty(name);
        if (property is null) return;
        if (property.Value is MutableAstTextNode text && !text.Text.TrimStart().StartsWith('{')) return;
        throw new InvalidOperationException($"{node.TypeName}.{name} contains an expression or structured value. Edit or reset it explicitly before arranging the control.");
    }

    private static string Number(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    private sealed record Item(MutableAstObjectNode Node, Control Control, Rect Bounds);
    private sealed record Write(MutableAstObjectNode Node, string Property, string? Value);
}
