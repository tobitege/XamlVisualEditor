using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Builds a workspace-scoped toolbox from the shared type metadata.</summary>
public sealed class MetadataControlCatalog : IControlCatalog, IControlCatalogUpdater
{
    private readonly ITypeMetadataService _metadata;
    private readonly object _gate = new();
    private string[] _assemblyPaths = Array.Empty<string>();

    /// <summary>Creates the catalog for the metadata used by the editor.</summary>
    public MetadataControlCatalog(ITypeMetadataService metadata)
    {
        _metadata = metadata;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public void SetWorkspaceAssemblies(IEnumerable<string> assemblyPaths)
    {
        string[] paths = assemblyPaths.Distinct(FileSystemPathComparison.Comparer).ToArray();
        lock (_gate)
        {
            _assemblyPaths = paths;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] paths;
        lock (_gate)
        {
            paths = _assemblyPaths;
        }
        // Der geteilte Metadaten-Cache darf fertig laden; ein abgebrochener UI-Aufruf wartet nicht darauf.
        return Task.Run(() => ReadSnapshot(paths, cancellationToken), CancellationToken.None)
            .WaitAsync(cancellationToken);
    }

    private ControlCatalogSnapshot ReadSnapshot(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        HashSet<string> assemblies = new(StringComparer.OrdinalIgnoreCase);
        List<string> diagnostics = new();
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using FileStream stream = File.OpenRead(path);
                using PEReader reader = new(stream);
                if (reader.HasMetadata)
                {
                    MetadataReader metadata = reader.GetMetadataReader();
                    assemblies.Add(metadata.GetString(metadata.GetAssemblyDefinition().Name));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                diagnostics.Add($"{path}: {error.Message}");
            }
        }

        List<ControlCatalogItem> controls = new();
        foreach (TypeMetadata type in _metadata.GetAvailableTypes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!type.IsControl || !type.IsInstantiable || !type.IsToolboxVisible || type.IsTopLevel)
            {
                continue;
            }
            if (!type.IsStandardControl && !assemblies.Contains(type.AssemblyName))
            {
                continue;
            }
            controls.Add(new ControlCatalogItem(type.Name, type.FullName,
                type.IsStandardControl ? "https://github.com/avaloniaui" : type.XmlNamespace,
                type.AssemblyName, type.DisplayName ?? type.Name,
                type.Category ?? (type.IsPanel ? "Layout" : "Controls"), type.IsStandardControl));
        }
        return new ControlCatalogSnapshot(controls, diagnostics);
    }
}
