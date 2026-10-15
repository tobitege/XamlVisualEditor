using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Xaml.Ast;

/// <summary>
/// Base class for all mutable AST nodes with change notification support.
/// </summary>
public abstract class MutableAstNode : INotifyPropertyChanged
{
    /// <summary>Gets comments and processing instructions immediately preceding this node.</summary>
    public List<XmlTrivia> LeadingTrivia { get; } = new();

    /// <summary>Gets comments and processing instructions at the end of this node's content.</summary>
    public List<XmlTrivia> TrailingTrivia { get; } = new();

    /// <summary>
    /// Gets the unique identifier for this node.
    /// </summary>
    public Guid Id { get; internal set; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the parent node. Null for the root.
    /// </summary>
    public MutableAstNode? Parent { get; internal set; }

    /// <summary>
    /// Gets the 1-based line number in the source (if available).
    /// </summary>
    public int Line { get; set; }

    /// <summary>
    /// Gets the 1-based column number in the source (if available).
    /// </summary>
    public int Column { get; set; }

    /// <summary>
    /// Gets the 1-based end line number in the source (if available). 0 means unknown.
    /// </summary>
    public int EndLine { get; set; }

    /// <summary>
    /// Gets the 1-based end column number in the source (if available). 0 means unknown.
    /// </summary>
    public int EndColumn { get; set; }

    /// <summary>
    /// Fires when any property of this node changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Fires when a structural or value change occurs that should be tracked.
    /// </summary>
    internal event Action<AstChange>? ChangeEmitted;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected void EmitChange(AstChange change)
    {
        ChangeEmitted?.Invoke(change);
    }

    /// <summary>
    /// Gets the human-readable kind name for this node (used in change records instead of reflection).
    /// </summary>
    public abstract string NodeKindName { get; }

    /// <summary>
    /// Accepts an AST visitor.
    /// </summary>
    public abstract void Accept(IAstVisitor visitor);

    /// <summary>
    /// Accepts an AST transformer, returning the potentially replaced node.
    /// </summary>
    public abstract MutableAstNode Accept(IAstTransformer transformer);
}

/// <summary>
/// Mutable AST document — the root container for a XAML file.
/// </summary>
public sealed class MutableAstDocument : IXamlDocumentModel
{
    private MutableAstObjectNode? _root;
    private readonly Dictionary<string, string> _detachedNamespaceAliases = new();
    public List<XmlTrivia> TrailingTrivia { get; } = new();

    /// <summary>
    /// Gets the unique document identifier.
    /// </summary>
    public Guid Id { get; } = Guid.NewGuid();

    /// <summary>
    /// Gets or sets the root element node.
    /// </summary>
    public MutableAstObjectNode? Root
    {
        get => _root;
        set
        {
            if (ReferenceEquals(_root, value))
            {
                return;
            }

            if (_root is not null)
            {
                _root.ChangeEmitted -= OnChildChangeEmitted;
            }

            _root = value;

            if (_root is not null)
            {
                _root.Parent = null;
                _root.ChangeEmitted += OnChildChangeEmitted;
                foreach (KeyValuePair<string, string> alias in _detachedNamespaceAliases)
                {
                    _root.NamespaceAliases.TryAdd(alias.Key, alias.Value);
                }

                _detachedNamespaceAliases.Clear();
            }
        }
    }

    /// <summary>
    /// Gets the namespace declarations of the root element. Without a root, declarations are held
    /// until a root is assigned, which then adopts them without overriding its own.
    /// </summary>
    public Dictionary<string, string> NamespaceAliases => _root?.NamespaceAliases ?? _detachedNamespaceAliases;

    /// <inheritdoc />
    public event Action<AstChange>? Changed;

    /// <summary>
    /// Gets the node map for this document.
    /// </summary>
    public AstNodeMap NodeMap { get; } = new();

    private void OnChildChangeEmitted(AstChange change)
    {
        Changed?.Invoke(change);
    }
}

/// <summary>
/// Represents a XAML element (object node) in the mutable AST.
/// </summary>
public sealed class MutableAstObjectNode : MutableAstNode
{
    private string _typeName = string.Empty;
    private string _xmlNamespace = string.Empty;

    /// <summary>Gets namespace declarations local to this element.</summary>
    public Dictionary<string, string> NamespaceAliases { get; } = new();

    /// <inheritdoc />
    public override string NodeKindName => "ObjectNode";

