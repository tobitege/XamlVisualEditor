using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// Finds the git executable on PATH and, on Windows, in the default Git for Windows installation folders.
/// </summary>
public sealed class GitExecutableLocator
{
    private const string VERSION_PREFIX = "git version ";

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _pathVariable;
    private readonly Func<IReadOnlyList<string>> _installationPaths;
    private readonly string _executableName;

    /// <summary>Creates a locator that inspects the PATH variable and the default installation folders.</summary>
    public GitExecutableLocator()
        : this(
            File.Exists,
            static () => Environment.GetEnvironmentVariable("PATH"),
            GetDefaultInstallationPaths,
            OperatingSystem.IsWindows() ? "git.exe" : "git")
    {
    }

    /// <summary>Creates a locator with custom file, PATH and installation lookups.</summary>
    /// <param name="fileExists">Tells whether a file exists.</param>
    /// <param name="pathVariable">Returns the value of the PATH variable.</param>
    /// <param name="installationPaths">Lists full paths of git executables to try when PATH has none.</param>
    /// <param name="executableName">The file name of the git executable in a PATH folder.</param>
    public GitExecutableLocator(
        Func<string, bool> fileExists,
        Func<string?> pathVariable,
        Func<IReadOnlyList<string>> installationPaths,
        string executableName)
    {
        _fileExists = fileExists;
        _pathVariable = pathVariable;
        _installationPaths = installationPaths;
        _executableName = executableName;
    }

    /// <summary>Gets the oldest git version whose commands this editor uses, such as <c>git restore</c>.</summary>
    public static Version MinimumVersion { get; } = new(2, 23);

    /// <summary>
    /// Returns the full path of the first git executable in a fully qualified PATH folder, then the first existing
    /// installation path, or null when none exists. Relative PATH entries are skipped, so a git executable in the
    /// working directory is never picked up.
    /// </summary>
    public string? Locate()
    {
        string? pathVariable = _pathVariable();
        if (!string.IsNullOrEmpty(pathVariable))
        {
            foreach (string entry in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string directory = entry.Trim().Trim('"');
                if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
                {
                    continue;
                }

                string candidate = Path.Combine(directory, _executableName);
                if (_fileExists(candidate))
                {
                    return candidate;
                }
            }
        }

        foreach (string candidate in _installationPaths())
        {
            if (_fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Lists the git executables of the machine-wide and the per-user Git for Windows installation; empty on other
    /// operating systems.
    /// </summary>
    public static IReadOnlyList<string> GetDefaultInstallationPaths()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<string>();
        }

        List<string> paths = new();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles))
        {
            paths.Add(Path.Combine(programFiles, "Git", "cmd", "git.exe"));
        }

        string localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApplicationData))
        {
            paths.Add(Path.Combine(localApplicationData, "Programs", "Git", "cmd", "git.exe"));
        }

        return paths;
    }

    /// <summary>
    /// Reads the version from the output of <c>git --version</c>, for example <c>git version 2.45.1.windows.1</c>.
    /// </summary>
    public static bool TryParseVersion(string versionOutput, [NotNullWhen(true)] out Version? version)
    {
        version = null;
        string text = versionOutput.Trim();
        if (!text.StartsWith(VERSION_PREFIX, StringComparison.Ordinal))
        {
            return false;
        }

        string versionText = text.Substring(VERSION_PREFIX.Length).Split(' ')[0];
        string[] parts = versionText.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out int major)
            || !int.TryParse(parts[1], out int minor))
        {
            return false;
        }

        int build = parts.Length > 2 && int.TryParse(parts[2], out int patch) ? patch : 0;
        version = new Version(major, minor, build);
        return true;
    }
}
