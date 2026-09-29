using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Determines the toolbox controls from the assemblies of the current project.</summary>
public sealed class MetadataControlCatalog : IControlCatalog, IControlCatalogUpdater
{
    private const int MAX_CACHED_SCOPES = 16;
    private readonly ControlTypeReader _readTypes;
    private readonly object _gate = new();
    private readonly Dictionary<string, ControlCatalogSnapshot> _snapshots = new(StringComparer.Ordinal);
    private string[] _assemblyPaths = Array.Empty<string>();

    /// <summary>Connects the catalog with the cancellable metadata search.</summary>
    public MetadataControlCatalog(ControlTypeReader readTypes)
    {
        _readTypes = readTypes;
    }

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool HasProjectAssemblies { get { lock (_gate) return _assemblyPaths.Length > 0; } }

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
    public Task<ControlCatalogSnapshot> GetControlsAsync(CancellationToken cancellationToken,
        IProgress<ControlCatalogProgress>? progress = null, bool includeProjectControls = true, bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string[] paths;
        lock (_gate)
        {
            paths = _assemblyPaths;
        }
        return Task.Run(() =>
        {
            // NuGet versions have paths of their own; rebuilds at the same path get a new fingerprint.
            string key = includeProjectControls + "|" + string.Join("|", paths.Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileInfo file = new(path);
                return path + ":" + (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing");
            }));
            cancellationToken.ThrowIfCancellationRequested();
            lock (_gate)
                if (!forceRefresh && _snapshots.TryGetValue(key, out ControlCatalogSnapshot? cached)) return cached;
            ControlCatalogSnapshot snapshot = ReadSnapshot(paths, progress, cancellationToken, includeProjectControls);
            lock (_gate)
            {
                if (_snapshots.Count >= MAX_CACHED_SCOPES) _snapshots.Clear();
                _snapshots[key] = snapshot;
            }
            return snapshot;
        }, cancellationToken);
    }

    private ControlCatalogSnapshot ReadSnapshot(IReadOnlyList<string> paths,
        IProgress<ControlCatalogProgress>? progress, CancellationToken cancellationToken, bool includeProjectControls)
    {
        IReadOnlyList<TypeMetadata> types = _readTypes(paths, message => throw new IOException(message),
            (completed, total, assembly) => progress?.Report(new(completed, total, assembly)), cancellationToken,
            includeProjectControls);
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
                type.Category ?? (type.IsPanel ? "Layout" : "Controls"), type.IsStandardControl)
            {
                AssemblyVersion = type.AssemblyVersion
            });
        }
        return new ControlCatalogSnapshot(controls, Array.Empty<string>());
    }
}
