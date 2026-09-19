using System.Text;
using System.Xml.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Xaml.Serialization;

/// <summary>
/// Serializes a mutable AST document back to formatted XAML text.
/// </summary>
public sealed class XamlSerializationService : IXamlSerializationService
{
    /// <inheritdoc />
    public string Serialize(IXamlDocumentModel document, SerializationOptions? options = null)
    {
        if (document is not MutableAstDocument mutableDoc || mutableDoc.Root is null)
        {
            return string.Empty;
        }

        options ??= new SerializationOptions();
        StringBuilder sb = new(4096);
        SerializeNode(mutableDoc.Root, mutableDoc.NamespaceAliases, sb, 0, options, isRoot: true);
        SerializeTrivia(mutableDoc.TrailingTrivia, sb, 0, options);
        return sb.ToString();
    }

    /// <inheritdoc />
    public IReadOnlyList<TextEdit> ComputeMinimalEdits(
        IXamlDocumentModel document,
        string currentText,
        IReadOnlyList<AstChange> changes)
    {
        // For now, serialize the full document and compute a single replacement edit.
        // This will be refined to produce truly minimal edits based on change records.
        string newText = Serialize(document);

        if (currentText == newText)
        {
            return Array.Empty<TextEdit>();
        }

        // Find common prefix and suffix to minimize the edit
        int commonPrefix = 0;
        int maxPrefix = Math.Min(currentText.Length, newText.Length);
        while (commonPrefix < maxPrefix && currentText[commonPrefix] == newText[commonPrefix])
        {
            commonPrefix++;
        }

        int commonSuffix = 0;
        int maxSuffix = Math.Min(currentText.Length - commonPrefix, newText.Length - commonPrefix);
        while (commonSuffix < maxSuffix &&
               currentText[currentText.Length - 1 - commonSuffix] == newText[newText.Length - 1 - commonSuffix])
        {
            commonSuffix++;
        }

        int oldLength = currentText.Length - commonPrefix - commonSuffix;
        string replacement = newText.Substring(commonPrefix, newText.Length - commonPrefix - commonSuffix);

        return new[]
        {
            new TextEdit
            {
                Offset = commonPrefix,
                Length = oldLength,
                NewText = replacement
            }
        };
    }

    private void SerializeNode(
        MutableAstNode node,
        Dictionary<string, string> namespaces,
        StringBuilder sb,
        int depth,
        SerializationOptions options,
        bool isRoot = false,
        bool inline = false,
        bool preserveWhitespace = false)
    {
        SerializeTrivia(node.LeadingTrivia, sb, depth, options, inline);
        switch (node)
        {
            case MutableAstObjectNode objectNode:
                SerializeObjectNode(objectNode, namespaces, sb, depth, options, isRoot, inline, preserveWhitespace);
                break;
            case MutableAstTextNode textNode:
                sb.Append(EscapeXmlText(textNode.Text));
                break;
        }
    }

