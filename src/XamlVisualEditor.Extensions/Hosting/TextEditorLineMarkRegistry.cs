using XamlVisualEditor.Core;

namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>
/// Holds the line marks of all layers. Every layer keeps an immutable map that <see cref="ITextEditorLineMarkLayer.Set"/>
/// replaces, so lookups from the UI thread need no lock.
/// </summary>
public sealed class TextEditorLineMarkRegistry
{
    private readonly object _gate = new();
    private Layer[] _layers = [];

    /// <summary>Raised on the thread of the change with the full path whose marks changed.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>Creates an empty layer whose marks run <paramref name="clickCommandId"/> when clicked.</summary>
    public ITextEditorLineMarkLayer CreateLayer(string? clickCommandId)
    {
        Layer layer = new(this, clickCommandId);
        lock (_gate)
        {
            _layers = [.. _layers, layer];
        }

        return layer;
    }

    /// <summary>
    /// Gets the marks of a file, sorted by start line, from the first layer that has marks for it, and the click
    /// command of that layer; an empty list when no layer has marks for the file.
    /// </summary>
    public IReadOnlyList<TextEditorLineMark> Find(string? filePath, out string? clickCommandId)
    {
        clickCommandId = null;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return [];
        }

        string key = Path.GetFullPath(filePath);
        foreach (Layer layer in Volatile.Read(ref _layers))
        {
            if (layer.Entries.TryGetValue(key, out TextEditorLineMark[]? marks))
            {
                clickCommandId = layer.ClickCommandId;
                return marks;
            }
        }

        return [];
    }

    private void Remove(Layer layer, IEnumerable<string> paths)
    {
        lock (_gate)
        {
            _layers = Array.FindAll(_layers, candidate => candidate != layer);
        }

        foreach (string path in paths)
        {
            Changed?.Invoke(this, path);
        }
    }

    private sealed class Layer(TextEditorLineMarkRegistry owner, string? clickCommandId) : ITextEditorLineMarkLayer
    {
        private readonly object _gate = new();
        private Dictionary<string, TextEditorLineMark[]> _entries = new(FileSystemPathComparison.Comparer);
        private bool _disposed;

        public string? ClickCommandId { get; } = clickCommandId;

        public Dictionary<string, TextEditorLineMark[]> Entries => Volatile.Read(ref _entries);

        public void Set(string filePath, IReadOnlyList<TextEditorLineMark> marks)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            ArgumentNullException.ThrowIfNull(marks);
            TextEditorLineMark[] sorted = marks.ToArray();
            foreach (TextEditorLineMark mark in sorted)
            {
                ArgumentNullException.ThrowIfNull(mark);
                ArgumentOutOfRangeException.ThrowIfLessThan(mark.StartLine, 1, nameof(marks));
                ArgumentOutOfRangeException.ThrowIfNegative(mark.LineCount, nameof(marks));
            }

            Array.Sort(sorted, static (left, right) => left.StartLine.CompareTo(right.StartLine));
            string key = Path.GetFullPath(filePath);
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                Dictionary<string, TextEditorLineMark[]> entries = new(_entries, FileSystemPathComparison.Comparer);
                if (sorted.Length == 0)
                {
                    entries.Remove(key);
                }
                else
                {
                    entries[key] = sorted;
                }

                Volatile.Write(ref _entries, entries);
            }

            owner.Changed?.Invoke(owner, key);
        }

        public void Dispose()
        {
            Dictionary<string, TextEditorLineMark[]> entries;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                entries = _entries;
                Volatile.Write(ref _entries, new Dictionary<string, TextEditorLineMark[]>());
            }

            owner.Remove(this, entries.Keys);
        }
    }
}
