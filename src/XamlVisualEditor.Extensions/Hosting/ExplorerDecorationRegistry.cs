using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Holds the explorer decorations of all layers. Every layer keeps an immutable map that <see cref="IExplorerDecorationLayer.Set"/>
/// replaces as a whole, so lookups from the UI thread need no lock.
/// </summary>
public sealed class ExplorerDecorationRegistry : IExplorerDecorations
{
    private readonly object _gate = new();
    private Layer[] _layers = [];

    /// <summary>Raised on the thread of the change after a layer was set or disposed.</summary>
    public event EventHandler? Changed;

    /// <inheritdoc />
    public IExplorerDecorationLayer CreateLayer()
    {
        Layer layer = new(this);
        lock (_gate)
        {
            _layers = [.. _layers, layer];
        }

        return layer;
    }

    /// <summary>Gets the decoration of a full path from the first layer that has one, or null.</summary>
    public ExplorerDecoration? Find(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        string key = Path.TrimEndingDirectorySeparator(path);
        foreach (Layer layer in Volatile.Read(ref _layers))
        {
            if (layer.Entries.TryGetValue(key, out ExplorerDecoration? decoration))
            {
                return decoration;
            }
        }

        return null;
    }

    private void Remove(Layer layer)
    {
        lock (_gate)
        {
            _layers = Array.FindAll(_layers, candidate => candidate != layer);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnLayerSet() => Changed?.Invoke(this, EventArgs.Empty);

    private sealed class Layer(ExplorerDecorationRegistry owner) : IExplorerDecorationLayer
    {
        private static readonly Dictionary<string, ExplorerDecoration> Empty = new();
        private volatile Dictionary<string, ExplorerDecoration> _entries = Empty;
        private int _disposed;

        public Dictionary<string, ExplorerDecoration> Entries => _entries;

        public void Set(IEnumerable<KeyValuePair<string, ExplorerDecoration>> decorations)
        {
            ArgumentNullException.ThrowIfNull(decorations);
            Dictionary<string, ExplorerDecoration> entries = new(FileSystemPathComparison.Comparer);
            foreach ((string path, ExplorerDecoration decoration) in decorations)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(path);
                ArgumentNullException.ThrowIfNull(decoration);
                entries[Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))] = decoration;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            _entries = entries;
            owner.OnLayerSet();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _entries = Empty;
            owner.Remove(this);
        }
    }
}
