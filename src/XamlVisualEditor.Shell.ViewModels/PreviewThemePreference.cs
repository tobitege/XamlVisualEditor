using XamlVisualEditor.Core;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Combines the stored workspace preview theme with the theme the editor options supply.</summary>
internal static class PreviewThemePreference
{
    /// <summary>Stored when the user picks the project default although the editor options supply a theme.</summary>
    internal static readonly DesignerThemeSelection ExplicitProjectDefault = new("xve", "project-default");

    /// <summary>An explicit workspace choice wins; without one the preview follows the editor options.</summary>
    internal static DesignerThemeSelection? GetEffective(DesignerThemeSelection? stored, DesignerThemeSelection? derived) =>
        stored == ExplicitProjectDefault ? null : stored ?? derived;

    /// <summary>Whether the stored value names a theme the user picked for the workspace.</summary>
    internal static bool IsExplicitChoice(DesignerThemeSelection? stored) =>
        stored is not null && stored != ExplicitProjectDefault;

    /// <summary>Picking the theme the options supply keeps following the options; the project default then needs a marker.</summary>
    internal static DesignerThemeSelection? ToStored(DesignerThemeSelection? selection, DesignerThemeSelection? derived) =>
        selection == derived ? null : selection ?? ExplicitProjectDefault;
}