    /// <summary>
    /// Gets or sets the type name of this element.
    /// </summary>
    public string TypeName
    {
        get => _typeName;
        set
        {
            if (_typeName == value) return;
            string old = _typeName;
            _typeName = value;
            OnPropertyChanged();
            EmitChange(new PropertyValueChanged(Id, nameof(TypeName), old, value));
        }
    }

    /// <summary>
    /// Gets or sets the XML namespace of this element.
    /// </summary>
    public string XmlNamespace
    {
        get => _xmlNamespace;
        set
        {
            if (_xmlNamespace == value) return;
            _xmlNamespace = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets the properties (attributes and property elements) of this element.
    /// </summary>
    public ObservableCollection<MutableAstPropertyNode> Properties { get; } = new AstNodeCollection<MutableAstPropertyNode>();

    /// <summary>
    /// Gets the child element nodes.
    /// </summary>
    public ObservableCollection<MutableAstNode> Children { get; } = new AstNodeCollection<MutableAstNode>();

    public MutableAstObjectNode()
    {
        Properties.CollectionChanged += OnPropertiesChanged;
        Children.CollectionChanged += OnChildrenChanged;
    }

    private void OnPropertiesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move) return;
        if (e.NewItems is not null)
        {
            foreach (MutableAstPropertyNode item in e.NewItems)
            {
                item.Parent = this;
                item.ChangeEmitted += BubbleChange;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (MutableAstPropertyNode item in e.OldItems)
            {
                item.Parent = null;
                item.ChangeEmitted -= BubbleChange;
            }
        }
    }

    private void OnChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Move && e.NewItems is not null)
        {
            foreach (MutableAstNode item in e.NewItems)
            {
                EmitChange(new NodeMoved(item.Id, Id, e.OldStartingIndex, Id, e.NewStartingIndex));
            }
            return;
        }
        if (e.NewItems is not null)
        {
            foreach (MutableAstNode item in e.NewItems)
            {
                item.Parent = this;
                item.ChangeEmitted += BubbleChange;
                int index = Children.IndexOf(item);
                EmitChange(new NodeAdded(item.Id, Id, index, item.NodeKindName));
            }
        }

        if (e.OldItems is not null)
        {
            for (int idx = 0; idx < e.OldItems.Count; idx++)
            {
                MutableAstNode item = (MutableAstNode)e.OldItems[idx]!;
                item.Parent = null;
                item.ChangeEmitted -= BubbleChange;
                // All items shift down after each removal, so use OldStartingIndex for each
                EmitChange(new NodeRemoved(item.Id, Id, e.OldStartingIndex, item.NodeKindName));
            }
        }
    }

    private void BubbleChange(AstChange change) => EmitChange(change);

    /// <summary>
    /// Gets a property value by name, or null if not set.
    /// </summary>
    public string? GetPropertyValue(string propertyName)
    {
        MutableAstPropertyNode? prop = FindProperty(propertyName);
        return prop?.Value is MutableAstTextNode textNode ? textNode.Text : null;
    }

    /// <summary>Finds a local property, including its explicit Owner.Property element spelling.</summary>
    public MutableAstPropertyNode? FindProperty(string propertyName) =>
        Properties.FirstOrDefault(property => property.PropertyName == propertyName)
        ?? Properties.FirstOrDefault(property => property.IsPropertyElement && property.PropertyName == TypeName + "." + propertyName);

    /// <summary>
    /// Sets a property value by name, creating the property if needed.
    /// </summary>
    public void SetPropertyValue(string propertyName, string? value)
    {
        MutableAstPropertyNode? existing = FindProperty(propertyName);

        if (value is null)
        {
            if (existing is not null)
            {
                Properties.Remove(existing);
            }
            return;
        }

        if (existing is not null)
        {
            if (existing.Value is MutableAstTextNode textNode)
            {
                textNode.Text = value;
            }
            else
            {
                existing.Value = new MutableAstTextNode { Text = value };
            }
        }
        else
        {
            var prop = new MutableAstPropertyNode
            {
                PropertyName = propertyName,
                Value = new MutableAstTextNode { Text = value }
            };
            Properties.Add(prop);
        }
    }

    /// <inheritdoc />
    public override void Accept(IAstVisitor visitor)
    {
        visitor.VisitObjectNode(this);
        foreach (MutableAstPropertyNode prop in Properties)
        {
            prop.Accept(visitor);
        }
        foreach (MutableAstNode child in Children)
        {
            child.Accept(visitor);
        }
        visitor.EndVisitObjectNode(this);
    }

