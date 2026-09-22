using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Ermittelt Toolbox-Controls aus den Assemblies des aktuellen Projekts.</summary>
public sealed class MetadataControlCatalog : IControlCatalog, IControlCatalogUpdater
{
    private readonly ControlTypeReader _readTypes;
    private readonly object _gate = new();
    private string[] _assemblyPaths = Array.Empty<string>();
    private bool _autoLoadProjectControls = true;

    /// <summary>Verbindet den Katalog mit der abbrechbaren Metadaten-Suche.</summary>
    public MetadataControlCatalog(ControlTypeReader readTypes)
    {
        _readTypes = readTypes;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool HasProjectAssemblies { get { lock (_gate) return _assemblyPaths.Length > 0; } }

    /// <inheritdoc />
    public bool AutoLoadProjectControls { get { lock (_gate) return _autoLoadProjectControls; } }

    /// <inheritdoc />
    public void SetWorkspaceAssemblies(IEnumerable<string> assemblyPaths, bool autoLoadProjectControls = true)
    {
        string[] paths = assemblyPaths.Distinct(FileSystemPathComparison.Comparer).ToArray();
        lock (_gate)
        {
            _assemblyPaths = paths;
            _autoLoadProjectControls = autoLoadProjectControls;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <inheritdoc />
    public Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken,
        IProgress<ControlCatalogProgress>? progress = null, bool includeProjectControls = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] paths;
        lock (_gate)
        {
            paths = includeProjectControls ? _assemblyPaths : Array.Empty<string>();
        }
        return Task.Run(() => ReadSnapshot(paths, progress, cancellationToken), cancellationToken);
    }

    private ControlCatalogSnapshot ReadSnapshot(IReadOnlyList<string> paths,
        IProgress<ControlCatalogProgress>? progress, CancellationToken cancellationToken)
    {
        IReadOnlyList<TypeMetadata> types = _readTypes(paths, message => throw new IOException(message),
            (completed, total, assembly) => progress?.Report(new(completed, total, assembly)), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        List<ControlCatalogItem> controls = new();
        foreach (TypeMetadata type in types)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!type.IsControl || !type.IsInstantiable || !type.IsToolboxVisible || type.IsTopLevel)
            {
                continue;
            }
            controls.Add(new ControlCatalogItem(type.Name, type.FullName,
                type.IsStandardControl ? "https://github.com/avaloniaui" : type.XmlNamespace,
                type.AssemblyName, type.DisplayName ?? type.Name,
                type.Category ?? (type.IsPanel ? "Layout" : "Controls"), type.IsStandardControl));
        }
        return new ControlCatalogSnapshot(controls, Array.Empty<string>());
    }
}
