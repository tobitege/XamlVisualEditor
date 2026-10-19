using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Sync;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Integration;

/// <summary>Structural edits and drops into a panel declared in a control library namespace.</summary>
public sealed class LibraryPanelDropTests
{
    private static readonly string LibraryNamespace =
        $"clr-namespace:XamlVisualEditor.TestControls;assembly={typeof(LibraryGrid).Assembly.GetName().Name}";

    private static string Document => $"""
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:local="{LibraryNamespace}">
          <StackPanel Name="stack">
            <Button Name="first" />
            <Button Name="second" />
            <local:LibraryGrid Name="custom" />
            <local:CaptionControl Name="caption"><TextBlock Name="occupant" /></local:CaptionControl>
          </StackPanel>
        </UserControl>
        """;

    [Fact]
    public async Task InsertMoveAndDeleteInALibraryPanelSurviveReopenAndUndo()
    {
        TypeMetadataService metadata = CreateMetadata();
        using SyncEngine engine = await LoadAsync(new AstNodeMap());
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        LayoutEditService editor = new(metadata);
        MutableAstObjectNode custom = Find(root, "custom");
        MutableAstObjectNode first = Find(root, "first");
        MutableAstObjectNode inserted = new() { TypeName = "TextBlock", XmlNamespace = root.XmlNamespace };
        Assert.Equal(LibraryNamespace, custom.XmlNamespace);
        Assert.Equal(LayoutContainerKind.Grid, editor.GetKind(custom));

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Insert and move"))
        {
            Assert.True(editor.TryInsert(custom, inserted, out string? error), error);
            Assert.True(editor.TryMove(first, custom, out error, 0), error);
            edit.Commit();
        }
        Assert.Equal(new MutableAstNode[] { first, inserted }, editor.GetChildren(custom));
        Assert.Equal("0", first.GetPropertyValue("Grid.Row"));
        Assert.Equal("0", inserted.GetPropertyValue("Grid.Column"));

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Delete"))
        {
            Assert.True(editor.TryRemove(inserted, out string? error), error);
            edit.Commit();
        }
        Assert.Null(inserted.Parent);
        Assert.Same(first, Assert.Single(editor.GetChildren(custom)));

        MutableAstObjectNode second = Find(root, "second");
        Assert.False(editor.TryMove(second, Find(root, "caption"), out string? reason));
        Assert.Equal(UiStrings.LayoutEditContentOccupied, reason);
        Assert.Same(Find(root, "stack"), LayoutEditService.GetParent(second));

        string saved = await engine.SaveAsync();
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService());
        await reopened.LoadAsync(saved);
        MutableAstObjectNode reopenedCustom = Find(reopened.CurrentDocument!.Root!, "custom");
        Assert.Equal(LibraryNamespace, reopenedCustom.XmlNamespace);
        Assert.Same(Find(reopened.CurrentDocument.Root!, "first"), Assert.Single(editor.GetChildren(reopenedCustom)));

