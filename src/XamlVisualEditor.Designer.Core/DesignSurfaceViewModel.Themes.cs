using System.Collections.ObjectModel;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Designer.Core;

/// <summary>A named theme option, distinct from the editor theme and the light/dark variant.</summary>
public sealed record DesignerThemeChoice
{
    private readonly string? _displayName;

    public DesignerThemeChoice(DesignerThemeSelection? selection, string displayName)
    {
        Selection = selection;
        _displayName = displayName;
    }

    private DesignerThemeChoice() { }

    /// <summary>The single choice without a library theme; its label follows the current UI culture.</summary>
    public static readonly DesignerThemeChoice ProjectDefault = new();

    public DesignerThemeSelection? Selection { get; }

    public string DisplayName => _displayName ?? UiStrings.PreviewThemeProjectDefault;
}

public sealed partial class DesignSurfaceViewModel
{
    public ObservableCollection<DesignerThemeChoice> PreviewThemes { get; } = new() { DesignerThemeChoice.ProjectDefault };

    [Reactive]
    public partial DesignerThemeChoice? SelectedPreviewTheme { get; set; } = DesignerThemeChoice.ProjectDefault;

    [Reactive]
    public partial bool HasPreviewThemeProviders { get; set; }

    [Reactive]
    public partial bool HasPreviewThemeCatalog { get; set; }

    [Reactive]
    public partial string PreviewThemeStatus { get; set; } = UiStrings.PreviewThemeLoadHint;
}
