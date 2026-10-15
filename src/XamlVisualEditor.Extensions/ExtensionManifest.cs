using System.Text.Json;

namespace XamlVisualEditor.Extensions;

/// <summary>Describes an extension manifest.</summary>
public sealed class ExtensionManifest
{
    /// <summary>Gets the extension name.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the optional display name.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Gets the extension publisher.</summary>
    public required string Publisher { get; init; }

    /// <summary>Gets the extension version.</summary>
    public required string Version { get; init; }

    /// <summary>Gets the entry point module or assembly path.</summary>
    public string? Main { get; init; }

    /// <summary>Gets the activation events.</summary>
    public IReadOnlyList<string> ActivationEvents { get; init; } = Array.Empty<string>();

    /// <summary>Gets the engine requirements block.</summary>
    public JsonElement? Engines { get; init; }

    /// <summary>Gets the contribution definitions.</summary>
    public JsonElement? Contributes { get; init; }

    /// <summary>Gets the sensitive host capabilities the extension uses, see <see cref="ExtensionHostCapabilities"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();

    /// <summary>Gets the computed extension id.</summary>
    public string ExtensionId => Publisher + "." + Name;
}

/// <summary>Sensitive host capabilities that an extension receives only when it declares them.</summary>
public static class ExtensionHostCapabilities
{
    /// <summary>Provides <see cref="ExtensionContext.DocumentOpenInterceptors"/>, which can redirect every user-requested open.</summary>
    public const string DocumentOpenInterceptors = "documentOpenInterceptors";
}

/// <summary>
/// Declares the sensitive host capabilities of an extension registered with the editor.
/// Installed extension packages declare them in the <c>capabilities</c> array of their manifest instead.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ExtensionCapabilitiesAttribute : Attribute
{
    /// <summary>Creates the declaration.</summary>
    public ExtensionCapabilitiesAttribute(params string[] capabilities)
    {
        Capabilities = capabilities;
    }

    /// <summary>Gets the declared capabilities, see <see cref="ExtensionHostCapabilities"/>.</summary>
    public IReadOnlyList<string> Capabilities { get; }

    /// <summary>Returns the capabilities declared on <paramref name="extensionType"/>.</summary>
    public static IReadOnlyList<string> GetDeclared(Type extensionType)
    {
        ArgumentNullException.ThrowIfNull(extensionType);
        return extensionType.GetCustomAttributes(typeof(ExtensionCapabilitiesAttribute), inherit: false)
            is [ExtensionCapabilitiesAttribute attribute] ? attribute.Capabilities : Array.Empty<string>();
    }
}

/// <summary>Describes an extension package.</summary>
public sealed class ExtensionPackageInfo
{
    /// <summary>Creates a package info.</summary>
    public ExtensionPackageInfo(string packagePath, ExtensionManifest manifest, string? installationDirectory = null)
    {
        PackagePath = packagePath;
        Manifest = manifest;
        InstallationDirectory = installationDirectory;
    }

    /// <summary>Gets the package path.</summary>
    public string PackagePath { get; }

    /// <summary>Gets the extracted package directory, or null for an uninstalled archive.</summary>
    public string? InstallationDirectory { get; }

    /// <summary>Gets the parsed manifest.</summary>
    public ExtensionManifest Manifest { get; }
}
