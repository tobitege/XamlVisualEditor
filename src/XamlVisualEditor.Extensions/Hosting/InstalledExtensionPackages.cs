using System.IO;
using System.Reflection;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Assembly plumbing for activating installed extension packages whose manifest names a
/// main assembly. The package store extracts every package into its installation
/// directory, so activation only resolves and inspects the main assembly there.
/// </summary>
public static class InstalledExtensionPackages
{
    /// <summary>
    /// Resolves the manifest's main assembly inside the package installation directory.
    /// Returns null when the package has no main entry, is not installed, or the file does
    /// not exist; throws when the entry is not a DLL or escapes the installation directory.
    /// </summary>
    public static string? ResolveMainAssemblyPath(ExtensionPackageInfo package)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (string.IsNullOrWhiteSpace(package.Manifest.Main) || package.InstallationDirectory is not { } directory)
        {
            return null;
        }

        string path = DesignerThemePackageContributions.ResolvePackagePath(directory, package.Manifest.Main.Trim());
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The main entry of an extension package must name a managed DLL.");
        }

        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Finds the activatable extension entry types of an assembly: public, non-abstract
    /// implementations of <see cref="IXveExtension"/> with a parameterless constructor.
    /// </summary>
    public static IReadOnlyList<Type> FindExtensionTypes(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            types = exception.Types.OfType<Type>().ToArray();
        }

        return types
            .Where(type => type is { IsClass: true, IsAbstract: false, IsPublic: true }
                && typeof(IXveExtension).IsAssignableFrom(type)
                && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();
    }
}
