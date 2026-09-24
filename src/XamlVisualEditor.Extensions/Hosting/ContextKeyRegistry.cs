namespace XamlVisualEditor.Extensions.Hosting;

/// <summary>Stores the context keys of all extensions; key names ignore case like the built-in condition names.</summary>
public sealed class ContextKeyRegistry : IContextKeys
{
    private readonly object _gate = new();
    private readonly Dictionary<string, bool> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public event EventHandler? Changed;

    /// <inheritdoc />
    public bool TryGet(string key, out bool value)
    {
        lock (_gate)
        {
            return _values.TryGetValue(key.Trim(), out value);
        }
    }

    /// <inheritdoc />
    public void Set(string key, bool value)
    {
        Set([new KeyValuePair<string, bool>(key, value)]);
    }

    /// <inheritdoc />
    public void Set(IEnumerable<KeyValuePair<string, bool>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        bool changed = false;
        lock (_gate)
        {
            foreach ((string key, bool value) in values)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(key);
                string name = key.Trim();
                if (!_values.TryGetValue(name, out bool current) || current != value)
                {
                    _values[name] = value;
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
