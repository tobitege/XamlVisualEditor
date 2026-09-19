using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Serialization;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Loads complete property, resource and template structures through Avalonia's XAML compiler.</summary>
public sealed class RuntimeDesignRenderer
{
    private const string IdentityNamespace =
        "clr-namespace:XamlVisualEditor.Designer.Rendering;assembly=XamlVisualEditor.Designer.Rendering";
    private const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly ITypeMetadataService _metadata;
    private readonly XamlSerializationService _serializer = new();

    public RuntimeDesignRenderer(ITypeMetadataService metadata) => _metadata = metadata;

    /// <summary>
    /// Constructs the actual document root on the UI thread. The caller supplies the project assembly
    /// and asset URI rather than inferring them from the type or an output-directory convention.
    /// </summary>
    public RuntimeDesignRenderResult Render(MutableAstDocument document, Uri baseUri,
        Assembly? localAssembly = null, CancellationToken cancellationToken = default)
    {
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        MutableAstObjectNode source = document.Root
            ?? throw new InvalidOperationException("The document has no root element to render.");

        MutableAstObjectNode copy = AstNodeCloner.Clone(source);
        MutableAstDocument preview = new() { Root = copy };
        foreach (var declaration in document.NamespaceAliases)
            preview.NamespaceAliases[declaration.Key] = declaration.Value;

        string prefix = ChooseIdentityPrefix(document);
        preview.NamespaceAliases[prefix] = IdentityNamespace;
        copy.NamespaceAliases[prefix] = IdentityNamespace;
        Dictionary<Guid, string> sourceTypes = new();
        AddDesignMetadata(source, copy, prefix, sourceTypes, cancellationToken);

        RuntimeRenderRecovery recovery = new(preview, sourceTypes, IdentityNamespace);
        object loaded;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string xaml = _serializer.Serialize(preview);
            HashSet<Guid> constructedNodes = new();
            using IDisposable capture = DesignNodeIdentity.NodeIdProperty.Changed.Subscribe(change =>
            {
                if (change.Sender is Control control && Guid.TryParse(DesignNodeIdentity.GetNodeId(control), out Guid id)
                    && sourceTypes.ContainsKey(id)) constructedNodes.Add(id);
            });
            Window? ownedWindow = null;
            try
            {
                TypeMetadata? rootType = _metadata.GetType(preview.Root!.XmlNamespace, preview.Root.TypeName);
                if (rootType?.IsTopLevel == true && _metadata.ResolveClrType(rootType) is { } clrType
                    && typeof(Window).IsAssignableFrom(clrType))
                    ownedWindow = (Window?)Activator.CreateInstance(clrType);
                loaded = AvaloniaRuntimeXamlLoader.Load(xaml, localAssembly, ownedWindow, baseUri, true);
                break;
            }
            catch (Exception exception)
            {
                ownedWindow?.Close();
                if (!recovery.TryIsolate(exception, constructedNodes, xaml))
                    throw new DesignRenderException(baseUri, source.TypeName, exception);
            }
        }

