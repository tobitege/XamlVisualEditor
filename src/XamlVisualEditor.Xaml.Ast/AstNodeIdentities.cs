using System.Collections.Immutable;

namespace XamlVisualEditor.Xaml.Ast;

/// <summary>Preserves designer node identities when a document state is restored from text.</summary>
public static class AstNodeIdentities
{
    /// <summary>Captures identities using structural paths and shares unchanged entries with the previous state.</summary>
    public static ImmutableDictionary<string, Guid> Capture(
        MutableAstDocument document, ImmutableDictionary<string, Guid>? previous = null)
    {
        ImmutableDictionary<string, Guid>.Builder identities =
            (previous ?? ImmutableDictionary<string, Guid>.Empty).ToBuilder();
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach ((string path, MutableAstNode node) in Enumerate(document))
        {
            visited.Add(path);
            identities[path] = node.Id;
        }
        foreach (string path in identities.Keys.ToArray())
        {
            if (!visited.Contains(path)) identities.Remove(path);
        }
        return identities.ToImmutable();
    }

    /// <summary>Restores matching identities and rebuilds the document's node index.</summary>
    public static void Restore(MutableAstDocument document, IReadOnlyDictionary<string, Guid> identities)
    {
        foreach ((string path, MutableAstNode node) in Enumerate(document))
        {
            if (identities.TryGetValue(path, out Guid id)) node.Id = id;
        }
        document.NodeMap.Clear();
        if (document.Root is not null) document.NodeMap.RegisterTree(document.Root);
    }

    private static IEnumerable<(string Path, MutableAstNode Node)> Enumerate(MutableAstDocument document)
    {
        if (document.Root is null) yield break;
        Stack<(string Path, MutableAstNode Node)> pending = new();
        pending.Push(("root", document.Root));
        while (pending.TryPop(out var current))
        {
            string identity = current.Path + "|" + current.Node.NodeKindName;
            if (current.Node is MutableAstObjectNode element)
            {
                identity += "|" + element.XmlNamespace + "|" + element.TypeName;
                for (int index = element.Children.Count - 1; index >= 0; index--)
                {
                    pending.Push((current.Path + "/c:" + index, element.Children[index]));
                }
                foreach (MutableAstPropertyNode property in element.Properties)
                {
                    pending.Push((current.Path + "/p:" + property.PropertyName, property));
                }
            }
            else if (current.Node is MutableAstPropertyNode property && property.Value is not null)
            {
                pending.Push((current.Path + "/value", property.Value));
            }
            yield return (identity, current.Node);
        }
    }
}
