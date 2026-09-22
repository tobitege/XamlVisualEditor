namespace XamlVisualEditor.Core;

/// <summary>Semantische Farben der Editor-Oberfläche, unabhängig von einer Control-Bibliothek.</summary>
public enum EditorThemeColor
{
    Background, Surface, SurfaceAlt, Foreground, MutedForeground, Border,
    Accent, AccentForeground, Selection, SelectionForeground,
    Error, ErrorForeground, Warning, WarningForeground, Success, SuccessForeground
}

/// <summary>Beschreibt ein Editor-Theme mit stabiler ID.</summary>
public sealed record EditorTheme(string Id, string Name, bool IsDark);

/// <summary>Enthält ARGB-Farben für alle semantischen Rollen des Editors.</summary>
public sealed record EditorThemePalette(bool IsDark, IReadOnlyDictionary<EditorThemeColor, string> Colors);

/// <summary>Liefert Themes, ohne Anwendungsressourcen oder den Zustand der Vorschau zu verändern.</summary>
public interface IEditorThemeProvider
{
    IReadOnlyList<EditorTheme> GetThemes();
    EditorThemePalette GetPalette(string themeId);
}

/// <summary>Registriert den Provider einer installierten Editor-Theme-Extension.</summary>
public sealed record EditorThemeProviderRegistration(string Id, string Name, string AssemblyPath, string TypeName);

/// <summary>Speichert die Editor-Auswahl getrennt von Designer-Themes.</summary>
public sealed record EditorThemeSelection(string ProviderId, string ThemeId)
{
    public static EditorThemeSelection Default { get; } = new("builtin", "dark");
}

/// <summary>Enthält verfügbare Editor-Themes oder einen Ladefehler des Providers.</summary>
public sealed record EditorThemeProviderCatalog(string Id, string Name, IReadOnlyList<EditorTheme> Themes, string? Error = null);

/// <summary>Verbindet die Editor-Optionen mit der Theme-Verwaltung der Oberfläche.</summary>
public interface IEditorThemeService
{
    event EventHandler? Changed;
    IReadOnlyList<EditorThemeProviderCatalog> Catalogs { get; }
    EditorThemeSelection Selection { get; }
    string? Error { get; }
    void Apply(EditorThemeSelection selection);
    /// <summary>Stellt eine Auswahl wieder her; ein fehlender Provider verwendet das Standard-Theme und meldet den Fehler.</summary>
    void Restore(EditorThemeSelection selection);
}
