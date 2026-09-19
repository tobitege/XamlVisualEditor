using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ReactiveUI;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.PropertyEditorExtension;

/// <summary>A detached XML-backed draft with explicit apply and cancel actions.</summary>
public sealed class PropertyStructureEditorViewModel : ReactiveObject, IDisposable
{
    private readonly IDesignerStructureHost _host;
    private readonly DesignerPropertyStructure _original;
    private readonly XElement _root;
    private readonly CancellationTokenSource _lifetime = new();
    private StructureNodeViewModel? _selectedNode;
    private StructureAttributeViewModel? _selectedAttribute;
    private DesignerStructureType? _selectedType;
    private string? _typeFilter;
    private string? _attributeName;
    private string? _attributeValue;
    private string? _propertyName;
    private string? _error;
    private bool _isBusy;

    public PropertyStructureEditorViewModel(IDesignerStructureHost host, DesignerPropertyStructure original)
    {
        _host = host;
        _original = original;
        _root = XElement.Parse(original.PropertyElementXaml, LoadOptions.PreserveWhitespace);
        var editable = this.WhenAnyValue(x => x.IsBusy).Select(busy => !busy);
        AddChildCommand = ReactiveCommand.Create(() => Edit(() => AddElement(sibling: false)), editable);
        AddSiblingCommand = ReactiveCommand.Create(() => Edit(() => AddElement(sibling: true)), editable);
        RemoveCommand = ReactiveCommand.Create(() => Edit(RemoveSelected), editable);
        MoveUpCommand = ReactiveCommand.Create(() => Edit(() => MoveSelected(-1)), editable);
        MoveDownCommand = ReactiveCommand.Create(() => Edit(() => MoveSelected(1)), editable);
        AddPropertyCommand = ReactiveCommand.Create(() => Edit(AddProperty), editable);
        SetAttributeCommand = ReactiveCommand.Create(() => Edit(SetAttribute), editable);
        RemoveAttributeCommand = ReactiveCommand.Create(() => Edit(RemoveAttribute), editable);
        ClearContentCommand = ReactiveCommand.Create(() => Edit(() =>
        {
            XElement selected = RequireSelected();
            selected.RemoveNodes();
            Refresh(selected);
        }), editable);
        ApplyCommand = ReactiveCommand.CreateFromTask(ApplyAsync, editable);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(), editable);
        Refresh(_root);
    }

    public event Action? CloseRequested;
    public string Title => $"Edit {_original.PropertyName}";
    public ObservableCollection<StructureNodeViewModel> Roots { get; } = new();
    public ObservableCollection<StructureAttributeViewModel> Attributes { get; } = new();
    public IEnumerable<DesignerStructureType> AvailableTypes => _original.AvailableTypes
        .Where(type => string.IsNullOrWhiteSpace(TypeFilter) || type.DisplayName.Contains(TypeFilter, StringComparison.OrdinalIgnoreCase));
    public ReactiveCommand<Unit, Unit> AddChildCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSiblingCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveDownCommand { get; }
    public ReactiveCommand<Unit, Unit> AddPropertyCommand { get; }
    public ReactiveCommand<Unit, Unit> SetAttributeCommand { get; }
    public ReactiveCommand<Unit, Unit> RemoveAttributeCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearContentCommand { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public StructureNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedNode, value);
            Attributes.Clear();
            if (value is not null)
                foreach (XAttribute attribute in value.Element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
                    Attributes.Add(new StructureAttributeViewModel(attribute, GetQualifiedName(value.Element, attribute.Name)));
            SelectedAttribute = null;
            this.RaisePropertyChanged(nameof(TextValue));
            this.RaisePropertyChanged(nameof(CanEditText));
            RefreshGridDefinitionSelection();
        }
    }

    public StructureAttributeViewModel? SelectedAttribute
    {
        get => _selectedAttribute;
        set
        {
            this.RaiseAndSetIfChanged(ref _selectedAttribute, value);
            AttributeName = value?.Name;
            AttributeValue = value?.Value;
        }
    }

    public DesignerStructureType? SelectedType { get => _selectedType; set => this.RaiseAndSetIfChanged(ref _selectedType, value); }
    public string? TypeFilter
    {
        get => _typeFilter;
        set { this.RaiseAndSetIfChanged(ref _typeFilter, value); this.RaisePropertyChanged(nameof(AvailableTypes)); }
    }
    public string? AttributeName { get => _attributeName; set => this.RaiseAndSetIfChanged(ref _attributeName, value); }
    public string? AttributeValue { get => _attributeValue; set => this.RaiseAndSetIfChanged(ref _attributeValue, value); }
    public string? PropertyName { get => _propertyName; set => this.RaiseAndSetIfChanged(ref _propertyName, value); }
    public string? Error { get => _error; private set => this.RaiseAndSetIfChanged(ref _error, value); }
    public bool IsBusy { get => _isBusy; private set { this.RaiseAndSetIfChanged(ref _isBusy, value); this.RaisePropertyChanged(nameof(CanEdit)); } }
    public bool CanEdit => !IsBusy;
    public bool CanEditText => SelectedNode is { } selected && !selected.Element.Elements().Any();
    public string TextValue
    {
        get => SelectedNode is { } selected ? string.Concat(selected.Element.Nodes().OfType<XText>().Select(text => text.Value)) : string.Empty;
        set
        {
            if (!CanEditText || IsBusy) return;
            XElement selected = RequireSelected();
            XText[] texts = selected.Nodes().OfType<XText>().ToArray();
            if (texts.Length > 0)
            {
                texts[0].Value = value;
                foreach (XText text in texts.Skip(1)) text.Remove();
            }
            else selected.Add(new XText(value));
            this.RaisePropertyChanged();
        }
    }

    private void AddElement(bool sibling)
    {
        DesignerStructureType type = SelectedType ?? throw new InvalidOperationException("Choose a type from the project metadata.");
        XElement selected = RequireSelected();
        XElement parent = sibling ? selected.Parent ?? throw new InvalidOperationException("The property root has no sibling.") : selected;
        if (parent.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
            throw new InvalidOperationException("Clear the scalar content explicitly before inserting an object.");
        if (ReferenceEquals(parent, _root) && !_original.IsCollection && parent.Elements().Any())
            throw new InvalidOperationException("This property accepts only one root object.");
        EnsureNamespace(type.XmlNamespace);
        XElement added = new(XName.Get(type.TypeName, type.XmlNamespace));
        if (sibling) selected.AddAfterSelf(added);
        else parent.Add(added);
        Refresh(added);
    }

    private void AddProperty()
    {
        XElement selected = RequireSelected();
        if (selected.Name.LocalName.Contains('.')) throw new InvalidOperationException("Select an object, not a property element.");
        string name = PropertyName?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new InvalidOperationException("Enter the property name.");
        XName qualified = name.Contains(':') ? ResolveName(selected, name)
            : selected.Name.Namespace + (name.Contains('.') ? name : selected.Name.LocalName + "." + name);
        if (!qualified.LocalName.Contains('.')) throw new InvalidOperationException("Use Owner.Property for an attached property.");
        if (selected.Elements(qualified).Any()) throw new InvalidOperationException("This property element already exists.");
        XElement added = new(qualified);
        selected.Add(added);
        PropertyName = null;
        Refresh(added);
    }

    private void RemoveSelected()
    {
        XElement selected = RequireSelected();
        XElement parent = selected.Parent ?? throw new InvalidOperationException("The property root cannot be removed. Use Reset in the property inspector.");
        selected.Remove();
        Refresh(parent);
    }

    private void MoveSelected(int direction)
    {
        XElement selected = RequireSelected();
        XElement? neighbour = direction < 0 ? selected.ElementsBeforeSelf().LastOrDefault() : selected.ElementsAfterSelf().FirstOrDefault();
        if (neighbour is null) return;
        selected.Remove();
        if (direction < 0) neighbour.AddBeforeSelf(selected);
        else neighbour.AddAfterSelf(selected);
        Refresh(selected);
    }

    private void SetAttribute()
    {
        XElement selected = RequireSelected();
        string name = AttributeName?.Trim() ?? string.Empty;
        if (name.Length == 0) throw new InvalidOperationException("Enter the attribute name.");
        XName qualified = ResolveName(selected, name);
        if (qualified.Namespace == XNamespace.Xmlns || name == "xmlns")
            throw new InvalidOperationException("Namespace declarations are managed by the type picker.");
        selected.SetAttributeValue(qualified, AttributeValue ?? string.Empty);
        Refresh(selected);
        SelectedAttribute = Attributes.FirstOrDefault(attribute => attribute.Attribute.Name == qualified);
    }

    private void RemoveAttribute()
    {
        StructureAttributeViewModel attribute = SelectedAttribute ?? throw new InvalidOperationException("Select an attribute to remove.");
        attribute.Attribute.Remove();
        Refresh(RequireSelected());
    }

    private void EnsureNamespace(string xmlNamespace)
    {
        if (_root.GetDefaultNamespace() == xmlNamespace || _root.GetPrefixOfNamespace(xmlNamespace) is not null) return;
        for (int index = 1; ; index++)
        {
            string prefix = "lib" + index;
            if (_root.GetNamespaceOfPrefix(prefix) is not null) continue;
            _root.SetAttributeValue(XNamespace.Xmlns + prefix, xmlNamespace);
            return;
        }
    }

    private static XName ResolveName(XElement element, string name)
    {
        int colon = name.IndexOf(':');
        if (colon < 0) return XName.Get(name);
        XNamespace ns = element.GetNamespaceOfPrefix(name[..colon])
            ?? throw new InvalidOperationException("The namespace prefix is not declared in this document.");
        return ns + name[(colon + 1)..];
    }

    private static string GetQualifiedName(XElement owner, XName name) => name.Namespace == XNamespace.None ? name.LocalName
        : owner.GetPrefixOfNamespace(name.Namespace) is { Length: > 0 } prefix ? prefix + ":" + name.LocalName : name.ToString();

    private XElement RequireSelected() => SelectedNode?.Element ?? throw new InvalidOperationException("Select a structure node first.");

    private void Refresh(XElement selection)
    {
        StructureNodeViewModel root = new(_root);
        Roots.Clear();
        Roots.Add(root);
        SelectedNode = root.DescendantsAndSelf().FirstOrDefault(node => ReferenceEquals(node.Element, selection)) ?? root;
    }

    private Dictionary<XElement, int>? _gridDefinitionIndices;
    private ReactiveCommand<Unit, Unit>? _addGridDefinitionCommand;
    private ReactiveCommand<Unit, Unit>? _setGridDefinitionSizeCommand;
    private string _gridDefinitionSize = string.Empty;

    public bool IsGridEditor => _original.GridAxis != DesignerGridAxis.None;
    public string AddGridDefinitionLabel => _original.GridAxis == DesignerGridAxis.Rows ? "Add row" : "Add column";
    public bool HasSelectedGridDefinition => SelectedGridDefinition is not null;

    public string GridDefinitionSize
    {
        get => _gridDefinitionSize;
        set => this.RaiseAndSetIfChanged(ref _gridDefinitionSize, value);
    }

    public ReactiveCommand<Unit, Unit> AddGridDefinitionCommand => _addGridDefinitionCommand ??=
        ReactiveCommand.Create(() => Edit(AddGridDefinition), this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy));

    public ReactiveCommand<Unit, Unit> SetGridDefinitionSizeCommand => _setGridDefinitionSizeCommand ??=
        ReactiveCommand.Create(() => Edit(SetGridDefinitionSize), this.WhenAnyValue(vm => vm.IsBusy).Select(busy => !busy));

    private XElement? SelectedGridDefinition
    {
        get
        {
            XElement? element = SelectedNode?.Element;
            if (!IsGridEditor || element is null || !ReferenceEquals(element.Parent, GridDefinitionContainer))
                return null;
            string builtInName = _original.GridAxis == DesignerGridAxis.Rows ? "RowDefinition" : "ColumnDefinition";
            bool known = element.Name == XName.Get(builtInName, "https://github.com/avaloniaui") ||
                _original.GridDefinitionElementNames.Contains(element.Name.ToString()) ||
                _original.AvailableTypes.Any(type => type.GridAxis == _original.GridAxis &&
                    element.Name == XName.Get(type.TypeName, type.XmlNamespace));
            return known ? element : null;
        }
    }

    private void RefreshGridDefinitionSelection()
    {
        this.RaisePropertyChanged(nameof(HasSelectedGridDefinition));
        XElement? definition = SelectedGridDefinition;
        string name = _original.GridAxis == DesignerGridAxis.Rows ? "Height" : "Width";
        XElement? property = definition?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.EndsWith("." + name, StringComparison.Ordinal));
        GridDefinitionSize = definition is null ? string.Empty :
            definition.Attribute(name)?.Value ?? property?.ToString(SaveOptions.DisableFormatting) ?? "*";
    }

    private void AddGridDefinition()
    {
        if (!IsGridEditor)
            return;
        XElement container = GridDefinitionContainer
            ?? throw new InvalidOperationException("The Grid definition collection wrapper is missing.");
        if (container.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
            throw new InvalidOperationException("Clear the existing collection expression before adding a definition.");
        string name = _original.GridAxis == DesignerGridAxis.Rows ? "RowDefinition" : "ColumnDefinition";
        EnsureNamespace("https://github.com/avaloniaui");
        XElement definition = new(XName.Get(name, "https://github.com/avaloniaui"));
        if (SelectedGridDefinition is { } selected)
            selected.AddAfterSelf(definition);
        else
            container.Add(definition);
        Refresh(definition);
    }

    private void SetGridDefinitionSize()
    {
        XElement definition = SelectedGridDefinition
            ?? throw new InvalidOperationException("Select a row or column definition first.");
        var size = Avalonia.Controls.GridLength.Parse(GridDefinitionSize);
        string value = size.IsAuto ? "Auto" :
            size.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + (size.IsStar ? "*" : string.Empty);
        string name = _original.GridAxis == DesignerGridAxis.Rows ? "Height" : "Width";
        foreach (XElement property in definition.Elements().Where(element =>
                     element.Name.LocalName.EndsWith("." + name, StringComparison.Ordinal)).ToArray())
            property.Remove();
        definition.SetAttributeValue(name, value);
        Refresh(definition);
    }

    private XElement? GridDefinitionContainer => _original.GridDefinitionContainerName is null
        ? _root : _root.Elements(XName.Get(_original.GridDefinitionContainerName)).SingleOrDefault();

    private void EnsureGridDefinitionTracking()
    {
        if (_original.GridAxis == DesignerGridAxis.None || _gridDefinitionIndices is not null)
            return;
        XElement container = GridDefinitionContainer
            ?? throw new InvalidOperationException("The Grid definition collection wrapper is missing.");
        _gridDefinitionIndices = new Dictionary<XElement, int>(ReferenceEqualityComparer.Instance);
        int index = 0;
        foreach (XElement definition in container.Elements())
            _gridDefinitionIndices.Add(definition, index++);
    }

    private IReadOnlyList<int?>? GetGridDefinitionMap()
    {
        if (_original.GridAxis == DesignerGridAxis.None)
            return null;
        EnsureGridDefinitionTracking();
        XElement container = GridDefinitionContainer
            ?? throw new InvalidOperationException("The Grid definition collection wrapper is missing.");
        return container.Elements().Select(element =>
            _gridDefinitionIndices!.TryGetValue(element, out int index) ? (int?)index : null).ToArray();
    }

    private void Edit(Action action)
    {
        EnsureGridDefinitionTracking();
        try { Error = null; action(); }
        catch (Exception exception) { Error = exception.Message; }
    }

    private async Task ApplyAsync()
    {
        try
        {
            IsBusy = true;
            Error = null;
            await _host.ApplyPropertyStructureAsync(
                _original with { GridDefinitionOriginalIndices = GetGridDefinitionMap() },
                _root.ToString(SaveOptions.DisableFormatting), _lifetime.Token);
            CloseRequested?.Invoke();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Error = exception.Message; }
        finally { IsBusy = false; }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _addGridDefinitionCommand?.Dispose();
        _setGridDefinitionSizeCommand?.Dispose();
        AddChildCommand.Dispose(); AddSiblingCommand.Dispose(); RemoveCommand.Dispose();
        MoveUpCommand.Dispose(); MoveDownCommand.Dispose(); AddPropertyCommand.Dispose();
        SetAttributeCommand.Dispose(); RemoveAttributeCommand.Dispose(); ClearContentCommand.Dispose();
        ApplyCommand.Dispose(); CancelCommand.Dispose();
    }
}

public sealed class StructureNodeViewModel
{
    public StructureNodeViewModel(XElement element)
    {
        Element = element;
        Children = element.Elements().Select(child => new StructureNodeViewModel(child)).ToArray();
    }
    internal XElement Element { get; }
    public string Label => Element.Name.LocalName + (Element.Attributes().FirstOrDefault(attribute =>
        attribute.Name.LocalName is "Name" or "Key") is { } identity ? $" ({identity.Value})" : string.Empty);
    public IReadOnlyList<StructureNodeViewModel> Children { get; }
    public IEnumerable<StructureNodeViewModel> DescendantsAndSelf()
    {
        yield return this;
        foreach (StructureNodeViewModel child in Children)
            foreach (StructureNodeViewModel descendant in child.DescendantsAndSelf()) yield return descendant;
    }
}

public sealed class StructureAttributeViewModel
{
    public StructureAttributeViewModel(XAttribute attribute, string name) { Attribute = attribute; Name = name; }
    internal XAttribute Attribute { get; }
    public string Name { get; }
    public string Value => Attribute.Value;
    public string Display => Name + " = " + Value;
}
