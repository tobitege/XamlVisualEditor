namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>In-memory command metadata registry.</summary>
public sealed class CommandMetadataRegistry : ICommandMetadataRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _metadata = new(StringComparer.Ordinal);

    public event EventHandler? Changed;

    public IDisposable Register(string commandId, CommandMetadata metadata)
    {
        if (string.IsNullOrWhiteSpace(commandId))
        {
            throw new ArgumentException("Command id is required.", nameof(commandId));
        }

        Entry entry = new(metadata);
        lock (_gate)
        {
            _metadata[commandId] = entry;
        }

        Changed?.Invoke(this, EventArgs.Empty);

        return new Registration(() => Unregister(commandId, entry));
    }

    public bool TryGet(string commandId, out CommandMetadata metadata)
    {
        lock (_gate)
        {
            if (_metadata.TryGetValue(commandId, out Entry? entry))
            {
                metadata = entry.Metadata;
                return true;
            }
        }

        metadata = null!;
        return false;
    }

    public IReadOnlyDictionary<string, CommandMetadata> GetAll()
    {
        lock (_gate)
        {
            Dictionary<string, CommandMetadata> results = new(_metadata.Count, StringComparer.Ordinal);
            foreach (KeyValuePair<string, Entry> pair in _metadata)
            {
                results.Add(pair.Key, pair.Value.Metadata);
            }

            return results;
        }
    }

    private void Unregister(string commandId, Entry entry)
    {
        lock (_gate)
        {
            if (!_metadata.TryGetValue(commandId, out Entry? current) || !ReferenceEquals(current, entry))
            {
                return;
            }

            _metadata.Remove(commandId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Gives each registration its own identity, so a replaced registration cannot remove its successor.</summary>
    private sealed class Entry
    {
        public Entry(CommandMetadata metadata)
        {
            Metadata = metadata;
        }

        public CommandMetadata Metadata { get; }
    }

    private sealed class Registration : IDisposable
    {
        private readonly Action _dispose;
        private bool _isDisposed;

        public Registration(Action dispose)
        {
            _dispose = dispose;
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _dispose();
            _isDisposed = true;
        }
    }
}
