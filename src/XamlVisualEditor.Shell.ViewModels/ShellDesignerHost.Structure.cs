using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class ShellDesignerHost : IDesignerStructureHost
{
    public async Task<DesignerPropertyStructure> OpenPropertyStructureAsync(
        string nodeId, string propertyName, CancellationToken cancellationToken)
    {
        var captured = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesignerDocumentViewModel document = RequireStructureDocument();
            MutableAstObjectNode node = RequireStructureNode(document, nodeId);
            string fragment = new DesignerSelectionEditor(document.SyncEngine, document.MetadataService).Copy(new[] { node });
            XElement owner = XElement.Parse(fragment, LoadOptions.PreserveWhitespace);
            MutableAstPropertyNode? localProperty = FindLocalProperty(document, node, propertyName);
            string localName = localProperty?.PropertyName ?? propertyName;
            localName = localName[(localName.IndexOf(':') + 1)..];
            if (!localName.Contains('.')) localName = owner.Name.LocalName + "." + localName;
            XElement? property = owner.Elements().FirstOrDefault(element => element.Name.LocalName == localName);
            bool implicitContent = property is null && UsesImplicitContent(document, node, propertyName)
                && !owner.Attributes().Any(attribute => attribute.Name.LocalName == propertyName);
            if (property is null)
            {
                property = new XElement(owner.Name.Namespace + localName);
                if (implicitContent)
                {
                    foreach (XNode content in owner.Nodes().Where(content =>
                        content is not XElement element || !element.Name.LocalName.Contains('.')).ToArray())
                    {
                        content.Remove();
                        property.Add(content);
                    }
                }
                else if (owner.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == propertyName) is { } value)
                {
                    property.Add(new XText(value.Value));
                }
            }
            else property = new XElement(property);

            foreach (XAttribute declaration in owner.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                if (property.Attribute(declaration.Name) is null) property.Add(new XAttribute(declaration));
            if (property.GetNamespaceOfPrefix("x") is null)
                property.SetAttributeValue(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml");

            TypeMetadata? type = document.MetadataService?.GetType(node.XmlNamespace, node.TypeName);
            PropertyMetadata? metadata = type is null ? null : document.MetadataService?.GetProperties(type)
                .FirstOrDefault(candidate => candidate.Name == propertyName);
            DesignerGridAxis gridAxis = DesignerGridAxis.None;
            XElement definitions = property;
            if (new LayoutEditService(document.MetadataService).GetKind(node) == LayoutContainerKind.Grid)
            {
                if (propertyName == "RowDefinitions" && metadata?.ClrType is { } rowsType && typeof(RowDefinitions).IsAssignableFrom(rowsType))
                    gridAxis = DesignerGridAxis.Rows;
                else if (propertyName == "ColumnDefinitions" && metadata?.ClrType is { } columnsType && typeof(ColumnDefinitions).IsAssignableFrom(columnsType))
                    gridAxis = DesignerGridAxis.Columns;
            }

            if (gridAxis != DesignerGridAxis.None)
            {
                XElement[] children = property.Elements().Take(2).ToArray();
                XElement? wrapper = children.Length == 1 ? children[0] : null;
                TypeMetadata? wrapperType = wrapper is null ? null : document.MetadataService?.GetType(wrapper.Name.NamespaceName, wrapper.Name.LocalName);
                Type collectionType = gridAxis == DesignerGridAxis.Rows ? typeof(RowDefinitions) : typeof(ColumnDefinitions);
                if (wrapper is not null && wrapperType is not null && IsStructureTypeOrDerived(wrapperType, collectionType)) definitions = wrapper;
                ExpandGridDefinitionText(definitions, gridAxis);
            }
            bool collection = metadata?.ClrType is { } clrType
                ? clrType != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(clrType)
                : metadata?.TypeFullName is { } typeName && (typeName.Contains("Collection", StringComparison.Ordinal)
                    || typeName.Contains("Dictionary", StringComparison.Ordinal) || typeName.Contains("IEnumerable", StringComparison.Ordinal)
                    || typeName.Contains("IList", StringComparison.Ordinal) || typeName.EndsWith("[]", StringComparison.Ordinal));
            if (implicitContent && new LayoutEditService(document.MetadataService).GetKind(node)
                is LayoutContainerKind.Panel or LayoutContainerKind.Canvas or LayoutContainerKind.Grid
                    or LayoutContainerKind.Stack or LayoutContainerKind.Dock or LayoutContainerKind.Items)
                collection = true;

            DesignerPropertyStructure snapshot = new(document.FilePath, nodeId, propertyName,
                document.SyncEngine.CurrentText ?? throw new InvalidOperationException("The document text is unavailable."),
                property.ToString(SaveOptions.DisableFormatting), collection,
                implicitContent, Array.Empty<DesignerStructureType>())
            {
                GridAxis = gridAxis,
                GridDefinitionContainerName = ReferenceEquals(definitions, property) ? null : definitions.Name.ToString(),
                GridDefinitionElementNames = gridAxis == DesignerGridAxis.None ? Array.Empty<string>() :
                    definitions.Elements().Where(element => document.MetadataService?.GetType(element.Name.NamespaceName, element.Name.LocalName) is { } definitionType
                        && GetGridDefinitionAxis(definitionType) == gridAxis)
                        .Select(element => element.Name.ToString()).Distinct(StringComparer.Ordinal).ToArray()
            };
            return (Snapshot: snapshot, Metadata: document.MetadataService);
        });

        IReadOnlyList<DesignerStructureType> types = await Task.Run(() =>
        {
            List<DesignerStructureType> available = new();
            foreach (TypeMetadata type in captured.Metadata?.GetAvailableTypes() ?? Array.Empty<TypeMetadata>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (type.IsInstantiable && !type.IsTopLevel && type.IsToolboxVisible)
                    available.Add(new(type.Name, type.XmlNamespace, $"{type.Name} [{type.AssemblyName}]")
                    {
                        GridAxis = GetGridDefinitionAxis(type)
                    });
            }
            return (IReadOnlyList<DesignerStructureType>)available
                .OrderBy(type => type.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        }, cancellationToken);
        return captured.Snapshot with { AvailableTypes = types };
    }

    public async Task ApplyPropertyStructureAsync(
        DesignerPropertyStructure original, string propertyElementXaml, CancellationToken cancellationToken)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            DesignerDocumentViewModel document = RequireStructureDocument();
            if (!string.Equals(document.FilePath, original.DocumentPath, StringComparison.OrdinalIgnoreCase)
                || document.SyncEngine.CurrentText != original.DocumentText)
                throw new InvalidOperationException("The document changed while this editor was open. Cancel and reopen the property to use the current version.");
            MutableAstObjectNode node = RequireStructureNode(document, original.OwnerNodeId);
            XElement property = XElement.Parse(propertyElementXaml, LoadOptions.PreserveWhitespace);
            XElement previous = XElement.Parse(original.PropertyElementXaml, LoadOptions.PreserveWhitespace);
            if (property.Name != previous.Name) throw new InvalidOperationException("The property owner cannot be changed.");
            if (XNode.DeepEquals(previous, property)) return;
            if (!original.IsCollection && property.Elements().Skip(1).Any())
                throw new InvalidOperationException("This property accepts only one value. Use a container for multiple children.");
            if (property.Elements().Any() && property.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new InvalidOperationException("A scalar expression cannot be mixed with object elements.");

            XElement owner = new(XName.Get(node.TypeName[(node.TypeName.IndexOf(':') + 1)..], node.XmlNamespace), property);
            foreach (XAttribute declaration in property.Attributes().Where(attribute => attribute.IsNamespaceDeclaration))
                owner.SetAttributeValue(declaration.Name, declaration.Value);
            var parsed = new XamlParsingService().Parse(owner.ToString(SaveOptions.DisableFormatting));
            if (parsed.HasErrors)
                throw new InvalidOperationException($"Cannot edit {original.PropertyName} in {document.FilePath}: " +
                    string.Join("; ", parsed.Diagnostics.Select(diagnostic =>
                        $"Line {diagnostic.Line}, column {diagnostic.Column}: {diagnostic.Message}")));
            if (parsed.Document is not MutableAstDocument { Root: { } parsedOwner }
                || parsedOwner.Properties.Count != 1)
                throw new InvalidOperationException(
                    $"Editing {original.PropertyName} on {node.TypeName} requires one property element, " +
                    $"such as <{node.TypeName}.{original.PropertyName}>, not <{property.Name.LocalName}>.");
            MutableAstPropertyNode replacement = parsedOwner.Properties[0];
            parsedOwner.Properties.Remove(replacement);

            IReadOnlyList<Action> gridCellChanges = PrepareGridCellEdits(document, node, original, propertyElementXaml);
            using DocumentEditTransaction edit = document.SyncEngine.BeginEditTransaction(
                $"Edit {original.PropertyName}", SyncSource.PropertyEditor);
            MutableAstPropertyNode? existing = FindLocalProperty(document, node, original.PropertyName);
            if (existing is not null) replacement.LeadingTrivia.AddRange(existing.LeadingTrivia);
            if (original.UsesImplicitContent) node.Children.Clear();
            if (existing is not null) node.Properties[node.Properties.IndexOf(existing)] = replacement;
            else node.Properties.Add(replacement);
            foreach (Action change in gridCellChanges)
                change();
            edit.Commit();
        });
    }

    private static DesignerGridAxis GetGridDefinitionAxis(TypeMetadata type) =>
        IsStructureTypeOrDerived(type, typeof(RowDefinition)) ? DesignerGridAxis.Rows :
        IsStructureTypeOrDerived(type, typeof(ColumnDefinition)) ? DesignerGridAxis.Columns : DesignerGridAxis.None;

    private static bool IsStructureTypeOrDerived(TypeMetadata type, Type expected)
    {
        string? assemblyName = expected.Assembly.GetName().Name;
        HashSet<TypeMetadata> visited = new(ReferenceEqualityComparer.Instance);
        for (TypeMetadata? current = type; current is not null && visited.Add(current); current = current.BaseType)
            if (current.FullName == expected.FullName && current.AssemblyName == assemblyName) return true;
        return false;
    }

    private static IReadOnlyList<Action> PrepareGridCellEdits(
        DesignerDocumentViewModel document,
        MutableAstObjectNode grid,
        DesignerPropertyStructure original,
        string propertyElementXaml)
    {
        if (original.GridAxis == DesignerGridAxis.None)
            return Array.Empty<Action>();

        IReadOnlyList<int?> indices = original.GridDefinitionOriginalIndices
            ?? throw new InvalidOperationException("Grid definition edits require the original row or column identities.");
        XElement before = GetGridDefinitionContainer(
            XElement.Parse(original.PropertyElementXaml, LoadOptions.PreserveWhitespace), original);
        XElement after = GetGridDefinitionContainer(
            XElement.Parse(propertyElementXaml, LoadOptions.PreserveWhitespace), original);
        XElement[] definitions = after.Elements().ToArray();
        int oldCount = before.Elements().Count();
        if (indices.Count != definitions.Length ||
            indices.Any(index => index.HasValue && (index.Value < 0 || index.Value >= oldCount)) ||
            indices.Where(index => index.HasValue).Distinct().Count() != indices.Count(index => index.HasValue))
            throw new InvalidOperationException("The original Grid definition identities are invalid. Reopen the editor.");

        foreach (XElement definition in definitions)
        {
            TypeMetadata? type = document.MetadataService?.GetType(definition.Name.NamespaceName, definition.Name.LocalName);
            if (type is null || GetGridDefinitionAxis(type) != original.GridAxis)
                throw new InvalidOperationException($"'{definition.Name.LocalName}' is not a definition for this Grid axis.");
        }

        if (indices.Count == oldCount && indices.Select((index, ordinal) => index == ordinal).All(same => same))
            return Array.Empty<Action>();

        int effectiveOldCount = Math.Max(1, oldCount);
        int effectiveNewCount = Math.Max(1, indices.Count);
        string positionName = original.GridAxis == DesignerGridAxis.Rows ? "Row" : "Column";
        string spanName = positionName + "Span";
        List<Action> changes = new();
        foreach (MutableAstObjectNode child in new LayoutEditService(document.MetadataService).GetChildren(grid))
        {
            MutableAstPropertyNode? position = FindGridPlacementProperty(document, child, positionName);
            MutableAstPropertyNode? span = FindGridPlacementProperty(document, child, spanName);
            int oldPosition = ReadGridPlacement(child, position, positionName, 0);
            int oldSpan = ReadGridPlacement(child, span, spanName, 1);
            int start = Math.Min(oldPosition, effectiveOldCount - 1);
            int end = (int)Math.Min((long)start + oldSpan, effectiveOldCount);
            int[] covered = Enumerable.Range(0, indices.Count)
                .Where(index => indices[index] is int previous && previous >= start && previous < end)
                .ToArray();
            int newPosition;
            int newSpan;
            if (covered.Length != 0)
            {
                newPosition = covered[0];
                int last = covered[^1];
                for (int index = newPosition; index <= last; index++)
                {
                    if (indices[index] is int previous && (previous < start || previous >= end))
                        throw new InvalidOperationException(
                            $"Moving this definition would split Grid.{spanName} on '{child.TypeName}'. Change that span first.");
                }
                newSpan = last - newPosition + 1;
            }
            else
            {
                int? successor = indices.Where(index => index >= end).OrderBy(index => index).FirstOrDefault();
                int? predecessor = indices.Where(index => index < start).OrderByDescending(index => index).FirstOrDefault();
                int? adjacent = successor ?? predecessor;
                newPosition = adjacent.HasValue
                    ? Enumerable.Range(0, indices.Count).First(index => indices[index] == adjacent)
                    : Math.Min(start, effectiveNewCount - 1);
                newSpan = 1;
            }

            PrepareGridPlacementChange(document, child, position, positionName, oldPosition, newPosition, changes);
            PrepareGridPlacementChange(document, child, span, spanName, oldSpan, newSpan, changes);
        }

        return changes;
    }

    private static XElement GetGridDefinitionContainer(XElement property, DesignerPropertyStructure original)
    {
        if (original.GridDefinitionContainerName is null)
            return property;
        XElement[] containers = property.Elements().Take(2).ToArray();
        if (containers.Length != 1 || containers[0].Name != XName.Get(original.GridDefinitionContainerName))
            throw new InvalidOperationException("Keep the Grid definition collection wrapper and edit its rows or columns.");
        return containers[0];
    }

    private static MutableAstPropertyNode? FindGridPlacementProperty(
        DesignerDocumentViewModel document, MutableAstObjectNode child, string name)
    {
        MutableAstPropertyNode? result = null;
        foreach (MutableAstPropertyNode property in child.Properties)
        {
            string localName = property.PropertyName[(property.PropertyName.LastIndexOf(':') + 1)..];
            int separator = localName.LastIndexOf('.');
            if (separator < 0 || localName[(separator + 1)..] != name)
                continue;
            string ownerName = localName[..separator];
            string xmlNamespace = string.IsNullOrEmpty(property.XmlNamespace)
                ? "https://github.com/avaloniaui" : property.XmlNamespace;
            TypeMetadata? ownerType = document.MetadataService?.GetType(xmlNamespace, ownerName);
            if (!(ownerName == "Grid" && xmlNamespace == "https://github.com/avaloniaui") &&
                (ownerType is null || !IsStructureTypeOrDerived(ownerType, typeof(Grid))))
                continue;
            if (result is not null)
                throw new InvalidOperationException($"'{child.TypeName}' has multiple Grid.{name} assignments.");
            result = property;
        }
        return result;
    }

    private static int ReadGridPlacement(
        MutableAstObjectNode child, MutableAstPropertyNode? property, string name, int defaultValue)
    {
        if (property is null)
            return defaultValue;
        string? value = child.GetPropertyValue(property.PropertyName);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) || number < defaultValue)
            throw new InvalidOperationException(
                $"Grid.{name} on '{child.TypeName}' is not a literal valid cell value. Its binding or expression cannot be moved automatically.");
        return number;
    }

    private static void PrepareGridPlacementChange(
        DesignerDocumentViewModel document, MutableAstObjectNode child, MutableAstPropertyNode? existing,
        string name, int oldValue, int newValue, List<Action> changes)
    {
        if (oldValue == newValue)
            return;
        string value = newValue.ToString(CultureInfo.InvariantCulture);
        if (existing is not null)
        {
            changes.Add(() => child.SetPropertyValue(existing.PropertyName, value));
            return;
        }

        XElement owner = XElement.Parse(new DesignerSelectionEditor(document.SyncEngine, document.MetadataService)
            .Copy(new[] { child }), LoadOptions.PreserveWhitespace);
        XElement wrapper = new(owner.Name,
            owner.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Select(attribute => new XAttribute(attribute)));
        XNamespace avalonia = "https://github.com/avaloniaui";
        string propertyName = "Grid." + name;
        if (wrapper.GetDefaultNamespace() == avalonia)
            wrapper.SetAttributeValue(propertyName, value);
        else
        {
            if (wrapper.GetPrefixOfNamespace(avalonia) is null)
            {
                string prefix = "grid";
                for (int suffix = 1; wrapper.GetNamespaceOfPrefix(prefix) is not null; suffix++)
                    prefix = "grid" + suffix.ToString(CultureInfo.InvariantCulture);
                wrapper.SetAttributeValue(XNamespace.Xmlns + prefix, avalonia.NamespaceName);
            }
            wrapper.SetAttributeValue(avalonia + propertyName, value);
        }
        var parsed = new XamlParsingService().Parse(wrapper.ToString(SaveOptions.DisableFormatting));
        if (parsed.HasErrors || parsed.Document is not MutableAstDocument { Root: { } parsedOwner }
            || parsedOwner.Properties.Count != 1)
            throw new InvalidOperationException($"Cannot create Grid.{name} for '{child.TypeName}'.");
        MutableAstPropertyNode replacement = parsedOwner.Properties[0];
        parsedOwner.Properties.Remove(replacement);
        changes.Add(() =>
        {
            foreach (var alias in parsedOwner.NamespaceAliases)
                child.NamespaceAliases[alias.Key] = alias.Value;
            child.Properties.Add(replacement);
        });
    }

    private static void ExpandGridDefinitionText(XElement definitions, DesignerGridAxis axis)
    {
        if (definitions.Elements().Any()) return;
        string value = string.Concat(definitions.Nodes().OfType<XText>().Select(text => text.Value)).Trim();
        if (value.Length == 0 || value.StartsWith('{')) return;
        IEnumerable<GridLength> sizes = axis == DesignerGridAxis.Rows
            ? new RowDefinitions(value).Select(row => row.Height)
            : new ColumnDefinitions(value).Select(column => column.Width);
        string definitionName = axis == DesignerGridAxis.Rows ? "RowDefinition" : "ColumnDefinition";
        string propertyName = axis == DesignerGridAxis.Rows ? "Height" : "Width";
        XElement[] elements = sizes.Select(size => new XElement(XName.Get(definitionName, "https://github.com/avaloniaui"),
            new XAttribute(propertyName, size.IsAuto ? "Auto" : size.Value.ToString("R", CultureInfo.InvariantCulture)
                + (size.IsStar ? "*" : string.Empty)))).ToArray();
        foreach (XText text in definitions.Nodes().OfType<XText>().ToArray()) text.Remove();
        definitions.Add(elements);
    }

    private DesignerDocumentViewModel RequireStructureDocument()
    {
        DesignerDocumentViewModel? document = GetActiveDocument();
        if (document is null || document.IsDisposed || !document.SyncEngine.CanEditAst)
            throw new InvalidOperationException("Open a valid XAML document before editing its structure.");
        return document;
    }

    private static MutableAstObjectNode RequireStructureNode(DesignerDocumentViewModel document, string nodeId) =>
        Guid.TryParse(nodeId, out Guid id) && document.NodeMap.FindById(id) is MutableAstObjectNode node
            ? node : throw new InvalidOperationException("The property owner is no longer available.");

    private static bool UsesImplicitContent(DesignerDocumentViewModel document, MutableAstObjectNode node, string propertyName) =>
        new LayoutEditService(document.MetadataService).GetKind(node) switch
        {
            LayoutContainerKind.Canvas or LayoutContainerKind.Grid or LayoutContainerKind.Stack
                or LayoutContainerKind.Panel or LayoutContainerKind.Dock => propertyName == "Children",
            LayoutContainerKind.Items => propertyName == "Items",
            LayoutContainerKind.Content => propertyName == "Content",
            LayoutContainerKind.Child => propertyName == "Child",
            _ => false
        };
}
