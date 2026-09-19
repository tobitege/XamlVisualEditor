using System.Xml.Linq;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class WhitespaceRoundTripTests
{
    [Fact]
    public void CopyingMixedContentPreservesLocalWhitespaceDirectivesCommentsAndAttributeCharacters()
    {
        const string source = """
            <StackPanel xmlns="https://github.com/avaloniaui">
                <TextBlock Text="first&#xA;second&#x9;end" />
                <TextBlock><TextBlock.Text xml:space="preserve">  property text  </TextBlock.Text></TextBlock>
                <TextBlock xml:space="preserve">before <Run Text="middle" /> after<!--keep-->  </TextBlock>
            </StackPanel>
            """;
        XamlParsingService parser = new();
        MutableAstDocument original = Assert.IsType<MutableAstDocument>(parser.Parse(source).Document);
        MutableAstDocument copy = new() { Root = AstNodeCloner.Clone(original.Root!) };
        XElement saved = XElement.Parse(new XamlSerializationService().Serialize(copy), LoadOptions.PreserveWhitespace);
        XNamespace avalonia = "https://github.com/avaloniaui";
        XElement[] blocks = System.Linq.Enumerable.ToArray(saved.Elements(avalonia + "TextBlock"));
        Assert.Equal("first\nsecond\tend", blocks[0].Attribute("Text")?.Value);
        Assert.Equal("preserve", blocks[1].Element(avalonia + "TextBlock.Text")?.Attribute(XNamespace.Xml + "space")?.Value);
        Assert.Equal("  property text  ", blocks[1].Element(avalonia + "TextBlock.Text")?.Value);
        Assert.Equal("before  after  ", blocks[2].Value);
        Assert.Equal("middle", blocks[2].Element(avalonia + "Run")?.Attribute("Text")?.Value);
        Assert.Contains(blocks[2].Nodes(), node => node is XComment { Value: "keep" });
    }

    [Theory]
    [InlineData("  leading and trailing  ", false)]
    [InlineData(" \t ", true)]
    [InlineData("first\n  second\n", true)]
    public void UnrelatedPropertyEditPreservesTextWithXmlSpaceAcrossSaveAndReopen(string text, bool propertyElement)
    {
        XNamespace avalonia = "https://github.com/avaloniaui";
        XElement source = new(avalonia + "TextBlock", new XAttribute(XNamespace.Xml + "space", "preserve"));
        source.Add(propertyElement ? new XElement(avalonia + "TextBlock.Text", text) : new XText(text));
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        MutableAstDocument document = Assert.IsType<MutableAstDocument>(parser.Parse(source.ToString(SaveOptions.DisableFormatting)).Document);
        document.Root!.Properties.Add(new MutableAstPropertyNode
        {
            PropertyName = "Opacity",
            Value = new MutableAstTextNode { Text = "0.75" }
        });

        for (int save = 0; save < 2; save++)
        {
            string saved = serializer.Serialize(document);
            XElement reopened = XElement.Parse(saved, LoadOptions.PreserveWhitespace);
            Assert.Equal("preserve", reopened.Attribute(XNamespace.Xml + "space")?.Value);
            Assert.Equal("0.75", reopened.Attribute("Opacity")?.Value);
            Assert.Equal(text, propertyElement ? reopened.Element(avalonia + "TextBlock.Text")?.Value : reopened.Value);
            document = Assert.IsType<MutableAstDocument>(parser.Parse(saved).Document);
        }
    }
}
