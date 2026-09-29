using System.Text.Json;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Reads declarative theme providers without loading extension code in the editor.</summary>
public static class DesignerThemePackageContributions
{
    public static IReadOnlyList<DesignerThemeProviderRegistration> Read(ExtensionPackageInfo package)
        => Read(package, "designerThemes");

    /// <summary>Reads the separate editor registration with the same package path checks.</summary>
    public static IReadOnlyList<EditorThemeProviderRegistration> ReadEditor(ExtensionPackageInfo package)
        => Read(package, "editorThemes").Select(provider => new EditorThemeProviderRegistration(
            provider.Id, provider.Name, provider.AssemblyPath, provider.TypeName)).ToArray();

    private static IReadOnlyList<DesignerThemeProviderRegistration> Read(ExtensionPackageInfo package, string contributionName)
    {
        if (package.Manifest.Contributes is not { ValueKind: JsonValueKind.Object } contributes
            || !contributes.TryGetProperty(contributionName, out JsonElement items))
            return Array.Empty<DesignerThemeProviderRegistration>();
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException(contributionName + " must be an array.");
        List<DesignerThemeProviderRegistration> providers = new();
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement item in items.EnumerateArray())
        {
            string id = Required(item, "id");
            ValidateSegment(id, "theme provider ID");
            if (!ids.Add(id)) throw new InvalidDataException("Duplicate theme provider ID: " + id);
            string directory = package.InstallationDirectory
                ?? throw new InvalidOperationException("Theme provider packages must be installed before activation.");
            string assemblyPath = ResolvePackagePath(directory, Required(item, "assembly"));
            if (!string.Equals(Path.GetExtension(assemblyPath), ".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Theme providers must name a managed DLL.");
            providers.Add(new(package.Manifest.ExtensionId.ToLowerInvariant() + "/" + id, Required(item, "name"),
                assemblyPath, Required(item, "type")));
        }
        return providers;
    }

    internal static void ValidateSegment(string value, string description)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_' and not '+'))
            throw new InvalidDataException("Invalid " + description + ".");
    }

    internal static string ResolvePackagePath(string directory, string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized) || Path.IsPathRooted(normalized) || normalized.Contains(':')
            || normalized.Split('/').Any(part => part is "." or ".."))
            throw new InvalidDataException("Package paths must stay inside the installation directory.");
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string path = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException("Package path escapes its installation directory.");
        for (string? current = path; current is not null && (current.StartsWith(root + Path.DirectorySeparatorChar, comparison)
            || string.Equals(current, root, comparison)); current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Package paths cannot traverse symbolic links or junctions.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
        return path;
    }

    private static string Required(JsonElement item, string name)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty(name, out JsonElement value)
            || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException("A designerThemes entry requires '" + name + "'.");
        return value.GetString()!;
    }
}
