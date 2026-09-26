using Avalonia;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerGeometryEditingTests
{
    [AvaloniaTheory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task AlignmentPreviewAndCommitShareScreenToleranceAndRespectIndependentOptions(bool snapping, bool guides)
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Canvas.Left=\"10\" Canvas.Top=\"20\" Content=\"Moving\" /><Button Canvas.Left=\"10\" Canvas.Top=\"60\" Content=\"Partner\" /><Button Canvas.Left=\"53\" Canvas.Top=\"101\" Content=\"Anchor\" /></Canvas>";
        using DesignerDocumentViewModel document = new("Alignment.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        document.DesignSurface.Zoom = 2;
        document.DesignSurface.SnapToGrid = true;
        document.DesignSurface.GridSnapSize = 8;
        document.DesignSurface.SnapLinesEnabled = snapping;
        document.DesignSurface.ShowAlignmentGuides = guides;
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode[] children = root.Children.OfType<MutableAstObjectNode>().ToArray();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 660, 600, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 30, 40, 600, 520, true,
                Layout: new() { Kind = "Canvas", Width = 300, Height = 260, M11 = 2, M22 = 2, M31 = 30, M32 = 40 }),
            new DesignerHostNode(children[0].Id, 2, 1, "Button", "Moving", 50, 80, 80, 40, true,
                Layout: new() { X = 10, Y = 20, Width = 40, Height = 20, CanvasLeft = 10, CanvasTop = 20,
                    M11 = 2, M22 = 2, M31 = 50, M32 = 80 }),
            new DesignerHostNode(children[1].Id, 3, 1, "Button", "Partner", 50, 160, 60, 40, true,
                Layout: new() { X = 10, Y = 60, Width = 30, Height = 20, CanvasLeft = 10, CanvasTop = 60,
                    M11 = 2, M22 = 2, M31 = 50, M32 = 160 }),
            new DesignerHostNode(children[2].Id, 4, 1, "Button", "Anchor", 136, 242, 200, 80, true,
                Layout: new() { X = 53, Y = 101, Width = 100, Height = 40, CanvasLeft = 53, CanvasTop = 101,
                    M11 = 2, M22 = 2, M31 = 136, M32 = 242 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem[] selection = { new(children[0]) { HostInstanceId = 2 }, new(children[1]) { HostInstanceId = 3 } };
        Avalonia.Vector delta = new(84, 160);
        Avalonia.Point pointer = new(144, 250);
        PreviewerMovePreview preview = editor.PreviewMove(selection, 2, delta, pointer);
        Assert.Equal(snapping ? new Avalonia.Vector(86, 162) : new Avalonia.Vector(76, 152), preview.Delta);
        Assert.Equal(guides && snapping ? 2 : 0, preview.SnapLines?.Count ?? 0);
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        editor.MovePointer(selection, 2, delta, pointer);
        Assert.Equal(snapping ? "53" : "48", children[0].GetPropertyValue("Canvas.Left"));
        Assert.Equal(snapping ? "101" : "96", children[0].GetPropertyValue("Canvas.Top"));
        Assert.Equal(snapping ? "141" : "136", children[1].GetPropertyValue("Canvas.Top"));
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());

        document.DesignSurface.SnapLinesEnabled = true;
        DesignItem[] all = { selection[0], selection[1], new(children[2]) { HostInstanceId = 4 } };
        Assert.Equal(new Avalonia.Vector(76, 152), editor.PreviewMove(all, 2, delta, pointer).Delta);
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.ShowAlignmentGuides = true;
        Avalonia.Vector exactDelta = new(86, 162);
        PreviewerMovePreview exact = editor.PreviewMove(selection, 2, exactDelta, new Avalonia.Point(146, 252));
        Assert.Equal(exactDelta, exact.Delta);
        Assert.Equal(2, exact.SnapLines!.Count);
    }

    [AvaloniaTheory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task ReparentingUsesMeasuredTargetLayoutPreservesScopeAndUndoesTheWholeSelection(string container)
    {
        string definitions = container == "Grid" ? "RowDefinitions=\"50,50,50\" ColumnDefinitions=\"50,50,50\"" : string.Empty;
        string xaml = $$"""
            <Canvas xmlns="https://github.com/avaloniaui">
              <Canvas Name="Source" xmlns:p="https://github.com/avaloniaui">
                <p:Button Name="First" Canvas.Left="10" Canvas.Top="20" Content="{Binding Caption}" />
                <Button Name="Second" Canvas.Left="70" Canvas.Top="120" />
              </Canvas>
              <{{container}} Name="Target" {{definitions}}>
                <TextBlock Name="Before" Text="Before" />
                <TextBlock Name="After" Text="After" />
              </{{container}}>
            </Canvas>
            """;
        using DesignerDocumentViewModel document = new("Reparent.axaml");
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode source = root.Children.OfType<MutableAstObjectNode>().First();
        MutableAstObjectNode target = root.Children.OfType<MutableAstObjectNode>().Last();
        MutableAstObjectNode[] controls = source.Children.OfType<MutableAstObjectNode>().ToArray();
        MutableAstObjectNode[] existing = target.Children.OfType<MutableAstObjectNode>().ToArray();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 600, 300, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 600, 300, true,
                Layout: new() { Kind = "Canvas", Width = 600, Height = 300 }),
            new DesignerHostNode(source.Id, 2, 1, "Canvas", "Source", 0, 0, 200, 250, true,
                Layout: new() { Kind = "Canvas", Width = 200, Height = 250 }),
            new DesignerHostNode(controls[0].Id, 3, 2, "Button", "First", 10, 20, 40, 20, true,
                Layout: new() { X = 10, Y = 20, Width = 40, Height = 20, CanvasLeft = 10, CanvasTop = 20 }),
            new DesignerHostNode(controls[1].Id, 4, 2, "Button", "Second", 70, 120, 40, 20, true,
                Layout: new() { X = 70, Y = 120, Width = 40, Height = 20, CanvasLeft = 70, CanvasTop = 120 }),
            new DesignerHostNode(target.Id, 5, 1, container, "Target", 250, 0, 300, 300, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, Width = 150, Height = 150,
                    M11 = 2, M22 = 2, M31 = 250, RowHeights = new[] { 50d, 50d, 50d }, ColumnWidths = new[] { 50d, 50d, 50d } }),
            new DesignerHostNode(existing[0].Id, 6, 5, "TextBlock", "Before", 250, 0, 280, 40, true,
                Layout: new() { Width = 140, Height = 20 }),
            new DesignerHostNode(existing[1].Id, 7, 5, "TextBlock", "After", 250, 160, 280, 40, true,
                Layout: new() { Y = 80, Width = 140, Height = 20 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem[] selection = { new(controls[1]) { HostInstanceId = 4 }, new(controls[0]) { HostInstanceId = 3 } };
        Avalonia.Vector delta = new(310, 30);
        Avalonia.Point pointer = new(330, 60);
        PreviewerMovePreview preview = editor.PreviewMove(selection, 3, delta, pointer);
        Assert.Null(preview.Error);
        Assert.Equal(new Avalonia.Rect(250, 0, 300, 300), preview.TargetBounds);
        if (container == "Grid") Assert.Equal(new Avalonia.Rect(250, 0, 100, 100), preview.DropIndicator!.Value.Bounds);
        if (container == "StackPanel")
        {
            Assert.Equal(new Avalonia.Point(250, 100), preview.DropIndicator!.Value.LineStart);
            Assert.Equal(new Avalonia.Point(550, 100), preview.DropIndicator.Value.LineEnd);
        }
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        editor.MovePointer(selection, 3, delta, pointer);
        Assert.Empty(source.Children);
        Assert.All(controls, node => Assert.Same(target, LayoutEditService.GetParent(node)));
        Assert.Equal("{Binding Caption}", controls[0].GetPropertyValue("Content"));
        if (container == "Canvas")
        {
            Assert.Equal("35", controls[0].GetPropertyValue("Canvas.Left"));
            Assert.Equal("25", controls[0].GetPropertyValue("Canvas.Top"));
            Assert.Equal("65", controls[1].GetPropertyValue("Canvas.Left"));
            Assert.Equal("75", controls[1].GetPropertyValue("Canvas.Top"));
        }
        else
        {
            Assert.All(controls, node => Assert.Null(node.GetPropertyValue("Canvas.Left")));
            if (container == "Grid")
            {
                Assert.Equal("0", controls[0].GetPropertyValue("Grid.Row"));
                Assert.Equal("0", controls[0].GetPropertyValue("Grid.Column"));
                Assert.Equal("1", controls[1].GetPropertyValue("Grid.Row"));
                Assert.Equal("1", controls[1].GetPropertyValue("Grid.Column"));
            }
            else Assert.Equal(new[] { "Before", "First", "Second", "After" },
                target.Children.OfType<MutableAstObjectNode>().Select(node => node.GetPropertyValue("Name")));
        }
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        string moved = document.SyncEngine.GetCurrentText();
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        document.SyncEngine.Redo();
        Assert.Equal(moved, document.SyncEngine.GetCurrentText());
        using DesignerDocumentViewModel reopened = new("Reparent.axaml");
        await reopened.SyncEngine.LoadAsync(moved);
        MutableAstObjectNode reopenedTarget = reopened.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>().Last();
        MutableAstObjectNode first = reopenedTarget.Children.OfType<MutableAstObjectNode>().Single(node => node.GetPropertyValue("Name") == "First");
        Assert.Equal("https://github.com/avaloniaui", first.XmlNamespace);
        Assert.Equal("{Binding Caption}", first.GetPropertyValue("Content"));
    }

    [AvaloniaTheory]
    [InlineData("Border", false)]
    [InlineData("Grid", true)]
    public async Task InvalidReparentingIsPreviewedAndDoesNotPartiallyMoveTheSelection(string container, bool binding)
    {
        string coordinate = binding ? "{Binding Left}" : "20";
        string xaml = $"<Canvas xmlns=\"https://github.com/avaloniaui\"><Canvas><Button /><Button Canvas.Left=\"{coordinate}\" /></Canvas><{container} /></Canvas>";
        using DesignerDocumentViewModel document = new("RejectedDrop.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode source = root.Children.OfType<MutableAstObjectNode>().First();
        MutableAstObjectNode target = root.Children.OfType<MutableAstObjectNode>().Last();
        MutableAstObjectNode[] controls = source.Children.OfType<MutableAstObjectNode>().ToArray();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 400, 200, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 400, 200, true, Layout: new() { Kind = "Canvas" }),
            new DesignerHostNode(source.Id, 2, 1, "Canvas", null, 0, 0, 150, 200, true, Layout: new() { Kind = "Canvas" }),
            new DesignerHostNode(controls[0].Id, 3, 2, "Button", null, 0, 0, 50, 20, true, Layout: new() { Width = 50, Height = 20 }),
            new DesignerHostNode(controls[1].Id, 4, 2, "Button", null, 20, 40, 50, 20, true, Layout: new() { X = 20, Y = 40, Width = 50, Height = 20, CanvasLeft = 20 }),
            new DesignerHostNode(target.Id, 5, 1, container, null, 200, 0, 200, 200, true,
                Layout: new() { Kind = container == "Border" ? "Child" : "Grid", M31 = 200, Width = 200, Height = 200 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem[] selection = { new(controls[0]) { HostInstanceId = 3 }, new(controls[1]) { HostInstanceId = 4 } };
        Avalonia.Point pointer = new(250, 50);
        Avalonia.Vector delta = new(230, 30);
        Assert.NotNull(editor.PreviewMove(selection, 3, delta, pointer).Error);
        Assert.Throws<InvalidOperationException>(() => editor.MovePointer(selection, 3, delta, pointer));
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
    }

    [AvaloniaFact]
    public async Task SnappedGhostAndCommitAgreeWhileSizeConstraintsTakePriority()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Canvas.Left=\"10\" Canvas.Top=\"20\" /></Canvas>";
        using DesignerDocumentViewModel document = new("SnappedPointer.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        document.DesignSurface.SnapToGrid = true;
        document.DesignSurface.SnapLinesEnabled = false;
        document.DesignSurface.GridSnapSize = 8;
        document.DesignSurface.SnapToGuides = true;
        document.DesignSurface.VerticalGuides.Add(192);
        document.DesignSurface.HorizontalGuides.Add(134);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 400, 200, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 400, 200, true,
                Layout: new() { Kind = "Canvas", M11 = 2, M22 = 2 }),
            new DesignerHostNode(button.Id, 2, 1, "Button", null, 20, 40, 160, 80, true,
                Layout: new() { Width = 80, Height = 40, X = 10, Y = 20, CanvasLeft = 10, CanvasTop = 20,
                    M11 = 2, M22 = 2, M31 = 20, M32 = 40, MaxWidth = 85 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem item = new(button) { HostInstanceId = 2 };
        Assert.Equal(new Avalonia.Vector(12, 24), editor.PreviewMove(new[] { item }, 2,
            new Avalonia.Vector(18, 26), new Avalonia.Point(38, 66)).Delta);
        editor.MovePointer(new[] { item }, 2, new Avalonia.Vector(18, 26), new Avalonia.Point(38, 66));
        Assert.Equal("16", button.GetPropertyValue("Canvas.Left"));
        Assert.Equal("32", button.GetPropertyValue("Canvas.Top"));
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(new Avalonia.Rect(20, 40, 170, 94), editor.PreviewResize(item, new Avalonia.Vector(22, 14),
            PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom));
        editor.ResizePointer(item, new Avalonia.Vector(22, 14), PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom);
        button = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
        Assert.Equal("85", button.GetPropertyValue("Width"));
        Assert.Equal("47", button.GetPropertyValue("Height"));
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
    }

    [AvaloniaTheory]
    [InlineData("Canvas", false, false)]
    [InlineData("Grid", false, false)]
    [InlineData("StackPanel", false, false)]
    [InlineData("Canvas", true, false)]
    [InlineData("Grid", true, false)]
    [InlineData("StackPanel", true, false)]
    [InlineData("StackPanel", false, true)]
    [InlineData("StackPanel", true, true)]
    public async Task ToolboxInsertionUsesMeasuredPlacementAndAutomationIds(string container, bool foreign, bool horizontal)
    {
        string definitions = container == "Grid" ? " RowDefinitions=\"30,30,40\" ColumnDefinitions=\"50,50,50\" RowSpacing=\"5\" ColumnSpacing=\"5\""
            : container == "StackPanel" ? $" Orientation=\"{(horizontal ? "Horizontal" : "Vertical")}\" Spacing=\"8\"" : "";
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"{definitions}><Button Name=\"Existing\" /></{container}>";
        XamlVisualEditor.Workspace.TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Avalonia.Controls.Control).Assembly.Location);
        metadata.LoadAssembly(typeof(XamlVisualEditor.TestControls.CaptionControl).Assembly.Location);
        using DesignerDocumentViewModel document = new("ToolboxDrop.axaml", metadata);
        await document.SyncEngine.LoadAsync(xaml);
        document.DesignSurface.AutoGenerateAutomationIds = true;
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode existing = root.Children.OfType<MutableAstObjectNode>().Single();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 400, 240, new[]
        {
            new DesignerHostNode(root.Id, 1, null, container, null, 0, 0, 400, 240, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, M11 = 2, M22 = 2,
                    Width = 200, Height = 120, RowSpacing = 5, ColumnSpacing = 5, Spacing = 8, IsHorizontal = horizontal,
                    RowHeights = new[] { 30d, 30d, 40d }, ColumnWidths = new[] { 50d, 50d, 50d } }),
            new DesignerHostNode(existing.Id, 2, 1, "Button", "Existing", 20, 20, 100, 40, true,
                Layout: new() { X = 10, Y = 10, Width = 50, Height = 20, M11 = 2, M22 = 2, M31 = 20, M32 = 20 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        string type = foreign ? "CaptionControl" : "Button";
        string ns = foreign ? "clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls" : "https://github.com/avaloniaui";
        LayoutDropIndicator preview = editor.PreviewInsertion(type, ns, new Avalonia.Point(250, 180))!.Value;
        if (container == "Grid") Assert.Equal(new Avalonia.Rect(220, 140, 100, 80), preview.Bounds);
        else if (container == "StackPanel")
        {
            Assert.Equal(horizontal ? new Avalonia.Point(128, 0) : new Avalonia.Point(0, 68), preview.LineStart);
            Assert.Equal(horizontal ? new Avalonia.Point(128, 240) : new Avalonia.Point(400, 68), preview.LineEnd);
            LayoutDropIndicator before = editor.PreviewInsertion(type, ns, new Avalonia.Point(2, 2))!.Value;
            Assert.Equal(horizontal ? new Avalonia.Point(12, 0) : new Avalonia.Point(0, 12), before.LineStart);
        }
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Guid id = editor.InsertControl(type, ns, new Avalonia.Point(250, 180));
        MutableAstObjectNode inserted = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(id));
        Assert.Equal(type + "_1", inserted.GetPropertyValue("AutomationProperties.AutomationId"));
        Assert.Equal(id, document.SelectedNodeId);
        if (container == "Canvas")
        {
            Assert.Equal("125", inserted.GetPropertyValue("Canvas.Left"));
            Assert.Equal("90", inserted.GetPropertyValue("Canvas.Top"));
        }
        else if (container == "Grid")
        {
            Assert.Equal("2", inserted.GetPropertyValue("Grid.Row"));
            Assert.Equal("2", inserted.GetPropertyValue("Grid.Column"));
            Assert.Null(inserted.GetPropertyValue("Canvas.Left"));
        }
        else Assert.Same(inserted, root.Children[1]);
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
    }

    [AvaloniaTheory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task ExternalArrangementUsesMeasuredGeometryAndOneUndoTransaction(string container)
    {
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"><Button Name=\"A\" /><Button Name=\"B\" /><Button Name=\"C\" /></{container}>";
        using DesignerDocumentViewModel document = new("Arrange.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode[] children = root.Children.OfType<MutableAstObjectNode>().ToArray();
        List<DesignerHostNode> nodes = new()
        {
            new(root.Id, 1, null, container, null, 0, 0, 500, 300, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, Width = 500, Height = 300,
                    IsHorizontal = true, RowHeights = new[] { 100d, 100d, 100d }, ColumnWidths = new[] { 100d, 100d, 100d } })
        };
        double[] positions = { 10, 100, 300 };
        for (int index = 0; index < children.Length; index++)
            nodes.Add(new(children[index].Id, index + 2, 1, "Button", null, positions[index], 10, (index + 1) * 20, 30, true,
                Layout: new() { X = positions[index], Y = 10, Width = (index + 1) * 20, Height = 30,
                    DesiredWidth = (index + 1) * 20 + 4, CanvasLeft = positions[index], CanvasTop = 10 }));
        DesignItem[] selection = children.Select((node, index) => new DesignItem(node) { HostInstanceId = index + 2 }).ToArray();
        PreviewerGeometryEditor editor = new(document, new DesignerHostSnapshot(Guid.NewGuid(), 500, 300, nodes));
        editor.Arrange(selection, DesignerLayoutOperation.AlignLeft);
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        if (container == "Canvas") Assert.All(children, node => Assert.Equal("10", node.GetPropertyValue("Canvas.Left")));
        else
        {
            // Edges move to the primary control's left edge through margins; alignment properties stay untouched.
            Assert.Null(children[0].GetPropertyValue("Margin"));
            Assert.Equal("-90,0,0,0", children[1].GetPropertyValue("Margin"));
            Assert.Equal("-290,0,0,0", children[2].GetPropertyValue("Margin"));
            Assert.All(children, node => Assert.Null(node.GetPropertyValue("HorizontalAlignment")));
        }
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        editor.Arrange(selection, DesignerLayoutOperation.SameWidth);
        Assert.All(document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>(),
            node => Assert.Equal("20", node.GetPropertyValue("Width")));
        document.SyncEngine.Undo();
        editor.Arrange(selection, DesignerLayoutOperation.DistributeHorizontally);
        MutableAstObjectNode current = document.SyncEngine.CurrentDocument!.Root!;
        if (container == "Canvas") Assert.Equal("145", Assert.IsType<MutableAstObjectNode>(current.Children[1]).GetPropertyValue("Canvas.Left"));
        else if (container == "Grid") Assert.Equal("2", Assert.IsType<MutableAstObjectNode>(current.Children[2]).GetPropertyValue("Grid.Column"));
        else Assert.Equal("184", current.GetPropertyValue("Spacing"));
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
    }

    [AvaloniaTheory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task MeasuredMovesUseLayoutSemanticsAndRemainOneUndoStep(string container)
    {
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"><Button Name=\"First\" /><Button Name=\"Second\" /><Button Name=\"Third\" /></{container}>";
        using DesignerDocumentViewModel document = new("MeasuredMove.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode node = Assert.IsType<MutableAstObjectNode>(root.Children[0]);
        DesignerHostLayout parent = new() { Kind = container == "StackPanel" ? "Stack" : container,
            RowHeights = new[] { 30d, 30d, 40d }, ColumnWidths = new[] { 60d, 60d, 80d } };
        DesignerHostLayout child = new() { Width = 80, Height = 30, X = 25, Y = 35,
            MarginLeft = 5, MarginTop = 5, CanvasLeft = 20, CanvasTop = 30, MinWidth = 40, MaxWidth = 85 };
        DesignerHostSnapshot snapshot = new(Guid.NewGuid(), 200, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, container, null, 0, 0, 200, 100, true, Layout: parent),
            new DesignerHostNode(node.Id, 2, 1, "Button", "First", 25, 35, 80, 30, true, Layout: child)
        });
        DesignItem selected = new(node) { HostInstanceId = 2 };
        PreviewerGeometryEditor editor = new(document, snapshot);
        editor.Nudge(new[] { selected }, 1, 1);
        editor.Nudge(new[] { selected }, 1, 1);
        Assert.Equal(2, document.SyncEngine.UndoRedo.UndoCount);
        if (container == "Canvas")
        {
            Assert.Equal("22", node.GetPropertyValue("Canvas.Left"));
            Assert.Equal("32", node.GetPropertyValue("Canvas.Top"));
        }
        else if (container == "Grid")
        {
            Assert.Equal("2", node.GetPropertyValue("Grid.Row"));
            Assert.Equal("2", node.GetPropertyValue("Grid.Column"));
            Assert.Null(node.GetPropertyValue("Canvas.Left"));
        }
        else Assert.Same(node, root.Children[2]);
        string moved = document.SyncEngine.GetCurrentText();
        document.SyncEngine.Undo();
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        document.SyncEngine.Redo();
        document.SyncEngine.Redo();
        Assert.Equal(moved, document.SyncEngine.GetCurrentText());
        document.SyncEngine.Undo();
        document.SyncEngine.Undo();
        editor.Resize(new[] { selected }, 20, 0);
        MutableAstObjectNode current = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(node.Id));
        Assert.Equal("85", current.GetPropertyValue("Width"));
        Assert.Null(current.GetPropertyValue("Height"));
        editor.Resize(new[] { selected }, -2, 0);
        Assert.Equal("83", current.GetPropertyValue("Width"));
        document.SyncEngine.Undo();
        Assert.Equal("85", Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(node.Id)).GetPropertyValue("Width"));
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
    }

    [AvaloniaFact]
    public async Task ADataBoundCoordinateRejectsTheWholeMove()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Canvas.Left=\"10\" Canvas.Top=\"{Binding Position}\" /></Canvas>";
        using DesignerDocumentViewModel document = new("BoundMove.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode node = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        DesignerHostSnapshot snapshot = new(Guid.NewGuid(), 200, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Canvas", null, 0, 0, 200, 100, true, Layout: new() { Kind = "Canvas" }),
            new DesignerHostNode(node.Id, 2, 1, "Button", null, 10, 20, 80, 30, true, Layout: new() { CanvasLeft = 10, CanvasTop = 20 })
        });
        Assert.Throws<InvalidOperationException>(() => new PreviewerGeometryEditor(document, snapshot)
            .Nudge(new[] { new DesignItem(node) { HostInstanceId = 2 } }, 1, 1));
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
    }

    [AvaloniaTheory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task PointerPlacementUsesMeasuredCellsAndContainerTransforms(string container)
    {
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"><Button Name=\"First\" /><Button Name=\"Second\" /><Button Name=\"Third\" /></{container}>";
        using DesignerDocumentViewModel document = new("PointerMove.axaml");
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode[] children = root.Children.OfType<MutableAstObjectNode>().ToArray();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 400, 240, new[]
        {
            new DesignerHostNode(root.Id, 1, null, container, null, 0, 0, 400, 240, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, M11 = 2, M22 = 2,
                    RowHeights = new[] { 30d, 30d, 40d }, ColumnWidths = new[] { 50d, 50d, 50d }, RowSpacing = 5, ColumnSpacing = 5 }),
            new DesignerHostNode(children[0].Id, 2, 1, "Button", "First", 0, 0, 80, 40, true,
                Layout: new() { Width = 40, Height = 20, CanvasLeft = 10, CanvasTop = 20 }),
            new DesignerHostNode(children[1].Id, 3, 1, "Button", "Second", 0, 60, 80, 40, true,
                Layout: new() { Y = 30, Width = 40, Height = 20 }),
            new DesignerHostNode(children[2].Id, 4, 1, "Button", "Third", 0, 120, 80, 40, true,
                Layout: new() { Y = 60, Width = 40, Height = 20 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        editor.MovePointer(new[] { new DesignItem(children[0]) { HostInstanceId = 2 } }, 2,
            new Avalonia.Vector(40, 20), new Avalonia.Point(250, 180));
        if (container == "Canvas")
        {
            Assert.Equal("30", children[0].GetPropertyValue("Canvas.Left"));
            Assert.Equal("30", children[0].GetPropertyValue("Canvas.Top"));
        }
        else if (container == "Grid")
        {
            Assert.Equal("2", children[0].GetPropertyValue("Grid.Row"));
            Assert.Equal("2", children[0].GetPropertyValue("Grid.Column"));
        }
        else Assert.Same(children[0], root.Children[2]);
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        string moved = document.SyncEngine.GetCurrentText();
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        document.SyncEngine.Redo();
        Assert.Equal(moved, document.SyncEngine.GetCurrentText());
    }

    [AvaloniaFact]
    public async Task GridMovePreviewMarksTheSpannedCellsTheDropAssigns()
    {
        const string xaml = "<Grid xmlns=\"https://github.com/avaloniaui\"><Button Name=\"Wide\" Grid.ColumnSpan=\"2\" /></Grid>";
        using DesignerDocumentViewModel document = new("SpanMove.axaml");
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode wide = root.Children.OfType<MutableAstObjectNode>().Single();
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 160, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Grid", null, 0, 0, 160, 100, true,
                Layout: new() { Kind = "Grid", Width = 160, Height = 100,
                    RowHeights = new[] { 30d, 30d, 30d }, ColumnWidths = new[] { 50d, 50d, 50d }, RowSpacing = 5, ColumnSpacing = 5 }),
            new DesignerHostNode(wide.Id, 2, 1, "Button", "Wide", 0, 0, 105, 30, true,
                Layout: new() { Width = 105, Height = 30, GridColumnSpan = 2 })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem[] selection = { new(wide) { HostInstanceId = 2 } };
        // The pointer lies in the last column; a control spanning two columns can start only in the middle one.
        Avalonia.Point pointer = new(140, 45);

        PreviewerMovePreview preview = editor.PreviewMove(selection, 2, new Avalonia.Vector(130, 40), pointer);
        editor.MovePointer(selection, 2, new Avalonia.Vector(130, 40), pointer);

        Assert.Equal(new Avalonia.Rect(55, 35, 105, 30), preview.DropIndicator!.Value.Bounds);
        Assert.Equal("1", wide.GetPropertyValue("Grid.Column"));
        Assert.Equal("1", wide.GetPropertyValue("Grid.Row"));
    }

    [AvaloniaTheory]
    [InlineData("Canvas", 1, 0, 0, 1)]
    [InlineData("Grid", 1, 0, 0, 1)]
    [InlineData("StackPanel", 1, 0, 0, 1)]
    [InlineData("Canvas", 2, 0, 0, 2)]
    [InlineData("Grid", 0, 2, -2, 0)]
    [InlineData("Canvas", 0.8, 0.6, -0.6, 0.8)]
    [InlineData("StackPanel", -2, 0, 0, 1)]
    [InlineData("Canvas", 1, 0.25, 0.5, 1)]
    public async Task ManualGuideResizeKeepsOppositeAnchorAcrossLayoutsTransformsAndZoom(
        string container, double m11, double m12, double m21, double m22)
    {
        string placement = container == "Canvas" ? "Canvas.Left=\"10\" Canvas.Top=\"20\"" : "Margin=\"10,20,0,0\"";
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"><Button {placement} Width=\"80\" Height=\"40\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" /></{container}>";
        using DesignerDocumentViewModel document = new("GuideResize.axaml");
        await document.SyncEngine.LoadAsync(xaml);
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        document.DesignSurface.SnapToGuides = true;
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode node = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        double tx = 90 - 40 * m11 - 20 * m21;
        double ty = 100 - 40 * m12 - 20 * m22;
        Point Project(double x, double y) => new(x * m11 + y * m21 + tx, x * m12 + y * m22 + ty);
        Rect Bounds(double width, double height, Vector anchor)
        {
            Point[] points = new[] { Project(-anchor.X, -anchor.Y), Project(width - anchor.X, -anchor.Y),
                Project(-anchor.X, height - anchor.Y), Project(width - anchor.X, height - anchor.Y) };
            return new Rect(new Point(points.Min(point => point.X), points.Min(point => point.Y)),
                new Point(points.Max(point => point.X), points.Max(point => point.Y)));
        }
        Rect initial = Bounds(80, 40, default);
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 600, 400, new[]
        {
            new DesignerHostNode(root.Id, 1, null, container, null, 40, 60, 400, 300, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, Width = 400, Height = 300, M31 = 40, M32 = 60 }),
            new DesignerHostNode(node.Id, 2, 1, "Button", null, initial.X, initial.Y, initial.Width, initial.Height, true,
                Layout: new() { Width = 80, Height = 40, X = 10, Y = 20, CanvasLeft = 10, CanvasTop = 20,
                    MarginLeft = container == "Canvas" ? 0 : 10, MarginTop = container == "Canvas" ? 0 : 20,
                    M11 = m11, M12 = m12, M21 = m21, M22 = m22, M31 = tx, M32 = ty,
                    OriginIsRelative = true, OriginX = 0.5, OriginY = 0.5, HorizontalAlignment = "Left", VerticalAlignment = "Top" })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem item = new(node) { HostInstanceId = 2 };
        foreach (PreviewerResizeEdges edges in new[] { PreviewerResizeEdges.Left, PreviewerResizeEdges.Top,
            PreviewerResizeEdges.Right, PreviewerResizeEdges.Bottom,
            PreviewerResizeEdges.Left | PreviewerResizeEdges.Top, PreviewerResizeEdges.Right | PreviewerResizeEdges.Top,
            PreviewerResizeEdges.Left | PreviewerResizeEdges.Bottom, PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom })
        {
            double ax = edges.HasFlag(PreviewerResizeEdges.Left) ? -1 : edges.HasFlag(PreviewerResizeEdges.Right) ? 1 : 0;
            double ay = edges.HasFlag(PreviewerResizeEdges.Top) ? -1 : edges.HasFlag(PreviewerResizeEdges.Bottom) ? 1 : 0;
            double width = ax < 0 ? 60 : ax > 0 ? 100 : 80;
            double height = ay < 0 ? 24 : ay > 0 ? 56 : 40;
            Vector anchor = new(ax < 0 ? width - 80 : 0, ay < 0 ? height - 40 : 0);
            Point target = Project(ax < 0 ? 80 - width : ax > 0 ? width : 40,
                ay < 0 ? 40 - height : ay > 0 ? height : 20);
            document.DesignSurface.VerticalGuides.Clear();
            document.DesignSurface.HorizontalGuides.Clear();
            document.DesignSurface.VerticalGuides.Add(target.X);
            document.DesignSurface.HorizontalGuides.Add(target.Y);
            Vector ideal = new(ax * (width - 80) * m11 + ay * (height - 40) * m21,
                ax * (width - 80) * m12 + ay * (height - 40) * m22);
            Vector direction = new(ax * m11 + ay * m21, ax * m12 + ay * m22);
            Vector error = ax != 0 && ay != 0 ? new Vector(1, 1) : direction / direction.Length;
            Rect expected = Bounds(width, height, anchor);
            foreach (double zoom in new[] { 0.25, 0.75, 1, 2, 3 })
            {
                document.DesignSurface.Zoom = zoom;
                Vector delta = ideal + error * (2 / zoom);
                Rect preview = editor.PreviewResize(item, delta, edges);
                Assert.Equal(expected.X, preview.X, 7);
                Assert.Equal(expected.Y, preview.Y, 7);
                Assert.Equal(expected.Width, preview.Width, 7);
                Assert.Equal(expected.Height, preview.Height, 7);
                document.DesignSurface.SnapToGuides = false;
                Rect free = editor.PreviewResize(item, delta, edges);
                Assert.NotEqual(preview, free);
                // Outward motion tests guide distance without reaching a minimum size.
                double projection = ax != 0 && ay != 0
                    ? Math.Min(Math.Abs(direction.X), Math.Abs(direction.Y)) : direction.Length;
                Vector distant = ideal + direction * (10 / (zoom * projection));
                Rect outsideTolerance = editor.PreviewResize(item, distant, edges);
                document.DesignSurface.SnapToGuides = true;
                Assert.Equal(outsideTolerance, editor.PreviewResize(item, distant, edges));
            }
            document.DesignSurface.Zoom = 1;
            editor.ResizePointer(item, ideal + error * 2, edges);
            MutableAstObjectNode changed = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(node.Id));
            Assert.Equal(width.ToString(System.Globalization.CultureInfo.InvariantCulture), changed.GetPropertyValue("Width"));
            Assert.Equal(height.ToString(System.Globalization.CultureInfo.InvariantCulture), changed.GetPropertyValue("Height"));
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
            string resized = document.SyncEngine.GetCurrentText();
            document.SyncEngine.Undo();
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
            document.SyncEngine.Redo();
            Assert.Equal(resized, document.SyncEngine.GetCurrentText());
            document.SyncEngine.Undo();
        }
    }

    [AvaloniaTheory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task LeadingResizeKeepsTheOppositeSideAndPreservesAdaptiveLayout(string container)
    {
        string xaml = $"<{container} xmlns=\"https://github.com/avaloniaui\"><Button /></{container}>";
        using DesignerDocumentViewModel document = new("PointerResize.axaml");
        document.DesignSurface.SnapToGrid = false;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode node = Assert.IsType<MutableAstObjectNode>(Assert.Single(root.Children));
        DesignerHostSnapshot tree = new(Guid.NewGuid(), 200, 100, new[]
        {
            new DesignerHostNode(root.Id, 1, null, container, null, 0, 0, 200, 100, true,
                Layout: new() { Kind = container == "StackPanel" ? "Stack" : container, Width = 200, Height = 100 }),
            new DesignerHostNode(node.Id, 2, 1, "Button", null, 10, 20, 80, 40, true,
                Layout: new() { Width = 80, Height = 40, X = 10, Y = 20, M31 = 10, M32 = 20,
                    CanvasLeft = 10, CanvasTop = 20, MinWidth = 60, HorizontalAlignment = "Left", VerticalAlignment = "Top" })
        });
        PreviewerGeometryEditor editor = new(document, tree);
        DesignItem item = new(node) { HostInstanceId = 2 };
        Avalonia.Rect preview = editor.PreviewResize(item, new Avalonia.Vector(30, 10), PreviewerResizeEdges.Left | PreviewerResizeEdges.Top);
        Assert.Equal(new Avalonia.Rect(30, 30, 60, 30), preview);
        editor.ResizePointer(item, new Avalonia.Vector(30, 10), PreviewerResizeEdges.Left | PreviewerResizeEdges.Top);
        Assert.Equal("60", node.GetPropertyValue("Width"));
        Assert.Equal("30", node.GetPropertyValue("Height"));
        if (container == "Canvas")
        {
            Assert.Equal("30", node.GetPropertyValue("Canvas.Left"));
            Assert.Equal("30", node.GetPropertyValue("Canvas.Top"));
        }
        else
        {
            Assert.Equal("20,10,0,0", node.GetPropertyValue("Margin"));
            Assert.Null(node.GetPropertyValue("Canvas.Left"));
            Assert.Null(node.GetPropertyValue("Canvas.Top"));
        }
        Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
        document.SyncEngine.Undo();
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
    }
}
