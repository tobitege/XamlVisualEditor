using System.Globalization;
using Avalonia.Controls;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.Core;

/// <summary>Describes the placement rules of a visual container.</summary>
public enum LayoutContainerKind { None, Canvas, Grid, Stack, Panel, Dock, Items, Content, Child }

/// <summary>Optional placement supplied by a measured design surface.</summary>
public sealed record LayoutPlacement(double? X = null, double? Y = null, int? Row = null, int? Column = null);

/// <summary>Applies validated structural edits without owning the document's undo transaction.</summary>
public sealed class LayoutEditService
{
    private const string CollectionNodeName = "__PropertyElementChildren__";
    private readonly ITypeMetadataService? _metadata;
    private readonly bool _generateAutomationIds;
    private static readonly string[] CanvasProperties = { "Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom" };
    private static readonly string[] GridProperties = { "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan" };

    /// <summary>Creates an editor that understands inherited control types when metadata is available.</summary>
    public LayoutEditService(ITypeMetadataService? metadata = null, bool generateAutomationIds = false)
    {
        _metadata = metadata;
        _generateAutomationIds = generateAutomationIds;
    }

    /// <summary>Gets the actual container kind, including custom subclasses.</summary>
    public LayoutContainerKind GetKind(MutableAstObjectNode node)
    {
        TypeMetadata? metadata = _metadata?.GetType(node.XmlNamespace, node.TypeName);
        Type? type = metadata is null ? null : _metadata?.ResolveClrType(metadata);
        if (type is not null)
        {
            if (typeof(Canvas).IsAssignableFrom(type)) return LayoutContainerKind.Canvas;
            if (typeof(Grid).IsAssignableFrom(type)) return LayoutContainerKind.Grid;
            if (typeof(StackPanel).IsAssignableFrom(type)) return LayoutContainerKind.Stack;
            if (typeof(DockPanel).IsAssignableFrom(type)) return LayoutContainerKind.Dock;
            if (typeof(Panel).IsAssignableFrom(type)) return LayoutContainerKind.Panel;
            if (typeof(ItemsControl).IsAssignableFrom(type)) return LayoutContainerKind.Items;
            if (typeof(Decorator).IsAssignableFrom(type) || typeof(Viewbox).IsAssignableFrom(type)) return LayoutContainerKind.Child;
            if (typeof(ContentControl).IsAssignableFrom(type)) return LayoutContainerKind.Content;
            return LayoutContainerKind.None;
        }
        if (!string.IsNullOrEmpty(node.XmlNamespace) && node.XmlNamespace != "https://github.com/avaloniaui") return LayoutContainerKind.None;
        return node.TypeName switch
        {
            "Canvas" => LayoutContainerKind.Canvas,
            "Grid" => LayoutContainerKind.Grid,
            "StackPanel" => LayoutContainerKind.Stack,
            "DockPanel" => LayoutContainerKind.Dock,
            "Panel" or "WrapPanel" or "UniformGrid" => LayoutContainerKind.Panel,
            "ItemsControl" or "ListBox" or "ComboBox" or "TabControl" or "Menu" or "MenuItem" or "TreeView" => LayoutContainerKind.Items,
            "Border" or "Decorator" or "Viewbox" => LayoutContainerKind.Child,
            "Window" or "UserControl" or "ContentControl" or "Button" or "ScrollViewer" or "Expander" or "TabItem" => LayoutContainerKind.Content,
            _ => LayoutContainerKind.None
        };
    }

    /// <summary>Gets the logical content owner, skipping property-element wrappers.</summary>
    public static MutableAstObjectNode? GetParent(MutableAstObjectNode node)
    {
        MutableAstNode? parent = node.Parent;
        if (parent is MutableAstObjectNode { TypeName: CollectionNodeName }) parent = parent.Parent;
        if (parent is MutableAstPropertyNode) parent = parent.Parent;
        return parent as MutableAstObjectNode;
    }

