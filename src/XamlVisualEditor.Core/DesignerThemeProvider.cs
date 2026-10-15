using System.Text.Json;
using System.Xml.Linq;

namespace XamlVisualEditor.Core;

/// <summary>Runs on the preview application's UI thread, never in the editor process.</summary>
public interface IDesignerThemeProvider
{
    /// <summary>Returns themes registered in the initialized target application.</summary>
    IReadOnlyList<DesignerTheme> GetThemes();

    /// <summary>Applies a stable theme ID; null restores the application's original theme.</summary>
    void ApplyTheme(string? themeId);
}

/// <summary>A theme's stable identity, display name and base variant.</summary>
public sealed record DesignerTheme(string Id, string Name, bool IsDark);

/// <summary>A trusted extension's public, parameterless provider type and absolute assembly path.</summary>
/// <param name="RequiredAssembly">Assembly name a project must reference before the provider follows the editor theme.</param>
public sealed record DesignerThemeProviderRegistration(string Id, string Name, string AssemblyPath, string TypeName,
    string? RequiredAssembly = null);

/// <summary>A selection independent of translated or renamed display text.</summary>
public sealed record DesignerThemeSelection(string ProviderId, string ThemeId);

/// <summary>Preview-only provider configuration correlated with its returned catalog.</summary>
public sealed record DesignerThemeRequest(Guid RequestId, IReadOnlyList<DesignerThemeProviderRegistration> Providers,
    DesignerThemeSelection? Selection);

/// <summary>The available themes of one provider in the target application's context.</summary>
public sealed record DesignerThemeProviderCatalog(string Id, string Name, IReadOnlyList<DesignerTheme> Themes, string? Error = null);

/// <summary>A catalog from the isolated preview host, not the editor's assembly context.</summary>
public sealed record DesignerThemeCatalog(Guid RequestId, IReadOnlyList<DesignerThemeProviderCatalog> Providers);

/// <summary>Confirms that the preview host processed a theme request; Error names a rejected selection.</summary>
public sealed record DesignerThemeResult(Guid RequestId, string? Error = null);

/// <summary>Framework-independent transport metadata for preview theme extensions.</summary>
public static class DesignerThemeProtocol
{
    public const string CatalogPrefix = "XVE-THEME-CATALOG ";

    /// <summary>Prefixes a theme request on the preview host's standard input, the only channel that loads providers.</summary>
    public const string RequestPrefix = "XVE-THEME-REQUEST ";

    public const string ResultPrefix = "XVE-THEME-RESULT ";

    /// <summary>Preview-only attribute that makes a theme change replace the rendered document; the host never reads it.</summary>
    public const string PropertyName = "DesignPreviewSettings.ThemeRequest";
    public const string XmlNamespace = "clr-namespace:XamlVisualEditor.Designer.Rendering;assembly=XamlVisualEditor.Designer.Rendering";

    public static DesignerThemeRequest? ReadRequest(XDocument document)
    {
        string? value = document.Descendants().Attributes(XName.Get(PropertyName, XmlNamespace)).FirstOrDefault()?.Value;
        return value is null ? null : JsonSerializer.Deserialize<DesignerThemeRequest>(
            value.StartsWith("{}", StringComparison.Ordinal) ? value.AsSpan(2) : value.AsSpan());
    }
}
