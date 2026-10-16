using System;
using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.TestControls;

[Flags]
public enum EditorFeatures : ulong
{
    None = 0,
    Alpha = 1,
    Beta = 2,
    All = Alpha | Beta,
    High = 1UL << 63
}

public sealed class PropertyFixtureControl : CheckBox
{
    public static readonly StyledProperty<EditorFeatures> FeaturesProperty =
        AvaloniaProperty.Register<PropertyFixtureControl, EditorFeatures>(nameof(Features));

    public EditorFeatures Features { get => GetValue(FeaturesProperty); set => SetValue(FeaturesProperty, value); }
}
