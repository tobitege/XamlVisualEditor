using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Performs atomic multi-selection edits and transfers detached XAML fragments.</summary>
public sealed class DesignerSelectionEditor
{
    private readonly SyncEngine _engine;
    private readonly LayoutEditService _layout;
    private readonly ITypeMetadataService? _metadata;
    private readonly XamlParsingService _parser = new();
    private readonly XamlSerializationService _serializer = new();
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>Creates an editor for one document.</summary>
    public DesignerSelectionEditor(SyncEngine engine, ITypeMetadataService? metadata = null, bool generateAutomationIds = false)
    {
        _engine = engine;
        _metadata = metadata;
        _layout = new LayoutEditService(metadata, generateAutomationIds);
    }

    /// <summary>Copies top-level selected roots in document order without detaching source nodes.</summary>
    public string Copy(IEnumerable<MutableAstObjectNode> selection)
    {
        List<string> fragments = new();
        foreach (MutableAstObjectNode source in SelectionRoots(selection))
        {
            MutableAstObjectNode copy = AstNodeCloner.Clone(source);
            AstSourceScope.Capture(source, _engine.CurrentDocument!.NamespaceAliases).ApplyTo(copy);
            MutableAstDocument fragment = new() { Root = copy };
            foreach (var alias in copy.NamespaceAliases) fragment.NamespaceAliases[alias.Key] = alias.Value;
            fragments.Add(_serializer.Serialize(fragment));
        }
        return string.Join(Environment.NewLine, fragments);
    }

    /// <summary>Deletes selected roots together, preserving selected descendants inside their parents.</summary>
    public void Delete(IEnumerable<MutableAstObjectNode> selection)
    {
        IReadOnlyList<MutableAstObjectNode> nodes = SelectionRoots(selection);
        if (nodes.Count == 0) return;
        if (nodes.Any(node => LayoutEditService.GetParent(node) is null))
            throw new InvalidOperationException(UiStrings.LayoutEditRootDelete);
        using DocumentEditTransaction edit = _engine.BeginEditTransaction("Delete selection");
        foreach (MutableAstObjectNode node in nodes)
        {
            if (!_layout.TryRemove(node, out string? error)) throw new InvalidOperationException(error);
        }
        edit.Commit();
    }

    /// <summary>Pastes one or more XAML fragments as one edit with fresh identities and nonconflicting names.</summary>
    public IReadOnlyList<MutableAstObjectNode> Paste(string text, MutableAstObjectNode? selection = null,
        MutableAstObjectNode? explicitParent = null)
    {
        MutableAstObjectNode root = _engine.CurrentDocument?.Root
            ?? throw new InvalidOperationException(UiStrings.DesignerOpenDocumentFirst);
        List<MutableAstObjectNode> nodes = ParseFragments(text);
        if (nodes.Count == 0) throw new InvalidOperationException(UiStrings.DesignerClipboardNoElements);
        MutableAstObjectNode? parent = explicitParent ?? _layout.FindInsertionParent(root, selection, nodes[0]);
        if (parent is null) throw new InvalidOperationException(UiStrings.DesignerSelectLayoutTarget);
        if (nodes.Count > 1 && _layout.GetKind(parent) is LayoutContainerKind.Content or LayoutContainerKind.Child)
            throw new InvalidOperationException(UiStrings.DesignerPasteMultipleNeedsPanel);
        foreach (MutableAstObjectNode node in nodes)
        {
            if (!_layout.CanInsert(parent, node, out string? error)) throw new InvalidOperationException(error);
        }
        MakeNamesUnique(nodes, root);
        using DocumentEditTransaction edit = _engine.BeginEditTransaction("Paste selection");
        foreach (MutableAstObjectNode node in nodes)
        {
            AstSourceScope sourceScope = AstSourceScope.Capture(node);
            foreach (MutableAstPropertyNode directive in node.Properties.Where(property => property.PropertyName == "x:Class"
                || property.XmlNamespace == XamlNamespace && LocalName(property.PropertyName) == "Class").ToArray())
                node.Properties.Remove(directive);
            if (!_layout.TryInsert(parent, node, out string? error)) throw new InvalidOperationException(error);
            sourceScope.ApplyTo(node);
        }
        edit.Commit();
        return nodes;
    }

    /// <summary>Duplicates the selection in its current layout with one undo entry.</summary>
    public IReadOnlyList<MutableAstObjectNode> Duplicate(IEnumerable<MutableAstObjectNode> selection)
    {
        IReadOnlyList<MutableAstObjectNode> roots = SelectionRoots(selection);
        if (roots.Count == 0) return Array.Empty<MutableAstObjectNode>();
        MutableAstObjectNode? parent = LayoutEditService.GetParent(roots[0]);
        if (parent is null || roots.Any(node => !ReferenceEquals(LayoutEditService.GetParent(node), parent)))
            throw new InvalidOperationException(UiStrings.DesignerDuplicateSiblingsRequired);
        using DocumentEditTransaction edit = _engine.BeginEditTransaction("Duplicate selection");
        IReadOnlyList<MutableAstObjectNode> copies = Paste(Copy(roots), explicitParent: parent);
        if (_layout.GetKind(parent) == LayoutContainerKind.Canvas)
        {
            foreach (MutableAstObjectNode copy in copies)
            {
                Offset(copy, "Canvas.Left");
                Offset(copy, "Canvas.Top");
            }
        }
        edit.Commit();
        return copies;
    }