    private void SerializeObjectNode(
        MutableAstObjectNode node,
        Dictionary<string, string> namespaces,
        StringBuilder sb,
        int depth,
        SerializationOptions options,
        bool isRoot,
        bool inline,
        bool preserveWhitespace)
    {
        Dictionary<string, string> inherited = namespaces;
        namespaces = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        foreach (var alias in node.NamespaceAliases) namespaces[alias.Key] = alias.Value;
        if (node.Properties.FirstOrDefault(property => property.PropertyName == "xml:space"
                || property.XmlNamespace == XNamespace.Xml.NamespaceName && LocalName(property.PropertyName) == "space")
                ?.Value is MutableAstTextNode directive)
            preserveWhitespace = directive.Text == "preserve";
        bool inlineContent = inline || preserveWhitespace || node.Children.Any(child => child is MutableAstTextNode);
        string indent = inline ? string.Empty : GetIndent(depth, options);
        string prefix = EnsurePrefix(node.XmlNamespace, namespaces);
        string tagName = string.IsNullOrEmpty(prefix) ? node.TypeName : $"{prefix}:{node.TypeName}";
        foreach (MutableAstPropertyNode property in node.Properties)
        {
            if (IsAttributeProperty(property) && !string.IsNullOrEmpty(property.XmlNamespace))
                EnsurePrefix(property.XmlNamespace, namespaces, requirePrefix: true);
        }

        sb.Append(indent);
        sb.Append('<');
        sb.Append(tagName);

        WriteNamespaces(namespaces, inherited, sb, depth, options, isRoot);

        // Write attribute properties
        List<MutableAstPropertyNode> attributeProps = new();
        List<MutableAstPropertyNode> elementProps = new();

        foreach (MutableAstPropertyNode prop in node.Properties)
        {
            if (IsAttributeProperty(prop))
            {
                attributeProps.Add(prop);
            }
            else
            {
                elementProps.Add(prop);
            }
        }

        // Apply attribute ordering strategy
        if (options.AttributeOrdering == AttributeOrdering.Alphabetical)
        {
            attributeProps.Sort((a, b) => string.Compare(a.PropertyName, b.PropertyName, StringComparison.Ordinal));
        }
        else if (options.AttributeOrdering == AttributeOrdering.ByCategory)
        {
            attributeProps.Sort((a, b) =>
            {
                int catA = GetAttributeCategory(a.PropertyName);
                int catB = GetAttributeCategory(b.PropertyName);
                int cmp = catA.CompareTo(catB);
                return cmp != 0 ? cmp : string.Compare(a.PropertyName, b.PropertyName, StringComparison.Ordinal);
            });
        }
        // AttributeOrdering.Preserve: no sorting

        foreach (MutableAstPropertyNode prop in attributeProps)
        {
            string value = prop.Value is MutableAstTextNode textNode ? textNode.Text : "";
            sb.AppendLine();
            sb.Append(GetIndent(depth + 1, options));
            string propertyName = prop.PropertyName;
            if (!string.IsNullOrEmpty(prop.XmlNamespace))
            {
                string attributePrefix = EnsurePrefix(prop.XmlNamespace, namespaces, requirePrefix: true);
                propertyName = attributePrefix + ":" + LocalName(propertyName);
            }
            sb.Append($"{propertyName}=\"{EscapeXmlAttribute(value)}\"");
        }

        bool hasContent = node.Children.Count > 0 || elementProps.Count > 0 || node.TrailingTrivia.Count > 0;

        if (!hasContent)
        {
            sb.Append(" />");
            if (!inline) sb.AppendLine();
        }
        else
        {
            sb.Append('>');
            if (!inlineContent) sb.AppendLine();

            // Write property element children
            foreach (MutableAstPropertyNode prop in elementProps)
            {
                SerializePropertyElement(prop, node.TypeName, node.XmlNamespace, namespaces, sb, depth + 1, options,
                    inlineContent, preserveWhitespace);
            }

            // Write child elements
            foreach (MutableAstNode child in node.Children)
            {
                SerializeNode(child, namespaces, sb, depth + 1, options,
                    inline: inlineContent, preserveWhitespace: preserveWhitespace);
            }

            SerializeTrivia(node.TrailingTrivia, sb, depth + 1, options, inlineContent);
            if (!inlineContent) sb.Append(indent);
            sb.Append($"</{tagName}>");
            if (!inline) sb.AppendLine();
        }
    }

    private void SerializePropertyElement(
        MutableAstPropertyNode prop,
        string parentTagName,
        string parentNamespace,
        Dictionary<string, string> namespaces,
        StringBuilder sb,
        int depth,
        SerializationOptions options,
        bool inline,
        bool preserveWhitespace)
    {
        SerializeTrivia(prop.LeadingTrivia, sb, depth, options, inline);
        if (prop.XmlSpace is not null) preserveWhitespace = prop.XmlSpace == "preserve";
        bool inlineContent = inline || preserveWhitespace || prop.Value is MutableAstTextNode
            || prop.Value is MutableAstObjectNode { TypeName: "__PropertyElementChildren__" } textCollection
                && textCollection.Children.Any(child => child is MutableAstTextNode);
        Dictionary<string, string> inherited = namespaces;
        namespaces = new Dictionary<string, string>(inherited, StringComparer.Ordinal);
        foreach (var alias in prop.NamespaceAliases) namespaces[alias.Key] = alias.Value;
        string propertyNamespace = string.IsNullOrEmpty(prop.XmlNamespace) ? parentNamespace : prop.XmlNamespace;
        string prefix = EnsurePrefix(propertyNamespace, namespaces);
        string indent = inline ? string.Empty : GetIndent(depth, options);
        string localName = LocalName(prop.PropertyName);
        string elementName = localName.Contains('.') ? localName : $"{parentTagName}.{localName}";
        if (!string.IsNullOrEmpty(prefix)) elementName = prefix + ":" + elementName;

        sb.Append(indent);
        sb.Append($"<{elementName}");
        WriteNamespaces(namespaces, inherited, sb, depth, options, false);
        if (prop.XmlSpace is not null) sb.Append($" xml:space=\"{EscapeXmlAttribute(prop.XmlSpace)}\"");
        sb.Append('>');
        if (!inlineContent) sb.AppendLine();

        if (prop.Value is MutableAstObjectNode { TypeName: "__PropertyElementChildren__" } collection)
        {
            foreach (MutableAstNode child in collection.Children)
                SerializeNode(child, namespaces, sb, depth + 1, options,
                    inline: inlineContent, preserveWhitespace: preserveWhitespace);
        }
        else if (prop.Value is not null)
        {
            SerializeNode(prop.Value, namespaces, sb, depth + 1, options,
                inline: inlineContent, preserveWhitespace: preserveWhitespace);
        }

        SerializeTrivia(prop.TrailingTrivia, sb, depth + 1, options, inlineContent);
        if (!inlineContent) sb.Append(indent);
        sb.Append($"</{elementName}>");
        if (!inline) sb.AppendLine();
    }

