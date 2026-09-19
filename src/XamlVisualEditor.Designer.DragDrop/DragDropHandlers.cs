using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
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
        ITypeMetadataService? metadata = null)
    {
        if (targetItem is not DesignItem designItem)
        {
            return null;
        }

        MutableAstObjectNode newNode = new()
        {
            TypeName = typeName,
            XmlNamespace = xmlNamespace
        };

        LayoutEditService editor = new(metadata);
        if (position == DropPosition.Inside)
            return editor.TryInsert(designItem.AstNode, newNode, out _) ? newNode : null;
        MutableAstObjectNode? parent = LayoutEditService.GetParent(designItem.AstNode);
        if (parent is null) return null;
        int index = editor.GetChildren(parent).ToList().IndexOf(designItem.AstNode);
        if (index < 0) return null;
        return editor.TryInsert(parent, newNode, out _, index + (position == DropPosition.After ? 1 : 0)) ? newNode : null;
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
    public bool Move(IDesignItem source, IDesignItem target, DropPosition position, ITypeMetadataService? metadata = null)
    {
        if (source is not DesignItem sourceItem || target is not DesignItem targetItem)
        {
            return false;
        }

        LayoutEditService editor = new(metadata);
        DesignItem? newParent = position == DropPosition.Inside ? targetItem : targetItem.Parent as DesignItem;
        if (newParent is null || !editor.TryMoveRelative(sourceItem.AstNode, targetItem.AstNode, position, out _)) return false;
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
        ITypeMetadataService? metadata = null)
    {
        MutableAstNode? sourceNode = nodeMap.FindById(sourceNodeId);
        MutableAstNode? targetNode = nodeMap.FindById(targetNodeId);

        if (sourceNode is not MutableAstObjectNode source ||
            targetNode is not MutableAstObjectNode target)
        {
            return false;
        }

        return new LayoutEditService(metadata).TryMoveRelative(source, target, position, out _);
    }
}
