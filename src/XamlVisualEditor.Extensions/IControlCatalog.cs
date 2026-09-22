using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions;

/// <summary>Provides the controls available in the active workspace.</summary>
public interface IControlCatalog
{
    /// <summary>Raised when the workspace assembly scope changes.</summary>
    event EventHandler? Changed;

    /// <summary>Gibt an, ob Projekt-Assemblies zur Suche bereitstehen.</summary>
    bool HasProjectAssemblies { get; }

    /// <summary>Gibt an, ob ein Workspace-Wechsel die Projekt-Controls automatisch laden soll.</summary>
    bool AutoLoadProjectControls { get; }

    /// <summary>Reads a control snapshot without constructing the controls.</summary>
    Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken,
        IProgress<ControlCatalogProgress>? progress = null, bool includeProjectControls = true);
}

/// <summary>Updates the catalog after the host loads a workspace or its build outputs.</summary>
public interface IControlCatalogUpdater
{
    /// <summary>Replaces the workspace scope. An empty scope exposes only standard controls.</summary>
    void SetWorkspaceAssemblies(IEnumerable<string> assemblyPaths, bool autoLoadProjectControls = true);
}

/// <summary>Liest Control-Typen mit Fortschritt und kooperativem Abbruch.</summary>
public delegate IReadOnlyList<TypeMetadata> ControlTypeReader(IReadOnlyList<string> assemblyPaths,
    Action<string> reportFailure, Action<int, int, string> reportProgress, CancellationToken cancellationToken);

/// <summary>Beschreibt den Fortschritt der Assembly-Suche.</summary>
public sealed record ControlCatalogProgress(int CompletedAssemblies, int TotalAssemblies, string AssemblyName);

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