    /// <inheritdoc />
    public override MutableAstNode Accept(IAstTransformer transformer)
    {
        return transformer.TransformObjectNode(this);
    }
}

/// <summary>
/// Represents a property assignment (attribute or property element) in the mutable AST.
/// </summary>
public sealed class MutableAstPropertyNode : MutableAstNode
{
    private string _propertyName = string.Empty;
    private MutableAstNode? _value;
    public bool IsPropertyElement { get; set; }

    /// <summary>Gets or sets the local xml:space directive of a property element.</summary>
    public string? XmlSpace { get; set; }

    /// <summary>Gets or sets the XML namespace of an attribute or property element.</summary>
    public string XmlNamespace { get; set; } = string.Empty;

    /// <summary>Gets namespace declarations local to a property element.</summary>
    public Dictionary<string, string> NamespaceAliases { get; } = new();

    /// <inheritdoc />
    public override string NodeKindName => "PropertyNode";

    /// <summary>
    /// Gets or sets the property name.
    /// </summary>
    public string PropertyName
    {
        get => _propertyName;
        set
        {
            if (_propertyName == value) return;
            _propertyName = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Gets or sets the property value node.
    /// </summary>
    public MutableAstNode? Value
    {
        get => _value;
        set
        {
            if (ReferenceEquals(_value, value)) return;

            if (_value is not null)
            {
                _value.Parent = null;
                _value.ChangeEmitted -= BubbleChange;
            }

            _value = value;

            if (_value is not null)
            {
                _value.Parent = this;
                _value.ChangeEmitted += BubbleChange;
            }

            OnPropertyChanged();
        }
    }

    private void BubbleChange(AstChange change) => EmitChange(change);

    /// <inheritdoc />
    public override void Accept(IAstVisitor visitor)
    {
        visitor.VisitPropertyNode(this);
        _value?.Accept(visitor);
        visitor.EndVisitPropertyNode(this);
    }

    /// <inheritdoc />
    public override MutableAstNode Accept(IAstTransformer transformer)
    {
        return transformer.TransformPropertyNode(this);
    }
}

/// <summary>
/// Represents a text content node in the mutable AST.
/// </summary>
public sealed class MutableAstTextNode : MutableAstNode
{
    private string _text = string.Empty;

    /// <inheritdoc />
    public override string NodeKindName => "TextNode";

    /// <summary>
    /// Gets or sets the text content.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            string old = _text;
            _text = value;
            OnPropertyChanged();
            EmitChange(new TextContentChanged(Id, old, value));
        }
    }

    /// <inheritdoc />
    public override void Accept(IAstVisitor visitor)
    {
        visitor.VisitTextNode(this);
    }

    /// <inheritdoc />
    public override MutableAstNode Accept(IAstTransformer transformer)
    {
        return transformer.TransformTextNode(this);
    }
}

/// <summary>
/// Represents an XML directive node (x:Name, x:Key, etc.) in the mutable AST.
/// </summary>
public sealed class MutableAstDirectiveNode : MutableAstNode
{
    private string _directiveNamespace = string.Empty;
    private string _directiveName = string.Empty;
    private string _value = string.Empty;

    /// <inheritdoc />
    public override string NodeKindName => "DirectiveNode";

    /// <summary>
    /// Gets or sets the directive namespace (e.g., "http://schemas.microsoft.com/winfx/2006/xaml").
    /// </summary>
    public string DirectiveNamespace
    {
        get => _directiveNamespace;
        set { _directiveNamespace = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Gets or sets the directive name (e.g., "Name", "Key", "Class").
    /// </summary>
    public string DirectiveName
    {
        get => _directiveName;
        set { _directiveName = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Gets or sets the directive value.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            string old = _value;
            _value = value;
            OnPropertyChanged();
            EmitChange(new PropertyValueChanged(Id, DirectiveName, old, value));
        }
    }

    /// <inheritdoc />
    public override void Accept(IAstVisitor visitor)
    {
        visitor.VisitDirectiveNode(this);
    }

    /// <inheritdoc />
    public override MutableAstNode Accept(IAstTransformer transformer)
    {
        return transformer.TransformDirectiveNode(this);
    }
}

// Clear has to raise the regular remove notifications so parents and event subscriptions are released.
internal sealed class AstNodeCollection<T> : ObservableCollection<T>
{
    protected override void ClearItems()
    {
        while (Count > 0) RemoveAt(Count - 1);
    }
}
