namespace XamlVisualEditor.Extensions;

/// <summary>Provides the controls available in the active workspace.</summary>
public interface IControlCatalog
{
    /// <summary>Raised when the workspace assembly scope changes.</summary>
    event EventHandler? Changed;

    /// <summary>Reads a control snapshot without constructing the controls.</summary>
    Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken);
}

/// <summary>Updates the catalog after the host loads a workspace or its build outputs.</summary>
public interface IControlCatalogUpdater
{
    /// <summary>Replaces the workspace scope. An empty scope exposes only standard controls.</summary>
    void SetWorkspaceAssemblies(IEnumerable<string> assemblyPaths);
}

/// <summary>Describes an insertable control and its actual assembly identity.</summary>
public sealed record ControlCatalogItem(
    string TypeName,
    string FullName,
    string XmlNamespace,
    string AssemblyName,
    string DisplayName,
    string Category,
    bool IsStandardControl);

/// <summary>Contains available controls and problems encountered reading the workspace assemblies.</summary>
public sealed record ControlCatalogSnapshot(
    IReadOnlyList<ControlCatalogItem> Controls,
    IReadOnlyList<string> Diagnostics);
