using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Maps user-defined layout names to the files that hold their serialized dock layout.
/// </summary>
public interface IUserLayoutStore
{
    /// <summary>Lists the names of all saved layouts in alphabetical order.</summary>
    IReadOnlyList<string> ListNames();

    /// <summary>Returns true when the name can be used as a layout name.</summary>
    bool IsValidName(string? name);

    /// <summary>Returns the file path that stores the layout with the given name.</summary>
    string GetPath(string name);

    /// <summary>Returns true when a layout with the given name exists.</summary>
    bool Exists(string name);

    /// <summary>Deletes the layout with the given name; returns true when a file was removed.</summary>
    bool Delete(string name);

    /// <summary>
    /// Renames a saved layout; returns true when the file was renamed. Fails when the new name is invalid or
    /// belongs to another saved layout; a change of letter case only is allowed.
    /// </summary>
    bool Rename(string name, string newName);
}

/// <summary>
/// File-based layout store. Each layout is a JSON file named after the layout inside one folder.
/// </summary>
public sealed class UserLayoutStore : IUserLayoutStore
{
    private const string FileExtension = ".json";
    private readonly string _directory;

    public UserLayoutStore(string directory)
    {
        _directory = directory;
    }

    /// <summary>Creates the store inside the shell settings folder.</summary>
    public static UserLayoutStore CreateDefault()
    {
        return new UserLayoutStore(Path.Combine(ShellSettingsPaths.GetRoot(), "layouts"));
    }

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(_directory))
        {
            return Array.Empty<string>();
        }

        List<string> names = new();
        foreach (string file in Directory.EnumerateFiles(_directory, "*" + FileExtension))
        {
            string name = Path.GetFileNameWithoutExtension(file);
            if (IsValidName(name))
            {
                names.Add(name);
            }
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    public bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length != name.Length)
        {
            return false;
        }

        return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
            && !name.EndsWith('.');
    }

    public string GetPath(string name)
    {
        if (!IsValidName(name))
        {
            throw new ArgumentException("Invalid layout name.", nameof(name));
        }

        Directory.CreateDirectory(_directory);
        return Path.Combine(_directory, name + FileExtension);
    }

    public bool Exists(string name)
    {
        return IsValidName(name) && File.Exists(Path.Combine(_directory, name + FileExtension));
    }

    public bool Delete(string name)
    {
        if (!Exists(name))
        {
            return false;
        }

        File.Delete(Path.Combine(_directory, name + FileExtension));
        return true;
    }

    public bool Rename(string name, string newName)
    {
        if (!Exists(name)
            || !IsValidName(newName)
            || string.Equals(name, newName, StringComparison.Ordinal)
            || ListNames().Any(existing => string.Equals(existing, newName, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(existing, name, StringComparison.Ordinal)))
        {
            return false;
        }

        File.Move(Path.Combine(_directory, name + FileExtension), Path.Combine(_directory, newName + FileExtension));
        return true;
    }
}
