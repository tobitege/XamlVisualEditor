using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.Core;

/// <summary>Assigns document-unique automation IDs only to newly inserted controls.</summary>
internal static class AutomationIdGenerator
{
    private const string AvaloniaNamespace = "https://github.com/avaloniaui";
    private const string PropertyName = "AutomationProperties.AutomationId";

    public static void Assign(MutableAstObjectNode parent, MutableAstObjectNode inserted, ITypeMetadataService metadata)
    {
        MutableAstNode root = parent;
        while (root.Parent is { } ancestor) root = ancestor;
        HashSet<string> used = Objects(root).Select(FindId).OfType<MutableAstPropertyNode>()
            .Select(property => (property.Value as MutableAstTextNode)?.Text)
            .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (MutableAstObjectNode node in Objects(inserted))
        {
            if (metadata.GetType(node.XmlNamespace, node.TypeName)?.IsControl != true) continue;
            MutableAstPropertyNode? property = FindId(node);
            string? current = (property?.Value as MutableAstTextNode)?.Text;
            if (property is not null && property.Value is not MutableAstTextNode) continue;
            if (!string.IsNullOrWhiteSpace(current)
                && (current.TrimStart().StartsWith('{') || used.Add(current))) continue;

            string? name = node.Properties.FirstOrDefault(value => LocalName(value.PropertyName) == "Name")
                ?.Value is MutableAstTextNode named ? named.Text : null;
            bool hasName = !string.IsNullOrWhiteSpace(name) && !name.TrimStart().StartsWith('{');
            string stem = hasName ? name! : LocalName(node.TypeName);
            int suffix = 1;
            string candidate = hasName ? stem : stem + "_1";
            while (!used.Add(candidate)) candidate = stem + "_" + (++suffix).ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (property?.Value is MutableAstTextNode text)
            {
                text.Text = candidate;
                continue;
            }
            string qualifiedName = GetPropertyName(parent, node);
            node.Properties.Add(new MutableAstPropertyNode
            {
                PropertyName = qualifiedName,
                XmlNamespace = qualifiedName.Contains(':') ? AvaloniaNamespace : string.Empty,
                Value = new MutableAstTextNode { Text = candidate }
            });
        }
    }

    private static string GetPropertyName(MutableAstObjectNode parent, MutableAstObjectNode node)
    {
        Dictionary<string, string> aliases = new(StringComparer.Ordinal);
        // The inserted subtree may not be attached yet, so both chains contribute their aliases.
        AddScopeAliases(parent, aliases);
        AddScopeAliases(node, aliases);
        foreach (var alias in aliases)
            if (alias.Value == AvaloniaNamespace)
                return string.IsNullOrEmpty(alias.Key) ? PropertyName : alias.Key + ":" + PropertyName;

        string prefix = "automation";
        for (int index = 1; aliases.ContainsKey(prefix); index++) prefix = "automation" + index;
        node.NamespaceAliases[prefix] = AvaloniaNamespace;
        return prefix + ":" + PropertyName;
    }

    /// <summary>Applies the namespace aliases from the root down to <paramref name="innermost"/>; inner declarations win.</summary>
    private static void AddScopeAliases(MutableAstNode innermost, Dictionary<string, string> aliases)
    {
        Stack<MutableAstNode> scope = new();
        for (MutableAstNode? current = innermost; current is not null; current = current.Parent) scope.Push(current);
        foreach (MutableAstNode current in scope)
        {
            if (current is MutableAstObjectNode element)
                foreach (var alias in element.NamespaceAliases) aliases[alias.Key] = alias.Value;
            else if (current is MutableAstPropertyNode property)
                foreach (var alias in property.NamespaceAliases) aliases[alias.Key] = alias.Value;
        }
    }

    private static MutableAstPropertyNode? FindId(MutableAstObjectNode node) =>
        node.Properties.FirstOrDefault(property => LocalName(property.PropertyName) == PropertyName);

    private static string LocalName(string name) => name[(name.IndexOf(':') + 1)..];

    private static IEnumerable<MutableAstObjectNode> Objects(MutableAstNode root)
    {
        Stack<MutableAstNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstObjectNode element)
            {
                yield return element;
                foreach (MutableAstNode child in element.Children.Reverse()) pending.Push(child);
                foreach (MutableAstPropertyNode property in element.Properties) pending.Push(property);
            }
            else if (node is MutableAstPropertyNode { Value: { } value }) pending.Push(value);
        }
    }
}
