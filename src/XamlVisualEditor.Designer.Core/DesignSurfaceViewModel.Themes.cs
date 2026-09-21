using System.Collections.ObjectModel;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Designer.Core;

/// <summary>A named theme option, distinct from the editor theme and the light/dark variant.</summary>
public sealed record DesignerThemeChoice(DesignerThemeSelection? Selection, string DisplayName)
{
    public static readonly DesignerThemeChoice ProjectDefault = new(null, "Theme default");
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
    public partial string PreviewThemeStatus { get; set; } = "Start the previewer to load library themes.";
}
