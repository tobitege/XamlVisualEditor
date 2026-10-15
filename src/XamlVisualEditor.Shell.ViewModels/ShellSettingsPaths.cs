using System;
using System.IO;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>
/// Resolves the folder in which the shell persists its per-user state (dock layout,
/// named layouts, canvas layout, previewer trust and telemetry).
/// </summary>
public static class ShellSettingsPaths
{
    /// <summary>
    /// Gets or sets a replacement for the per-user settings folder. Test hosts point it at
    /// an isolated folder so real shell view models never read or overwrite the user's files.
    /// </summary>
    public static string? RootOverride { get; set; }

    /// <summary>Gets the settings folder and creates it when missing.</summary>
    public static string GetRoot()
    {
        string root = RootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XamlVisualEditor");
        Directory.CreateDirectory(root);
        return root;
    }
}
