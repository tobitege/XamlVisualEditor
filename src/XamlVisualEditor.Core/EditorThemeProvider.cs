namespace XamlVisualEditor.Core;

/// <summary>Semantic colors of the editor interface, independent of a control library.</summary>
public enum EditorThemeColor
{
    Background, Surface, SurfaceAlt, Foreground, MutedForeground, Border,
    Accent, AccentForeground, Selection, SelectionForeground,
    Error, ErrorForeground, Warning, WarningForeground, Success, SuccessForeground
}

/// <summary>Describes an editor theme with a stable ID.</summary>
public sealed record EditorTheme(string Id, string Name, bool IsDark);

/// <summary>Holds ARGB colors for all semantic roles of the editor.</summary>
public sealed record EditorThemePalette(bool IsDark, IReadOnlyDictionary<EditorThemeColor, string> Colors);

/// <summary>Provides themes without changing application resources or the state of the preview.</summary>
public interface IEditorThemeProvider
{
    IReadOnlyList<EditorTheme> GetThemes();
    EditorThemePalette GetPalette(string themeId);
}

/// <summary>Registers the provider of an installed editor theme extension.</summary>
public sealed record EditorThemeProviderRegistration(string Id, string Name, string AssemblyPath, string TypeName);

/// <summary>Stores the editor selection separately from the designer themes.</summary>
public sealed record EditorThemeSelection(string ProviderId, string ThemeId)
{
    public static EditorThemeSelection Default { get; } = new("builtin", "dark");
}

/// <summary>Holds the available editor themes or a load error of the provider.</summary>
public sealed record EditorThemeProviderCatalog(string Id, string Name, IReadOnlyList<EditorTheme> Themes, string? Error = null);

/// <summary>Connects the editor options with the theme management of the interface.</summary>
public interface IEditorThemeService
{
    event EventHandler? Changed;
    IReadOnlyList<EditorThemeProviderCatalog> Catalogs { get; }
    EditorThemeSelection Selection { get; }
    string? Error { get; }
    /// <summary>Gets whether the applied editor theme renders dark, including a resolved system theme.</summary>
    bool IsDark { get; }
    void Apply(EditorThemeSelection selection);
    /// <summary>Restores a selection; a missing provider falls back to the default theme and reports the error.</summary>
    void Restore(EditorThemeSelection selection);
}
