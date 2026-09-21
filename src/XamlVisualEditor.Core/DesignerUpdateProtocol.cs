using System.Xml.Linq;

namespace XamlVisualEditor.Core;

public sealed record DesignerUpdateRequest(Guid RequestId, Guid BaseRenderId, Guid RenderId, string Xaml);
public sealed record DesignerUpdateResult(Guid RequestId, bool Applied, string? Error = null);
public sealed record DesignerControlUpdate(Guid NodeId, string Xaml, string PreviousXaml,
    IReadOnlyList<string> ClearedProperties, IReadOnlyList<string> PreviouslyUnsetProperties);

/// <summary>Beschreibt Änderungen an bestehenden Eigenschaften ohne Austausch des Controlbaums.</summary>
public static class DesignerUpdateProtocol
{
    public const string ResultPrefix = "XVE-UPDATE-RESULT ";
    public const string IdentityNamespace = "clr-namespace:XamlVisualEditor.Designer.Rendering;assembly=XamlVisualEditor.Designer.Rendering";
    private static readonly XName NodeIdName = XName.Get("DesignNodeIdentity.NodeId", IdentityNamespace);
    private static readonly XName RenderIdName = XName.Get("DesignNodeIdentity.RenderId", IdentityNamespace);

    public static bool TryCreateUpdates(string previous, string current, out IReadOnlyList<DesignerControlUpdate> updates)
    {
        updates = Array.Empty<DesignerControlUpdate>();
        XDocument oldDocument;
        XDocument newDocument;
        try
        {
            oldDocument = XDocument.Parse(previous);
            newDocument = XDocument.Parse(current);
        }
        catch (System.Xml.XmlException) { return false; }
        if (oldDocument.Root is null || newDocument.Root is null) return false;
        List<DesignerControlUpdate> changes = new();
        if (!Compare(oldDocument.Root, newDocument.Root, changes)) return false;
        updates = changes;
        return true;
    }

    private static bool Compare(XElement oldElement, XElement newElement, List<DesignerControlUpdate> updates)
    {
        if (oldElement.Name != newElement.Name) return false;
        Dictionary<XName, string> oldAttributes = oldElement.Attributes().Where(attribute => attribute.Name != RenderIdName)
            .ToDictionary(attribute => attribute.Name, attribute => attribute.Value);
        Dictionary<XName, string> newAttributes = newElement.Attributes().Where(attribute => attribute.Name != RenderIdName)
            .ToDictionary(attribute => attribute.Name, attribute => attribute.Value);
        XName[] changed = oldAttributes.Keys.Union(newAttributes.Keys)
            .Where(name => oldAttributes.GetValueOrDefault(name) != newAttributes.GetValueOrDefault(name)).ToArray();
        if (changed.Length > 0)
        {
            if (!Guid.TryParse(newElement.Attribute(NodeIdName)?.Value, out Guid nodeId)) return false;
            foreach (XName name in changed)
            {
                if (name.NamespaceName == IdentityNamespace || name.LocalName is "Name" or "Class" or "DataType"
                    || name.Namespace == XNamespace.Xmlns || name.Namespace == XNamespace.Xml || name.LocalName == "xmlns"
                    || name.NamespaceName == "http://schemas.microsoft.com/winfx/2006/xaml"
                    || !CanPopulateValue(oldAttributes.GetValueOrDefault(name))
                    || !CanPopulateValue(newAttributes.GetValueOrDefault(name))) return false;
            }
            updates.Add(new(nodeId, Fragment(newElement, changed), Fragment(oldElement, changed),
                changed.Where(name => !newAttributes.ContainsKey(name)).Select(name => name.ToString()).ToArray(),
                changed.Where(name => !oldAttributes.ContainsKey(name)).Select(name => name.ToString()).ToArray()));
        }
        XNode[] oldChildren = oldElement.Nodes().Where(node => node is not XComment).ToArray();
        XNode[] newChildren = newElement.Nodes().Where(node => node is not XComment).ToArray();
        if (oldChildren.Length != newChildren.Length) return false;
        for (int index = 0; index < oldChildren.Length; index++)
        {
            if (oldChildren[index] is XElement oldChild && newChildren[index] is XElement newChild)
            {
                if (!Compare(oldChild, newChild, updates)) return false;
            }
            else if (!XNode.DeepEquals(oldChildren[index], newChildren[index])) return false;
        }
        return true;
    }

    private static bool CanPopulateValue(string? value)
    {
        if (value is null) return true;
        string text = value.TrimStart();
        return !text.StartsWith('{') || text.StartsWith("{}", StringComparison.Ordinal)
            || text.StartsWith("{DynamicResource ", StringComparison.Ordinal) || text == "{x:Null}";
    }

    private static string Fragment(XElement element, IReadOnlyList<XName> changed)
    {
        XElement fragment = new(element.Name);
        foreach (XElement ancestor in element.AncestorsAndSelf().Reverse())
            foreach (XAttribute attribute in ancestor.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                fragment.SetAttributeValue(attribute.Name, attribute.Value);
        foreach (XName name in changed)
            if (element.Attribute(name) is { } attribute) fragment.Add(new XAttribute(attribute));
        return fragment.ToString(SaveOptions.DisableFormatting);
    }
}
