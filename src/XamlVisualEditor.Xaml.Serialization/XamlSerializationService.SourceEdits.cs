using System.Text;
using System.Xml;
using System.Xml.Linq;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Xaml.Serialization;

public sealed partial class XamlSerializationService
{
    private static bool TryComputeAttributeEdits(string sourceText, string targetText,
        out IReadOnlyList<TextEdit> result)
    {
        result = Array.Empty<TextEdit>();
        try
        {
            XDocument source = XDocument.Parse(sourceText, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            XDocument target = XDocument.Parse(targetText, LoadOptions.PreserveWhitespace);
            IReadOnlyList<int> lineOffsets = GetSourceLineOffsets(sourceText);

            List<TextEdit> edits = new();
            if (!CompareContent(source, target, sourceText, lineOffsets, edits)) return false;
            TextEdit[] ordered = edits.OrderBy(edit => edit.Offset).ToArray();
            if (!EditsReproduceTarget(sourceText, ordered, target)) return false;
            result = ordered;
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private static bool EditsReproduceTarget(string sourceText, TextEdit[] orderedEdits, XDocument target)
    {
        for (int index = 1; index < orderedEdits.Length; index++)
        {
            TextEdit previous = orderedEdits[index - 1];
            if (previous.Offset + previous.Length > orderedEdits[index].Offset) return false;
        }

        // Edits are applied from the highest offset down, as the editor and the sync engine do.
        StringBuilder builder = new(sourceText);
        foreach (TextEdit edit in orderedEdits.OrderByDescending(edit => edit.Offset))
            builder.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);

        string editedText = builder.ToString();
        XDocument edited = XDocument.Parse(editedText, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        List<TextEdit> remaining = new();
        return CompareContent(edited, target, editedText, GetSourceLineOffsets(editedText), remaining) &&
            remaining.Count == 0;
    }

    private static bool CompareContent(XContainer source, XContainer target, string sourceText,
        IReadOnlyList<int> lineOffsets, List<TextEdit> edits)
    {
        XNode[] sourceNodes = SignificantContent(source);
        XNode[] targetNodes = SignificantContent(target);
        if (sourceNodes.Length != targetNodes.Length) return false;

        for (int index = 0; index < sourceNodes.Length; index++)
        {
            if (sourceNodes[index] is XElement sourceElement && targetNodes[index] is XElement targetElement)
            {
                if (sourceElement.Name != targetElement.Name ||
                    !CompareAttributes(sourceElement, targetElement, sourceText, lineOffsets, edits) ||
                    !CompareContent(sourceElement, targetElement, sourceText, lineOffsets, edits)) return false;
            }
            else if (sourceNodes[index] is XText sourceValue && targetNodes[index] is XText targetValue)
            {
                if (sourceValue.Value != targetValue.Value) return false;
            }
            else if (!XNode.DeepEquals(sourceNodes[index], targetNodes[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static XNode[] SignificantContent(XContainer container)
    {
        bool preserveWhitespace = PreservesContentWhitespace(container);
        return container.Nodes().Where(node => preserveWhitespace ||
            node is not XText text || !string.IsNullOrWhiteSpace(text.Value)).ToArray();
    }

    private static bool PreservesContentWhitespace(XContainer container) => container is XElement element &&
            (!element.HasElements ||
             element.AncestorsAndSelf().Select(parent => (string?)parent.Attribute(XNamespace.Xml + "space"))
                 .FirstOrDefault(value => value is not null) == "preserve" ||
             element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)));

    private static IReadOnlyList<int> GetSourceLineOffsets(string text)
    {
        List<int> offsets = new() { 0 };
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r')
            {
                if (index + 1 < text.Length && text[index + 1] == '\n') index++;
                offsets.Add(index + 1);
            }
            else if (text[index] == '\n') offsets.Add(index + 1);
        }
        return offsets;
    }

    private static bool CompareAttributes(XElement source, XElement target, string sourceText,
        IReadOnlyList<int> lineOffsets, List<TextEdit> edits)
    {
        // Retained indentation may become content when the inherited whitespace mode changes.
        if ((string?)source.Attribute(XNamespace.Xml + "space") !=
            (string?)target.Attribute(XNamespace.Xml + "space")) return false;

        int firstElementEdit = edits.Count;
        foreach (XAttribute attribute in source.Attributes())
        {
            XAttribute? replacement = target.Attribute(attribute.Name);
            if (replacement?.Value == attribute.Value) continue;

            // Changing a prefix binding could also change retained element or attribute names.
            if (attribute.IsNamespaceDeclaration ||
                !TryGetAttributeSpan(attribute, sourceText, lineOffsets, out AttributeSpan span)) return false;

            if (replacement is null)
            {
                int start = span.Start;
                int end = span.End;
                while (start > 0 && sourceText[start - 1] is ' ' or '\t') start--;
                int lineEnd = end;
                while (lineEnd < sourceText.Length && sourceText[lineEnd] is ' ' or '\t') lineEnd++;
                // An attribute alone on its line takes the line with it instead of leaving it empty.
                if (start > 0 && sourceText[start - 1] is '\r' or '\n' &&
                    (lineEnd == sourceText.Length || sourceText[lineEnd] is '\r' or '\n'))
                {
                    start--;
                    if (sourceText[start] == '\n' && start > 0 && sourceText[start - 1] == '\r') start--;
                    end = lineEnd;
                }
                edits.Add(new TextEdit { Offset = start, Length = end - start, NewText = string.Empty });
            }
            else
            {
                string escaped = EscapeXmlAttribute(replacement.Value);
                if (span.Quote == '\'') escaped = escaped.Replace("'", "&apos;", StringComparison.Ordinal);
                edits.Add(new TextEdit
                {
                    Offset = span.ValueStart,
                    Length = span.ValueEnd - span.ValueStart,
                    NewText = escaped
                });
            }
        }

        StringBuilder additions = new();
        foreach (XAttribute attribute in target.Attributes())
        {
            if (source.Attribute(attribute.Name) is not null) continue;
            if (attribute.IsNamespaceDeclaration)
            {
                string prefix = attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName;
                XNamespace? previous = prefix.Length == 0 ? source.GetDefaultNamespace() : source.GetNamespaceOfPrefix(prefix);
                if (previous is not null && previous.NamespaceName != attribute.Value) return false;
            }

            string? name = QualifiedAttributeName(target, attribute);
            if (name is null) return false;
            additions.Append(' ').Append(name).Append("=\"").Append(EscapeXmlAttribute(attribute.Value)).Append('"');
        }

        if (additions.Length == 0) return true;
        int offset = SourceOffset(source, lineOffsets);
        if (offset <= 0 || offset >= sourceText.Length || sourceText[offset - 1] != '<') return false;
        char quote = '\0';
        for (; offset < sourceText.Length; offset++)
        {
            char character = sourceText[offset];
            if (quote != '\0')
            {
                if (character == quote) quote = '\0';
            }
            else if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '>')
            {
                if (sourceText[offset - 1] == '/') offset--;
                while (offset > 0 && char.IsWhiteSpace(sourceText[offset - 1])) offset--;
                for (int index = firstElementEdit; index < edits.Count; index++)
                {
                    TextEdit removal = edits[index];
                    // An insertion point inside a removed range would interleave with the removal,
                    // so the additions replace the removed text instead.
                    if (removal.NewText.Length != 0 || removal.Offset >= offset ||
                        offset > removal.Offset + removal.Length) continue;
                    edits[index] = new TextEdit
                    {
                        Offset = removal.Offset,
                        Length = removal.Length,
                        NewText = additions.ToString()
                    };
                    return true;
                }

                edits.Add(new TextEdit { Offset = offset, Length = 0, NewText = additions.ToString() });
                return true;
            }
        }

        return false;
    }

    private static string? QualifiedAttributeName(XElement owner, XAttribute attribute)
    {
        if (attribute.IsNamespaceDeclaration)
            return attribute.Name.LocalName == "xmlns" ? "xmlns" : "xmlns:" + attribute.Name.LocalName;
        if (attribute.Name.Namespace == XNamespace.None) return attribute.Name.LocalName;
        if (attribute.Name.Namespace == XNamespace.Xml) return "xml:" + attribute.Name.LocalName;

        foreach (XElement scope in owner.AncestorsAndSelf())
        {
            foreach (XAttribute declaration in scope.Attributes().Where(value => value.IsNamespaceDeclaration))
            {
                string prefix = declaration.Name.LocalName;
                if (prefix != "xmlns" && declaration.Value == attribute.Name.NamespaceName &&
                    owner.GetNamespaceOfPrefix(prefix) == attribute.Name.Namespace)
                    return prefix + ":" + attribute.Name.LocalName;
            }
        }

        return null;
    }

    private static bool TryGetAttributeSpan(XAttribute attribute, string text, IReadOnlyList<int> lineOffsets,
        out AttributeSpan span)
    {
        span = default;
        int start = SourceOffset(attribute, lineOffsets);
        if (start < 0 || start >= text.Length) return false;
        int index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '=') index++;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || text[index++] != '=') return false;
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
        if (index >= text.Length || text[index] is not ('\'' or '"')) return false;
        char quote = text[index++];
        int end = text.IndexOf(quote, index);
        if (end < 0) return false;
        span = new AttributeSpan(start, index, end, end + 1, quote);
        return true;
    }

    private static int SourceOffset(XObject value, IReadOnlyList<int> lineOffsets)
    {
        IXmlLineInfo location = value;
        return location.HasLineInfo() && location.LineNumber > 0 && location.LineNumber <= lineOffsets.Count
            ? lineOffsets[location.LineNumber - 1] + location.LinePosition - 1
            : -1;
    }

    private readonly record struct AttributeSpan(int Start, int ValueStart, int ValueEnd, int End, char Quote);
}