    private static bool IsAttributeProperty(MutableAstPropertyNode prop)
    {
        return !prop.IsPropertyElement && prop.Value is MutableAstTextNode;
    }

    private static void SerializeTrivia(IEnumerable<XmlTrivia> trivia, StringBuilder output, int depth, SerializationOptions options,
        bool inline = false)
    {
        foreach (XmlTrivia item in trivia)
        {
            XNode xml = item.Target is null ? new XComment(item.Content) : new XProcessingInstruction(item.Target, item.Content);
            if (!inline) output.Append(GetIndent(depth, options));
            output.Append(xml.ToString(SaveOptions.DisableFormatting));
            if (!inline) output.AppendLine();
        }
    }

    private static string EnsurePrefix(string xmlNamespace, Dictionary<string, string> namespaces, bool requirePrefix = false)
    {
        if (xmlNamespace == XNamespace.Xml.NamespaceName) return "xml";
        if (string.IsNullOrEmpty(xmlNamespace))
        {
            if (namespaces.ContainsKey(string.Empty)) namespaces[string.Empty] = string.Empty;
            return string.Empty;
        }
        foreach (KeyValuePair<string, string> kvp in namespaces)
        {
            if (kvp.Value == xmlNamespace && (!requirePrefix || !string.IsNullOrEmpty(kvp.Key)))
            {
                return kvp.Key;
            }
        }
        if (!requirePrefix && !namespaces.ContainsKey(string.Empty))
        {
            namespaces[string.Empty] = xmlNamespace;
            return string.Empty;
        }
        string prefix = "ns1";
        for (int index = 2; namespaces.ContainsKey(prefix); index++) prefix = "ns" + index;
        namespaces[prefix] = xmlNamespace;
        return prefix;
    }

    private static string LocalName(string name) => name[(name.IndexOf(':') + 1)..];

    private static void WriteNamespaces(Dictionary<string, string> namespaces, Dictionary<string, string> inherited,
        StringBuilder output, int depth, SerializationOptions options, bool all)
    {
        foreach (var alias in namespaces)
        {
            if (!all && inherited.TryGetValue(alias.Key, out string? previous) && previous == alias.Value) continue;
            output.AppendLine();
            output.Append(GetIndent(depth + 1, options));
            string name = string.IsNullOrEmpty(alias.Key) ? "xmlns" : "xmlns:" + alias.Key;
            output.Append($"{name}=\"{EscapeXmlAttribute(alias.Value)}\"");
        }
    }

    private static string GetIndent(int depth, SerializationOptions options)
    {
        return string.Concat(Enumerable.Repeat(options.IndentString, depth));
    }

    private static string EscapeXmlText(string text)
    {
        return text
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\r", "&#xD;");
    }

    private static string EscapeXmlAttribute(string value)
    {
        return value
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("\t", "&#x9;")
            .Replace("\n", "&#xA;")
            .Replace("\r", "&#xD;");
    }

    /// <summary>
    /// Returns a sort category for a property name when using ByCategory ordering.
    /// Lower numbers appear first.
    /// </summary>
    private static int GetAttributeCategory(string propertyName)
    {
        // 0: Identity (x:Name, x:Key, x:Class)
        if (propertyName.StartsWith("x:", StringComparison.Ordinal) ||
            propertyName.Equals("Name", StringComparison.Ordinal) ||
            propertyName.Equals("Key", StringComparison.Ordinal))
        {
            return 0;
        }

        // 1: Layout (Width, Height, Margin, Padding, HorizontalAlignment, VerticalAlignment, Grid.*)
        if (propertyName is "Width" or "Height" or "MinWidth" or "MinHeight" or "MaxWidth" or "MaxHeight" or
            "Margin" or "Padding" or "HorizontalAlignment" or "VerticalAlignment" or
            "HorizontalContentAlignment" or "VerticalContentAlignment" ||
            propertyName.StartsWith("Grid.", StringComparison.Ordinal) ||
            propertyName.StartsWith("Canvas.", StringComparison.Ordinal) ||
            propertyName.StartsWith("DockPanel.", StringComparison.Ordinal))
        {
            return 1;
        }

        // 2: Appearance (Background, Foreground, FontSize, Opacity, etc.)
        if (propertyName is "Background" or "Foreground" or "BorderBrush" or "BorderThickness" or
            "FontSize" or "FontWeight" or "FontFamily" or "FontStyle" or
            "Opacity" or "IsVisible" or "CornerRadius")
        {
            return 2;
        }

        // 3: Common (Content, Text, Header, Command, etc.)
        if (propertyName is "Content" or "Text" or "Header" or "Title" or
            "Command" or "CommandParameter" or "IsEnabled" or "IsChecked")
        {
            return 3;
        }

        // 4: Events and other
        return 4;
    }
}
