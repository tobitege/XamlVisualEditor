using XamlVisualEditor.Core;
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

public sealed class LayoutEditingWorkflowTests
{
    [Theory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task NewControlNamespaceIsDeclaredAtTheRootAndSurvivesUndoAndReopen(string panel)
    {
        const string libraryNamespace = "clr-namespace:Sample.Controls;assembly=Sample.Controls";
        string original = $"<UserControl xmlns='https://github.com/avaloniaui' xmlns:ns1='using:Existing.Controls'>\n  <!-- Keep this comment. -->\n  <{panel} Name='target' />\n</UserControl>";
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService());
        await engine.LoadAsync(original);
        MutableAstObjectNode target = Find(engine.CurrentDocument!.Root!, "target");
        MutableAstObjectNode added = new() { TypeName = "LibraryButton", XmlNamespace = libraryNamespace };
        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Insert library control"))
        {
            Assert.True(new LayoutEditService().TryInsert(target, added, out string? error), error);
            edit.Commit();
        }
        string inserted = engine.GetCurrentText();
        System.Xml.Linq.XDocument xml = System.Xml.Linq.XDocument.Parse(inserted);
        Assert.Equal(libraryNamespace, xml.Root!.GetNamespaceOfPrefix("ns2")!.NamespaceName);
        Assert.Equal("using:Existing.Controls", xml.Root.GetNamespaceOfPrefix("ns1")!.NamespaceName);
        Assert.Contains("<!-- Keep this comment. -->", inserted);
        Assert.DoesNotContain(xml.Root.Descendants().SelectMany(element => element.Attributes()), attribute => attribute.IsNamespaceDeclaration);
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        engine.Redo();
        Assert.Equal(inserted, await engine.SaveAsync());
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService());
        await reopened.LoadAsync(inserted);
        MutableAstObjectNode savedControl = Assert.Single(Find(reopened.CurrentDocument!.Root!, "target").Children.OfType<MutableAstObjectNode>());
        Assert.Equal(libraryNamespace, savedControl.XmlNamespace);
    }

    [Fact]
    public async Task ReparentWrapUndoAndReopenPreserveBindingsAndContainerRules()
    {
        const string input = """
            <StackPanel xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <Canvas Name="canvas">
                    <Button Name="moving" Content="{Binding Caption}" Margin="{DynamicResource Gap}"
                            Canvas.Left="17" Canvas.Top="23" Width="120" Grid.RowSpan="4" />
                </Canvas>
                <Grid Name="grid" RowDefinitions="Auto,*" ColumnDefinitions="*,2*">
                    <Grid.Children><TextBlock Name="existing" Text="Keep" /></Grid.Children>
                </Grid>
                <StackPanel Name="stack">
                    <StackPanel.Children><Button Content="First" /><Button Content="Last" /></StackPanel.Children>
                </StackPanel>
                <Border Name="occupied"><Border.Child><TextBlock Text="Protected" /></Border.Child></Border>
            </StackPanel>
            """;
        AstNodeMap map = new();
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), map);
        await engine.LoadAsync(input);
        LayoutEditService editor = new();
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        MutableAstObjectNode moving = Find(root, "moving");
        Guid movingId = moving.Id;

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Move to grid"))
        {
            Assert.True(editor.TryMove(moving, Find(root, "grid"), out string? error,
                placement: new LayoutPlacement(Row: 99, Column: 1)), error);
            edit.Commit();
        }
        Assert.Null(moving.GetPropertyValue("Canvas.Left"));
        Assert.Null(moving.GetPropertyValue("Canvas.Top"));
        Assert.Equal("1", moving.GetPropertyValue("Grid.Row"));
        Assert.Equal("1", moving.GetPropertyValue("Grid.Column"));
        Assert.Equal("1", moving.GetPropertyValue("Grid.RowSpan"));
        Assert.Equal(2, editor.GetChildren(Find(root, "grid")).Count);

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Move to stack"))
        {
            Assert.True(editor.TryMove(moving, Find(root, "stack"), out string? error, 1), error);
            edit.Commit();
        }
        Assert.Same(moving, editor.GetChildren(Find(root, "stack"))[1]);
        Assert.Null(moving.GetPropertyValue("Grid.Row"));
        Assert.Null(moving.GetPropertyValue("Grid.RowSpan"));
        Assert.Equal("{Binding Caption}", moving.GetPropertyValue("Content"));
        Assert.Equal("{DynamicResource Gap}", moving.GetPropertyValue("Margin"));

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Move to canvas"))
        {
            Assert.True(editor.TryMove(moving, Find(root, "canvas"), out string? error,
                placement: new LayoutPlacement(30, 40)), error);
            edit.Commit();
        }
        Assert.Equal("30", moving.GetPropertyValue("Canvas.Left"));
        Assert.Equal("40", moving.GetPropertyValue("Canvas.Top"));
        string beforeRejectedMove = await engine.SaveAsync();
        Assert.False(editor.TryMove(moving, Find(root, "occupied"), out _));
        Assert.Equal(beforeRejectedMove, await engine.SaveAsync());
        Assert.False(new TreeDropHandler().MoveNode(Find(root, "canvas").Id, moving.Id, DropPosition.Inside, map));
        Assert.False(editor.TryMoveRelative(moving, moving, DropPosition.Before, out _));

        MutableAstObjectNode wrapper = new() { TypeName = "Grid", XmlNamespace = root.XmlNamespace };
        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Wrap"))
        {
            Assert.True(editor.TryWrap(moving, wrapper, out string? error), error);
            edit.Commit();
        }
        Assert.Equal("30", wrapper.GetPropertyValue("Canvas.Left"));
        Assert.Equal("{DynamicResource Gap}", wrapper.GetPropertyValue("Margin"));
        Assert.Null(moving.GetPropertyValue("Canvas.Left"));
        Assert.Equal("120", moving.GetPropertyValue("Width"));
        engine.Undo();
        moving = Assert.IsType<MutableAstObjectNode>(map.FindById(movingId));
        Assert.Equal("30", moving.GetPropertyValue("Canvas.Left"));
        Assert.Equal("{DynamicResource Gap}", moving.GetPropertyValue("Margin"));
        engine.Redo();

        string saved = await engine.SaveAsync();
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await reopened.LoadAsync(saved);
        Assert.Equal("{Binding Caption}", Find(reopened.CurrentDocument!.Root!, "moving").GetPropertyValue("Content"));
        Assert.Equal("Keep", Find(reopened.CurrentDocument.Root!, "existing").GetPropertyValue("Text"));
        Assert.Equal("Protected", Assert.IsType<MutableAstObjectNode>(editor.GetChildren(Find(reopened.CurrentDocument.Root!, "occupied"))[0]).GetPropertyValue("Text"));
    }

    [Fact]
    public void InheritedContainersAndExplicitContentUseTheSameInsertionContract()
    {
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(LibraryGrid).Assembly.Location);
        LayoutEditService editor = new(metadata);
        string libraryNamespace = $"clr-namespace:XamlVisualEditor.TestControls;assembly={typeof(LibraryGrid).Assembly.GetName().Name}";
        MutableAstObjectNode inherited = new() { TypeName = nameof(LibraryGrid), XmlNamespace = libraryNamespace };
        ToolboxDragData payload = new(nameof(CaptionControl), libraryNamespace);
        Assert.True(ToolboxDragData.TryParse(payload.Serialize(), out ToolboxDragData? decoded));
        MutableAstObjectNode child = new() { TypeName = decoded!.TypeName, XmlNamespace = decoded.XmlNamespace };
        Assert.True(editor.TryInsert(inherited, child, out string? error), error);
        Assert.Equal(LayoutContainerKind.Grid, editor.GetKind(inherited));
        Assert.Equal("0", child.GetPropertyValue("Grid.Row"));
        Assert.Equal(libraryNamespace, child.XmlNamespace);
        MutableAstObjectNode nonVisual = new() { TypeName = "SolidColorBrush", XmlNamespace = "https://github.com/avaloniaui" };
        Assert.False(editor.TryInsert(inherited, nonVisual, out _));
        Assert.Null(nonVisual.Parent);
        MutableAstObjectNode missing = new() { TypeName = "Button", XmlNamespace = "using:Missing.Controls" };
        Assert.False(editor.TryInsert(inherited, missing, out _));
        Assert.Null(missing.Parent);

        MutableAstObjectNode border = new() { TypeName = "Border", XmlNamespace = "https://github.com/avaloniaui" };
        border.Properties.Add(new MutableAstPropertyNode { PropertyName = "Border.Child" });
        Assert.True(editor.TryMove(child, border, out error), error);
        Assert.Same(border, LayoutEditService.GetParent(child));
        MutableAstObjectNode extra = new() { TypeName = "Button", XmlNamespace = border.XmlNamespace };
        Assert.False(editor.TryInsert(border, extra, out _));
        Assert.Null(extra.Parent);
        Assert.True(editor.TryRemove(child, out error), error);
        Assert.Null(child.Parent);
        Assert.True(editor.TryInsert(border, extra, out error), error);
        Assert.Same(extra, Assert.Single(editor.GetChildren(border)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OptionalAutomationIdsCoverInsertionThirdPartyControlsAndDuplication(bool enabled)
    {
        const string input = "<StackPanel xmlns=\"https://github.com/avaloniaui\"><Button Name=\"existing\" AutomationProperties.AutomationId=\"Button_1\" /></StackPanel>";
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(CaptionControl).Assembly.Location);
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await engine.LoadAsync(input);
        MutableAstObjectNode root = engine.CurrentDocument!.Root!;
        LayoutEditService editor = new(metadata, enabled);
        MutableAstObjectNode button = new() { TypeName = "Button", XmlNamespace = root.XmlNamespace };
        MutableAstObjectNode foreign = new()
        {
            TypeName = nameof(CaptionControl),
            XmlNamespace = $"clr-namespace:XamlVisualEditor.TestControls;assembly={typeof(CaptionControl).Assembly.GetName().Name}"
        };
        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Insert controls"))
        {
            Assert.True(editor.TryInsert(root, button, out string? error), error);
            Assert.True(editor.TryInsert(root, foreign, out error), error);
            edit.Commit();
        }
        Assert.Equal("Button_1", Find(root, "existing").GetPropertyValue("AutomationProperties.AutomationId"));
        Assert.Equal(enabled ? "Button_2" : null, button.GetPropertyValue("AutomationProperties.AutomationId"));
        Assert.Equal(enabled ? "CaptionControl_1" : null, foreign.GetPropertyValue("AutomationProperties.AutomationId"));
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        string inserted = await engine.SaveAsync();
        engine.Undo();
        Assert.Equal(input, engine.GetCurrentText());
        engine.Redo();
        Assert.Equal(inserted, engine.GetCurrentText());

        root = engine.CurrentDocument!.Root!;
        button = root.Children.OfType<MutableAstObjectNode>().First(node => node.TypeName == "Button" && node.GetPropertyValue("Name") is null);
        XamlVisualEditor.Shell.ViewModels.DesignerSelectionEditor selection = new(engine, metadata, enabled);
        MutableAstObjectNode copy = Assert.Single(selection.Duplicate(new[] { button }));
        Assert.Equal(enabled ? "Button_3" : null, copy.GetPropertyValue("AutomationProperties.AutomationId"));
        string saved = await engine.SaveAsync();
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await reopened.LoadAsync(saved);
        string?[] ids = reopened.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>()
            .Select(node => node.GetPropertyValue("AutomationProperties.AutomationId")).Where(id => id is not null).ToArray();
        Assert.Equal(enabled ? 4 : 1, ids.Length);
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        engine.Undo();
        Assert.Equal(inserted, engine.GetCurrentText());
        engine.Redo();
        Assert.Equal(saved, engine.GetCurrentText());
    }

    private static MutableAstObjectNode Find(MutableAstObjectNode root, string name)
    {
        Stack<MutableAstNode> nodes = new();
        nodes.Push(root);
        while (nodes.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstObjectNode element)
            {
                if (element.GetPropertyValue("Name") == name || element.GetPropertyValue("x:Name") == name) return element;
                foreach (MutableAstNode child in element.Children) nodes.Push(child);
                foreach (MutableAstPropertyNode property in element.Properties) nodes.Push(property);
            }
            else if (node is MutableAstPropertyNode { Value: { } value }) nodes.Push(value);
        }
        throw new InvalidOperationException("Missing fixture element: " + name);
    }
}
