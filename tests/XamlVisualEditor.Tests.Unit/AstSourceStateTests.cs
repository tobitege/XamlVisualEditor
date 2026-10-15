using System.Collections.Immutable;
using System.Text;
using System.Xml.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class AstSourceStateTests
{
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void Removing_an_attribute_on_its_own_line_removes_the_line(string newline)
    {
        string source = string.Join(newline,
            "<StackPanel xmlns=\"https://github.com/avaloniaui\">",
            "    <Button",
            "        Content=\"Ok\"",
            "        Width=\"80\"",
            "        Height=\"20\" />",
            "</StackPanel>");
        MutableAstDocument document = Parse(source);
        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(Assert.Single(document.Root!.Children));

        button.SetPropertyValue("Width", null);
        IReadOnlyList<TextEdit> edits = new XamlSerializationService()
            .ComputeMinimalEdits(document, source, Array.Empty<AstChange>());

        string expected = source.Replace(newline + "        Width=\"80\"", string.Empty, StringComparison.Ordinal);
        Assert.Equal(expected, Apply(source, edits));
    }

    [Fact]
    public void Root_namespace_declarations_edited_through_the_document_or_the_root_round_trip()
    {
        const string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         xmlns:local="clr-namespace:App">
              <Button />
            </UserControl>
            """;
        MutableAstDocument document = Parse(xaml);
        MutableAstObjectNode root = document.Root!;
        Assert.Same(root.NamespaceAliases, document.NamespaceAliases);

        document.NamespaceAliases.Remove("local");
        root.NamespaceAliases.Remove("x");
        document.NamespaceAliases["doc"] = "clr-namespace:FromDocument";
        root.NamespaceAliases["own"] = "clr-namespace:FromRoot";

        XElement saved = XElement.Parse(new XamlSerializationService().Serialize(document));
        Dictionary<string, string> declared = saved.Attributes()
            .Where(attribute => attribute.IsNamespaceDeclaration)
            .ToDictionary(attribute => attribute.Name.LocalName, attribute => attribute.Value);
        Assert.Equal(AvaloniaNamespace, declared["xmlns"]);
        Assert.False(declared.ContainsKey("local"));
        Assert.False(declared.ContainsKey("x"));
        Assert.Equal("clr-namespace:FromDocument", declared["doc"]);
        Assert.Equal("clr-namespace:FromRoot", declared["own"]);

        MutableAstDocument built = new();
        built.NamespaceAliases["x"] = XamlNamespace;
        built.Root = new MutableAstObjectNode { TypeName = "UserControl", XmlNamespace = AvaloniaNamespace };
        Assert.Equal(XamlNamespace, built.Root.NamespaceAliases["x"]);
        Assert.Same(built.Root.NamespaceAliases, built.NamespaceAliases);
    }

    [Fact]
    public void Whitespace_mode_is_inherited_until_a_nested_directive_overrides_it()
    {
        const string xaml = "<StackPanel xmlns=\"https://github.com/avaloniaui\" xml:space=\"preserve\">"
            + "<Border><TextBlock> </TextBlock></Border>"
            + "<Border xml:space=\"default\"><TextBlock> </TextBlock></Border>"
            + "</StackPanel>";

        MutableAstObjectNode[] borders = Parse(xaml).Root!.Children.OfType<MutableAstObjectNode>().ToArray();

        MutableAstObjectNode inherited = Assert.IsType<MutableAstObjectNode>(Assert.Single(borders[0].Children));
        MutableAstObjectNode overridden = Assert.IsType<MutableAstObjectNode>(Assert.Single(borders[1].Children));
        Assert.Equal(" ", Assert.IsType<MutableAstTextNode>(Assert.Single(inherited.Children)).Text);
        Assert.Empty(overridden.Children);
    }

    [Fact]
    public void Node_identities_survive_a_reparse_and_removed_paths_are_dropped()
    {
        const string xaml = "<StackPanel xmlns=\"https://github.com/avaloniaui\"><Button Content=\"A\" /><TextBlock Text=\"B\" /></StackPanel>";
        MutableAstDocument original = Parse(xaml);
        ImmutableDictionary<string, Guid> identities = AstNodeIdentities.Capture(original);
        MutableAstDocument reparsed = Parse(xaml);
        Assert.NotEqual(original.Root!.Id, reparsed.Root!.Id);

        AstNodeIdentities.Restore(reparsed, identities);

        MutableAstObjectNode[] originalChildren = original.Root.Children.OfType<MutableAstObjectNode>().ToArray();
        MutableAstObjectNode[] restoredChildren = reparsed.Root.Children.OfType<MutableAstObjectNode>().ToArray();
        Assert.Equal(original.Root.Id, reparsed.Root.Id);
        Assert.Equal(originalChildren.Select(node => node.Id), restoredChildren.Select(node => node.Id));
        Assert.Equal(originalChildren[0].Properties[0].Id, restoredChildren[0].Properties[0].Id);
        Assert.Same(restoredChildren[1], reparsed.NodeMap.FindById(originalChildren[1].Id));

        reparsed.Root.Children.RemoveAt(1);
        ImmutableDictionary<string, Guid> next = AstNodeIdentities.Capture(reparsed, identities);

        // The removed element, its property and the property value leave the captured state.
        Assert.Equal(identities.Count - 3, next.Count);
        Assert.DoesNotContain(originalChildren[1].Id, next.Values);
        Assert.Contains(originalChildren[0].Id, next.Values);
    }

    [Fact]
    public void Source_positions_follow_the_parsed_text_and_reset_for_unmatched_nodes()
    {
        MutableAstDocument live = Parse("<StackPanel xmlns=\"https://github.com/avaloniaui\">\n  <Button />\n</StackPanel>");
        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(Assert.Single(live.Root!.Children));
        Guid buttonId = button.Id;
        MutableAstObjectNode added = new() { TypeName = "TextBlock", XmlNamespace = AvaloniaNamespace, Line = 9, Column = 9 };
        live.Root.Children.Add(added);
        MutableAstDocument parsed = Parse("<StackPanel xmlns=\"https://github.com/avaloniaui\">\n\n\n  <Button />\n</StackPanel>");
        MutableAstObjectNode parsedButton = Assert.IsType<MutableAstObjectNode>(Assert.Single(parsed.Root!.Children));

        AstNodeIdentities.CopySourcePositions(live, parsed);

        Assert.Equal(4, button.Line);
        Assert.Equal(parsedButton.Column, button.Column);
        Assert.Equal(buttonId, button.Id);
        Assert.Equal(0, added.Line);
        Assert.Equal(0, added.Column);
    }

    [Fact]
    public void Source_scope_restores_only_the_namespaces_and_whitespace_mode_missing_at_the_target()
    {
        const string xaml = """
            <StackPanel xmlns="https://github.com/avaloniaui" xmlns:local="clr-namespace:App">
              <Border xml:space="preserve"><local:Card /></Border>
              <Border />
            </StackPanel>
            """;
        MutableAstObjectNode[] borders = Parse(xaml).Root!.Children.OfType<MutableAstObjectNode>().ToArray();
        MutableAstObjectNode card = Assert.IsType<MutableAstObjectNode>(Assert.Single(borders[0].Children));
        AstSourceScope scope = AstSourceScope.Capture(card);

        MutableAstObjectNode moved = AstNodeCloner.Clone(card);
        borders[1].Children.Add(moved);
        scope.ApplyTo(moved);
        Assert.Empty(moved.NamespaceAliases);
        Assert.Equal("preserve", moved.GetPropertyValue("xml:space"));

        MutableAstObjectNode detached = AstNodeCloner.Clone(card);
        scope.ApplyTo(detached);
        Assert.Equal(AvaloniaNamespace, detached.NamespaceAliases[string.Empty]);
        Assert.Equal("clr-namespace:App", detached.NamespaceAliases["local"]);
        Assert.Equal("preserve", detached.GetPropertyValue("xml:space"));

        MutableAstObjectNode sibling = AstNodeCloner.Clone(card);
        borders[0].Children.Add(sibling);
        scope.ApplyTo(sibling);
        Assert.Empty(sibling.NamespaceAliases);
        Assert.Null(sibling.GetPropertyValue("xml:space"));
    }

    private static MutableAstDocument Parse(string xaml) =>
        Assert.IsType<MutableAstDocument>(new XamlParsingService()
            .Parse(xaml, new XamlParserOptions { UseTolerantParser = false }).Document);

    private static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        StringBuilder builder = new(text);
        foreach (TextEdit edit in edits.OrderByDescending(edit => edit.Offset))
        {
            builder.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
        }

        return builder.ToString();
    }
}
