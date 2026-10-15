using System.Xml.Linq;

namespace XamlVisualEditor.Xaml;

/// <summary>
/// Whitespace significance of XAML element content. The parser and the source-preserving serializer
/// compile this file both, so they agree on which text nodes form the content of an element.
/// Whitespace-only text is significant only under xml:space="preserve" or in mixed content; in every
/// other element, including an element without child elements, it is insignificant.
/// </summary>
internal static class XamlWhitespaceRules
{
    /// <summary>
    /// Returns whether xml:space="preserve" applies to the element; a local directive overrides the
    /// mode inherited from the enclosing element.
    /// </summary>
    public static bool PreservesWhitespace(XElement element, bool inheritedPreserve) =>
        element.Attribute(XNamespace.Xml + "space") is { } directive ? directive.Value == "preserve" : inheritedPreserve;

    /// <summary>
    /// Returns whether xml:space="preserve" applies to the element, taken from the nearest directive.
    /// </summary>
    public static bool PreservesWhitespace(XElement element) =>
        element.AncestorsAndSelf().Select(parent => (string?)parent.Attribute(XNamespace.Xml + "space"))
            .FirstOrDefault(value => value is not null) == "preserve";

    /// <summary>
    /// Returns whether whitespace-only text nodes in the content of the element are significant.
    /// </summary>
    public static bool PreservesContentWhitespace(XElement element, bool preservesWhitespace) =>
        preservesWhitespace || HasMixedContent(element);

    // In mixed content such as "Hello <Bold>x</Bold> <Italic>y</Italic>" the whitespace between the
    // inline elements is displayed text.
    private static bool HasMixedContent(XElement element) =>
        element.HasElements && element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value));
}
