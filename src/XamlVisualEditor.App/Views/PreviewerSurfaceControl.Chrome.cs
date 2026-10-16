using System.Linq;
using Avalonia;
using Avalonia.Input;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

public sealed partial class PreviewerSurfaceControl
{
    private const double HandleHitRadius = 5;
    private const double BorderHitWidth = 4;

    /// <summary>Clears the selection after a press in the viewport outside the rendered document.</summary>
    public void ClearSelectionFromViewport()
    {
        if (!CanSelect || _document!.DesignSurface.Selection.SelectedItems.Count == 0) return;
        SelectFromInput([]);
    }

    /// <summary>Finds the resize handle of the primary selection at a view position.</summary>
    private PreviewerResizeEdges HitResizeHandle(Point position)
    {
        if (_document!.DesignSurface.Selection.PrimarySelection is not DesignItem { HostInstanceId: { } primaryInstance }
            || _designTree!.Nodes.FirstOrDefault(node => node.InstanceId == primaryInstance) is not { Layout: not null } primaryNode)
            return PreviewerResizeEdges.None;
        return ResizeHandles(primaryNode).FirstOrDefault(handle => new Rect(handle.Position.X - HandleHitRadius,
            handle.Position.Y - HandleHitRadius, HandleHitRadius * 2, HandleHitRadius * 2).Contains(position)).Edges;
    }

    /// <summary>Finds a movable selected node whose selection border lies at a view position.</summary>
    private DesignerHostNode? HitSelectionBorder(Point position)
    {
        double scaleX = Bounds.Width / _designTree!.Width;
        double scaleY = Bounds.Height / _designTree.Height;
        foreach (DesignItem item in _document!.DesignSurface.Selection.SelectedItems.OfType<DesignItem>())
        {
            DesignerHostNode? node = _designTree.Nodes.FirstOrDefault(node => node.InstanceId == item.HostInstanceId && node.NodeId == item.AstNodeId);
            if (node is not { IsVisible: true, ParentInstanceId: not null, Layout: not null } || node.Width <= 0 || node.Height <= 0) continue;
            Rect rectangle = new(node.X * scaleX, node.Y * scaleY, node.Width * scaleX, node.Height * scaleY);
            if (rectangle.Inflate(BorderHitWidth).Contains(position) && !rectangle.Deflate(BorderHitWidth).Contains(position)) return node;
        }
        return null;
    }

    private void UpdateChromeCursor(Point position)
    {
        if (!CanSelect)
        {
            Cursor = null;
            return;
        }
        PreviewerResizeEdges edges = HitResizeHandle(position);
        Cursor = edges != PreviewerResizeEdges.None ? DesignerCursors.ForEdges(edges)
            : HitSelectionBorder(position) is not null ? DesignerCursors.Move : null;
    }
}
