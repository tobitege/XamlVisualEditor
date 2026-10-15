using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Isolates evidenced failures in the preview clone, never in the source document.</summary>
internal sealed class RuntimeRenderRecovery
{
    private static readonly HashSet<string> LayoutProperties = new(StringComparer.Ordinal)
    {
        "Width", "Height", "MinWidth", "MinHeight", "MaxWidth", "MaxHeight", "Margin",
        "HorizontalAlignment", "VerticalAlignment", "Canvas.Left", "Canvas.Top", "Canvas.Right", "Canvas.Bottom",
        "Grid.Row", "Grid.Column", "Grid.RowSpan", "Grid.ColumnSpan", "DockPanel.Dock", "Panel.ZIndex", "ZIndex",
        "Name", "x:Name", "x:Key"
    };
    private readonly MutableAstDocument _preview;
    private readonly IReadOnlyDictionary<Guid, string> _sourceTypes;
    private readonly string _identityNamespace;
    private readonly HashSet<Guid> _isolated = new();

    public RuntimeRenderRecovery(MutableAstDocument preview, IReadOnlyDictionary<Guid, string> sourceTypes, string identityNamespace)
    {
        _preview = preview;
        _sourceTypes = sourceTypes;
        _identityNamespace = identityNamespace;
    }

    public List<DesignRenderDiagnostic> Diagnostics { get; } = new();

    public bool TryIsolate(Exception exception, ISet<Guid> constructedNodes, string xaml)
    {
        MutableAstObjectNode[] nodes = Objects(_preview.Root!).ToArray();
        MutableAstObjectNode? failed = FindCompilerLocation(exception, xaml, nodes);
        if (failed is null)
        {
            for (Exception? current = exception; current is not null && failed is null; current = current.InnerException)
            {
                foreach (StackFrame frame in new StackTrace(current, false).GetFrames())
                {
                    if (frame.GetMethod() is not ConstructorInfo constructor || constructor.DeclaringType?.FullName is not { } typeName) continue;
                    failed = nodes.FirstOrDefault(node => Identity(node) is { } id && !_isolated.Contains(id)
                        && !constructedNodes.Contains(id) && _sourceTypes.TryGetValue(id, out string? sourceType) && sourceType == typeName);
                    if (failed is not null) break;
                }
            }
        }
        if (failed is null || Identity(failed) is not { } failedId || !_isolated.Add(failedId)) return false;

        string message = exception.GetBaseException().Message;
        string type = _sourceTypes[failedId];
        MutableAstPropertyNode[] retained = failed.Properties.Where(property =>
            property.XmlNamespace == _identityNamespace || LayoutProperties.Contains(property.PropertyName)).ToArray();
        failed.Properties.Clear();
        foreach (MutableAstPropertyNode property in retained) failed.Properties.Add(property);
        failed.Children.Clear();
        failed.TypeName = nameof(DesignErrorControl);
        failed.XmlNamespace = _identityNamespace;
        failed.SetPropertyValue(nameof(DesignErrorControl.OriginalType), type);
        failed.SetPropertyValue(nameof(DesignErrorControl.Error), message);
        Diagnostics.Add(new(failedId, type, message));
        return true;
    }

    private MutableAstObjectNode? FindCompilerLocation(Exception exception, string xaml, IReadOnlyList<MutableAstObjectNode> nodes)
    {
        int line = 0;
        for (Exception? current = exception; current is not null && line == 0; current = current.InnerException)
        {
            if (current is IXmlLineInfo info && info.HasLineInfo()) line = info.LineNumber;
            else if (current.GetType().GetProperty("LineNumber")?.GetValue(current) is int number) line = number;
        }
        if (line <= 0) return null;
        XDocument xml = XDocument.Parse(xaml, LoadOptions.SetLineInfo);
        XElement? element = xml.Descendants().LastOrDefault(candidate => ((IXmlLineInfo)candidate).LineNumber <= line);
        while (element is not null)
        {
            string? idText = (string?)element.Attribute(XName.Get("DesignNodeIdentity.NodeId", _identityNamespace));
            if (Guid.TryParse(idText, out Guid id))
                return _isolated.Contains(id) ? null : nodes.FirstOrDefault(node => Identity(node) == id);
            if (!element.Name.LocalName.Contains('.')) return null;
            element = element.Parent;
        }
        return null;
    }

    private Guid? Identity(MutableAstObjectNode node)
    {
        MutableAstPropertyNode? property = node.Properties.FirstOrDefault(property => property.XmlNamespace == _identityNamespace
            && property.PropertyName.EndsWith("DesignNodeIdentity.NodeId", StringComparison.Ordinal));
        return property?.Value is MutableAstTextNode text && Guid.TryParse(text.Text, out Guid id) ? id : null;
    }

    private static IEnumerable<MutableAstObjectNode> Objects(MutableAstObjectNode node)
    {
        yield return node;
        foreach (MutableAstPropertyNode property in node.Properties)
            if (property.Value is MutableAstObjectNode value)
                foreach (MutableAstObjectNode descendant in Objects(value)) yield return descendant;
        foreach (MutableAstObjectNode child in node.Children.OfType<MutableAstObjectNode>())
            foreach (MutableAstObjectNode descendant in Objects(child)) yield return descendant;
    }
}

public sealed record DesignRenderDiagnostic(Guid NodeId, string TypeName, string Message);