        engine.Undo();
        engine.Undo();
        Assert.Equal(Document, engine.GetCurrentText());
    }

    [Fact]
    public async Task ToolboxDropInsertsIntoALibraryPanelAndExplainsRejectedDrops()
    {
        TypeMetadataService metadata = CreateMetadata();
        using SyncEngine engine = await LoadAsync(new AstNodeMap());
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        ToolboxDropHandler handler = new();

        MutableAstObjectNode? dropped = handler.Drop(nameof(CaptionControl), string.Empty, new DesignItem(Find(root, "custom")),
            DropPosition.Inside, out string? error, LibraryNamespace, metadata);
        Assert.NotNull(dropped);
        Assert.Null(error);
        Assert.Same(Find(root, "custom"), LayoutEditService.GetParent(dropped));
        Assert.Equal(LibraryNamespace, dropped.XmlNamespace);

        MutableAstObjectNode? beside = handler.Drop("Button", string.Empty, new DesignItem(Find(root, "custom")),
            DropPosition.Before, out error, metadata: metadata);
        Assert.NotNull(beside);
        Assert.Same(beside, new LayoutEditService(metadata).GetChildren(Find(root, "stack"))[2]);

        Assert.Null(handler.Drop("Button", string.Empty, new DesignItem(Find(root, "caption")),
            DropPosition.Inside, out error, metadata: metadata));
        Assert.Equal(UiStrings.LayoutEditContentOccupied, error);
        Assert.Null(handler.Drop("Button", string.Empty, new DesignItem(root), DropPosition.After, out error, metadata: metadata));
        Assert.Equal(UiStrings.DropPositionUnavailable, error);
        Assert.Null(handler.Drop("Button", string.Empty, null, DropPosition.Inside, out error, metadata: metadata));
        Assert.Equal(UiStrings.DropTargetUnavailable, error);
    }

    [Fact]
    public async Task SurfaceMoveIntoALibraryPanelUpdatesBothTreesAndExplainsRejectedMoves()
    {
        TypeMetadataService metadata = CreateMetadata();
        using SyncEngine engine = await LoadAsync(new AstNodeMap());
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        DesignItem stack = new(Find(root, "stack"));
        DesignItem first = new(Find(root, "first"));
        DesignItem second = new(Find(root, "second"));
        DesignItem custom = new(Find(root, "custom"));
        DesignItem caption = new(Find(root, "caption"));
        foreach (DesignItem child in new[] { first, second, custom, caption }) stack.AddChild(child);
        SurfaceDropHandler handler = new();

        Assert.True(handler.Move(first, custom, DropPosition.Inside, metadata, out string? error), error);
        Assert.Same(custom, first.Parent);
        Assert.Same(first, Assert.Single(custom.Children));
        Assert.DoesNotContain<IDesignItem>(first, stack.Children);
        Assert.Same(custom.AstNode, LayoutEditService.GetParent(first.AstNode));

        Assert.False(handler.Move(second, caption, DropPosition.Inside, metadata, out error));
        Assert.Equal(UiStrings.LayoutEditContentOccupied, error);
        Assert.Same(stack, second.Parent);
        Assert.Same(stack.AstNode, LayoutEditService.GetParent(second.AstNode));

        Assert.False(handler.Move(second, stack, DropPosition.Before, metadata, out error));
        Assert.Equal(UiStrings.DropPositionUnavailable, error);
    }

    [Fact]
    public async Task TreeMoveIntoALibraryPanelSucceedsAndExplainsRejectedMoves()
    {
        TypeMetadataService metadata = CreateMetadata();
        AstNodeMap map = new();
        using SyncEngine engine = await LoadAsync(map);
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        MutableAstObjectNode second = Find(root, "second");
        TreeDropHandler handler = new();

        Assert.True(handler.MoveNode(second.Id, Find(root, "custom").Id, DropPosition.Inside, map, metadata, out string? error), error);
        Assert.Same(Find(root, "custom"), LayoutEditService.GetParent(second));

        Assert.False(handler.MoveNode(second.Id, root.Id, DropPosition.Before, map, metadata, out error));
        Assert.Equal(UiStrings.LayoutEditNoRootSibling, error);
        Assert.False(handler.MoveNode(second.Id, Guid.NewGuid(), DropPosition.Inside, map, metadata, out error));
        Assert.Equal(UiStrings.DropTargetUnavailable, error);
        Assert.Same(Find(root, "custom"), LayoutEditService.GetParent(second));
    }

    private static TypeMetadataService CreateMetadata()
    {
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(LibraryGrid).Assembly.Location);
        return metadata;
    }

    private static async Task<SyncEngine> LoadAsync(AstNodeMap map)
    {
        SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), map);
        await engine.LoadAsync(Document);
        return engine;
    }

    private static MutableAstObjectNode Find(MutableAstObjectNode root, string name)
    {
        Stack<MutableAstNode> nodes = new();
        nodes.Push(root);
        while (nodes.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstObjectNode element)
            {
                if (element.GetPropertyValue("Name") == name) return element;
                foreach (MutableAstNode child in element.Children) nodes.Push(child);
                foreach (MutableAstPropertyNode property in element.Properties) nodes.Push(property);
            }
            else if (node is MutableAstPropertyNode { Value: { } value }) nodes.Push(value);
        }
        throw new InvalidOperationException("Missing fixture element: " + name);
    }
}
