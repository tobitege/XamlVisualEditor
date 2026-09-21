using System.Text;
using System.Xml;
using System.Xml.Linq;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Xaml.Serialization;

public sealed partial class XamlSerializationService
{
    private static bool TryPreserveStructuralSource(string sourceText, string targetText, out string mergedText)
    {
        mergedText = targetText;
        try
        {
            XmlSource source = new(sourceText);
            string normalizedTarget = targetText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (source.NewLine != "\n") normalizedTarget = normalizedTarget.Replace("\n", source.NewLine, StringComparison.Ordinal);
            XmlSource target = new(normalizedTarget);
            if (!source.Map() || !target.Map()) return false;
            SourceMerger merger = new(source, target);
            string merged = sourceText[..source.Spans[source.Document].OpenEnd] +
                merger.RenderContent(source.Document, target.Document);
            XDocument reparsed = XDocument.Parse(merged, LoadOptions.PreserveWhitespace);
            if (!merger.Equivalent(reparsed, target.Document)) return false;
            mergedText = merged;
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    private sealed class SourceMerger
    {
        private readonly XmlSource _source;
        private readonly XmlSource _target;
        private readonly Dictionary<XNode, int> _hashes = new();
        private readonly Dictionary<string, XNode> _named;
        private readonly HashSet<string> _uniqueTargetNames;
        private readonly Dictionary<int, XNode[]> _exact;

        public SourceMerger(XmlSource source, XmlSource target)
        {
            _source = source;
            _target = target;
            _named = UniqueNames(source.Document);
            _uniqueTargetNames = UniqueNames(target.Document).Keys.ToHashSet(StringComparer.Ordinal);
            _exact = source.Document.DescendantNodes().GroupBy(Hash)
                .ToDictionary(group => group.Key, group => group.ToArray());
        }

        public string RenderContent(XContainer? source, XContainer target)
        {
            if (source is not null && ContentEquivalent(source, target) && ContentBindingsMatch(source, target))
                return _source.Inner(source);

            XNode[] oldNodes = source is null ? Array.Empty<XNode>() : SignificantContent(source);
            XNode[] newNodes = SignificantContent(target);
            XNode?[] pairs = MatchChildren(oldNodes, newNodes);
            Dictionary<XNode, int> oldIndices = oldNodes.Select((node, index) => (node, index))
                .ToDictionary(pair => pair.node, pair => pair.index);
            bool retainGaps = source is not null && !PreservesContentWhitespace(source) && !PreservesContentWhitespace(target);
            StringBuilder result = new();
            for (int index = 0; index < newNodes.Length; index++)
            {
                XNode node = newNodes[index];
                XNode? previous = pairs[index] ?? FindGlobal(node);
                string gap;
                if (retainGaps && previous is not null && oldIndices.TryGetValue(previous, out int oldIndex))
                    gap = _source.GapBefore(source!, oldNodes, oldIndex);
                else if (retainGaps && oldNodes.Length > 0)
                    gap = _source.GapBefore(source!, oldNodes, 0);
                else
                    gap = _target.GapBefore(target, newNodes, index).ReplaceLineEndings(_source.NewLine);
                result.Append(gap).Append(RenderNode(previous, node));
            }

            string tail = retainGaps && oldNodes.Length > 0 && newNodes.Length > 0
                ? _source.GapAfter(source!, oldNodes)
                : _target.GapAfter(target, newNodes).ReplaceLineEndings(_source.NewLine);
            return result.Append(tail).ToString();
        }

        private string RenderNode(XNode? source, XNode target)
        {
            if (source is not null && Equivalent(source, target) && BindingsMatch(source, target))
                return _source.Raw(source);
            if (target is not XElement element) return _target.Raw(target);

            XElement? original = source as XElement;
            if (original is not null && WhitespaceMode(original) != WhitespaceMode(element))
                return _target.Raw(element);

            List<TextEdit> attributes = new();
            bool retainHeader = original is not null && original.Name == element.Name &&
                HeaderBindingsMatch(original, element) &&
                CompareAttributes(original, element, _source.Text, _source.LineOffsets, attributes);
            XmlSource headerSource = retainHeader ? _source : _target;
            XElement headerElement = retainHeader ? original! : element;
            MarkupSpan span = headerSource.Spans[headerElement];
            StringBuilder opening = new(headerSource.Text[span.Start..span.OpenEnd]);
            if (retainHeader)
            {
                foreach (TextEdit edit in attributes.OrderByDescending(edit => edit.Offset))
                    opening.Remove(edit.Offset - span.Start, edit.Length).Insert(edit.Offset - span.Start, edit.NewText);
            }

            string body = RenderContent(original, element);
            if (body.Length == 0 && span.Empty) return opening.ToString();
            if (span.Empty) opening.Remove(opening.Length - 2, 1);
            string closing = span.Empty
                ? "</" + ReadMarkupName(opening.ToString(), 1) + ">"
                : headerSource.Text[span.CloseStart..span.End];
            return opening.Append(body).Append(closing).ToString();
        }

        private XNode?[] MatchChildren(XNode[] source, XNode[] target)
        {
            XNode?[] pairs = new XNode?[target.Length];
            HashSet<XNode> remaining = new(source);
            Dictionary<string, XNode> named = source.Where(node => Identity(node) is not null)
                .GroupBy(node => Identity(node)!, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
            for (int index = 0; index < target.Length; index++)
            {
                string? key = Identity(target[index]);
                if (key is not null && named.TryGetValue(key, out XNode? node) && remaining.Remove(node))
                    pairs[index] = node;
            }

            Dictionary<int, Queue<XNode>> exact = source.Where(remaining.Contains).GroupBy(Hash)
                .ToDictionary(group => group.Key, group => new Queue<XNode>(group));
            for (int index = 0; index < target.Length; index++)
            {
                if (pairs[index] is not null || !exact.TryGetValue(Hash(target[index]), out Queue<XNode>? queue)) continue;
                int candidates = queue.Count;
                while (candidates-- > 0)
                {
                    XNode node = queue.Dequeue();
                    if (Equivalent(node, target[index]))
                    {
                        pairs[index] = node;
                        remaining.Remove(node);
                        break;
                    }
                    queue.Enqueue(node);
                }
            }

            for (int index = 0; index < target.Length; index++)
            {
                if (pairs[index] is not null || FindGlobal(target[index]) is not { } original) continue;
                pairs[index] = original;
                remaining.Remove(original);
            }

            Dictionary<string, Queue<XNode>> compatible = source.Where(remaining.Contains).GroupBy(Kind)
                .ToDictionary(group => group.Key, group => new Queue<XNode>(group), StringComparer.Ordinal);
            for (int index = 0; index < target.Length; index++)
            {
                if (pairs[index] is null && compatible.TryGetValue(Kind(target[index]), out Queue<XNode>? queue) && queue.Count > 0)
                    pairs[index] = queue.Dequeue();
            }
            return pairs;
        }

        private XNode? FindGlobal(XNode target)
        {
            string? key = Identity(target);
            if (key is not null && _uniqueTargetNames.Contains(key) && _named.TryGetValue(key, out XNode? named)) return named;
            return _exact.TryGetValue(Hash(target), out XNode[]? candidates)
                ? candidates.FirstOrDefault(node => Equivalent(node, target)) : null;
        }

        private static Dictionary<string, XNode> UniqueNames(XDocument document) => document.DescendantNodes()
            .Where(node => Identity(node) is not null).GroupBy(node => Identity(node)!, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

        private static string? Identity(XNode node)
        {
            if (node is not XElement element) return null;
            XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
            string? name = (string?)element.Attribute(xaml + "Name") ?? (string?)element.Attribute("Name");
            if (name is not null) return element.Name + "|name|" + name;
            string? key = (string?)element.Attribute(xaml + "Key");
            return key is null ? null : element.Name + "|key|" + key;
        }

        private static string Kind(XNode node) => node switch
        {
            XElement element => "element|" + element.Name + "|" + Identity(element),
            XText => "text",
            XProcessingInstruction instruction => "instruction|" + instruction.Target,
            _ => node.NodeType.ToString()
        };

        public bool Equivalent(XNode first, XNode second)
        {
            if (Hash(first) != Hash(second)) return false;
            if (first is XText firstText && second is XText secondText) return firstText.Value == secondText.Value;
            if (first is XElement a && second is XElement b)
                return a.Name == b.Name && a.Attributes().Count() == b.Attributes().Count() &&
                    a.Attributes().All(attribute => b.Attribute(attribute.Name)?.Value == attribute.Value) && ContentEquivalent(a, b);
            if (first is XDocument source && second is XDocument target) return ContentEquivalent(source, target);
            return XNode.DeepEquals(first, second);
        }

        private bool ContentEquivalent(XContainer source, XContainer target)
        {
            using IEnumerator<object> a = ContentParts(source).GetEnumerator();
            using IEnumerator<object> b = ContentParts(target).GetEnumerator();
            while (a.MoveNext())
            {
                if (!b.MoveNext()) return false;
                if (a.Current is string text)
                {
                    if (b.Current is not string other || text != other) return false;
                }
                else if (b.Current is not XNode other || !Equivalent((XNode)a.Current, other)) return false;
            }
            return !b.MoveNext();
        }

        private int Hash(XNode node)
        {
            if (_hashes.TryGetValue(node, out int value)) return value;
            HashCode hash = new();
            hash.Add(node is XText ? XmlNodeType.Text : node.NodeType);
            if (node is XElement element)
            {
                hash.Add(element.Name);
                foreach (XAttribute attribute in element.Attributes().OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal))
                {
                    hash.Add(attribute.Name);
                    hash.Add(attribute.Value, StringComparer.Ordinal);
                }
            }
            if (node is XContainer container)
            {
                foreach (object part in ContentParts(container))
                {
                    if (part is string text) { hash.Add(0); hash.Add(text, StringComparer.Ordinal); }
                    else { hash.Add(1); hash.Add(Hash((XNode)part)); }
                }
            }
            else if (node is XText text) hash.Add(text.Value, StringComparer.Ordinal);
            else hash.Add(node.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal);
            value = hash.ToHashCode();
            _hashes[node] = value;
            return value;
        }

        private static IEnumerable<object> ContentParts(XContainer container)
        {
            StringBuilder? text = null;
            foreach (XNode node in SignificantContent(container))
            {
                if (node is XText value) { (text ??= new StringBuilder()).Append(value.Value); continue; }
                if (text is not null) { yield return text.ToString(); text = null; }
                yield return node;
            }
            if (text is not null) yield return text.ToString();
        }

        private bool BindingsMatch(XNode source, XNode target) => source is not XElement a || target is not XElement b ||
            HeaderBindingsMatch(a, b) && ContentBindingsMatch(a, b);

        private bool ContentBindingsMatch(XContainer source, XContainer target)
        {
            using IEnumerator<XElement> a = source.Descendants().GetEnumerator();
            using IEnumerator<XElement> b = target.Descendants().GetEnumerator();
            while (a.MoveNext())
                if (!b.MoveNext() || !HeaderBindingsMatch(a.Current, b.Current)) return false;
            return !b.MoveNext();
        }

        private bool HeaderBindingsMatch(XElement source, XElement target)
        {
            if (WhitespaceMode(source) != WhitespaceMode(target)) return false;
            string elementName = ReadMarkupName(_source.Text, _source.Spans[source].Start + 1);
            if (!NameBindingMatches(elementName, source.Name.Namespace, target, false)) return false;
            foreach (XAttribute attribute in source.Attributes())
            {
                if (attribute.IsNamespaceDeclaration || target.Attribute(attribute.Name) is null) continue;
                string name = ReadMarkupName(_source.Text, SourceOffset(attribute, _source.LineOffsets));
                if (!NameBindingMatches(name, attribute.Name.Namespace, target, true)) return false;
            }
            return true;
        }

        private static bool NameBindingMatches(string name, XNamespace ns, XElement target, bool attribute)
        {
            int colon = name.IndexOf(':');
            return colon >= 0 ? target.GetNamespaceOfPrefix(name[..colon]) == ns :
                (attribute ? XNamespace.None : target.GetDefaultNamespace()) == ns;
        }

        private static string? WhitespaceMode(XElement element) => element.AncestorsAndSelf()
            .Select(parent => (string?)parent.Attribute(XNamespace.Xml + "space")).FirstOrDefault(value => value is not null);
    }

    private static string ReadMarkupName(string text, int start)
    {
        if (start < 0 || start >= text.Length) return string.Empty;
        int end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('=' or '/' or '>')) end++;
        return text[start..end];
    }

    private sealed class XmlSource
    {
        public XmlSource(string text)
        {
            Text = text;
            Document = XDocument.Parse(text, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            LineOffsets = GetSourceLineOffsets(text);
            int newline = text.IndexOf('\n');
            NewLine = newline >= 0 ? newline > 0 && text[newline - 1] == '\r' ? "\r\n" : "\n"
                : text.Contains('\r') ? "\r" : Environment.NewLine;
        }

        public string Text { get; }
        public XDocument Document { get; }
        public IReadOnlyList<int> LineOffsets { get; }
        public string NewLine { get; }
        public Dictionary<XNode, MarkupSpan> Spans { get; } = new();

        public bool Map()
        {
            if (Document.Root is null || Document.DocumentType is not null) return false;
            int cursor = 0;
            if (Document.Declaration is not null)
            {
                int end = Text.IndexOf("?>", StringComparison.Ordinal);
                if (end < 0) return false;
                cursor = end + 2;
            }
            int contentStart = cursor;
            foreach (XNode node in Document.Nodes()) if (!MapNode(node, ref cursor)) return false;
            if (cursor != Text.Length) return false;
            Spans[Document] = new MarkupSpan(0, contentStart, cursor, cursor, false);
            return true;
        }

        private bool MapNode(XNode node, ref int cursor)
        {
            int start = cursor;
            if (node is XElement element)
            {
                if (cursor >= Text.Length || Text[cursor] != '<') return false;
                int openEnd = TagEnd(cursor + 1);
                if (openEnd < 0) return false;
                bool empty = Text[openEnd - 2] == '/';
                cursor = openEnd;
                if (!empty)
                {
                    foreach (XNode child in element.Nodes()) if (!MapNode(child, ref cursor)) return false;
                    if (!Text.AsSpan(cursor).StartsWith("</", StringComparison.Ordinal)) return false;
                }
                int closeStart = cursor;
                if (!empty) cursor = Text.IndexOf('>', cursor) + 1;
                if (cursor <= 0) return false;
                Spans[node] = new MarkupSpan(start, openEnd, closeStart, cursor, empty);
                return true;
            }

            string? terminator = node switch { XCData => "]]>", XComment => "-->", XProcessingInstruction => "?>", _ => null };
            if (terminator is not null)
            {
                int end = Text.IndexOf(terminator, cursor, StringComparison.Ordinal);
                if (end < 0) return false;
                cursor = end + terminator.Length;
            }
            else if (node is XText)
            {
                int end = Text.IndexOf('<', cursor);
                cursor = end < 0 ? Text.Length : end;
            }
            else return false;
            if (cursor <= start) return false;
            Spans[node] = new MarkupSpan(start, start, cursor, cursor, false);
            return true;
        }

        private int TagEnd(int start)
        {
            char quote = '\0';
            for (int index = start; index < Text.Length; index++)
            {
                char character = Text[index];
                if (quote != '\0') { if (character == quote) quote = '\0'; }
                else if (character is '\'' or '"') quote = character;
                else if (character == '>') return index + 1;
            }
            return -1;
        }

        public string Raw(XNode node) => Text[Spans[node].Start..Spans[node].End];
        public string Inner(XContainer node) => Text[Spans[node].OpenEnd..Spans[node].CloseStart];
        public string GapBefore(XContainer parent, XNode[] nodes, int index) =>
            Text[(index == 0 ? Spans[parent].OpenEnd : Spans[nodes[index - 1]].End)..Spans[nodes[index]].Start];
        public string GapAfter(XContainer parent, XNode[] nodes) =>
            Text[(nodes.Length == 0 ? Spans[parent].OpenEnd : Spans[nodes[^1]].End)..Spans[parent].CloseStart];
    }

    private readonly record struct MarkupSpan(int Start, int OpenEnd, int CloseStart, int End, bool Empty);
}
