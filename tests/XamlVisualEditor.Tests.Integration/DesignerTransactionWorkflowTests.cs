using XamlVisualEditor.Core;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Integration;

public sealed class DesignerTransactionWorkflowTests
{
    [Theory]
    [InlineData("Canvas")]
    [InlineData("Grid")]
    [InlineData("StackPanel")]
    public async Task StructuralEditUndoRedoCancelAndReopenPreserveTheWholeForm(string layout)
    {
        string original = $$"""
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
                <UserControl.Resources>
                    <SolidColorBrush x:Key="Accent">Red</SolidColorBrush>
                </UserControl.Resources>
                <{{layout}}>
                    <Border Background="{StaticResource Accent}">
                        <StackPanel>
                            <TextBlock Text="{Binding Caption}" />
                            <Button Content="Keep this subtree" />
                        </StackPanel>
                    </Border>
                    <Button Content="Other" />
                </{{layout}}>
            </UserControl>
            """;
        AstNodeMap map = new();
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), map);
        await engine.LoadAsync(original);
        using IDisposable feedback = engine.SyncEvents.Subscribe(sync =>
        {
            if (sync.UpdatedText is not null) engine.NotifyTextChanged(sync.UpdatedText, SyncSource.CodeEditor);
        });
        MutableAstObjectNode container = Find(engine.CurrentDocument!.Root!, layout);
        MutableAstObjectNode border = Find(container, "Border");
        MutableAstObjectNode boundText = Find(border, "TextBlock");
        Guid borderId = border.Id;
        Guid boundTextId = boundText.Id;

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Change form layout"))
        {
            container.Children.Move(0, 1);
            Assert.Same(container, border.Parent);
            container.Children.Remove(border);
            container.SetPropertyValue("Margin", "12");
            engine.NotifyAstChanged(engine.CurrentDocument!, SyncSource.DesignSurface);
            Assert.Equal(0, engine.UndoRedo.UndoCount);
            using (DocumentEditTransaction nested = engine.BeginEditTransaction("Set empty caption"))
            {
                Find(container, "Button").SetPropertyValue("Content", string.Empty);
                nested.Commit();
            }
            Assert.Equal(0, engine.UndoRedo.UndoCount);
            edit.Commit();
        }

        string edited = engine.GetCurrentText();
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        Assert.DoesNotContain("Keep this subtree", edited);
        Assert.Contains("Content=\"\"", edited);
        Assert.Contains("Accent", edited);

        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        MutableAstObjectNode restoredBorder = Assert.IsType<MutableAstObjectNode>(map.FindById(borderId));
        Assert.Equal("{StaticResource Accent}", restoredBorder.GetPropertyValue("Background"));
        Assert.Equal("{Binding Caption}", Assert.IsType<MutableAstObjectNode>(map.FindById(boundTextId)).GetPropertyValue("Text"));
        Assert.Equal("Keep this subtree", Find(restoredBorder, "Button").GetPropertyValue("Content"));

        engine.Redo();
        Assert.Equal(edited, engine.GetCurrentText());
        using (engine.BeginEditTransaction("Cancelled resize"))
        {
            MutableAstObjectNode current = Find(engine.CurrentDocument!.Root!, layout);
            current.SetPropertyValue("Width", "777");
            MutableAstNode[] removed = current.Children.ToArray();
            current.Children.Clear();
            Assert.All(removed, node => Assert.Null(node.Parent));
            engine.NotifyAstChanged(engine.CurrentDocument!, SyncSource.DesignSurface);
        }
        Assert.Equal(edited, engine.GetCurrentText());
        Assert.Equal(1, engine.UndoRedo.UndoCount);

        using (engine.BeginEditTransaction("No-op cancellation")) { }
        Assert.Equal(edited, engine.GetCurrentText());
        Assert.Equal(1, engine.UndoRedo.UndoCount);

        engine.Undo();
        string saved = await engine.SaveAsync();
        Assert.Equal(original, saved);
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await reopened.LoadAsync(saved);
        Assert.Equal("{Binding Caption}", Find(reopened.CurrentDocument!.Root!, "TextBlock").GetPropertyValue("Text"));
        Assert.Equal("{StaticResource Accent}", Find(reopened.CurrentDocument.Root!, "Border").GetPropertyValue("Background"));
    }

    [Fact]
    public async Task InvalidCodeRemainsSaveableAndUndoableButCannotBeOverwrittenByVisualEdits()
    {
        const string original = "<Grid xmlns=\"https://github.com/avaloniaui\"><Button Content=\"Original\" /></Grid>";
        const string incomplete = "<Grid xmlns=\"https://github.com/avaloniaui\"><Button";
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await engine.LoadAsync(original);
        engine.NotifyTextChanged(incomplete, SyncSource.CodeEditor);

        Assert.Equal(incomplete, await engine.SaveAsync());
        Assert.False(engine.CanEditAst);
        Assert.Throws<InvalidOperationException>(() => engine.BeginEditTransaction("Invalid designer edit"));
        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        Assert.True(engine.CanEditAst);
        engine.Redo();
        Assert.Equal(incomplete, engine.GetCurrentText());
    }

    private static MutableAstObjectNode Find(MutableAstObjectNode root, string typeName)
    {
        Stack<MutableAstObjectNode> nodes = new();
        nodes.Push(root);
        while (nodes.TryPop(out MutableAstObjectNode? node))
        {
            if (node.TypeName == typeName) return node;
            foreach (MutableAstObjectNode child in node.Children.OfType<MutableAstObjectNode>().Reverse()) nodes.Push(child);
        }
        throw new InvalidOperationException("Missing fixture control: " + typeName);
    }
}
