using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Xaml.Language;

/// <summary>Resolves name declarations in their name scopes and renames them with the references to them.</summary>
internal static class XamlNames
{
    internal const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>
    /// Checks the rename of the declaration of <paramref name="node"/> and returns the action that renames it with
    /// the selectors and bindings that refer to it, or removes it when <paramref name="newName"/> is null; null when
    /// the name does not change. Throws without changing the document when the new name is taken in the scope or a
    /// reference cannot follow.
    /// </summary>
    internal static Action? PlanRename(MutableAstDocument document, MutableAstObjectNode node, string? newName,
        ITypeMetadataService? metadata)
    {
        MutableAstPropertyNode? declaration = FindNameProperty(node, metadata);
        string? oldName = (declaration?.Value as MutableAstTextNode)?.Text;
        if (oldName == newName) return null;
        MutableAstObjectNode scope = ScopeRoot(node);
        if (newName is not null && ScopeObjects(scope).Any(other => !ReferenceEquals(other, node)
                && (FindNameProperty(other, metadata)?.Value as MutableAstTextNode)?.Text == newName))
            throw new InvalidOperationException(string.Format(UiStrings.DesignerNameInUse, newName));

        List<(MutableAstTextNode Value, string Text)> references = new();
        if (!string.IsNullOrEmpty(oldName))
        {
            Dictionary<string, string> names = new(StringComparer.Ordinal) { [oldName] = newName ?? oldName };
            MutableAstObjectNode[] objects = Objects(document.Root!).ToArray();
            bool selectorNameIsAmbiguous = objects.Any(other => !ReferenceEquals(other, node)
                && (FindNameProperty(other, metadata)?.Value as MutableAstTextNode)?.Text is { } otherName
                && (otherName == oldName || otherName == newName));
            foreach (MutableAstObjectNode owner in objects)
            {
                bool bindingRefersToNode = ReferenceEquals(ResolveName(owner, oldName, metadata), node);
                foreach (MutableAstPropertyNode property in owner.Properties)
                {
                    if (property.Value is not MutableAstTextNode text) continue;
                    if (owner.TypeName == "Style" && property.PropertyName == "Selector")
                    {
                        var selector = XamlNameReferences.RewriteSelector(text.Text, names);
                        if (!selector.Found) continue;
                        if (selectorNameIsAmbiguous)
                            throw new InvalidOperationException(string.Format(UiStrings.DesignerNameSelectorAmbiguous, text.Text));
                        if (newName is null)
                            throw new InvalidOperationException(string.Format(UiStrings.DesignerNameUsedBySelector, oldName));
                        references.Add((text, selector.Text));
                        continue;
                    }
                    if (!bindingRefersToNode) continue;
                    var rewritten = XamlNameReferences.Rewrite(owner, property, names,
                        Namespaces(property, document.NamespaceAliases));
                    if (!rewritten.Found) continue;
                    if (newName is null) throw new InvalidOperationException(string.Format(UiStrings.DesignerNameUsedByBinding, oldName));
                    references.Add((text, rewritten.Text));
                }
            }
        }

        return () =>
        {
            if (newName is null)
            {
                if (declaration is not null) node.Properties.Remove(declaration);
            }
            else if (declaration?.Value is MutableAstTextNode nameValue) nameValue.Text = newName;
            else node.SetPropertyValue("Name", newName);
            foreach (var reference in references) reference.Value.Text = reference.Text;
        };
    }

    internal static MutableAstPropertyNode? FindNameProperty(MutableAstObjectNode node, ITypeMetadataService? metadata = null) =>
        node.Properties.FirstOrDefault(property => property.PropertyName == "x:Name"
            || property.XmlNamespace == XamlNamespace && property.PropertyName.EndsWith(":Name", StringComparison.Ordinal))
        ?? (metadata is null || metadata.GetType(node.XmlNamespace, node.TypeName)?.IsControl == true
            ? node.Properties.FirstOrDefault(property => property.PropertyName == "Name") : null);

    internal static MutableAstObjectNode ScopeRoot(MutableAstObjectNode node)
    {
        MutableAstObjectNode root = node;
        for (MutableAstNode? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not MutableAstObjectNode owner) continue;
            root = owner;
            if (IsTemplate(owner)) break;
        }
        return root;
    }

    private static MutableAstObjectNode? ResolveName(MutableAstObjectNode owner, string name, ITypeMetadataService? metadata)
    {
        MutableAstObjectNode scope = ScopeRoot(owner);
        while (true)
        {
            MutableAstObjectNode? declaration = ScopeObjects(scope).FirstOrDefault(candidate =>
                (FindNameProperty(candidate, metadata)?.Value as MutableAstTextNode)?.Text == name);
            if (declaration is not null || scope.Parent is null) return declaration;
            scope = ScopeRoot(scope);
        }
    }

    internal static IEnumerable<MutableAstObjectNode> ScopeObjects(MutableAstObjectNode root) =>
        Objects(root, true).Where(node => !ReferenceEquals(node, root) || !IsTemplate(root));

    internal static IEnumerable<MutableAstObjectNode> Objects(MutableAstObjectNode root, bool stopAtTemplates = false)
    {
        Stack<MutableAstNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstPropertyNode { Value: { } value }) { pending.Push(value); continue; }
            if (node is not MutableAstObjectNode element) continue;
            yield return element;
            if (stopAtTemplates && !ReferenceEquals(root, element) && IsTemplate(element)) continue;
            for (int index = element.Children.Count - 1; index >= 0; index--) pending.Push(element.Children[index]);
            for (int index = element.Properties.Count - 1; index >= 0; index--) pending.Push(element.Properties[index]);
        }
    }

    internal static Dictionary<string, string> Namespaces(MutableAstNode node, IReadOnlyDictionary<string, string> documentNamespaces)
    {
        Dictionary<string, string> result = new(documentNamespaces);
        Stack<MutableAstNode> parents = new();
        for (MutableAstNode? current = node; current is not null; current = current.Parent) parents.Push(current);
        foreach (MutableAstNode current in parents)
        {
            IReadOnlyDictionary<string, string>? local = current switch
            {
                MutableAstObjectNode element => element.NamespaceAliases,
                MutableAstPropertyNode property => property.NamespaceAliases,
                _ => null
            };
            if (local is not null) foreach (var alias in local) result[alias.Key] = alias.Value;
        }
        return result;
    }

    /// <summary>Returns the x:Class of the root element, which names the code-behind class.</summary>
    internal static string? FindClassName(MutableAstObjectNode root, MutableAstDocument document)
    {
        IReadOnlyDictionary<string, string> aliases = Namespaces(root, document.NamespaceAliases);
        foreach (MutableAstPropertyNode property in root.Properties)
        {
            int separator = property.PropertyName.IndexOf(':');
            string localName = separator < 0 ? property.PropertyName : property.PropertyName[(separator + 1)..];
            string? xmlNamespace = property.XmlNamespace;
            if (separator >= 0 && aliases.TryGetValue(property.PropertyName[..separator], out string? mappedNamespace))
            {
                xmlNamespace = mappedNamespace;
            }

            if (localName == "Class" && xmlNamespace == XamlNamespace)
            {
                return root.GetPropertyValue(property.PropertyName);
            }
        }

        return null;
    }

    private static bool IsTemplate(MutableAstObjectNode node) => node.TypeName is "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate";
}
