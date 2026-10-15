namespace XamlVisualEditor.Xaml.Ast;

/// <summary>Preserves inherited XML semantics when an element is copied or reparented.</summary>
public sealed class AstSourceScope
{
    private readonly Dictionary<string, string> _namespaces;
    private readonly bool _preserveWhitespace;

    private AstSourceScope(Dictionary<string, string> namespaces, bool preserveWhitespace)
    {
        _namespaces = namespaces;
        _preserveWhitespace = preserveWhitespace;
    }

    public static AstSourceScope Capture(MutableAstNode node,
        IReadOnlyDictionary<string, string>? documentNamespaces = null)
    {
        Dictionary<string, string> namespaces = new(StringComparer.Ordinal);
        if (documentNamespaces is not null)
            foreach ((string prefix, string xmlNamespace) in documentNamespaces) namespaces[prefix] = xmlNamespace;

        Stack<MutableAstNode> ancestors = new();
        for (MutableAstNode? current = node; current is not null; current = current.Parent) ancestors.Push(current);
        while (ancestors.TryPop(out MutableAstNode? current))
        {
            IReadOnlyDictionary<string, string>? declarations = current switch
            {
                MutableAstObjectNode element => element.NamespaceAliases,
                MutableAstPropertyNode property => property.NamespaceAliases,
                _ => null
            };
            if (declarations is not null)
                foreach ((string prefix, string xmlNamespace) in declarations) namespaces[prefix] = xmlNamespace;
        }
        return new AstSourceScope(namespaces, PreservesWhitespace(node));
    }

    public void ApplyTo(MutableAstObjectNode node)
    {
        AstSourceScope current = Capture(node);
        foreach ((string prefix, string xmlNamespace) in _namespaces)
        {
            if (!current._namespaces.TryGetValue(prefix, out string? inherited) || inherited != xmlNamespace)
                node.NamespaceAliases[prefix] = xmlNamespace;
        }
        if (current._preserveWhitespace != _preserveWhitespace)
            node.SetPropertyValue("xml:space", _preserveWhitespace ? "preserve" : "default");
    }

    private static bool PreservesWhitespace(MutableAstNode node)
    {
        for (MutableAstNode? current = node; current is not null; current = current.Parent)
        {
            if (current is MutableAstPropertyNode { XmlSpace: { } space }) return space == "preserve";
            if (current is not MutableAstObjectNode element) continue;
            MutableAstPropertyNode? directive = element.Properties.FirstOrDefault(property =>
                property.PropertyName == "xml:space" ||
                property.XmlNamespace == "http://www.w3.org/XML/1998/namespace" &&
                (property.PropertyName == "space" || property.PropertyName.EndsWith(":space", StringComparison.Ordinal)));
            if (directive?.Value is MutableAstTextNode text) return text.Text == "preserve";
        }
        return false;
    }
}
