using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace XamlVisualEditor.Workspace;

/// <summary>A git executable and, when it was resolved from a Git for Windows launcher, its installation.</summary>
/// <param name="Path">The full path of the executable, or the plain name <c>git</c>.</param>
/// <param name="Layout">
/// The Git for Windows installation whose launcher starts this git; null when git was not resolved from a launcher,
/// and its environment then stays unchanged.
/// </param>
internal sealed record GitExecutable(string Path, GitForWindowsLayout? Layout = null);

/// <summary>
/// A Git for Windows installation and the environment folder of its git, from which the launcher derives its
/// environment.
/// </summary>
/// <param name="Root">The installation folder, which holds <c>cmd</c>, <c>usr</c> and the environment folder.</param>
/// <param name="EnvironmentFolder">The environment folder: <c>mingw64</c>, <c>clangarm64</c> or <c>mingw32</c>.</param>
internal sealed record GitForWindowsLayout(string Root, string EnvironmentFolder)
{
    /// <summary>Gets the value of <c>MSYSTEM</c> that the launcher sets, such as <c>MINGW64</c>.</summary>
    public string MsysEnvironment => EnvironmentFolder.ToUpperInvariant();

    /// <summary>
    /// Gets the folders that the launcher puts in front of PATH before <c>%HOME%\bin</c>:
    /// <c>&lt;root&gt;\&lt;environment&gt;\bin</c> and <c>&lt;root&gt;\usr\bin</c>.
    /// </summary>
    public IReadOnlyList<string> SearchPathFolders =>
        [Path.Combine(Root, EnvironmentFolder, "bin"), Path.Combine(Root, "usr", "bin")];
}

/// <summary>
/// Finds the git executable on PATH and, on Windows, in the default Git for Windows installation folders.
/// </summary>
internal sealed class GitExecutableLocator
{
    private const string VERSION_PREFIX = "git version ";

    private readonly Func<string, bool> _fileExists;
    private readonly Func<string?> _pathVariable;
    private readonly Func<IReadOnlyList<string>> _installationPaths;
    private readonly string _executableName;
    private readonly Func<string, string?> _readFile;

    /// <summary>Creates a locator that inspects the PATH variable and the default installation folders.</summary>
    public GitExecutableLocator()
        : this(
            File.Exists,
            static () => Environment.GetEnvironmentVariable("PATH"),
            GetDefaultInstallationPaths,
            OperatingSystem.IsWindows() ? "git.exe" : "git",
            ReadFileOrNull)
    {
    }

    /// <summary>Creates a locator with custom file, PATH and installation lookups.</summary>
    /// <param name="fileExists">Tells whether a file exists.</param>
    /// <param name="pathVariable">Returns the value of the PATH variable.</param>
    /// <param name="installationPaths">Lists full paths of git executables to try when PATH has none.</param>
    /// <param name="executableName">The file name of the git executable in a PATH folder.</param>
    /// <param name="readFile">
    /// Returns the text of a file, or null when it cannot be read; no file can be read when null.
    /// </param>
    public GitExecutableLocator(
        Func<string, bool> fileExists,
        Func<string?> pathVariable,
        Func<IReadOnlyList<string>> installationPaths,
        string executableName,
        Func<string, string?>? readFile = null)
    {
        _fileExists = fileExists;
        _pathVariable = pathVariable;
        _installationPaths = installationPaths;
        _executableName = executableName;
        _readFile = readFile ?? (static _ => null);
    }

    /// <summary>
    /// Gets the oldest git version whose commands this editor uses, such as <c>git restore</c> with
    /// <c>--pathspec-from-file</c>.
    /// </summary>
    public static Version MinimumVersion { get; } = new(2, 26);

    /// <summary>
    /// Returns the first git executable in a fully qualified PATH folder, then the first existing installation path,
    /// or null when none exists. Relative PATH entries are skipped, so a git executable in the working directory is
    /// never picked up. A Git for Windows launcher is replaced by the git executable it starts, see
    /// <see cref="ResolveLauncher"/>.
    /// </summary>
    public GitExecutable? Locate()
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
                    return ResolveLauncher(candidate);
                }
            }
        }

        foreach (string candidate in _installationPaths())
        {
            if (_fileExists(candidate))
            {
                return ResolveLauncher(candidate);
            }
        }

        return null;
    }

    /// <summary>
    /// Returns the git executable that a Git for Windows launcher starts. The installer puts <c>&lt;root&gt;\cmd</c>
    /// on PATH; its <c>git.exe</c>, like <c>&lt;root&gt;\bin\git.exe</c>, is a launcher that sets <c>MSYSTEM</c>, puts
    /// <c>&lt;root&gt;\&lt;environment&gt;\bin</c>, <c>&lt;root&gt;\usr\bin</c> and <c>%HOME%\bin</c> in this order in
    /// front of PATH and starts <c>&lt;root&gt;\&lt;environment&gt;\bin\git.exe</c>, where the environment is
    /// <c>mingw64</c>, <c>clangarm64</c> or <c>mingw32</c>. The returned executable is that git with its
    /// <see cref="GitForWindowsLayout"/>, from which <see cref="GitProcessRunner"/> sets the environment of the
    /// launcher, so the process the editor starts is git itself with the environment of the launcher. A scoop shim is
    /// replaced by the program it starts, which is then resolved the same way, see <see cref="ResolveScoopShim"/>. Any
    /// other path is returned unchanged, without layout.
    /// </summary>
    internal GitExecutable ResolveLauncher(string path)
    {
        string launcher = ResolveScoopShim(path) ?? path;
        string? folder = Path.GetDirectoryName(launcher);
        string? root = string.IsNullOrEmpty(folder) ? null : Path.GetDirectoryName(folder);
        string folderName = Path.GetFileName(folder) ?? string.Empty;
        if (!string.IsNullOrEmpty(root)
            && string.Equals(Path.GetFileName(launcher), "git.exe", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(folderName, "cmd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(folderName, "bin", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (string environment in GetEnvironmentFolders())
            {
                string candidate = Path.Combine(root, environment, "bin", "git.exe");
                if (_fileExists(candidate))
                {
                    return new GitExecutable(candidate, new GitForWindowsLayout(root, environment));
                }
            }
        }

        return new GitExecutable(launcher);
    }

    /// <summary>
    /// Returns the program that a scoop shim starts, or null when <paramref name="path"/> is no shim. Scoop puts
    /// <c>&lt;scoop&gt;\shims</c> on PATH; its <c>git.exe</c> starts the program named by the line
    /// <c>path = "&lt;program&gt;"</c> of <c>git.shim</c> next to it. A shim with an <c>args</c> line is not resolved,
    /// because the program would then miss these arguments.
    /// </summary>
    private string? ResolveScoopShim(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string shim = Path.ChangeExtension(path, ".shim");
        string? text = _fileExists(shim) ? _readFile(shim) : null;
        if (text is null)
        {
            return null;
        }

        string? program = null;
        foreach (string line in text.Split('\n'))
        {
            int separator = line.IndexOf('=', StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            string key = line[..separator].Trim();
            if (string.Equals(key, "args", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
            {
                program = line[(separator + 1)..].Trim().Trim('"');
            }
        }

        return program is not null && Path.IsPathFullyQualified(program) && _fileExists(program) ? program : null;
    }

    private static string? ReadFileOrNull(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // The environment of the machine's processor architecture first.
    private static string[] GetEnvironmentFolders() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? ["clangarm64", "mingw64", "mingw32"]
            : ["mingw64", "mingw32", "clangarm64"];

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