    /// <summary>Gets visual content in source order, including explicit content properties.</summary>
    public IReadOnlyList<MutableAstNode> GetChildren(MutableAstObjectNode parent) => GetSlot(parent).Nodes;

    /// <summary>Checks whether a destination can accept an element without replacing existing content.</summary>
    public bool CanInsert(MutableAstObjectNode parent, MutableAstObjectNode node, out string? error,
        MutableAstObjectNode? replacing = null)
    {
        error = null;
        for (MutableAstNode? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ReferenceEquals(ancestor, node)) return Fail(UiStrings.LayoutEditContainsItself, out error);
        }
        LayoutContainerKind kind = GetKind(parent);
        if (kind == LayoutContainerKind.None) return Fail(string.Format(UiStrings.LayoutEditUnsupportedContainer, parent.TypeName), out error);
        TypeMetadata? childType = _metadata?.GetType(node.XmlNamespace, node.TypeName);
        if (_metadata is not null && childType is null)
            return Fail(UiStrings.LayoutEditTypeUnresolved, out error);
        if (childType is { IsControl: false })
            return Fail(UiStrings.LayoutEditNotVisualControl, out error);
        if (childType?.IsTopLevel == true || node.TypeName == "Window")
            return Fail(UiStrings.LayoutEditTopLevelWindow, out error);
        if (kind == LayoutContainerKind.Items && FindProperty(parent, "ItemsSource") is not null)
            return Fail(UiStrings.LayoutEditItemsSource, out error);
        ContentSlot slot = GetSlot(parent);
        if (slot.Property?.Value is MutableAstTextNode)
            return Fail(UiStrings.LayoutEditContentAssigned, out error);
        if (slot.Property is not null && parent.Children.Count > 0)
            return Fail(UiStrings.LayoutEditMixedContent, out error);
        if (kind is LayoutContainerKind.Content or LayoutContainerKind.Child
            && slot.Nodes.Any(child => !ReferenceEquals(child, node) && !ReferenceEquals(child, replacing)))
            return Fail(UiStrings.LayoutEditContentOccupied, out error);
        return true;
    }

    /// <summary>Inserts a detached element at a validated destination.</summary>
    public bool TryInsert(MutableAstObjectNode parent, MutableAstObjectNode node, out string? error,
        int? index = null, LayoutPlacement? placement = null)
    {
        if (node.Parent is not null) return Fail(UiStrings.LayoutEditHasParent, out error);
        if (!CanInsert(parent, node, out error)) return false;
        ContentSlot slot = GetSlot(parent);
        int insertion = index ?? slot.Nodes.Count;
        if (insertion < 0 || insertion > slot.Nodes.Count) return Fail(UiStrings.LayoutEditInsertionOutside, out error);
        DeclareControlNamespace(parent, node.XmlNamespace);
        if (_generateAutomationIds && _metadata is not null) AutomationIdGenerator.Assign(parent, node, _metadata);
        ApplyPlacement(node, parent, placement);
        slot.Insert(insertion, node);
        return true;
    }

    private static void DeclareControlNamespace(MutableAstObjectNode parent, string xmlNamespace)
    {
        if (string.IsNullOrEmpty(xmlNamespace)) return;
        MutableAstObjectNode root = parent;
        HashSet<string> reserved = new(StringComparer.Ordinal);
        for (MutableAstNode? ancestor = parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            if (ancestor is MutableAstObjectNode element)
            {
                root = element;
                reserved.UnionWith(element.NamespaceAliases.Keys);
            }
            else if (ancestor is MutableAstPropertyNode property)
                reserved.UnionWith(property.NamespaceAliases.Keys);
        }
        if (root.XmlNamespace == xmlNamespace || root.NamespaceAliases.Values.Contains(xmlNamespace)) return;
        string prefix = "ns1";
        for (int index = 2; reserved.Contains(prefix); index++) prefix = "ns" + index;
        root.NamespaceAliases[prefix] = xmlNamespace;
    }

    /// <summary>Moves an existing element atomically after validating both locations.</summary>
    public bool TryMove(MutableAstObjectNode node, MutableAstObjectNode parent, out string? error,
        int? index = null, LayoutPlacement? placement = null)
    {
        MutableAstObjectNode? oldParent = GetParent(node);
        if (oldParent is null) return Fail(UiStrings.LayoutEditRootMove, out error);
        ContentSlot source = GetSlot(oldParent);
        int oldIndex = source.Nodes.ToList().IndexOf(node);
        if (oldIndex < 0) return Fail(UiStrings.LayoutEditSourceNotContent, out error);
        if (!CanInsert(parent, node, out error)) return false;
        ContentSlot target = GetSlot(parent);
        int insertion = index ?? target.Nodes.Count;
        if (insertion < 0 || insertion > target.Nodes.Count) return Fail(UiStrings.LayoutEditInsertionOutside, out error);
        if (ReferenceEquals(oldParent, parent) && insertion > oldIndex) insertion--;
        AstSourceScope sourceScope = AstSourceScope.Capture(node);
        source.Remove(node);
        ApplyPlacement(node, parent, placement);
        target.Insert(insertion, node);
        sourceScope.ApplyTo(node);
        return true;
    }

    /// <summary>Moves relative to a visible target without detaching the source on invalid drops.</summary>
    public bool TryMoveRelative(MutableAstObjectNode source, MutableAstObjectNode target, DropPosition position, out string? error)
    {
        if (position is not (DropPosition.Inside or DropPosition.Before or DropPosition.After))
            return Fail(UiStrings.LayoutEditDropUnsupported, out error);
        if (ReferenceEquals(source, target)) return Fail(UiStrings.LayoutEditDropOnItself, out error);
        if (position == DropPosition.Inside) return TryMove(source, target, out error);
        MutableAstObjectNode? parent = GetParent(target);
        if (parent is null) return Fail(UiStrings.LayoutEditNoRootSibling, out error);
        int index = GetSlot(parent).Nodes.ToList().IndexOf(target);
        if (index < 0) return Fail(UiStrings.LayoutEditTargetNotContent, out error);
        return TryMove(source, parent, out error, index + (position == DropPosition.After ? 1 : 0));
    }

    /// <summary>Removes visual content from an implicit or explicit content slot.</summary>
    public bool TryRemove(MutableAstObjectNode node, out string? error)
    {
        MutableAstObjectNode? parent = GetParent(node);
        if (parent is null) return Fail(UiStrings.LayoutEditRootDelete, out error);
        ContentSlot slot = GetSlot(parent);
        if (!slot.Nodes.Contains(node)) return Fail(UiStrings.LayoutEditElementNotContent, out error);
        slot.Remove(node);
        error = null;
        return true;
    }

    /// <summary>Wraps an element while preserving its parent-facing placement and unrelated properties.</summary>
    public bool TryWrap(MutableAstObjectNode node, MutableAstObjectNode wrapper, out string? error)
    {
        MutableAstObjectNode? parent = GetParent(node);
        if (parent is null) return Fail(UiStrings.LayoutEditRootWrap, out error);
        if (wrapper.Parent is not null) return Fail(UiStrings.LayoutEditWrapperAttached, out error);
        if (!CanInsert(parent, wrapper, out error, node) || !CanInsert(wrapper, node, out error)) return false;
        ContentSlot slot = GetSlot(parent);
        int index = slot.Nodes.ToList().IndexOf(node);
        if (index < 0) return Fail(UiStrings.LayoutEditElementNotContent, out error);
        if (_generateAutomationIds && _metadata is not null) AutomationIdGenerator.Assign(parent, wrapper, _metadata);
        slot.Remove(node);
        foreach (MutableAstPropertyNode property in node.Properties.Where(property =>
            CanvasProperties.Contains(property.PropertyName) || GridProperties.Contains(property.PropertyName)
            || property.PropertyName is "DockPanel.Dock" or "Margin" or "HorizontalAlignment" or "VerticalAlignment").ToArray())
        {
            node.Properties.Remove(property);
            wrapper.Properties.Add(property);
        }
        ApplyPlacement(node, wrapper, null);
        GetSlot(wrapper).Insert(0, node);
        slot.Insert(index, wrapper);
        return true;
    }

    /// <summary>Finds a usable layout owner for insertion without an explicit target.</summary>
    public MutableAstObjectNode? FindInsertionParent(MutableAstObjectNode root, MutableAstObjectNode? selection,
        MutableAstObjectNode child)
    {
        for (MutableAstObjectNode? candidate = selection; candidate is not null; candidate = GetParent(candidate))
        {
            LayoutContainerKind kind = GetKind(candidate);
            if (kind is not (LayoutContainerKind.None or LayoutContainerKind.Content or LayoutContainerKind.Child)
                && CanInsert(candidate, child, out _)) return candidate;
        }
        MutableAstObjectNode current = root;
        while (GetKind(current) is LayoutContainerKind.Content or LayoutContainerKind.Child)
        {
            IReadOnlyList<MutableAstNode> children = GetChildren(current);
            if (children.Count != 1 || children[0] is not MutableAstObjectNode nested) break;
            current = nested;
        }
        return CanInsert(current, child, out _) ? current : null;
    }

    private ContentSlot GetSlot(MutableAstObjectNode parent)
    {
        string name = GetKind(parent) switch
        {
            LayoutContainerKind.Child => "Child",
            LayoutContainerKind.Content => "Content",
            LayoutContainerKind.Items => "Items",
            _ => "Children"
        };
        return new ContentSlot(parent, FindProperty(parent, name));
    }

    private void ApplyPlacement(MutableAstObjectNode node, MutableAstObjectNode parent, LayoutPlacement? placement)
    {
        LayoutContainerKind kind = GetKind(parent);
        if (kind != LayoutContainerKind.Canvas) RemoveProperties(node, CanvasProperties);
        if (kind != LayoutContainerKind.Grid) RemoveProperties(node, GridProperties);
        if (kind != LayoutContainerKind.Dock) node.SetPropertyValue("DockPanel.Dock", null);
        if (kind == LayoutContainerKind.Canvas)
        {
            SetCoordinate(node, "Canvas.Left", placement?.X);
            SetCoordinate(node, "Canvas.Top", placement?.Y);
        }
        if (kind == LayoutContainerKind.Grid)
        {
            bool plainGrid = IsPlainGrid(parent);
            ClampGridPosition(node, "Grid.Row", placement?.Row, plainGrid ? DefinitionCount(parent, "RowDefinitions") : null);
            ClampGridPosition(node, "Grid.Column", placement?.Column, plainGrid ? DefinitionCount(parent, "ColumnDefinitions") : null);
        }
    }

    // count is null when the definitions in the AST are not the complete set of the grid; the position is
    // then kept and only negative values are corrected.
    private static void ClampGridPosition(MutableAstObjectNode node, string property, int? supplied, int? count)
    {
        string? current = node.GetPropertyValue(property);
        if (supplied is null && current is not null && !int.TryParse(current, out _)) return;
        int value = supplied ?? (int.TryParse(current, out int parsed) ? parsed : 0);
        value = count is int known ? Math.Clamp(value, 0, known - 1) : Math.Max(0, value);
        node.SetPropertyValue(property, value.ToString(CultureInfo.InvariantCulture));
        string spanProperty = property + "Span";
        if (int.TryParse(node.GetPropertyValue(spanProperty), out int span))
        {
            span = count is int total ? Math.Clamp(span, 1, total - value) : Math.Max(1, span);
            node.SetPropertyValue(spanProperty, span.ToString(CultureInfo.InvariantCulture));
        }
    }

    // A subclass of Grid can define rows and columns in code or its template, so only a plain Grid has
    // all its definitions in the markup.
    private bool IsPlainGrid(MutableAstObjectNode node)
    {
        TypeMetadata? metadata = _metadata?.GetType(node.XmlNamespace, node.TypeName);
        Type? type = metadata is null ? null : _metadata?.ResolveClrType(metadata);
        if (type is not null) return type == typeof(Grid);
        return node.TypeName == "Grid"
            && (string.IsNullOrEmpty(node.XmlNamespace) || node.XmlNamespace == "https://github.com/avaloniaui");
    }

    // Returns the number of definitions the element sets itself, or null when it sets none or sets them
    // through a binding or resource; a style can then supply definitions the AST does not show.
    private static int? DefinitionCount(MutableAstObjectNode parent, string name)
    {
        MutableAstPropertyNode? property = FindProperty(parent, name);
        return property?.Value switch
        {
            MutableAstTextNode text when !text.Text.TrimStart().StartsWith('{') =>
                Math.Max(1, text.Text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Length),
            MutableAstObjectNode { TypeName: CollectionNodeName } collection =>
                Math.Max(1, collection.Children.OfType<MutableAstObjectNode>().Count()),
            MutableAstObjectNode { TypeName: "RowDefinition" or "ColumnDefinition" } => 1,
            _ => null
        };
    }

    private static void SetCoordinate(MutableAstObjectNode node, string property, double? value)
    {
        if (value.HasValue && double.IsFinite(value.Value))
            node.SetPropertyValue(property, value.Value.ToString(CultureInfo.InvariantCulture));
    }

    private static void RemoveProperties(MutableAstObjectNode node, IEnumerable<string> names)
    {
        foreach (string name in names) node.SetPropertyValue(name, null);
    }

    private static MutableAstPropertyNode? FindProperty(MutableAstObjectNode node, string name) =>
        node.Properties.FirstOrDefault(property => property.PropertyName == name
            || property.PropertyName.EndsWith("." + name, StringComparison.Ordinal));

    private static bool Fail(string message, out string? error) { error = message; return false; }

    private sealed class ContentSlot(MutableAstObjectNode owner, MutableAstPropertyNode? property)
    {
        public MutableAstPropertyNode? Property { get; } = property;
        public IReadOnlyList<MutableAstNode> Nodes => Property?.Value switch
        {
            MutableAstObjectNode { TypeName: CollectionNodeName } collection => collection.Children,
            { } value => new[] { value },
            _ => Property is null ? owner.Children : Array.Empty<MutableAstNode>()
        };

        public void Insert(int index, MutableAstObjectNode node)
        {
            if (Property is null) { owner.Children.Insert(index, node); return; }
            if (Property.Value is null) { Property.Value = node; return; }
            if (Property.Value is MutableAstObjectNode { TypeName: CollectionNodeName } collection)
            {
                collection.Children.Insert(index, node);
                return;
            }
            MutableAstNode previous = Property.Value;
            Property.Value = null;
            MutableAstObjectNode wrapper = new() { TypeName = CollectionNodeName, XmlNamespace = owner.XmlNamespace };
            wrapper.Children.Add(previous);
            wrapper.Children.Insert(index, node);
            Property.Value = wrapper;
        }

        public void Remove(MutableAstObjectNode node)
        {
            if (Property is null) { owner.Children.Remove(node); return; }
            if (ReferenceEquals(Property.Value, node)) { Property.Value = null; return; }
            if (Property.Value is not MutableAstObjectNode { TypeName: CollectionNodeName } collection) return;
            collection.Children.Remove(node);
            if (collection.Children.Count > 1) return;
            MutableAstNode? remaining = collection.Children.FirstOrDefault();
            collection.Children.Clear();
            Property.Value = remaining;
        }
    }
}
