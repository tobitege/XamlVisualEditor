using System.Linq;
using System.Xml.Linq;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class SourceTriviaWorkflowTests
{
    [Fact]
    public void Property_edit_reopen_and_subtree_copy_preserve_comments_and_processing_instructions()
    {
        const string xaml = """
            <?editor preserve="yes"?>
            <!--file note-->
            <UserControl xmlns="https://github.com/avaloniaui">
              <StackPanel>
                <!--inside-->
                <Button>
                  <!--content note-->
                  <Button.Content><!--value note-->before<!--value tail--></Button.Content>
                </Button>
                <?panel mode="design"?>
                <!--between-->
                <TextBlock Text="{Binding Caption}" />
              </StackPanel>
            </UserControl>
            <!--tail-->
            """;
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(parser.Parse(xaml).Document);
        MutableAstObjectNode panel = Assert.Single(document.Root!.Children.OfType<MutableAstObjectNode>());
        panel.Children.OfType<MutableAstObjectNode>().First().SetPropertyValue("Content", "after");
        string edited = serializer.Serialize(document);
        XDocument xml = XDocument.Parse(edited);
        Assert.Equal(new[] { "file note", "inside", "content note", "value note", "value tail", "between", "tail" }, xml.DescendantNodes().OfType<XComment>().Select(comment => comment.Value));
        Assert.Equal(new[] { "editor:preserve=\"yes\"", "panel:mode=\"design\"" },
            xml.DescendantNodes().OfType<XProcessingInstruction>().Select(instruction => instruction.Target + ":" + instruction.Data));
        XNamespace ns = "https://github.com/avaloniaui";
        Assert.Null(xml.Descendants(ns + "Button").Single().Attribute("Content"));
        Assert.Equal("after", xml.Descendants(ns + "Button.Content").Single().Value.Trim());
        Assert.Equal("{Binding Caption}", (string?)xml.Descendants(ns + "TextBlock").Single().Attribute("Text"));

        MutableAstDocument reopened = Assert.IsType<MutableAstDocument>(parser.Parse(edited).Document);
        Assert.Equal(edited, serializer.Serialize(reopened));
        MutableAstDocument fragment = new() { Root = AstNodeCloner.Clone(panel) };
        XDocument copied = XDocument.Parse(serializer.Serialize(fragment));
        Assert.Equal(new[] { "inside", "content note", "value note", "value tail", "between" }, copied.DescendantNodes().OfType<XComment>().Select(comment => comment.Value));
        Assert.Equal("panel", Assert.Single(copied.DescendantNodes().OfType<XProcessingInstruction>()).Target);
    }
}
