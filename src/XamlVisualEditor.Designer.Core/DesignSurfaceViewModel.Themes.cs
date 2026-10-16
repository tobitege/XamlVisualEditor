using System.Collections.ObjectModel;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Designer.Core;

/// <summary>A named theme option, distinct from the editor theme and the light/dark variant.</summary>
/// <remarks>
/// The members match the former positional record: the constructor parameters, the init accessors and
/// <see cref="Deconstruct"/> keep their names, so object initializers, <c>with</c> expressions and deconstruction compile
/// unchanged.
/// </remarks>
public sealed record DesignerThemeChoice
{
    // Null only for ProjectDefault, whose label is read from the current UI culture on every access.
    private readonly string? _displayName;

    public DesignerThemeChoice(DesignerThemeSelection? Selection, string DisplayName)
    {
        ArgumentNullException.ThrowIfNull(DisplayName);
        this.Selection = Selection;
        _displayName = DisplayName;
    }

    private DesignerThemeChoice() { }

    /// <summary>The single choice without a library theme; its label follows the current UI culture.</summary>
    public static readonly DesignerThemeChoice ProjectDefault = new();

    public DesignerThemeSelection? Selection { get; init; }

    public string DisplayName
    {
        get => _displayName ?? UiStrings.PreviewThemeProjectDefault;
        init => _displayName = value ?? throw new ArgumentNullException(nameof(DisplayName));
    }

    public void Deconstruct(out DesignerThemeSelection? Selection, out string DisplayName)
    {
        Selection = this.Selection;
        DisplayName = this.DisplayName;
    }
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
