using System;
using System.Collections.Generic;
using System.IO;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Locates tf.exe.</summary>
public interface ITfExeLocator
{
    /// <summary>Returns the configured path when it exists, otherwise the newest detected installation, or null.</summary>
    string? Locate(string? configuredPath);
}

/// <summary>Finds tf.exe in the Team Explorer component of installed Visual Studio versions.</summary>
public sealed class TfExeLocator : ITfExeLocator
{
    private const string TF_RELATIVE_PATH = @"Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\TF.exe";

    private static readonly string[] Editions = ["Enterprise", "Professional", "Community", "BuildTools", "TeamExplorer"];

    private readonly Func<string, bool> _fileExists;
    private readonly Func<IReadOnlyList<string>> _installationRoots;
    private string? _cachedDetectedPath;

    /// <summary>Creates a locator that inspects the default Visual Studio installation folders.</summary>
    public TfExeLocator()
        : this(File.Exists, GetDefaultInstallationRoots)
    {
    }

    /// <summary>Creates a locator with custom file and installation lookups.</summary>
    public TfExeLocator(Func<string, bool> fileExists, Func<IReadOnlyList<string>> installationRoots)
    {
        _fileExists = fileExists;
        _installationRoots = installationRoots;
    }

    /// <inheritdoc />
    public string? Locate(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            string trimmed = configuredPath.Trim().Trim('"');
            return _fileExists(trimmed) ? trimmed : null;
        }

        if (_cachedDetectedPath is not null && _fileExists(_cachedDetectedPath))
        {
            return _cachedDetectedPath;
        }

        foreach (string root in _installationRoots())
        {
            string candidate = Path.Combine(root, TF_RELATIVE_PATH);
            if (_fileExists(candidate))
            {
                _cachedDetectedPath = candidate;
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Lists Visual Studio installation roots, newest version first.</summary>
    public static IReadOnlyList<string> GetDefaultInstallationRoots()
    {
        List<string> roots = new();
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string baseDirectory in new[] { programFiles, programFilesX86 })
        {
            if (string.IsNullOrEmpty(baseDirectory))
            {
                continue;
            }

            string visualStudioRoot = Path.Combine(baseDirectory, "Microsoft Visual Studio");
            if (!Directory.Exists(visualStudioRoot))
            {
                continue;
            }

            List<string> versions = new(Directory.GetDirectories(visualStudioRoot));
            versions.Sort(CompareVersionFolders);
            foreach (string versionDirectory in versions)
            {
                foreach (string edition in Editions)
                {
                    roots.Add(Path.Combine(versionDirectory, edition));
                }

                foreach (string editionDirectory in Directory.GetDirectories(versionDirectory))
                {
                    if (!roots.Contains(editionDirectory))
                    {
                        roots.Add(editionDirectory);
                    }
                }
            }
        }

        return roots;
    }

    // Visual Studio folders are named after the release year ("2022") or the major version ("18");
    // a major version below 100 is newer than every release year folder.
    private static int CompareVersionFolders(string left, string right)
    {
        return GetVersionRank(Path.GetFileName(right)).CompareTo(GetVersionRank(Path.GetFileName(left)));
    }

    private static int GetVersionRank(string name)
    {
        if (!int.TryParse(name, out int number))
        {
            return 0;
        }

        return number < 100 ? 10000 + number : number;
    }
}
