using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.DragDrop;

/// <summary>
/// Custom data format for toolbox item drag-and-drop.
/// </summary>
public static class DesignerDataFormats
{
    /// <summary>
    /// Data format for toolbox items being dragged onto the design surface.
    /// </summary>
    public static DataFormat<string> ToolboxItem { get; } = DataFormat.CreateStringApplicationFormat("ToolboxItem");
}

/// <summary>
/// Handles drop operations from the toolbox onto the design surface.
/// </summary>
public sealed class ToolboxDropHandler
{
    /// <summary>
    /// Determines whether the drag data can be dropped at the specified position.
    /// </summary>
    public bool CanDrop(IDataTransfer data, IDesignItem? targetItem, DropPosition position)
    {
        return data.Contains(DesignerDataFormats.ToolboxItem) && targetItem is not null;
    }

    /// <summary>
    /// Handles the drop operation, creating a new AST node from the toolbox item.
    /// </summary>
    public MutableAstObjectNode? Drop(
        string typeName,
        string defaultXaml,
        IDesignItem? targetItem,
        DropPosition position,
        string xmlNamespace = "https://github.com/avaloniaui",
        ITypeMetadataService? metadata = null,
        bool generateAutomationIds = false) =>
        Drop(typeName, defaultXaml, targetItem, position, out _, xmlNamespace, metadata, generateAutomationIds);

    /// <summary>
    /// Handles the drop operation, creating a new AST node from the toolbox item;
    /// <paramref name="error"/> explains a rejected drop.
    /// </summary>
    public MutableAstObjectNode? Drop(
        string typeName,
        string defaultXaml,
        IDesignItem? targetItem,
        DropPosition position,
        out string? error,
        string xmlNamespace = "https://github.com/avaloniaui",
        ITypeMetadataService? metadata = null,
        bool generateAutomationIds = false)
    {
        if (targetItem is not DesignItem designItem)
        {
            error = UiStrings.DropTargetUnavailable;
            return null;
        }

        MutableAstObjectNode newNode = new()
        {
            TypeName = typeName,
            XmlNamespace = xmlNamespace
        };

        LayoutEditService editor = new(metadata, generateAutomationIds);
        if (position == DropPosition.Inside)
            return editor.TryInsert(designItem.AstNode, newNode, out error) ? newNode : null;
        MutableAstObjectNode? parent = LayoutEditService.GetParent(designItem.AstNode);
        int index = parent is null ? -1 : editor.GetChildren(parent).ToList().IndexOf(designItem.AstNode);
        if (parent is null || index < 0)
        {
            error = UiStrings.DropPositionUnavailable;
            return null;
        }
        return editor.TryInsert(parent, newNode, out error, index + (position == DropPosition.After ? 1 : 0)) ? newNode : null;
    }
}

/// <summary>
/// Handles drag-and-drop operations within the design surface for rearranging elements.
/// </summary>
public sealed class SurfaceDropHandler
{
    /// <summary>
    /// Moves a design item to a new position relative to a target.
    /// </summary>
    public bool Move(IDesignItem source, IDesignItem target, DropPosition position, ITypeMetadataService? metadata = null) =>
        Move(source, target, position, metadata, out _);

    /// <summary>
    /// Moves a design item to a new position relative to a target; <paramref name="error"/> explains a rejected move.
    /// </summary>
    public bool Move(IDesignItem source, IDesignItem target, DropPosition position, ITypeMetadataService? metadata,
        out string? error)
    {
        if (source is not DesignItem sourceItem || target is not DesignItem targetItem)
        {
            error = UiStrings.DropTargetUnavailable;
            return false;
        }

        LayoutEditService editor = new(metadata);
        DesignItem? newParent = position == DropPosition.Inside ? targetItem : targetItem.Parent as DesignItem;
        if (newParent is null)
        {
            error = UiStrings.DropPositionUnavailable;
            return false;
        }
        if (!editor.TryMoveRelative(sourceItem.AstNode, targetItem.AstNode, position, out error)) return false;
        if (sourceItem.Parent is DesignItem previousParent) previousParent.RemoveChild(sourceItem);
        int index = editor.GetChildren(newParent.AstNode).OfType<MutableAstObjectNode>().ToList().IndexOf(sourceItem.AstNode);
        newParent.InsertChild(Math.Clamp(index, 0, newParent.Children.Count), sourceItem);
        return true;
    }
}

/// <summary>
/// Handles drag-and-drop operations from the tree view.
/// </summary>
public sealed class TreeDropHandler
{
    /// <summary>
    /// Moves a node in the AST based on tree view drag-and-drop.
    /// </summary>
    public bool MoveNode(Guid sourceNodeId, Guid targetNodeId, DropPosition position, AstNodeMap nodeMap,
        ITypeMetadataService? metadata = null) =>
        MoveNode(sourceNodeId, targetNodeId, position, nodeMap, metadata, out _);

    /// <summary>
    /// Moves a node in the AST based on tree view drag-and-drop; <paramref name="error"/> explains a rejected move.
    /// </summary>
    public bool MoveNode(Guid sourceNodeId, Guid targetNodeId, DropPosition position, AstNodeMap nodeMap,
        ITypeMetadataService? metadata, out string? error)
    {
        MutableAstNode? sourceNode = nodeMap.FindById(sourceNodeId);
        MutableAstNode? targetNode = nodeMap.FindById(targetNodeId);

        if (sourceNode is not MutableAstObjectNode source ||
            targetNode is not MutableAstObjectNode target)
        {
            error = UiStrings.DropTargetUnavailable;
            return false;
        }

        return new LayoutEditService(metadata).TryMoveRelative(source, target, position, out error);
    }
}
