namespace XamlVisualEditor.Xaml.Ast;

/// <summary>Creates detached AST copies with fresh identities and without changing the source hierarchy.</summary>
public static class AstNodeCloner
{
    /// <summary>Clones an element and its complete property/content tree.</summary>
    public static MutableAstObjectNode Clone(MutableAstObjectNode source) =>
        (MutableAstObjectNode)CloneNode(source, new HashSet<Guid>());

    private static MutableAstNode CloneNode(MutableAstNode source, HashSet<Guid> ancestors)
    {
        if (!ancestors.Add(source.Id)) throw new InvalidOperationException("Cannot copy a cyclic AST.");
        try
        {
            switch (source)
            {
                case MutableAstObjectNode element:
                    MutableAstObjectNode copy = new() { TypeName = element.TypeName, XmlNamespace = element.XmlNamespace };
                    foreach (var alias in element.NamespaceAliases) copy.NamespaceAliases.Add(alias.Key, alias.Value);
                    foreach (MutableAstPropertyNode property in element.Properties)
                        copy.Properties.Add((MutableAstPropertyNode)CloneNode(property, ancestors));
                    foreach (MutableAstNode child in element.Children) copy.Children.Add(CloneNode(child, ancestors));
                    return CopyTrivia(source, copy);
                case MutableAstPropertyNode property:
                    MutableAstPropertyNode propertyCopy = new()
                    {
                        PropertyName = property.PropertyName, XmlNamespace = property.XmlNamespace,
                        IsPropertyElement = property.IsPropertyElement, XmlSpace = property.XmlSpace
                    };
                    foreach (var alias in property.NamespaceAliases) propertyCopy.NamespaceAliases.Add(alias.Key, alias.Value);
                    if (property.Value is not null) propertyCopy.Value = CloneNode(property.Value, ancestors);
                    return CopyTrivia(source, propertyCopy);
                case MutableAstTextNode text:
                    return CopyTrivia(source, new MutableAstTextNode { Text = text.Text });
                case MutableAstDirectiveNode directive:
                    return CopyTrivia(source, new MutableAstDirectiveNode
                    {
                        DirectiveNamespace = directive.DirectiveNamespace,
                        DirectiveName = directive.DirectiveName,
                        Value = directive.Value
                    });
                default:
                    throw new InvalidOperationException("Unsupported AST node: " + source.NodeKindName);
            }
        }
        finally
        {
            ancestors.Remove(source.Id);
        }
    }

    private static T CopyTrivia<T>(MutableAstNode source, T copy) where T : MutableAstNode
    {
        copy.LeadingTrivia.AddRange(source.LeadingTrivia);
        copy.TrailingTrivia.AddRange(source.TrailingTrivia);
        return copy;
    }
}
