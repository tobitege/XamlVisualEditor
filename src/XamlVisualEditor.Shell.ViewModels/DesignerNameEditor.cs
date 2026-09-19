using Microsoft.CodeAnalysis.CSharp;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Renames document-local declarations and their AXAML bindings in one undo transaction.</summary>
internal static class DesignerNameEditor
{
    public static void RenameWithCodeBehind(
        DesignerDocumentViewModel document,
        MutableAstObjectNode node,
        string? value,
        System.Collections.Generic.IReadOnlyList<XamlVisualEditor.Sync.DocumentCompanionChange> companionChanges)
    {
        using var transaction = document.SyncEngine.BeginEditTransaction("Rename control and code-behind");
        foreach (XamlVisualEditor.Sync.DocumentCompanionChange change in companionChanges)
        {
            transaction.Enlist(change);
        }

        Rename(document, node, value);
        transaction.Commit();
    }

    internal const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    internal static void Rename(DesignerDocumentViewModel document, MutableAstObjectNode node, string? value)
    {
        if (!string.IsNullOrEmpty(value) && (value.StartsWith('@') || !SyntaxFacts.IsValidIdentifier(value)))
            throw new InvalidOperationException("Enter a valid control name without spaces or C# keywords.");
        string? newName = string.IsNullOrEmpty(value) ? null : value;
        MutableAstPropertyNode? declaration = FindNameProperty(node, document.MetadataService);
        string? oldName = (declaration?.Value as MutableAstTextNode)?.Text;
        if (oldName == newName) return;
        MutableAstObjectNode scope = ScopeRoot(node);
        if (newName is not null && ScopeObjects(scope).Any(other => !ReferenceEquals(other, node)
                && (FindNameProperty(other, document.MetadataService)?.Value as MutableAstTextNode)?.Text == newName))
            throw new InvalidOperationException($"The name '{newName}' is already used in this name scope.");

        List<(MutableAstTextNode Value, string Text)> references = new();
        if (!string.IsNullOrEmpty(oldName))
        {
            Dictionary<string, string> names = new(StringComparer.Ordinal) { [oldName] = newName ?? oldName };
            MutableAstObjectNode[] objects = Objects(document.SyncEngine.CurrentDocument!.Root!).ToArray();
            bool selectorNameIsAmbiguous = objects.Any(other => !ReferenceEquals(other, node)
                && (FindNameProperty(other, document.MetadataService)?.Value as MutableAstTextNode)?.Text is { } otherName
                && (otherName == oldName || otherName == newName));
            foreach (MutableAstObjectNode owner in objects)
            {
                bool bindingRefersToNode = ReferenceEquals(ResolveName(owner, oldName, document.MetadataService), node);
                foreach (MutableAstPropertyNode property in owner.Properties)
                {
                    if (property.Value is not MutableAstTextNode text) continue;
                    if (owner.TypeName == "Style" && property.PropertyName == "Selector")
                    {
                        var selector = XamlNameReferences.RewriteSelector(text.Text, names);
                        if (!selector.Found) continue;
                        if (selectorNameIsAmbiguous)
                            throw new InvalidOperationException($"Style selector '{text.Text}' refers to a name shared across name scopes. Resolve the selector ambiguity before renaming.");
                        if (newName is null)
                            throw new InvalidOperationException($"The name '{oldName}' is used by a style selector and cannot be removed.");
                        references.Add((text, selector.Text));
                        continue;
                    }
                    if (!bindingRefersToNode) continue;
                    var rewritten = XamlNameReferences.Rewrite(owner, property, names,
                        Namespaces(property, document.SyncEngine.CurrentDocument.NamespaceAliases));
                    if (!rewritten.Found) continue;
                    if (newName is null) throw new InvalidOperationException($"The name '{oldName}' is referenced by a binding and cannot be removed.");
                    references.Add((text, rewritten.Text));
                }
            }
        }

        using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction("Rename control", SyncSource.PropertyEditor);
        if (newName is null)
        {
            if (declaration is not null) node.Properties.Remove(declaration);
        }
        else if (declaration?.Value is MutableAstTextNode nameValue) nameValue.Text = newName;
        else node.SetPropertyValue("Name", newName);
        foreach (var reference in references) reference.Value.Text = reference.Text;
        edit.Commit();
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

    private static bool IsTemplate(MutableAstObjectNode node) => node.TypeName is "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate";
}
