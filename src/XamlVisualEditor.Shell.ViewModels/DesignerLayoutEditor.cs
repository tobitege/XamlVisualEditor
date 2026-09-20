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

namespace XamlVisualEditor.Shell.ViewModels;

public enum DesignerLayoutOperation
{
    AlignLeft, CenterHorizontally, AlignRight, AlignTop, CenterVertically, AlignBottom,
    DistributeHorizontally, DistributeVertically, SameWidth, SameHeight
}

/// <summary>Plans layout-specific multi-selection changes before committing one document transaction.</summary>
public sealed class DesignerLayoutEditor
{
    private readonly DesignerDocumentViewModel _document;

    public DesignerLayoutEditor(DesignerDocumentViewModel document) => _document = document;

    public static string Title(DesignerLayoutOperation operation) => operation switch
    {
        DesignerLayoutOperation.AlignLeft => "Align left",
        DesignerLayoutOperation.CenterHorizontally => "Center horizontally",
        DesignerLayoutOperation.AlignRight => "Align right",
        DesignerLayoutOperation.AlignTop => "Align top",
        DesignerLayoutOperation.CenterVertically => "Center vertically",
        DesignerLayoutOperation.AlignBottom => "Align bottom",
        DesignerLayoutOperation.DistributeHorizontally => "Distribute horizontally",
        DesignerLayoutOperation.DistributeVertically => "Distribute vertically",
        DesignerLayoutOperation.SameWidth => "Same width",
        DesignerLayoutOperation.SameHeight => "Same height",
        _ => throw new ArgumentOutOfRangeException(nameof(operation))
    };

    public static string IconData(DesignerLayoutOperation operation) => operation switch
    {
        DesignerLayoutOperation.AlignLeft => "M1,1 H3 V19 H1 Z M5,4 H18 V8 H5 Z M5,12 H13 V16 H5 Z",
        DesignerLayoutOperation.CenterHorizontally => "M9,1 H11 V19 H9 Z M2,4 H18 V8 H2 Z M5,12 H15 V16 H5 Z",
        DesignerLayoutOperation.AlignRight => "M17,1 H19 V19 H17 Z M2,4 H15 V8 H2 Z M7,12 H15 V16 H7 Z",
        DesignerLayoutOperation.AlignTop => "M1,1 H19 V3 H1 Z M4,5 H8 V18 H4 Z M12,5 H16 V13 H12 Z",
        DesignerLayoutOperation.CenterVertically => "M1,9 H19 V11 H1 Z M4,2 H8 V18 H4 Z M12,5 H16 V15 H12 Z",
        DesignerLayoutOperation.AlignBottom => "M1,17 H19 V19 H1 Z M4,2 H8 V15 H4 Z M12,7 H16 V15 H12 Z",
        DesignerLayoutOperation.DistributeHorizontally => "M1,2 H3 V18 H1 Z M17,2 H19 V18 H17 Z M5,7 H7 V13 H5 Z M9,5 H11 V15 H9 Z M13,7 H15 V13 H13 Z",
        DesignerLayoutOperation.DistributeVertically => "M2,1 H18 V3 H2 Z M2,17 H18 V19 H2 Z M7,5 H13 V7 H7 Z M5,9 H15 V11 H5 Z M7,13 H13 V15 H7 Z",
        DesignerLayoutOperation.SameWidth => "M2,1 H18 V6 H2 Z M2,14 H18 V19 H2 Z M2,10 L6,7 V9 H14 V7 L18,10 L14,13 V11 H6 V13 Z",
        DesignerLayoutOperation.SameHeight => "M1,2 H6 V18 H1 Z M14,2 H19 V18 H14 Z M10,2 L13,6 H11 V14 H13 L10,18 L7,14 H9 V6 H7 Z",
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
        else if (kind == LayoutContainerKind.Canvas)
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
                MoveCanvas(item, horizontal, delta, writes);
            }
        }
        else
        {
            (string property, string value) = operation switch
            {
                DesignerLayoutOperation.AlignLeft => ("HorizontalAlignment", "Left"),
                DesignerLayoutOperation.CenterHorizontally => ("HorizontalAlignment", "Center"),
                DesignerLayoutOperation.AlignRight => ("HorizontalAlignment", "Right"),
                DesignerLayoutOperation.AlignTop => ("VerticalAlignment", "Top"),
                DesignerLayoutOperation.CenterVertically => ("VerticalAlignment", "Center"),
                DesignerLayoutOperation.AlignBottom => ("VerticalAlignment", "Bottom"),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            foreach (Item item in items) writes.Add(new(item.Node, property, value));
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