    /// <summary>Returns selected roots in source order, omitting descendants of another selected element.</summary>
    public IReadOnlyList<MutableAstObjectNode> SelectionRoots(IEnumerable<MutableAstObjectNode> selection)
    {
        HashSet<Guid> selected = selection.Select(node => node.Id).ToHashSet();
        List<MutableAstObjectNode> result = new();
        if (_engine.CurrentDocument?.Root is not { } root) return result;
        foreach (MutableAstObjectNode node in Objects(root))
        {
            if (!selected.Contains(node.Id)) continue;
            bool nested = false;
            for (MutableAstNode? ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent)
            {
                if (selected.Contains(ancestor.Id)) { nested = true; break; }
            }
            if (!nested) result.Add(node);
        }
        return result;
    }

    private List<MutableAstObjectNode> ParseFragments(string text)
    {
        XamlParserOptions options = new() { UseTolerantParser = false };
        ParseResult parsed = _parser.Parse(text, options);
        if (!parsed.HasErrors && parsed.Document is MutableAstDocument { Root: { } single } document)
        {
            foreach (var alias in document.NamespaceAliases) single.NamespaceAliases.TryAdd(alias.Key, alias.Value);
            document.Root = null;
            return new List<MutableAstObjectNode> { single };
        }
        parsed = _parser.Parse("<Panel xmlns=\"https://github.com/avaloniaui\">" + text + "</Panel>", options);
        if (parsed.HasErrors || parsed.Document is not MutableAstDocument { Root: { } wrapper })
            throw new InvalidOperationException(UiStrings.DesignerClipboardInvalidXaml);
        if (wrapper.Properties.Count > 0 || wrapper.Children.Any(node =>
                node is MutableAstTextNode value && !string.IsNullOrWhiteSpace(value.Text)))
            throw new InvalidOperationException(UiStrings.DesignerPasteCompleteElements);
        List<MutableAstObjectNode> nodes = wrapper.Children.OfType<MutableAstObjectNode>().ToList();
        wrapper.Children.Clear();
        return nodes;
    }

    private void MakeNamesUnique(IReadOnlyList<MutableAstObjectNode> roots, MutableAstObjectNode destination)
    {
        HashSet<string> used = DesignerNameEditor.ScopeObjects(DesignerNameEditor.ScopeRoot(destination))
            .Select(node => DesignerNameEditor.FindNameProperty(node, _metadata))
            .Where(property => property?.Value is MutableAstTextNode)
            .Select(property => ((MutableAstTextNode)property!.Value!).Text).ToHashSet(StringComparer.Ordinal);
        Dictionary<string, string> renamed = new(StringComparer.Ordinal);
        MutableAstObjectNode[] elements = roots.SelectMany(root => Objects(root, stopAtTemplates: true)).ToArray();
        foreach (MutableAstObjectNode node in elements)
        {
            if (DesignerNameEditor.FindNameProperty(node, _metadata)?.Value is not MutableAstTextNode name || string.IsNullOrEmpty(name.Text)) continue;
            string original = name.Text;
            string candidate = original;
            for (int suffix = 1; used.Contains(candidate); suffix++) candidate = original + "Copy" + (suffix == 1 ? string.Empty : suffix);
            used.Add(candidate);
            if (candidate == original) continue;
            renamed[original] = candidate;
            name.Text = candidate;
        }
        foreach (MutableAstObjectNode node in elements)
        {
            foreach (MutableAstPropertyNode property in node.Properties)
            {
                if (property.Value is not MutableAstTextNode text) continue;
                text.Text = XamlNameReferences.Rewrite(node, property, renamed,
                    DesignerNameEditor.Namespaces(property, _engine.CurrentDocument!.NamespaceAliases)).Text;
            }
        }
    }

    private static string LocalName(string name) => name[(name.IndexOf(':') + 1)..];

    private static void Offset(MutableAstObjectNode node, string property)
    {
        string? text = node.GetPropertyValue(property);
        if (text is not null && !double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) return;
        double value = text is null ? 0 : double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        node.SetPropertyValue(property, (value + 10).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static IEnumerable<MutableAstObjectNode> Objects(MutableAstObjectNode root, bool stopAtTemplates = false)
    {
        Stack<MutableAstNode> pending = new();
        pending.Push(root);
        while (pending.TryPop(out MutableAstNode? node))
        {
            if (node is MutableAstPropertyNode { Value: { } value }) { pending.Push(value); continue; }
            if (node is not MutableAstObjectNode element) continue;
            if (stopAtTemplates && element.TypeName is "ControlTemplate" or "DataTemplate" or "ItemsPanelTemplate") continue;
            yield return element;
            for (int index = element.Children.Count - 1; index >= 0; index--) pending.Push(element.Children[index]);
            for (int index = element.Properties.Count - 1; index >= 0; index--) pending.Push(element.Properties[index]);
        }
    }
}
