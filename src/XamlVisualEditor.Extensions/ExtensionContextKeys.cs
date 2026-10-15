namespace XamlVisualEditor.Extensions;

/// <summary>Named boolean values that extensions set and that command and menu conditions read.</summary>
public interface IContextKeys
{
    /// <summary>Gets the value of a key; returns false when no extension has set it.</summary>
    bool TryGet(string key, out bool value);

    /// <summary>Sets the value of a key.</summary>
    void Set(string key, bool value);

    /// <summary>Sets several keys; <see cref="Changed"/> is raised once when any of them changed.</summary>
    void Set(IEnumerable<KeyValuePair<string, bool>> values);

    /// <summary>Raised after values changed.</summary>
    event EventHandler? Changed;
}