        if (loaded is not Control root)
            throw new InvalidOperationException($"{baseUri}: {source.TypeName} does not produce a Control. Select a design preview for this document.");
        if (cancellationToken.IsCancellationRequested)
        {
            if (root is Window window) window.Close();
            cancellationToken.ThrowIfCancellationRequested();
        }
        return new RuntimeDesignRenderResult(root, baseUri, sourceTypes, recovery.Diagnostics.ToArray());
    }

    private void AddDesignMetadata(MutableAstObjectNode source, MutableAstObjectNode copy, string prefix,
        IDictionary<Guid, string> sourceTypes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Pair the untouched clone first: design-only properties change its collection afterwards.
        for (int index = 0; index < source.Properties.Count; index++)
        {
            if (source.Properties[index].Value is MutableAstObjectNode sourceValue
                && copy.Properties[index].Value is MutableAstObjectNode copyValue)
                AddDesignMetadata(sourceValue, copyValue, prefix, sourceTypes, cancellationToken);
        }
        for (int index = 0; index < source.Children.Count; index++)
        {
            if (source.Children[index] is MutableAstObjectNode sourceChild && copy.Children[index] is MutableAstObjectNode copyChild)
                AddDesignMetadata(sourceChild, copyChild, prefix, sourceTypes, cancellationToken);
        }

        TypeMetadata? type = source.TypeName == "__PropertyElementChildren__" ? null
            : _metadata.GetType(source.XmlNamespace, source.TypeName);
        HashSet<string> events = type is null ? new(StringComparer.Ordinal)
            : _metadata.GetEvents(type).Select(metadata => metadata.Name).ToHashSet(StringComparer.Ordinal);
        foreach (MutableAstPropertyNode property in copy.Properties.ToArray())
        {
            bool codeBehind = property.PropertyName == "x:Class"
                || property.XmlNamespace == XamlNamespace && LocalName(property.PropertyName) == "Class";
            if (codeBehind || events.Contains(property.PropertyName)) copy.Properties.Remove(property);
        }
        if (type?.IsControl != true) return;

        copy.Properties.Add(new MutableAstPropertyNode
        {
            PropertyName = prefix + ":DesignNodeIdentity.NodeId",
            XmlNamespace = IdentityNamespace,
            Value = new MutableAstTextNode { Text = source.Id.ToString("D") }
        });
        sourceTypes[source.Id] = type.FullName;
    }

    private static string ChooseIdentityPrefix(MutableAstDocument document)
    {
        HashSet<string> prefixes = document.NamespaceAliases.Keys.ToHashSet(StringComparer.Ordinal);
        Stack<MutableAstNode> pending = new();
        if (document.Root is { } root) pending.Push(root);
        while (pending.TryPop(out MutableAstNode? node))
        {
            switch (node)
            {
                case MutableAstObjectNode element:
                    prefixes.UnionWith(element.NamespaceAliases.Keys);
                    foreach (MutableAstPropertyNode property in element.Properties) pending.Push(property);
                    foreach (MutableAstNode child in element.Children) pending.Push(child);
                    break;
                case MutableAstPropertyNode property:
                    prefixes.UnionWith(property.NamespaceAliases.Keys);
                    if (property.Value is { } value) pending.Push(value);
                    break;
            }
        }
        string prefix = "xveDesign";
        for (int index = 1; prefixes.Contains(prefix); index++) prefix = "xveDesign" + index;
        return prefix;
    }

    private static string LocalName(string name) => name[(name.IndexOf(':') + 1)..];
}

/// <summary>A real rendered root, with mappings refreshable after lazy templates are instantiated.</summary>
public sealed class RuntimeDesignRenderResult
{
    internal RuntimeDesignRenderResult(Control root, Uri documentUri, IReadOnlyDictionary<Guid, string> sourceTypes,
        IReadOnlyList<DesignRenderDiagnostic> diagnostics)
    {
        Root = root;
        DocumentUri = documentUri;
        SourceTypes = sourceTypes;
        Diagnostics = diagnostics;
    }

    public Control Root { get; }
    public Uri DocumentUri { get; }
    public IReadOnlyDictionary<Guid, string> SourceTypes { get; }
    public IReadOnlyList<DesignRenderDiagnostic> Diagnostics { get; }

    /// <summary>
    /// Maps each live instance separately; repeated template instances can share one source node.
    /// Template-generated controls without an AST identity are deliberately not assigned by position.
    /// </summary>
    public IReadOnlyDictionary<Control, Guid> GetControlNodeIds(Control? surfaceRoot = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        Dictionary<Control, Guid> result = new();
        Control root = surfaceRoot ?? Root;
        IEnumerable<Control> controls = new[] { root }
            .Concat(root.GetVisualDescendants().OfType<Control>());
        foreach (Control control in controls)
            if (Guid.TryParse(DesignNodeIdentity.GetNodeId(control), out Guid id) && SourceTypes.ContainsKey(id))
                result[control] = id;
        return result;
    }
}

/// <summary>A rendering failure, not a successfully rendered replacement control.</summary>
public sealed class DesignRenderException : Exception
{
    public DesignRenderException(Uri documentUri, string rootType, Exception innerException)
        : base($"Could not render {rootType}: {DescribeFailure(innerException)}", innerException)
    {
        DocumentUri = documentUri;
        RootType = rootType;
    }
    public Uri DocumentUri { get; }
    public string RootType { get; }

    private static string DescribeFailure(Exception exception)
    {
        List<string> messages = new();
        HashSet<string> seen = new(StringComparer.Ordinal);
        Queue<Exception> pending = new();
        pending.Enqueue(exception);
        while (pending.TryDequeue(out Exception? current))
        {
            if (current is ReflectionTypeLoadException types)
            {
                foreach (Exception? error in types.LoaderExceptions)
                    if (error is not null) pending.Enqueue(error);
            }
            else if (current is AggregateException aggregate)
            {
                foreach (Exception error in aggregate.InnerExceptions) pending.Enqueue(error);
            }
            else if (current.InnerException is { } inner) pending.Enqueue(inner);
            else if (seen.Add(current.Message)) messages.Add(current.Message);
        }
        string summary = string.Join(Environment.NewLine, messages.Take(5));
        return messages.Count > 5 ? summary + $"\n{messages.Count - 5} additional errors; see the application log." : summary;
    }
}
