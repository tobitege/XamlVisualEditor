using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Integration;

public sealed class DesignerTransactionWorkflowTests
{
    [Theory]
    [InlineData("Canvas", "\n")]
    [InlineData("Grid", "\r\n")]
    [InlineData("StackPanel", "\n")]
    public async Task AttributeEditPreservesSourceAndWhitespaceModeChangesRemainConsistent(string layout, string newline)
    {
        string original = $$"""
            <?design preserve-this?>
            <UserControl xmlns='https://github.com/avaloniaui' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
              <!-- Keep the original formatting and expressions. -->
              <{{layout}}>
                <Button x:Name='Action' Width = '80' Content='{Binding Caption}' Tag='A &amp; B'>
                  <Button.Resources>
                    <SolidColorBrush x:Key='Accent' Color='#123456' />
                  </Button.Resources>
                  <Button.Template>
                    <ControlTemplate><Border Background='{DynamicResource Accent}' /></ControlTemplate>
                  </Button.Template>
                </Button>
              </{{layout}}>
            </UserControl>
            """;
        original = original.ReplaceLineEndings(newline);
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        AstNodeMap map = new();
        using SyncEngine engine = new(parser, serializer, map);
        await engine.LoadAsync(original);
        MutableAstObjectNode button = Find(engine.CurrentDocument!.Root!, "Button");
        Guid buttonId = button.Id;
        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Resize control"))
        {
            button.SetPropertyValue("Width", "120");
            edit.Commit();
        }
        string edited = original.Replace("Width = '80'", "Width = '120'", StringComparison.Ordinal);
        Assert.Equal(edited, engine.GetCurrentText());
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        Assert.Equal("{Binding Caption}", Assert.IsType<MutableAstObjectNode>(map.FindById(buttonId)).GetPropertyValue("Content"));
        engine.Redo();
        Assert.Equal(edited, engine.GetCurrentText());

        string path = Path.Combine(Path.GetTempPath(), "xve-source-fidelity-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(path, await engine.SaveAsync());
        string persisted = await File.ReadAllTextAsync(path);
        Assert.Equal(edited, persisted);
        using SyncEngine reopened = new(parser, serializer, new AstNodeMap());
        await reopened.LoadAsync(persisted);
        Assert.Equal("120", Find(reopened.CurrentDocument!.Root!, "Button").GetPropertyValue("Width"));

        using (DocumentEditTransaction edit = reopened.BeginEditTransaction("Preserve whitespace"))
        {
            Find(reopened.CurrentDocument.Root!, layout).SetPropertyValue("xml:space", "preserve");
            edit.Commit();
        }
        string whitespaceChanged = await reopened.SaveAsync();
        ParseResult parsed = parser.Parse(whitespaceChanged, new XamlParserOptions { UseTolerantParser = false });
        Assert.False(parsed.HasErrors);
        Assert.Equal(serializer.Serialize(reopened.CurrentDocument!),
            serializer.Serialize(Assert.IsType<MutableAstDocument>(parsed.Document)));
        reopened.Undo();
        Assert.Equal(edited, reopened.GetCurrentText());
        reopened.Redo();
        Assert.Equal(whitespaceChanged, await reopened.SaveAsync());
    }

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

    [Theory]
    [InlineData("\n", false)]
    [InlineData("\r\n", false)]
    [InlineData("\n", true)]
    [InlineData("\r\n", true)]
    public async Task StructuralChangesPreserveUnchangedMarkupAndSavedSource(string newLine, bool remove)
    {
        string header = "<?design keep-this?>" + newLine +
            "<UserControl xmlns='https://github.com/avaloniaui' xmlns:x = 'http://schemas.microsoft.com/winfx/2006/xaml'>";
        string resources = string.Join(newLine,
            "  <UserControl.Resources>",
            "    <!-- Keep this resource comment. -->",
            "    <SolidColorBrush x:Key='Accent' Color = '#112233' />",
            "  </UserControl.Resources>");
        string moved = string.Join(newLine,
            "    <Border x:Name='Moved' Background = '{StaticResource Accent}'>",
            "      <!-- Keep this template comment. -->",
            "      <Button Content = '{Binding Caption}'>",
            "        <Button.Template>",
            "          <ControlTemplate>",
            "            <Border Background = '{TemplateBinding Background}' />",
            "          </ControlTemplate>",
            "        </Button.Template>",
            "      </Button>",
            "    </Border>");
        const string untouched = "    <Button x:Name='Untouched' Width = '80' Content='A &amp; B' />";
        string changed = untouched.Replace("'80'", "'120'", StringComparison.Ordinal);
        string original = string.Join(newLine, header, resources, "  <StackPanel x:Name='Layout'>",
            moved, untouched, "  </StackPanel>", "</UserControl>", "");
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await engine.LoadAsync(original);
        MutableAstObjectNode container = Find(engine.CurrentDocument!.Root!, "StackPanel");
        MutableAstObjectNode border = Find(container, "Border");
        MutableAstObjectNode button = container.Children.OfType<MutableAstObjectNode>()
            .Single(node => node.TypeName == "Button");

        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Change structure and width"))
        {
            container.Children.Remove(border);
            if (!remove) container.Children.Add(border);
            button.SetPropertyValue("Width", "120");
            engine.NotifyAstChanged(engine.CurrentDocument, SyncSource.DesignSurface);
            edit.Commit();
        }

        string edited = engine.GetCurrentText();
        Assert.StartsWith(header, edited);
        Assert.Contains(resources, edited);
        Assert.Contains(changed, edited);
        Assert.EndsWith(newLine, edited);
        if (remove)
        {
            Assert.DoesNotContain("x:Name='Moved'", edited);
            Assert.DoesNotContain("Keep this template comment.", edited);
        }
        else
        {
            Assert.Contains(moved, edited);
            Assert.True(edited.IndexOf(changed, StringComparison.Ordinal) < edited.IndexOf(moved, StringComparison.Ordinal));
        }
        Assert.Equal(1, engine.UndoRedo.UndoCount);
        Assert.Equal(edited, await engine.SaveAsync());

        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await reopened.LoadAsync(edited);
        MutableAstObjectNode reopenedContainer = Find(reopened.CurrentDocument!.Root!, "StackPanel");
        MutableAstObjectNode reopenedButton = reopenedContainer.Children.OfType<MutableAstObjectNode>()
            .Single(node => node.TypeName == "Button");
        Assert.Equal("120", reopenedButton.GetPropertyValue("Width"));
        Assert.Equal("A & B", reopenedButton.GetPropertyValue("Content"));
        if (!remove)
            Assert.Equal("{Binding Caption}", Find(Find(reopenedContainer, "Border"), "Button").GetPropertyValue("Content"));

        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        engine.Redo();
        Assert.Equal(edited, engine.GetCurrentText());
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public async Task InsertedMarkupKeepsDocumentLineEndingsWithoutChangingLiteralValues(string newLine)
    {
        const string existing = "  <Button Content='Keep this' />";
        string original = string.Join(newLine,
            "<StackPanel xmlns='https://github.com/avaloniaui'>", existing, "</StackPanel>", "");
        const string literal = @"Literal \r\n is not a line break";
        const string separator = "First\u2028second";
        using SyncEngine engine = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await engine.LoadAsync(original);
        MutableAstObjectNode inserted = new() { TypeName = "TextBlock", XmlNamespace = "https://github.com/avaloniaui" };
        inserted.SetPropertyValue("Name", "Inserted");
        inserted.SetPropertyValue("Text", literal);
        inserted.SetPropertyValue("Tag", separator);
        using (DocumentEditTransaction edit = engine.BeginEditTransaction("Insert control"))
        {
            engine.CurrentDocument!.Root!.Children.Add(inserted);
            edit.Commit();
        }
        string saved = await engine.SaveAsync();
        string consistent = saved.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Replace("\n", newLine, StringComparison.Ordinal);
        Assert.Equal(consistent, saved);
        Assert.Contains(existing, saved);
        Assert.EndsWith(newLine, saved);
        using SyncEngine reopened = new(new XamlParsingService(), new XamlSerializationService(), new AstNodeMap());
        await reopened.LoadAsync(saved);
        MutableAstObjectNode restored = Find(reopened.CurrentDocument!.Root!, "TextBlock");
        Assert.Equal(literal, restored.GetPropertyValue("Text"));
        Assert.Equal(separator, restored.GetPropertyValue("Tag"));
        engine.Undo();
        Assert.Equal(original, engine.GetCurrentText());
        engine.Redo();
        Assert.Equal(saved, engine.GetCurrentText());
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
