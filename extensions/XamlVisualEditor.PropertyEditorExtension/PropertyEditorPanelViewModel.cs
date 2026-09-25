using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Collections;
using Avalonia.Media;
using ReactiveUI;
using XamlVisualEditor.Extensions;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.PropertyEditorExtension;

public sealed class PropertyEntryViewModel : ReactiveObject
{
    private static readonly HashSet<string> BooleanTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bool",
        "boolean",
        "System.Boolean"
    };
    private static readonly HashSet<string> NumericTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "byte",
        "sbyte",
        "short",
        "ushort",
        "int",
        "int32",
        "uint",
        "uint32",
        "long",
        "int64",
        "ulong",
        "uint64",
        "float",
        "single",
        "double",
        "decimal",
        "System.Byte",
        "System.SByte",
        "System.Int16",
        "System.UInt16",
        "System.Int32",
        "System.UInt32",
        "System.Int64",
        "System.UInt64",
        "System.Single",
        "System.Double",
        "System.Decimal"
    };
    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> WellKnownEnumOptions = new Dictionary<string, IReadOnlyList<string>>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["HorizontalAlignment"] = new[] { "Stretch", "Left", "Center", "Right" },
        ["VerticalAlignment"] = new[] { "Stretch", "Top", "Center", "Bottom" },
        ["HorizontalContentAlignment"] = new[] { "Stretch", "Left", "Center", "Right" },
        ["VerticalContentAlignment"] = new[] { "Stretch", "Top", "Center", "Bottom" },
        ["TextAlignment"] = new[] { "Left", "Center", "Right", "Justify" },
        ["TextWrapping"] = new[] { "NoWrap", "Wrap", "WrapWithOverflow" },
        ["TextTrimming"] = new[] { "None", "CharacterEllipsis", "WordEllipsis" },
        ["FontWeight"] = new[] { "Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black" },
        ["FontStyle"] = new[] { "Normal", "Italic", "Oblique" },
        ["Orientation"] = new[] { "Horizontal", "Vertical" },
        ["DockPanel.Dock"] = new[] { "Left", "Top", "Right", "Bottom" },
        ["Dock"] = new[] { "Left", "Top", "Right", "Bottom" },
        ["HorizontalScrollBarVisibility"] = new[] { "Disabled", "Auto", "Hidden", "Visible" },
        ["VerticalScrollBarVisibility"] = new[] { "Disabled", "Auto", "Hidden", "Visible" }
    };
    private string? _value;
    private string? _committedValue;
    private bool _committedHasLocalValue;
    private bool _isSet;
    private bool? _boolValue;
    private double? _numberValue;
    private string? _enumValue;
    private Color? _colorValue;
    private IBrush? _brushPreview;
    private bool _isUpdating;
    private string? _validationError;

    public PropertyEntryViewModel(
        string nodeId,
        string name,
        string propertyType,
        string? value,
        bool isReadOnly,
        string? category,
        string? description,
        string? defaultValue,
        bool isAttached,
        string? ownerType,
        IReadOnlyList<string>? enumOptions,
        PropertyEditorDescriptor? descriptor,
        bool isCollection = false,
        bool isFlagsEnum = false,
        bool isSignedEnum = true,
        IReadOnlyDictionary<string, ulong>? enumValues = null,
        bool hasLocalValue = false,
        bool isTemplate = false,
        bool canAssignNull = false)
    {
        NodeId = nodeId;
        Name = name;
        PropertyType = propertyType;
        IsReadOnly = isReadOnly;
        Category = category ?? "Misc";
        Description = description;
        DefaultValue = defaultValue;
        IsAttached = isAttached;
        OwnerType = ownerType;
        Descriptor = descriptor;
        IsCollection = isCollection;
        IsTemplate = isTemplate;
        CanAssignNull = canAssignNull && !isReadOnly;
        IsFlagsEnum = isFlagsEnum && enumValues is { Count: > 0 };
        IsSignedEnum = isSignedEnum;
        FlagOptions = IsFlagsEnum
            ? enumValues!.Select(value => new EnumFlagOptionViewModel(value.Key, value.Value, SetFlag)).ToArray()
            : Array.Empty<EnumFlagOptionViewModel>();
        IReadOnlyList<string>? resolvedEnumOptions = descriptor?.EnumOptions ?? enumOptions ?? ResolveWellKnownEnumOptions(name);
        EnumOptions = resolvedEnumOptions;
        BrushPresets = descriptor?.BrushPresets;
        EditorKind = descriptor?.Kind ?? (isTemplate ? PropertyEditorKind.Template
            : isCollection ? PropertyEditorKind.Collection : GetEditorKind(name, propertyType, resolvedEnumOptions, null));
        InitializeValue(value);
        _committedValue = _value;
        _committedHasLocalValue = hasLocalValue || _value is not null;
        _isSet = _committedHasLocalValue;
    }

    /// <summary>
    /// Sets the initial values without change notifications. The panel constructs
    /// one entry per property of the selected control; raising events for every
    /// value here materializes per-object ReactiveUI state with no subscriber yet
    /// and dominates selection time for large property sets.
    /// </summary>
    private void InitializeValue(string? value)
    {
        _value = value;
        _boolValue = bool.TryParse(value, out bool parsedBool) ? parsedBool : null;
        _numberValue = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedNumber)
            ? parsedNumber
            : null;
        _enumValue = FindEnumValue(EnumOptions, value);
        if (FlagOptions.Count > 0)
        {
            ulong flags = ReadFlagBits(value ?? DefaultValue);
            foreach (EnumFlagOptionViewModel option in FlagOptions)
                option.Refresh(option.Bits == 0 ? flags == 0 : (flags & option.Bits) == option.Bits);
        }

        if (Color.TryParse(value, out Color parsedColor))
        {
            _colorValue = parsedColor;
            _brushPreview = new SolidColorBrush(parsedColor);
        }
    }

    // The commands are created on first use: the panel builds one entry per property of the
    // selected control, and creating every command up front dominates selection time.
    private ReactiveCommand<Unit, Unit>? _setNullCommand;
    private ReactiveCommand<string?, Unit>? _applyPresetCommand;
    private ReactiveCommand<Unit, Unit>? _resetCommand;
    private ReactiveCommand<Unit, Unit>? _commitCommand;
    private ReactiveCommand<Unit, Unit>? _cancelEditCommand;

    public string NodeId { get; }
    public bool CanAssignNull { get; }
    public ReactiveCommand<Unit, Unit> SetNullCommand => _setNullCommand ??= ReactiveCommand.Create(
        () => { NullRequested?.Invoke(); CommitRequested?.Invoke(); },
        Observable.Return(CanAssignNull));
    public event Action? NullRequested;
    public string Name { get; }
    public string DisplayName => Name;
    public string PropertyType { get; }
    public bool IsReadOnly { get; }
    public string Category { get; }
    public string CategoryDisplayName => UiLabels.Category(Category);
    public string? Description { get; }
    public string? DefaultValue { get; }
    public bool IsAttached { get; }
    public string? OwnerType { get; }
    public PropertyEditorDescriptor? Descriptor { get; }
    public PropertyEditorKind EditorKind { get; }
    public bool IsCollection { get; }
    public bool IsTemplate { get; }
    public bool IsFlagsEnum { get; }
    public bool IsSignedEnum { get; }
    public IReadOnlyList<EnumFlagOptionViewModel> FlagOptions { get; }
    public string FlagSummary => Value ?? DefaultValue ?? "Default";
    public IReadOnlyList<string>? EnumOptions { get; }
    public IReadOnlyList<string>? BrushPresets { get; }
    public ReactiveCommand<string?, Unit> ApplyPresetCommand => _applyPresetCommand ??= ReactiveCommand.Create<string?>(preset =>
    {
        Value = preset;
        CommitRequested?.Invoke();
    });
    public ReactiveCommand<Unit, Unit> ResetCommand => _resetCommand ??= ReactiveCommand.Create(
        () => { Value = null; ResetRequested?.Invoke(); CommitRequested?.Invoke(); },
        this.WhenAnyValue(x => x.IsSet));
    public ReactiveCommand<Unit, Unit> CommitCommand => _commitCommand ??= ReactiveCommand.Create(() => CommitRequested?.Invoke());
    public ReactiveCommand<Unit, Unit> CancelEditCommand => _cancelEditCommand ??= ReactiveCommand.Create(() =>
    {
        Value = _committedValue;
        IsSet = _committedHasLocalValue;
        ValidationError = null;
        EditCanceled?.Invoke();
    });

    /// <summary>
    /// Direct notification hooks for the owning panel, raised when <see cref="Value" /> or
    /// <see cref="IsSet" /> actually changes. Plain delegates instead of PropertyChanged
    /// subscriptions: the panel hooks every entry on each selection change, and each
    /// subscription materializes per-object ReactiveUI event state.
    /// </summary>
    public Action<PropertyEntryViewModel>? ValueChanged { get; set; }

    /// <inheritdoc cref="ValueChanged" />
    public Action<PropertyEntryViewModel>? IsSetChanged { get; set; }

    public event Action? CommitRequested;
    public event Action? ResetRequested;
    public event Action? EditCanceled;
    public ReactiveCommand<Unit, Unit>? EditStructureCommand { get; set; }
    public bool HasStructureEditor => EditStructureCommand is not null;

    public string? ValidationError
    {
        get => _validationError;
        set
        {
            this.RaiseAndSetIfChanged(ref _validationError, value);
            this.RaisePropertyChanged(nameof(HasValidationError));
        }
    }
    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public bool IsExpression => Value?.TrimStart() is { } text && text.StartsWith('{') && !text.StartsWith("{}", StringComparison.Ordinal);
    public string ValueSourceDescription
    {
        get
        {
            if (!IsSet) return UiStrings.ValueSourceDefault;
            string? text = Value?.Trim();
            if (text == "{x:Null}") return UiStrings.ValueSourceNull;
            if (text?.StartsWith("{DynamicResource ", StringComparison.Ordinal) == true)
                return UiStrings.ValueSourceDynamic;
            if (IsExpression) return UiStrings.ValueSourceExpression;
            if (EditorKind == PropertyEditorKind.Brush && Color.TryParse(text, out Color color) && color.A == 0)
                return UiStrings.ValueSourceTransparent;
            return UiStrings.ValueSourceLocal;
        }
    }

    public string? Value
    {
        get => _value;
        set
        {
            if (_isUpdating)
            {
                return;
            }

            SetValueInternal(value);
        }
    }

    public string? CommittedValue => _committedValue;

    public bool IsSet
    {
        get => _isSet;
        private set
        {
            bool changed = _isSet != value;
            this.RaiseAndSetIfChanged(ref _isSet, value);
            this.RaisePropertyChanged(nameof(ValueSourceDescription));
            if (changed)
            {
                IsSetChanged?.Invoke(this);
            }
        }
    }

    public bool? BoolValue
    {
        get => _boolValue;
        set
        {
            if (_isUpdating)
            {
                return;
            }

            SetValueInternal(value.HasValue ? value.Value.ToString() : null);
            CommitRequested?.Invoke();
        }
    }

    public double? NumberValue
    {
        get => _numberValue;
        set
        {
            if (_isUpdating)
            {
                return;
            }

            SetValueInternal(value?.ToString(CultureInfo.InvariantCulture));
        }
    }

    public string? EnumValue
    {
        get => _enumValue;
        set
        {
            if (_isUpdating)
            {
                return;
            }

            SetValueInternal(value);
            CommitRequested?.Invoke();
        }
    }

    public Color? ColorValue
    {
        get => _colorValue;
        set
        {
            if (_isUpdating)
            {
                return;
            }

            SetValueInternal(value?.ToString());
            CommitRequested?.Invoke();
        }
    }

    public IBrush? BrushPreview
    {
        get => _brushPreview;
        private set => this.RaiseAndSetIfChanged(ref _brushPreview, value);
    }

    public void MarkCommitted(string? value)
    {
        _committedValue = value;
        _committedHasLocalValue = IsSet;
    }

    public void UpdateSourceValue(string? value, bool hasLocalValue, bool preserveDraft)
    {
        _committedValue = value;
        _committedHasLocalValue = hasLocalValue || value is not null;
        if (preserveDraft) return;
        SetValueInternal(value);
        IsSet = _committedHasLocalValue;
        ValidationError = null;
    }

    private void SetValueInternal(string? value)
    {
        bool valueChanged = !string.Equals(_value, value, StringComparison.Ordinal);
        _isUpdating = true;
        try
        {
            _value = value;
            this.RaisePropertyChanged(nameof(Value));
            this.RaisePropertyChanged(nameof(IsExpression));
            IsSet = value is not null;

            if (bool.TryParse(value, out bool parsedBool))
            {
                _boolValue = parsedBool;
            }
            else
            {
                _boolValue = null;
            }

            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedNumber))
            {
                _numberValue = parsedNumber;
            }
            else
            {
                _numberValue = null;
            }

            _enumValue = FindEnumValue(EnumOptions, value);
            ulong flags = ReadFlagBits(value ?? DefaultValue);
            foreach (EnumFlagOptionViewModel option in FlagOptions)
                option.Refresh(option.Bits == 0 ? flags == 0 : (flags & option.Bits) == option.Bits);
            this.RaisePropertyChanged(nameof(FlagSummary));

            if (Color.TryParse(value, out Color parsedColor))
            {
                _colorValue = parsedColor;
                BrushPreview = new SolidColorBrush(parsedColor);
            }
            else
            {
                _colorValue = null;
                BrushPreview = null;
            }

            this.RaisePropertyChanged(nameof(BoolValue));
            this.RaisePropertyChanged(nameof(NumberValue));
            this.RaisePropertyChanged(nameof(EnumValue));
            this.RaisePropertyChanged(nameof(ColorValue));
        }
        finally
        {
            _isUpdating = false;
        }

        if (valueChanged)
        {
            ValueChanged?.Invoke(this);
        }
    }

    private void SetFlag(EnumFlagOptionViewModel option, bool isChecked)
    {
        if (_isUpdating || IsReadOnly) return;
        ulong current = ReadFlagBits(Value ?? DefaultValue);
        ulong flags = option.Bits == 0 && isChecked ? 0
            : isChecked ? current | option.Bits : current & ~option.Bits;
        SetValueInternal(FormatFlags(flags));
        CommitRequested?.Invoke();
    }

    private ulong ReadFlagBits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        if (ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong unsigned)) return unsigned;
        if (IsSignedEnum && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long signed))
            return unchecked((ulong)signed);
        ulong result = 0;
        foreach (string name in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            EnumFlagOptionViewModel? option = FlagOptions.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
            if (option is not null) result |= option.Bits;
        }
        return result;
    }

    private string FormatFlags(ulong flags)
    {
        EnumFlagOptionViewModel? exact = FlagOptions.FirstOrDefault(option => option.Bits == flags);
        if (exact is not null) return exact.Name;
        ulong remaining = flags;
        List<string> names = new();
        foreach (EnumFlagOptionViewModel option in FlagOptions.Where(option => option.Bits != 0).OrderByDescending(option => option.Bits))
        {
            if ((remaining & option.Bits) != option.Bits) continue;
            names.Add(option.Name);
            remaining &= ~option.Bits;
        }
        if (remaining == 0 && names.Count > 0) return string.Join(", ", names);
        return IsSignedEnum ? unchecked((long)flags).ToString(CultureInfo.InvariantCulture) : flags.ToString(CultureInfo.InvariantCulture);
    }

    private static PropertyEditorKind GetEditorKind(
        string propertyName,
        string propertyType,
        IReadOnlyList<string>? enumOptions,
        PropertyEditorDescriptor? descriptor)
    {
        if (descriptor is not null)
        {
            return descriptor.Kind;
        }

        if (enumOptions is not null && enumOptions.Count > 0)
        {
            return PropertyEditorKind.Enum;
        }

        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return PropertyEditorKind.Text;
        }

        string normalizedType = NormalizeTypeName(propertyType);
        string simpleType = GetSimpleTypeName(normalizedType);

        if (IsMarkupExtensionType(normalizedType) || IsMarkupExtensionType(simpleType))
        {
            return PropertyEditorKind.MarkupExtension;
        }

        if (IsTemplateType(normalizedType) || IsTemplateType(simpleType))
        {
            return PropertyEditorKind.Template;
        }

        if (IsCollectionType(normalizedType) || IsCollectionType(simpleType))
        {
            return PropertyEditorKind.Collection;
        }

        if (IsUriType(normalizedType) || IsUriType(simpleType))
        {
            return PropertyEditorKind.Uri;
        }

        if (IsTimeSpanType(normalizedType) || IsTimeSpanType(simpleType))
        {
            return PropertyEditorKind.TimeSpan;
        }

        if (IsFontWeightType(normalizedType) || IsFontWeightType(simpleType))
        {
            return PropertyEditorKind.FontWeight;
        }

        if (IsFontStyleType(normalizedType) || IsFontStyleType(simpleType))
        {
            return PropertyEditorKind.FontStyle;
        }

        if (IsFontFamilyType(normalizedType) || IsFontFamilyType(simpleType))
        {
            return PropertyEditorKind.FontFamily;
        }

        if (IsGridLengthType(normalizedType) || IsGridLengthType(simpleType))
        {
            return PropertyEditorKind.GridLength;
        }

        if (IsRectType(normalizedType) || IsRectType(simpleType))
        {
            return PropertyEditorKind.Rect;
        }

        if (IsSizeType(normalizedType) || IsSizeType(simpleType))
        {
            return PropertyEditorKind.Size;
        }

        if (IsPointType(normalizedType) || IsPointType(simpleType))
        {
            return PropertyEditorKind.Point;
        }

        if (IsCornerRadiusType(normalizedType) || IsCornerRadiusType(simpleType))
        {
            return PropertyEditorKind.CornerRadius;
        }

        if (IsThicknessType(normalizedType) || IsThicknessType(simpleType))
        {
            return PropertyEditorKind.Thickness;
        }

        if (IsColorType(normalizedType) || IsColorType(simpleType))
        {
            return PropertyEditorKind.Color;
        }

        if (IsBrushType(normalizedType) || IsBrushType(simpleType))
        {
            return PropertyEditorKind.Brush;
        }

        if (BooleanTypeNames.Contains(normalizedType) || BooleanTypeNames.Contains(simpleType))
        {
            return PropertyEditorKind.Boolean;
        }

        if (NumericTypeNames.Contains(normalizedType) || NumericTypeNames.Contains(simpleType))
        {
            return PropertyEditorKind.Number;
        }

        if (IsThicknessPropertyName(propertyName))
        {
            return PropertyEditorKind.Thickness;
        }

        if (IsCornerRadiusPropertyName(propertyName))
        {
            return PropertyEditorKind.CornerRadius;
        }

        if (IsBrushPropertyName(propertyName))
        {
            return PropertyEditorKind.Brush;
        }

        if (IsBooleanPropertyName(propertyName))
        {
            return PropertyEditorKind.Boolean;
        }

        if (IsNumericPropertyName(propertyName))
        {
            return PropertyEditorKind.Number;
        }

        return PropertyEditorKind.Text;
    }

    private static bool IsColorType(string propertyType)
    {
        return IsKnownType(propertyType, "Color", "System.Drawing.Color", "Avalonia.Media.Color");
    }

    private static bool IsBrushType(string propertyType)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return false;
        }

        string normalized = propertyType.Trim();
        if (normalized.Equals("Brush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("IBrush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("SolidColorBrush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("ImmutableSolidColorBrush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Avalonia.Media.IBrush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Avalonia.Media.Brush", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Avalonia.Media.SolidColorBrush", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("Brush", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsBooleanPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName switch
        {
            "IsVisible" or "ClipToBounds" or "IsEnabled" or "IsHitTestVisible"
                or "IsChecked" or "IsReadOnly" or "AcceptsReturn" or "AcceptsTab"
                or "ShowButtonSpinner" or "IsThreeState" or "CanDrag"
                or "AllowAutoHide" or "IsDefault" or "IsCancel"
                => true,
            _ => false
        };
    }

    private static bool IsNumericPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName switch
        {
            "Width" or "Height" or "MinWidth" or "MinHeight" or "MaxWidth" or "MaxHeight"
                or "FontSize" or "Opacity" or "Spacing" or "Row" or "Column"
                or "RowSpan" or "ColumnSpan" or "Canvas.Left" or "Canvas.Top"
                or "Canvas.Right" or "Canvas.Bottom" or "ZIndex"
                or "SelectedIndex" or "Minimum" or "Maximum" or "Value"
                or "Increment" or "TabIndex"
                => true,
            _ => false
        };
    }

    private static bool IsBrushPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName switch
        {
            "Background" or "Foreground" or "BorderBrush" or "Fill" or "Stroke"
                or "OpacityMask" or "CaretBrush" or "SelectionBrush"
                => true,
            _ => false
        };
    }

    private static bool IsThicknessPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName is "Margin" or "Padding" or "BorderThickness";
    }

    private static bool IsCornerRadiusPropertyName(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return false;
        }

        return propertyName.Equals("CornerRadius", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string>? ResolveWellKnownEnumOptions(string propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            return null;
        }

        return WellKnownEnumOptions.TryGetValue(propertyName, out IReadOnlyList<string>? options)
            ? options
            : null;
    }

    private static bool IsThicknessType(string propertyType)
    {
        return IsKnownType(propertyType, "Thickness", "Avalonia.Thickness");
    }

    private static bool IsCornerRadiusType(string propertyType)
    {
        return IsKnownType(propertyType, "CornerRadius", "Avalonia.CornerRadius");
    }

    private static bool IsPointType(string propertyType)
    {
        return IsKnownType(propertyType, "Point", "Avalonia.Point");
    }

    private static bool IsSizeType(string propertyType)
    {
        return IsKnownType(propertyType, "Size", "Avalonia.Size");
    }

    private static bool IsRectType(string propertyType)
    {
        return IsKnownType(propertyType, "Rect", "Avalonia.Rect");
    }

    private static bool IsGridLengthType(string propertyType)
    {
        return IsKnownType(propertyType, "GridLength", "Avalonia.Controls.GridLength");
    }

    private static bool IsFontFamilyType(string propertyType)
    {
        return IsKnownType(propertyType, "FontFamily", "Avalonia.Media.FontFamily");
    }

    private static bool IsFontWeightType(string propertyType)
    {
        return IsKnownType(propertyType, "FontWeight", "Avalonia.Media.FontWeight");
    }

    private static bool IsFontStyleType(string propertyType)
    {
        return IsKnownType(propertyType, "FontStyle", "Avalonia.Media.FontStyle");
    }

    private static bool IsTimeSpanType(string propertyType)
    {
        return IsKnownType(propertyType, "TimeSpan", "System.TimeSpan");
    }

    private static bool IsUriType(string propertyType)
    {
        return IsKnownType(propertyType, "Uri", "System.Uri");
    }

    private static bool IsCollectionType(string propertyType)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return false;
        }

        string normalized = propertyType.Trim();
        return normalized.Contains("IList", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("ICollection", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("IEnumerable", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Collection", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("[]", StringComparison.Ordinal)
            || normalized.Contains("Dictionary", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("System.Collections.", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTemplateType(string propertyType)
    {
        return IsKnownType(propertyType, "ControlTemplate", "DataTemplate", "ITemplate", "Avalonia.Controls.Templates.IDataTemplate");
    }

    private static bool IsMarkupExtensionType(string propertyType)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return false;
        }

        string normalized = propertyType.Trim();
        return normalized.Contains("MarkupExtension", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Binding", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CompiledBinding", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("DynamicResource", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("StaticResource", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsKnownType(string propertyType, params string[] candidates)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return false;
        }

        string normalized = propertyType.Trim();
        for (int i = 0; i < candidates.Length; i++)
        {
            if (normalized.Equals(candidates[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetSimpleTypeName(string propertyType)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return string.Empty;
        }

        string normalized = propertyType.Trim();
        if (normalized.EndsWith("[]", StringComparison.Ordinal))
        {
            normalized = normalized[..^2];
        }

        int genericTick = normalized.IndexOf('`');
        if (genericTick > 0)
        {
            normalized = normalized[..genericTick];
        }

        int genericStart = normalized.IndexOf('<');
        if (genericStart > 0)
        {
            normalized = normalized[..genericStart];
        }

        int lastDot = normalized.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < normalized.Length - 1)
        {
            normalized = normalized[(lastDot + 1)..];
        }

        int lastPlus = normalized.LastIndexOf('+');
        if (lastPlus >= 0 && lastPlus < normalized.Length - 1)
        {
            normalized = normalized[(lastPlus + 1)..];
        }

        return normalized;
    }

    private static string NormalizeTypeName(string propertyType)
    {
        if (string.IsNullOrWhiteSpace(propertyType))
        {
            return string.Empty;
        }

        string normalized = propertyType.Trim();
        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized["global::".Length..];
        }

        normalized = normalized.TrimEnd('?');
        if (normalized.StartsWith("System.Nullable<", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(">", StringComparison.Ordinal))
        {
            normalized = normalized["System.Nullable<".Length..^1].Trim();
        }
        else if (normalized.StartsWith("Nullable<", StringComparison.OrdinalIgnoreCase)
                 && normalized.EndsWith(">", StringComparison.Ordinal))
        {
            normalized = normalized["Nullable<".Length..^1].Trim();
        }
        else if (normalized.StartsWith("System.Nullable`1", StringComparison.OrdinalIgnoreCase)
                 || normalized.StartsWith("Nullable`1", StringComparison.OrdinalIgnoreCase))
        {
            int start = normalized.IndexOf('[');
            int end = normalized.LastIndexOf(']');
            if (start >= 0 && end > start)
            {
                string inner = normalized[(start + 1)..end].Trim();
                inner = inner.Trim('[', ']');
                int comma = inner.IndexOf(',');
                if (comma > 0)
                {
                    inner = inner[..comma];
                }

                if (!string.IsNullOrWhiteSpace(inner))
                {
                    normalized = inner.Trim();
                }
            }
        }

        if (normalized.StartsWith("global::", StringComparison.Ordinal))
        {
            normalized = normalized["global::".Length..];
        }

        return normalized.Trim();
    }

    private static string? FindEnumValue(IReadOnlyList<string>? options, string? value)
    {
        if (options is null || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        for (int i = 0; i < options.Count; i++)
        {
            string option = options[i];
            if (string.Equals(option, value, StringComparison.OrdinalIgnoreCase))
            {
                return option;
            }
        }

        return null;
    }
}

public sealed class EventEntryViewModel : ReactiveObject
{
    private string? _handlerName;
    private string? _committedHandler;
    private string? _validationError;
    private bool _isApplying;

    public EventEntryViewModel(string nodeId, string name, string? handlerName, string? description)
    {
        NodeId = nodeId;
        Name = name;
        Description = description;
        _handlerName = handlerName;
        _committedHandler = handlerName;
    }

    // Created on first use; see PropertyEntryViewModel for the rationale.
    private ReactiveCommand<Unit, Unit>? _commitCommand;
    private ReactiveCommand<Unit, Unit>? _cancelEditCommand;
    private ReactiveCommand<Unit, Unit>? _resetCommand;
    private ReactiveCommand<Unit, Unit>? _navigateCommand;

    public string NodeId { get; }
    public string Name { get; }
    public string? Description { get; }

    /// <summary>
    /// Direct notification hook for the owning panel, raised when <see cref="HandlerName" />
    /// actually changes; see <see cref="PropertyEntryViewModel.ValueChanged" /> for the rationale.
    /// </summary>
    public Action<EventEntryViewModel>? HandlerNameChanged { get; set; }

    public string? HandlerName
    {
        get => _handlerName;
        set
        {
            bool changed = !string.Equals(_handlerName, value, StringComparison.Ordinal);
            this.RaiseAndSetIfChanged(ref _handlerName, value);
            if (changed)
            {
                HandlerNameChanged?.Invoke(this);
            }
        }
    }

    public bool IsApplying
    {
        get => _isApplying;
        internal set => this.RaiseAndSetIfChanged(ref _isApplying, value);
    }

    public string? ValidationError
    {
        get => _validationError;
        set
        {
            this.RaiseAndSetIfChanged(ref _validationError, value);
            this.RaisePropertyChanged(nameof(HasValidationError));
        }
    }

    public bool HasValidationError => !string.IsNullOrEmpty(ValidationError);
    public ReactiveCommand<Unit, Unit> CommitCommand => _commitCommand ??=
        ReactiveCommand.Create(() => CommitRequested?.Invoke(), CanEdit());
    public ReactiveCommand<Unit, Unit> CancelEditCommand => _cancelEditCommand ??= ReactiveCommand.Create(() =>
    {
        RestoreCommitted();
        ValidationError = null;
        EditCanceled?.Invoke();
    }, CanEdit());
    public ReactiveCommand<Unit, Unit> ResetCommand => _resetCommand ??= ReactiveCommand.Create(() =>
    {
        HandlerName = null;
        CommitRequested?.Invoke();
    }, CanEdit());
    public ReactiveCommand<Unit, Unit> NavigateCommand => _navigateCommand ??= ReactiveCommand.Create(
        () => NavigateRequested?.Invoke(),
        this.WhenAnyValue(entry => entry.HandlerName, entry => entry.IsApplying,
            (handler, applying) => !applying && !string.IsNullOrWhiteSpace(handler)));

    private IObservable<bool> CanEdit() => this.WhenAnyValue(entry => entry.IsApplying).Select(applying => !applying);
    public event Action? CommitRequested;
    public event Action? EditCanceled;
    public event Action? NavigateRequested;

    public string? CommittedHandler => _committedHandler;

    public void MarkCommitted(string? value)
    {
        _committedHandler = value;
    }

    public void UpdateSourceValue(string? value, bool preserveDraft)
    {
        _committedHandler = value;
        if (!preserveDraft)
        {
            HandlerName = value;
            ValidationError = null;
        }
    }

    public void RestoreCommitted() => HandlerName = _committedHandler;
}

public sealed class PropertyRowViewModel : ReactiveObject
{
    public PropertyRowViewModel(PropertyEntryViewModel property, string groupName)
    {
        Property = property;
        GroupName = groupName;
    }

    public PropertyRowViewModel(string groupName, bool isExpanded, bool canCollapse, Action<string, bool>? setExpanded)
    {
        GroupName = groupName;
        _isExpanded = isExpanded;
        CanCollapse = canCollapse;
        _setExpanded = setExpanded;
    }

    private readonly Action<string, bool>? _setExpanded;
    private bool _isExpanded;

    public PropertyEntryViewModel? Property { get; }

    public string GroupName { get; }
    public string GroupLabel => UiLabels.Category(GroupName);
    public bool IsHeader => Property is null;
    public bool CanCollapse { get; }
    public bool IsPinned => IsHeader && !CanCollapse;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            this.RaiseAndSetIfChanged(ref _isExpanded, value);
            _setExpanded?.Invoke(GroupName, value);
        }
    }
}

/// <summary>Eine auswählbare Kategorie im Eigenschaftenfenster.</summary>
public sealed class PropertyCategoryOption(string? value, string label)
{
    public string? Value { get; } = value;
    public string Label { get; } = label;
}

public sealed class PropertyEditorPanelViewModel : ReactiveObject, IDisposable
{
    private const string CollapseCategoriesSetting = "propertyEditor.collapseOtherCategories";
    private readonly IDesignerHost _designer;
    private readonly IPropertyEditorRegistry _propertyEditors;
    private readonly ISettings? _settings;
    private readonly Dictionary<string, PropertyEditorDescriptor?> _descriptorCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly CompositeDisposable _disposables = new();
    private readonly CompositeDisposable _entrySubscriptions = new();
    private readonly CompositeDisposable _eventSubscriptions = new();
    private readonly BulkObservableCollection<PropertyEntryViewModel> _properties = new();
    private readonly BulkObservableCollection<PropertyRowViewModel> _groupedRows = new();
    private readonly Dictionary<string, bool> _expandedCategories = new(StringComparer.OrdinalIgnoreCase);
    private readonly BulkObservableCollection<EventEntryViewModel> _events = new();
    private readonly Dictionary<string, PendingPropertyValue> _pendingPropertyValues = new(StringComparer.Ordinal);
    private sealed record PendingPropertyValue(string DocumentPath, string? Value, string? Error,
        bool ForceReset = false, bool ForceNull = false);
    private readonly Dictionary<string, PendingPropertyCommit> _propertyCommits = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _propertyCommitGate = new(1, 1);
    private sealed record PendingPropertyCommit(CancellationTokenSource Cancellation, PendingPropertyValue Draft);
    private bool _isDisposed;
    private readonly Dictionary<string, PendingEventHandler> _pendingEventHandlers = new(StringComparer.Ordinal);
    private sealed record PendingEventHandler(string DocumentPath, string? Value, string? Error);
    private CancellationTokenSource? _loadCts;
    private string? _selectedTypeName;
    private string? _selectedNodeId;
    private string[] _secondaryNodeIds = [];
    private string? _searchText;
    private PropertyCategoryOption? _selectedCategory;
    private bool _updatingCategories;
    private string? _statusText;
    private int _suspendUpdates;
    private bool _showProperties = true;
    private bool _showGroupedView = true;
    private bool _collapseOtherCategoriesOnSelection = true;
    private bool _showLocalValuesOnly;
    private bool _groupedViewDirty = true;
    private string _lastSelectionKey = string.Empty;
    private string _lastActiveDocumentPath = string.Empty;
    private DateTime _lastSelectionUpdateUtc = DateTime.MinValue;
    private static readonly TimeSpan SelectionPollInterval = TimeSpan.FromMilliseconds(500);
    private static string EssentialPropertiesGroupName => UiStrings.EssentialProperties;
    private static string LocalValuesGroupName => UiStrings.LocalValues;
    private static readonly IReadOnlyDictionary<string, int> CategorySortOrder = new Dictionary<string, int>(
        StringComparer.OrdinalIgnoreCase)
    {
        ["Layout"] = 0,
        ["Appearance"] = 1,
        ["Text"] = 2,
        ["Common"] = 3,
        ["Miscellaneous"] = 4,
        ["Misc"] = 5
    };

    public PropertyEditorPanelViewModel(IDesignerHost designer, IPropertyEditorRegistry propertyEditors,
        ISettings? settings = null)
    {
        _designer = designer;
        _propertyEditors = propertyEditors;
        _settings = settings;
        _collapseOtherCategoriesOnSelection = settings?.Get(CollapseCategoriesSetting, true) ?? true;
        PropertiesView = new DataGridCollectionView(_properties)
        {
            Filter = FilterProperty
        };
        GroupedPropertiesView = new DataGridCollectionView(_groupedRows);
        EventsView = new DataGridCollectionView(_events)
        {
            Filter = FilterEvent
        };
        IObservable<bool> canExpandCategories = this.WhenAnyValue(
            x => x.ShowProperties, x => x.ShowGroupedView, x => x.ShowLocalValuesOnly, x => x.SearchText,
            (showProperties, grouped, localOnly, search) =>
                showProperties && grouped && !localOnly && string.IsNullOrWhiteSpace(search));
        CollapseAllCategoriesCommand = ReactiveCommand.Create(() => SetAllCategoriesExpanded(false), canExpandCategories);
        ExpandAllCategoriesCommand = ReactiveCommand.Create(() => SetAllCategoriesExpanded(true), canExpandCategories);
        _disposables.Add(CollapseAllCategoriesCommand);
        _disposables.Add(ExpandAllCategoriesCommand);
        if (designer is IDesignerDocumentNotifications notifications)
        {
            EventHandler<DesignerDocumentChangedEventArgs> changed = (_, args) =>
                RunBackground(HandleDocumentChangedAsync(args.DocumentPath, CancellationToken.None));
            notifications.DocumentChanged += changed;
            _disposables.Add(Disposable.Create(() => notifications.DocumentChanged -= changed));
        }

        _disposables.Add(this.WhenAnyValue(x => x.SearchText)
            .Subscribe(_ =>
            {
                PropertiesView.Refresh();
                EventsView.Refresh();
                RequestGroupedRefresh();
            }));
        Categories.Add(new PropertyCategoryOption(null, UiStrings.AllCategories));
        _selectedCategory = Categories[0];
        _disposables.Add(this.WhenAnyValue(x => x.SelectedCategory)
            .Skip(1)
            .Subscribe(_ =>
            {
                if (_updatingCategories) return;
                PropertiesView.Refresh();
                RequestGroupedRefresh();
            }));

        _disposables.Add(this.WhenAnyValue(x => x.ShowLocalValuesOnly)
            .Subscribe(_ =>
            {
                PropertiesView.Refresh();
                RequestGroupedRefresh();
            }));

        _disposables.Add(this.WhenAnyValue(x => x.ShowGroupedView)
            .Subscribe(showGrouped =>
            {
                if (showGrouped && _groupedViewDirty)
                {
                    RebuildGroupedView();
                }
            }));

        _disposables.Add(Observable.Interval(SelectionPollInterval, RxSchedulers.TaskpoolScheduler)
            .Where(_ => !string.IsNullOrWhiteSpace(_designer.ActiveDocumentPath))
            .Where(_ => DateTime.UtcNow - _lastSelectionUpdateUtc >= SelectionPollInterval)
            .SelectMany(_ => Observable.FromAsync(PollSelectedNodesAsync))
            .ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(nodes =>
            {
                string activeDocumentPath = _designer.ActiveDocumentPath ?? _lastActiveDocumentPath;
                string selectionKey = BuildSelectionKey(activeDocumentPath, nodes);
                if (string.Equals(selectionKey, _lastSelectionKey, StringComparison.Ordinal))
                {
                    return;
                }

                RunBackground(UpdateSelectionAsync(nodes, CancellationToken.None, activeDocumentPath: activeDocumentPath));
            }));
    }

    public DataGridCollectionView PropertiesView { get; }
    public DataGridCollectionView GroupedPropertiesView { get; }
    public ReactiveCommand<Unit, Unit> CollapseAllCategoriesCommand { get; }
    public ReactiveCommand<Unit, Unit> ExpandAllCategoriesCommand { get; }
    /// <summary>Die Kategorien des ausgewählten Controls.</summary>
    public ObservableCollection<PropertyCategoryOption> Categories { get; } = new();

    /// <summary>Begrenzt die Eigenschaften auf eine Kategorie.</summary>
    public PropertyCategoryOption? SelectedCategory
    {
        get => _selectedCategory;
        set => this.RaiseAndSetIfChanged(ref _selectedCategory, value);
    }
    public DataGridCollectionView EventsView { get; }
    public Interaction<PropertyStructureEditorViewModel, Unit> EditStructureInteraction { get; } = new();
    public string? StatusText { get => _statusText; private set => this.RaiseAndSetIfChanged(ref _statusText, value); }

    public string? SelectedTypeName
    {
        get => _selectedTypeName;
        private set => this.RaiseAndSetIfChanged(ref _selectedTypeName, value);
    }

    public string? SelectedNodeId
    {
        get => _selectedNodeId;
        private set => this.RaiseAndSetIfChanged(ref _selectedNodeId, value);
    }

    public string? SearchText
    {
        get => _searchText;
        set => this.RaiseAndSetIfChanged(ref _searchText, value);
    }

    public bool ShowProperties
    {
        get => _showProperties;
        set => this.RaiseAndSetIfChanged(ref _showProperties, value);
    }

    public bool ShowGroupedView
    {
        get => _showGroupedView;
        set => this.RaiseAndSetIfChanged(ref _showGroupedView, value);
    }

    /// <summary>Legt fest, ob andere Kategorien bei einer neuen Auswahl zunächst geschlossen sind.</summary>
    public bool CollapseOtherCategoriesOnSelection
    {
        get => _collapseOtherCategoriesOnSelection;
        set
        {
            if (_collapseOtherCategoriesOnSelection == value) return;
            this.RaiseAndSetIfChanged(ref _collapseOtherCategoriesOnSelection, value);
            _expandedCategories.Clear();
            RequestGroupedRefresh();
            if (_settings is not null)
                RunBackground(_settings.UpdateAsync(CollapseCategoriesSetting, value, SettingsTarget.User, CancellationToken.None));
        }
    }

    public bool ShowLocalValuesOnly
    {
        get => _showLocalValuesOnly;
        set => this.RaiseAndSetIfChanged(ref _showLocalValuesOnly, value);
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<DesignerNodeSummary> selected = await _designer.GetSelectedNodesAsync(cancellationToken);
        string activeDocumentPath = _designer.ActiveDocumentPath ?? string.Empty;
        _lastActiveDocumentPath = activeDocumentPath;
        _lastSelectionUpdateUtc = DateTime.UtcNow;
        _lastSelectionKey = BuildSelectionKey(activeDocumentPath, selected);
        await UpdateSelectionAsync(selected, cancellationToken, forceReload: true, activeDocumentPath: activeDocumentPath);
    }

    public async Task HandleSelectionChangedAsync(
        IReadOnlyList<DesignerNodeSummary> selectedNodes,
        CancellationToken cancellationToken)
    {
        string activeDocumentPath = _designer.ActiveDocumentPath ?? _lastActiveDocumentPath;
        if (selectedNodes.Count == 0 && !string.IsNullOrWhiteSpace(activeDocumentPath))
        {
            try
            {
                IReadOnlyList<DesignerNodeSummary> refreshed = await _designer.GetSelectedNodesAsync(cancellationToken);
                if (refreshed.Count > 0)
                {
                    selectedNodes = refreshed;
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        await UpdateSelectionAsync(selectedNodes, cancellationToken, activeDocumentPath: activeDocumentPath);
    }

    public async Task HandleDocumentChangedAsync(string? documentPath, CancellationToken cancellationToken)
    {
        string normalizedDocumentPath = documentPath ?? string.Empty;
        _lastActiveDocumentPath = normalizedDocumentPath;

        IReadOnlyList<DesignerNodeSummary> selectedNodes = await _designer.GetSelectedNodesAsync(cancellationToken);
        await UpdateSelectionAsync(
            selectedNodes,
            cancellationToken,
            forceReload: true,
            activeDocumentPath: normalizedDocumentPath);
    }

    public async Task UpdateSelectionAsync(
        IReadOnlyList<DesignerNodeSummary> selectedNodes,
        CancellationToken cancellationToken,
        bool forceReload = false,
        string? activeDocumentPath = null)
    {
        _lastActiveDocumentPath = activeDocumentPath ?? _designer.ActiveDocumentPath ?? _lastActiveDocumentPath;
        _lastSelectionUpdateUtc = DateTime.UtcNow;
        string selectionKey = BuildSelectionKey(_lastActiveDocumentPath, selectedNodes);
        if (!forceReload && string.Equals(selectionKey, _lastSelectionKey, StringComparison.Ordinal))
        {
            return;
        }

        bool selectionChanged = !string.Equals(_lastSelectionKey, selectionKey, StringComparison.Ordinal);
        _lastSelectionKey = selectionKey;

        if (_isDisposed) return;
        CancellationTokenSource? previous = _loadCts;
        _loadCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        previous?.Cancel();
        previous?.Dispose();

        CancellationToken token = _loadCts.Token;
        try
        {
            _suspendUpdates++;
            if (selectedNodes.Count == 0)
            {
                SelectedNodeId = null;
                SelectedTypeName = null;
                _secondaryNodeIds = [];
                ClearProperties();
                ClearEvents();
                StatusText = null;
                return;
            }

            DesignerNodeSummary node = selectedNodes[0];
            SelectedNodeId = node.NodeId;
            SelectedTypeName = selectedNodes.Count == 1 ? node.TypeName
                : $"{string.Join(", ", selectedNodes.Select(selected => selected.TypeName).Distinct(StringComparer.Ordinal))} ({selectedNodes.Count})";
            if (selectionChanged)
            {
                _expandedCategories.Clear();
                ClearProperties();
                ClearEvents();
                StatusText = UiStrings.LoadingPropertiesEvents;
            }

            Task<IReadOnlyList<DesignerPropertyInfo>> propertiesTask = _designer.GetPropertiesAsync(node.NodeId, token);
            Task<IReadOnlyList<DesignerEventInfo>> eventsTask = _designer.GetEventsAsync(node.NodeId, token);
            string[] secondaryNodeIds = selectedNodes.Skip(1).Select(selected => selected.NodeId).ToArray();
            Task<IReadOnlyList<DesignerPropertyInfo>>[] secondaryTasks = secondaryNodeIds
                .Select(nodeId => _designer.GetPropertiesAsync(nodeId, token)).ToArray();
            await Task.WhenAll(secondaryTasks.Append<Task>(propertiesTask).Append(eventsTask));
            token.ThrowIfCancellationRequested();
            if (_isDisposed) return;
            // A multi-selection shows the properties shared by all selected nodes and applies edits to each of them.
            IReadOnlyList<DesignerPropertyInfo> properties = secondaryTasks.Length == 0 ? propertiesTask.Result
                : MergeSelectionProperties(propertiesTask.Result, secondaryTasks.Select(task => task.Result).ToArray());
            IReadOnlyList<DesignerEventInfo> events = secondaryTasks.Length == 0 ? eventsTask.Result : [];
            _secondaryNodeIds = secondaryNodeIds;

            UpdatePropertyEntries(node, properties);
            RefreshCategoryChoices();
            UpdateEventEntries(node, events);
            PropertiesView.Refresh();
            EventsView.Refresh();
            RequestGroupedRefresh();
            PropertyEntryViewModel? propertyError = _properties.FirstOrDefault(entry => entry.HasValidationError);
            EventEntryViewModel? eventError = _events.FirstOrDefault(entry => entry.HasValidationError);
            StatusText = propertyError is not null ? $"{propertyError.Name}: {propertyError.ValidationError}"
                : eventError is not null ? $"{eventError.Name}: {eventError.ValidationError}" : null;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            if (token.IsCancellationRequested || _isDisposed) return;
            ClearProperties();
            ClearEvents();
            StatusText = string.Format(UiStrings.InspectSelectionFailed, (object?)(SelectedTypeName ?? UiStrings.Selection), (object?)(_lastActiveDocumentPath), (object?)(exception.Message));
        }
        finally
        {
            _suspendUpdates = Math.Max(0, _suspendUpdates - 1);
        }
    }

    public void Dispose()
    {
        _isDisposed = true;
        foreach (PendingPropertyCommit commit in _propertyCommits.Values.ToArray())
        {
            commit.Cancellation.Cancel();
        }

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _entrySubscriptions.Dispose();
        _eventSubscriptions.Dispose();
        _disposables.Dispose();
    }

    private void ClearProperties()
    {
        _entrySubscriptions.Clear();
        _properties.Clear();
        _groupedRows.Clear();
        Categories.Clear();
        Categories.Add(new PropertyCategoryOption(null, UiStrings.AllCategories));
        SelectedCategory = Categories[0];
        _groupedViewDirty = true;
    }

    private void RefreshCategoryChoices()
    {
        string? selected = SelectedCategory?.Value;
        _updatingCategories = true;
        try
        {
            Categories.Clear();
            Categories.Add(new PropertyCategoryOption(null, UiStrings.AllCategories));
            foreach (var category in _properties.GroupBy(entry => string.IsNullOrWhiteSpace(entry.Category) ? "Misc" : entry.Category,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => GetCategorySortKey(group.Key))
                     .ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase))
                Categories.Add(new PropertyCategoryOption(category.Key,
                    $"{UiLabels.Category(category.Key)} ({category.Count()})"));
            SelectedCategory = Categories.FirstOrDefault(category => category.Value == selected) ?? Categories[0];
        }
        finally
        {
            _updatingCategories = false;
        }
    }

    private void ClearEvents()
    {
        _eventSubscriptions.Clear();
        _events.Clear();
    }

    private void UpdatePropertyEntries(DesignerNodeSummary node, IReadOnlyList<DesignerPropertyInfo> properties)
    {
        Dictionary<string, PropertyEntryViewModel> existing = new(StringComparer.OrdinalIgnoreCase);
        foreach (PropertyEntryViewModel entry in _properties)
        {
            if (string.Equals(entry.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                existing[BuildPropertyEntryKey(entry.Name, entry.OwnerType)] = entry;
            }
        }

        List<PropertyEntryViewModel> ordered = new(properties.Count);
        bool structureChanged = _properties.Count != properties.Count;
        _entrySubscriptions.Clear();

        foreach (DesignerPropertyInfo property in properties)
        {
            string propertyKey = BuildPropertyEntryKey(property.Name, property.OwnerType);
            if (existing.TryGetValue(propertyKey, out PropertyEntryViewModel? reused)
                && CanReusePropertyEntry(reused, node.NodeId, property))
            {
                existing.Remove(propertyKey);
                reused.UpdateSourceValue(property.Value, property.HasLocalValue, _pendingPropertyValues.ContainsKey(GetDraftKey(reused)));
                ordered.Add(reused);
                continue;
            }

            structureChanged = true;
            ordered.Add(CreatePropertyEntry(node.NodeId, property));
        }

        ordered = ordered.OrderBy(entry => EssentialRank(entry.Name)).ToList();
        if (existing.Count > 0 || !AreSameEntrySequence(_properties, ordered))
        {
            structureChanged = true;
        }

        if (structureChanged)
        {
            _properties.ReplaceAll(ordered);
        }

        foreach (PropertyEntryViewModel entry in _properties)
        {
            HookPropertyEntry(entry);
        }
    }

    private void UpdateEventEntries(DesignerNodeSummary node, IReadOnlyList<DesignerEventInfo> events)
    {
        Dictionary<string, EventEntryViewModel> existing = new(StringComparer.OrdinalIgnoreCase);
        foreach (EventEntryViewModel entry in _events)
        {
            if (string.Equals(entry.NodeId, node.NodeId, StringComparison.OrdinalIgnoreCase))
            {
                existing[entry.Name] = entry;
            }
        }

        List<EventEntryViewModel> ordered = new(events.Count);
        bool structureChanged = _events.Count != events.Count;
        _eventSubscriptions.Clear();

        foreach (DesignerEventInfo evt in events)
        {
            if (existing.TryGetValue(evt.Name, out EventEntryViewModel? reused)
                && CanReuseEventEntry(reused, node.NodeId, evt))
            {
                existing.Remove(evt.Name);
                UpdateEventSource(reused, evt.HandlerName);
                ordered.Add(reused);
                continue;
            }

            structureChanged = true;
            EventEntryViewModel created = new(node.NodeId, evt.Name, evt.HandlerName, evt.Description);
            UpdateEventSource(created, evt.HandlerName);
            ordered.Add(created);
        }

        if (existing.Count > 0 || !AreSameEntrySequence(_events, ordered))
        {
            structureChanged = true;
        }

        if (structureChanged)
        {
            _events.ReplaceAll(ordered);
        }

        foreach (EventEntryViewModel entry in _events)
        {
            HookEventEntry(entry);
        }
    }

    private PropertyEntryViewModel CreatePropertyEntry(string nodeId, DesignerPropertyInfo property)
    {
        PropertyEntryViewModel entry = new(
            nodeId,
            property.Name,
            property.PropertyType,
            property.Value,
            property.IsReadOnly,
            property.Category,
            property.Description,
            property.DefaultValue,
            property.IsAttached,
            property.OwnerType,
            property.EnumOptions,
            ResolveDescriptor(property.Name, property.PropertyType),
            property.IsCollection,
            property.IsFlagsEnum,
            property.IsSignedEnum,
            property.EnumValues,
            property.HasLocalValue,
            property.IsTemplate,
            property.CanAssignNull);
        if (_designer is IDesignerStructureHost && entry.EditorKind is PropertyEditorKind.Collection or PropertyEditorKind.Template)
            entry.EditStructureCommand = ReactiveCommand.CreateFromTask(() => OpenStructureEditorAsync(entry));
        if (_pendingPropertyValues.TryGetValue(GetDraftKey(entry), out PendingPropertyValue? draft))
        {
            entry.Value = draft.Value;
            entry.ValidationError = draft.Error;
        }
        return entry;
    }

    private async Task OpenStructureEditorAsync(PropertyEntryViewModel entry)
    {
        try
        {
            StatusText = null;
            if (_designer is not IDesignerStructureHost host) return;
            CancellationToken token = _loadCts?.Token ?? CancellationToken.None;
            DesignerPropertyStructure snapshot = await host.OpenPropertyStructureAsync(entry.NodeId, entry.Name, token);
            token.ThrowIfCancellationRequested();
            using PropertyStructureEditorViewModel editor = new(host, snapshot);
            await EditStructureInteraction.Handle(editor);
            IReadOnlyList<DesignerNodeSummary> selection = await _designer.GetSelectedNodesAsync(CancellationToken.None);
            await UpdateSelectionAsync(selection, CancellationToken.None, forceReload: true);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { StatusText = exception.Message; }
    }

    private static bool CanReusePropertyEntry(PropertyEntryViewModel entry, string nodeId, DesignerPropertyInfo property)
    {
        return string.Equals(entry.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Name, property.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.PropertyType, property.PropertyType, StringComparison.OrdinalIgnoreCase)
            && entry.IsReadOnly == property.IsReadOnly
            && entry.IsAttached == property.IsAttached
            && entry.IsCollection == property.IsCollection
            && entry.IsTemplate == property.IsTemplate
            && entry.CanAssignNull == (property.CanAssignNull && !property.IsReadOnly)
            && entry.IsFlagsEnum == property.IsFlagsEnum
            && entry.IsSignedEnum == property.IsSignedEnum
            && (property.EnumValues is null ? entry.FlagOptions.Count == 0
                : entry.FlagOptions.Count == property.EnumValues.Count
                    && entry.FlagOptions.All(option => property.EnumValues.TryGetValue(option.Name, out ulong bits) && bits == option.Bits))
            && string.Equals(entry.OwnerType, property.OwnerType, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Category, property.Category ?? "Misc", StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanReuseEventEntry(EventEntryViewModel entry, string nodeId, DesignerEventInfo evt)
    {
        return string.Equals(entry.NodeId, nodeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Name, evt.Name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(entry.Description, evt.Description, StringComparison.Ordinal);
    }

    private static string BuildPropertyEntryKey(string name, string? ownerType)
    {
        string owner = string.IsNullOrWhiteSpace(ownerType) ? string.Empty : ownerType.Trim();
        return owner + "|" + name;
    }

    /// <summary>
    /// Keeps the primary node's properties that every other selected node exposes with the same type. Names and
    /// structured values stay per node. A value that differs between the nodes is shown as not set.
    /// </summary>
    private static IReadOnlyList<DesignerPropertyInfo> MergeSelectionProperties(
        IReadOnlyList<DesignerPropertyInfo> primary, IReadOnlyList<DesignerPropertyInfo>[] secondary)
    {
        Dictionary<string, DesignerPropertyInfo>[] secondaryMaps = new Dictionary<string, DesignerPropertyInfo>[secondary.Length];
        for (int i = 0; i < secondary.Length; i++)
        {
            secondaryMaps[i] = new Dictionary<string, DesignerPropertyInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (DesignerPropertyInfo property in secondary[i])
            {
                secondaryMaps[i].TryAdd(BuildPropertyEntryKey(property.Name, property.OwnerType), property);
            }
        }

        List<DesignerPropertyInfo> merged = new(primary.Count);
        foreach (DesignerPropertyInfo property in primary)
        {
            if (property.IsCollection || property.IsTemplate || property.Name is "Name" or "x:Name")
            {
                continue;
            }

            string key = BuildPropertyEntryKey(property.Name, property.OwnerType);
            bool shared = true;
            bool mixed = false;
            foreach (Dictionary<string, DesignerPropertyInfo> map in secondaryMaps)
            {
                if (!map.TryGetValue(key, out DesignerPropertyInfo? other) || other.IsReadOnly != property.IsReadOnly
                    || !string.Equals(other.PropertyType, property.PropertyType, StringComparison.OrdinalIgnoreCase))
                {
                    shared = false;
                    break;
                }

                mixed |= other.HasLocalValue != property.HasLocalValue
                    || !string.Equals(other.Value, property.Value, StringComparison.Ordinal);
            }

            if (shared)
            {
                merged.Add(mixed ? property with { Value = null, HasLocalValue = false } : property);
            }
        }

        return merged;
    }

    private static bool AreSameEntrySequence<T>(IList<T> current, IList<T> next)
        where T : class
    {
        if (current.Count != next.Count)
        {
            return false;
        }

        for (int i = 0; i < current.Count; i++)
        {
            if (!ReferenceEquals(current[i], next[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string BuildSelectionKey(string documentPath, IReadOnlyList<DesignerNodeSummary> selectedNodes)
    {
        if (selectedNodes.Count == 0)
        {
            return documentPath;
        }

        return documentPath + "|" + string.Join("|", selectedNodes.Select(node => node.NodeId));
    }

    private void HookPropertyEntry(PropertyEntryViewModel entry)
    {
        void CancelPendingCommit()
        {
            if (_propertyCommits.TryGetValue(GetDraftKey(entry), out PendingPropertyCommit? commit))
            {
                commit.Cancellation.Cancel();
            }
        }

        void SetNull()
        {
            if (!entry.CanAssignNull || string.IsNullOrWhiteSpace(_designer.ActiveDocumentPath)) return;
            CancelPendingCommit();
            _pendingPropertyValues[GetDraftKey(entry)] = new PendingPropertyValue(
                _designer.ActiveDocumentPath, entry.Value, null, ForceNull: true);
            entry.ValidationError = null;
        }

        entry.NullRequested += SetNull;
        _entrySubscriptions.Add(Disposable.Create(() => entry.NullRequested -= SetNull));

        string documentPath = _designer.ActiveDocumentPath ?? _lastActiveDocumentPath;
        void Commit() => RunBackground(ApplyPropertyChangeAsync(entry, entry.Value));
        void Cancel()
        {
            _pendingPropertyValues.Remove(GetDraftKey(entry));
            StatusText = null;
            CancelPendingCommit();
        }
        void Reset() => _pendingPropertyValues[GetDraftKey(entry)] = new(documentPath, null, null, ForceReset: true);
        entry.CommitRequested += Commit;
        entry.EditCanceled += Cancel;
        entry.ResetRequested += Reset;

        // Direct callbacks instead of WhenAnyValue subscriptions: the panel hooks every entry on each
        // selection change, and a per-entry subscription dominates selection time for large property sets.
        entry.ValueChanged = changed =>
        {
            if (_suspendUpdates == 0)
            {
                CancelPendingCommit();
                changed.ValidationError = null;
                _pendingPropertyValues[GetDraftKey(changed)] = new(documentPath, changed.Value, null);
            }
            if (!string.IsNullOrWhiteSpace(SearchText)
                && PropertiesView.Contains(changed) != MatchesPropertyFilter(changed))
            {
                PropertiesView.Refresh();
                RequestGroupedRefresh();
            }
        };
        entry.IsSetChanged = changed =>
        {
            if (_suspendUpdates > 0 || !string.Equals(changed.Value, changed.CommittedValue, StringComparison.Ordinal))
            {
                return;
            }

            RequestGroupedRefresh();
        };
        _entrySubscriptions.Add(Disposable.Create(() =>
        {
            entry.CommitRequested -= Commit;
            entry.EditCanceled -= Cancel;
            entry.ResetRequested -= Reset;
            entry.ValueChanged = null;
            entry.IsSetChanged = null;
        }));
    }

    private void UpdateEventSource(EventEntryViewModel entry, string? handlerName)
    {
        if (_pendingEventHandlers.TryGetValue(GetEventDraftKey(entry), out PendingEventHandler? draft) &&
            string.Equals(draft.DocumentPath, _designer.ActiveDocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            entry.UpdateSourceValue(handlerName, preserveDraft: true);
            entry.HandlerName = draft.Value;
            entry.ValidationError = draft.Error;
        }
        else
        {
            entry.UpdateSourceValue(handlerName, preserveDraft: false);
        }
    }

    private void HookEventEntry(EventEntryViewModel entry)
    {
        // Direct callback instead of a WhenAnyValue subscription; see HookPropertyEntry.
        entry.HandlerNameChanged = changed =>
        {
            if (_suspendUpdates > 0 || string.IsNullOrWhiteSpace(_designer.ActiveDocumentPath))
            {
                return;
            }

            _pendingEventHandlers[GetEventDraftKey(changed)] =
                new PendingEventHandler(_designer.ActiveDocumentPath, changed.HandlerName, null);
            changed.ValidationError = null;
            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                EventsView.Refresh();
            }
        };

        void Commit() => RunBackground(ApplyEventChangeAsync(entry, entry.HandlerName));
        void Navigate() => RunBackground(NavigateEventHandlerAsync(entry));
        void Cancel()
        {
            _pendingEventHandlers.Remove(GetEventDraftKey(entry));
            StatusText = null;
        }

        entry.CommitRequested += Commit;
        entry.EditCanceled += Cancel;
        entry.NavigateRequested += Navigate;
        _eventSubscriptions.Add(Disposable.Create(() =>
        {
            entry.CommitRequested -= Commit;
            entry.EditCanceled -= Cancel;
            entry.NavigateRequested -= Navigate;
            entry.HandlerNameChanged = null;
        }));
    }

    private async Task ApplyPropertyChangeAsync(PropertyEntryViewModel entry, string? value)
    {
        string key = GetDraftKey(entry);
        if (_isDisposed || !_pendingPropertyValues.TryGetValue(key, out PendingPropertyValue? draft) ||
            entry.IsReadOnly && !draft.ForceReset)
        {
            return;
        }

        if (!draft.ForceReset && !draft.ForceNull && string.Equals(entry.CommittedValue, value, StringComparison.Ordinal))
        {
            _pendingPropertyValues.Remove(key);
            entry.ValidationError = null;
            return;
        }

        if (!string.Equals(_designer.ActiveDocumentPath, draft.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            SetPropertyError(entry, draft, UiStrings.PropertyDraftDocumentChanged);
            return;
        }

        if (_propertyCommits.TryGetValue(key, out PendingPropertyCommit? previous))
        {
            if (ReferenceEquals(previous.Draft, draft))
            {
                return;
            }

            previous.Cancellation.Cancel();
        }

        using CancellationTokenSource cancellation = new();
        PendingPropertyCommit commit = new(cancellation, draft);
        _propertyCommits[key] = commit;
        string[] targetNodeIds = string.Equals(entry.NodeId, SelectedNodeId, StringComparison.OrdinalIgnoreCase)
            ? [entry.NodeId, .. _secondaryNodeIds]
            : [entry.NodeId];
        bool enteredGate = false;
        try
        {
            await _propertyCommitGate.WaitAsync(cancellation.Token);
            enteredGate = true;
            cancellation.Token.ThrowIfCancellationRequested();
            if (_isDisposed || !string.Equals(_designer.ActiveDocumentPath, draft.DocumentPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(UiStrings.PropertyDraftDocumentChanged);
            }

            string? committedValue = draft.ForceReset ? null : value;
            bool forceNull = draft.ForceNull && !draft.ForceReset;
            IDesignerPropertyValueHost? valueHost = _designer as IDesignerPropertyValueHost;
            if (forceNull && valueHost is null)
            {
                throw new InvalidOperationException(UiStrings.ExplicitNullUnsupported);
            }

            // One undo step for all selected nodes; a failure on any node rolls back the whole edit.
            using IDesignerTransaction? transaction = targetNodeIds.Length > 1
                ? _designer.BeginTransaction(string.Format(UiStrings.UndoSetProperty, entry.Name))
                : null;
            foreach (string nodeId in targetNodeIds)
            {
                if (forceNull)
                {
                    committedValue = await valueHost!.SetPropertyNullAsync(nodeId, entry.Name, cancellation.Token);
                }
                else if (!await _designer.SetPropertyAsync(nodeId, entry.Name, committedValue, cancellation.Token))
                {
                    throw new InvalidOperationException(UiStrings.PropertyValueApplyFailed);
                }
            }

            if (transaction is not null) await transaction.CommitAsync(cancellation.Token);
            entry.MarkCommitted(committedValue);
            if (_pendingPropertyValues.TryGetValue(key, out PendingPropertyValue? current) && ReferenceEquals(current, draft))
            {
                _suspendUpdates++;
                try { entry.UpdateSourceValue(committedValue, committedValue is not null, false); }
                finally { _suspendUpdates--; }
                entry.ValidationError = null;
                _pendingPropertyValues.Remove(key);
            }

            StatusText = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_pendingPropertyValues.TryGetValue(key, out PendingPropertyValue? current) && ReferenceEquals(current, draft))
            {
                SetPropertyError(entry, draft, exception.Message);
            }
            else
            {
                StatusText = $"{entry.Name}: {exception.Message}";
            }
        }
        finally
        {
            if (_propertyCommits.TryGetValue(key, out PendingPropertyCommit? current) && ReferenceEquals(current, commit))
            {
                _propertyCommits.Remove(key);
            }

            if (enteredGate)
            {
                _propertyCommitGate.Release();
            }
        }
    }

    private static string GetDraftKey(PropertyEntryViewModel entry) =>
        entry.NodeId + "|" + BuildPropertyEntryKey(entry.Name, entry.OwnerType);

    private void SetPropertyError(PropertyEntryViewModel entry, PendingPropertyValue draft, string message)
    {
        entry.ValidationError = message;
        _pendingPropertyValues[GetDraftKey(entry)] = draft with { Value = entry.Value, Error = message };
        StatusText = $"{entry.Name}: {message}";
    }

    private async Task ApplyEventChangeAsync(EventEntryViewModel entry, string? value)
    {
        string key = GetEventDraftKey(entry);
        if (_suspendUpdates > 0 || entry.IsApplying ||
            !_pendingEventHandlers.TryGetValue(key, out PendingEventHandler? draft))
        {
            return;
        }

        if (!string.Equals(_designer.ActiveDocumentPath, draft.DocumentPath, StringComparison.OrdinalIgnoreCase))
        {
            SetEventError(entry, draft, UiStrings.EventDraftDocumentChanged);
            return;
        }

        string? handlerName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (string.Equals(entry.CommittedHandler, handlerName, StringComparison.Ordinal))
        {
            entry.HandlerName = handlerName;
            entry.ValidationError = null;
            _pendingEventHandlers.Remove(key);
            return;
        }

        CancellationToken token = _loadCts?.Token ?? CancellationToken.None;
        entry.IsApplying = true;
        try
        {
            if (_designer is not IDesignerEventHost eventHost)
            {
                throw new InvalidOperationException(UiStrings.ValidatedEventsUnsupported);
            }

            bool applied = await eventHost.SetEventHandlerAsync(entry.NodeId, entry.Name, handlerName, token);
            if (!applied)
            {
                throw new InvalidOperationException(UiStrings.EventHandlerApplyFailed);
            }

            entry.MarkCommitted(handlerName);
            if (_pendingEventHandlers.TryGetValue(key, out PendingEventHandler? current) && ReferenceEquals(current, draft))
            {
                entry.HandlerName = handlerName;
                entry.ValidationError = null;
                _pendingEventHandlers.Remove(key);
            }

            StatusText = null;
        }
        catch (OperationCanceledException)
        {
            SetEventError(entry, draft, UiStrings.EventHandlerCanceled);
        }
        catch (Exception exception)
        {
            SetEventError(entry, draft, exception.Message);
        }
        finally
        {
            entry.IsApplying = false;
        }
    }

    private async Task NavigateEventHandlerAsync(EventEntryViewModel entry)
    {
        await ApplyEventChangeAsync(entry, entry.HandlerName);
        if (entry.IsApplying || entry.HasValidationError || _pendingEventHandlers.ContainsKey(GetEventDraftKey(entry)) ||
            string.IsNullOrWhiteSpace(entry.CommittedHandler))
        {
            return;
        }

        entry.IsApplying = true;
        try
        {
            if (_designer is not IDesignerEventHost eventHost ||
                !await eventHost.NavigateToEventHandlerAsync(entry.NodeId, entry.Name, _loadCts?.Token ?? CancellationToken.None))
            {
                throw new InvalidOperationException(UiStrings.EventDefinitionOpenFailed);
            }

            StatusText = null;
        }
        catch (Exception exception)
        {
            entry.ValidationError = exception.Message;
            StatusText = $"{entry.Name}: {exception.Message}";
        }
        finally
        {
            entry.IsApplying = false;
        }
    }

    private static string GetEventDraftKey(EventEntryViewModel entry) => entry.NodeId + "|" + entry.Name;

    private void SetEventError(EventEntryViewModel entry, PendingEventHandler draft, string message)
    {
        entry.ValidationError = message;
        _pendingEventHandlers[GetEventDraftKey(entry)] = draft with { Value = entry.HandlerName, Error = message };
        StatusText = $"{entry.Name}: {message}";
    }

    private bool FilterProperty(object? item)
    {
        if (item is not PropertyEntryViewModel entry)
        {
            return false;
        }

        return MatchesPropertyFilter(entry);
    }

    private void RebuildGroupedView()
    {
        List<PropertyEntryViewModel> filtered = _properties
            .Where(MatchesPropertyFilter)
            .ToList();

        if (filtered.Count == 0)
        {
            _groupedRows.Clear();
            GroupedPropertiesView.Refresh();
            _groupedViewDirty = false;
            return;
        }

        // The rows are collected first and replaced with one reset: item-by-item inserts make the
        // listening collection view process every insert separately.
        List<PropertyRowViewModel> rows = new(filtered.Count + 8);
        List<PropertyEntryViewModel> essentials = filtered.Where(entry => IsEssential(entry.Name))
            .OrderBy(entry => EssentialRank(entry.Name)).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AddGroup(EssentialPropertiesGroupName, essentials, true, false);

        List<PropertyEntryViewModel> localValues = filtered.Where(entry => entry.IsSet && !IsEssential(entry.Name))
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase).ToList();
        AddGroup(LocalValuesGroupName, localValues, true, false);

        Dictionary<string, List<PropertyEntryViewModel>> categories = new(StringComparer.OrdinalIgnoreCase);
        if (!ShowLocalValuesOnly)
        {
            foreach (PropertyEntryViewModel entry in filtered.Where(entry => !entry.IsSet && !IsEssential(entry.Name)))
            {
                string category = string.IsNullOrWhiteSpace(entry.Category) ? "Misc" : entry.Category;
                if (!categories.TryGetValue(category, out List<PropertyEntryViewModel>? list))
                {
                    list = new List<PropertyEntryViewModel>();
                    categories[category] = list;
                }
                list.Add(entry);
            }
        }
        List<string> categoryOrder = categories.Keys
            .OrderBy(GetCategorySortKey)
            .ThenBy(category => category, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (string category in categoryOrder)
        {
            bool expanded = _expandedCategories.TryGetValue(category, out bool saved)
                ? saved : !CollapseOtherCategoriesOnSelection;
            bool searching = !string.IsNullOrWhiteSpace(SearchText);
            AddGroup(category, categories[category].OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase),
                expanded || searching, !searching);
        }

        _groupedRows.ReplaceAll(rows);
        GroupedPropertiesView.Refresh();
        _groupedViewDirty = false;

        void AddGroup(string groupName, IEnumerable<PropertyEntryViewModel> properties, bool expanded, bool canCollapse)
        {
            using IEnumerator<PropertyEntryViewModel> iterator = properties.GetEnumerator();
            if (!iterator.MoveNext()) return;
            rows.Add(new PropertyRowViewModel(groupName, expanded, canCollapse,
                canCollapse ? SetCategoryExpanded : null));
            if (!expanded) return;
            do
                rows.Add(new PropertyRowViewModel(iterator.Current, groupName));
            while (iterator.MoveNext());
        }
    }

    private void SetCategoryExpanded(string category, bool expanded)
    {
        _expandedCategories[category] = expanded;
        if (!ShowGroupedView || _groupedViewDirty)
        {
            RequestGroupedRefresh();
            return;
        }

        int headerIndex = -1;
        for (int index = 0; index < _groupedRows.Count; index++)
        {
            if (_groupedRows[index].IsHeader && string.Equals(_groupedRows[index].GroupName, category, StringComparison.OrdinalIgnoreCase))
            {
                headerIndex = index;
                break;
            }
        }
        if (headerIndex < 0) return;

        int childIndex = headerIndex + 1;
        if (expanded)
        {
            foreach (PropertyEntryViewModel entry in _properties
                         .Where(MatchesPropertyFilter)
                         .Where(entry => !entry.IsSet && !IsEssential(entry.Name)
                             && string.Equals(string.IsNullOrWhiteSpace(entry.Category) ? "Misc" : entry.Category,
                                 category, StringComparison.OrdinalIgnoreCase))
                         .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase))
            {
                _groupedRows.Insert(childIndex++, new PropertyRowViewModel(entry, category));
            }
        }
        else
        {
            while (childIndex < _groupedRows.Count && !_groupedRows[childIndex].IsHeader)
                _groupedRows.RemoveAt(childIndex);
        }
    }

    private void SetAllCategoriesExpanded(bool expanded)
    {
        foreach (PropertyCategoryOption category in Categories)
        {
            if (category.Value is { } value) _expandedCategories[value] = expanded;
        }
        if (!ShowGroupedView || _groupedViewDirty)
        {
            RequestGroupedRefresh();
            return;
        }

        foreach (PropertyRowViewModel header in _groupedRows.Where(row => row.IsHeader && row.CanCollapse).ToArray())
            header.IsExpanded = expanded;
    }

    private static bool IsEssential(string name) => EssentialRank(name) != int.MaxValue;

    private static int EssentialRank(string name) => name switch
    {
        "Name" or "x:Name" => 0,
        "Location" => 5,
        "Canvas.Left" => 10,
        "Canvas.Top" => 11,
        "Canvas.Right" => 12,
        "Canvas.Bottom" => 13,
        "Grid.Row" or "Row" => 14,
        "Grid.Column" or "Column" => 15,
        "Grid.RowSpan" or "RowSpan" => 16,
        "Grid.ColumnSpan" or "ColumnSpan" => 17,
        "Margin" => 18,
        "Padding" => 19,
        "Size" => 20,
        "Width" => 21,
        "Height" => 22,
        "MinWidth" => 23,
        "MinHeight" => 24,
        "MaxWidth" => 25,
        "MaxHeight" => 26,
        "Content" => 30,
        "Header" => 31,
        "Text" => 32,
        "FontFamily" => 40,
        "FontSize" => 41,
        "FontWeight" => 42,
        "FontStyle" => 43,
        "TextAlignment" => 44,
        "TextWrapping" => 45,
        "TextTrimming" => 46,
        "TextDecorations" => 47,
        "BorderBrush" => 50,
        "BorderThickness" => 51,
        "CornerRadius" => 52,
        "DockPanel.Dock" or "Dock" => 60,
        "HorizontalAlignment" => 61,
        "VerticalAlignment" => 62,
        _ => int.MaxValue
    };

    private static int GetCategorySortKey(string category)
    {
        if (CategorySortOrder.TryGetValue(category, out int order))
        {
            return order;
        }

        return 100;
    }

    private void RequestGroupedRefresh()
    {
        _groupedViewDirty = true;
        if (!ShowGroupedView)
        {
            return;
        }

        RebuildGroupedView();
    }

    private bool FilterEvent(object? item)
    {
        if (item is not EventEntryViewModel entry)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || (entry.HandlerName?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private async Task<IReadOnlyList<DesignerNodeSummary>> PollSelectedNodesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _designer.GetSelectedNodesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<DesignerNodeSummary>();
        }
        catch
        {
            return Array.Empty<DesignerNodeSummary>();
        }
    }

    private bool MatchesPropertyFilter(PropertyEntryViewModel entry)
    {
        if (SelectedCategory?.Value is { } selectedCategory
            && !string.Equals(entry.Category, selectedCategory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (ShowLocalValuesOnly && !entry.IsSet && !IsEssential(entry.Name))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return entry.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || (entry.CommittedValue?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)
            || entry.PropertyType.Contains(SearchText, StringComparison.OrdinalIgnoreCase)
            || entry.Category.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
    }

    private PropertyEditorDescriptor? ResolveDescriptor(string propertyName, string propertyType)
    {
        string cacheKey = (propertyType ?? string.Empty).Trim()
            + "||"
            + (propertyName ?? string.Empty).Trim();
        if (_descriptorCache.TryGetValue(cacheKey, out PropertyEditorDescriptor? cached))
        {
            return cached;
        }

        _propertyEditors.TryResolve(propertyName, propertyType, out PropertyEditorDescriptor? descriptor);
        _descriptorCache[cacheKey] = descriptor;
        return descriptor;
    }

    private static void RunBackground(Task task)
    {
        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
