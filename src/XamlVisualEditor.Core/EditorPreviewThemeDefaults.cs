namespace XamlVisualEditor.Core;

/// <summary>Derives the preview theme that follows the editor theme selected in the options.</summary>
public static class EditorPreviewThemeDefaults
{
    /// <summary>The editor provider whose light, dark and system themes map to theme IDs of the same name.</summary>
    public const string BuiltinProviderId = "builtin";

    /// <summary>
    /// Returns the preview theme for a project, or null when the project default applies. Only providers that
    /// declare a required assembly referenced by the project take part, so an editor theme never restyles a
    /// project that does not use the provider's library.
    /// </summary>
    /// <param name="editorTheme">The editor theme from the options.</param>
    /// <param name="editorIsDark">Whether the applied editor theme renders dark; resolves the builtin system theme.</param>
    /// <param name="providers">The registered preview theme providers.</param>
    /// <param name="project">The project that renders the document.</param>
    /// <param name="catalog">
    /// The catalog the preview host reported for the document, or null before one arrived. A derived theme that this
    /// catalog does not offer yields the project default, because the target application may hide themes from its
    /// provider and the preview host rejects a selection outside the catalog.
    /// </param>
    public static DesignerThemeSelection? Resolve(EditorThemeSelection editorTheme, bool editorIsDark,
        IReadOnlyList<DesignerThemeProviderRegistration> providers, ProjectModel? project, DesignerThemeCatalog? catalog = null)
    {
        DesignerThemeSelection? selection = Derive(editorTheme, editorIsDark, providers, project);
        return selection is not null && IsOffered(selection, catalog) ? selection : null;
    }

    /// <summary>
    /// Returns whether a catalog offers the theme. A provider that reported an error offers none; a catalog that does
    /// not list the provider, or no catalog, carries no information and therefore does not reject the theme.
    /// </summary>
    public static bool IsOffered(DesignerThemeSelection selection, DesignerThemeCatalog? catalog)
    {
        DesignerThemeProviderCatalog? provider = catalog?.Providers.FirstOrDefault(
            item => string.Equals(item.Id, selection.ProviderId, StringComparison.Ordinal));
        return provider is null || (provider.Error is null
            && provider.Themes.Any(theme => string.Equals(theme.Id, selection.ThemeId, StringComparison.Ordinal)));
    }

    private static DesignerThemeSelection? Derive(EditorThemeSelection editorTheme, bool editorIsDark,
        IReadOnlyList<DesignerThemeProviderRegistration> providers, ProjectModel? project)
    {
        if (project is null) return null;
        DesignerThemeProviderRegistration? fallback = null;
        foreach (DesignerThemeProviderRegistration provider in providers)
        {
            if (provider.RequiredAssembly is not { } required || !References(project, required)) continue;
            if (string.Equals(provider.Id, editorTheme.ProviderId, StringComparison.Ordinal))
                return new DesignerThemeSelection(provider.Id, editorTheme.ThemeId);
            if (fallback is null || string.CompareOrdinal(provider.Id, fallback.Id) < 0) fallback = provider;
        }
        if (fallback is null || !string.Equals(editorTheme.ProviderId, BuiltinProviderId, StringComparison.Ordinal))
            return null;
        string themeId = editorTheme.ThemeId switch
        {
            "light" or "dark" => editorTheme.ThemeId,
            "system" => editorIsDark ? "dark" : "light",
            _ => string.Empty
        };
        return themeId.Length == 0 ? null : new DesignerThemeSelection(fallback.Id, themeId);
    }

    private static bool References(ProjectModel project, string assemblyName)
    {
        foreach (AssemblyReference reference in project.References)
            if (string.Equals(reference.Name, assemblyName, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
