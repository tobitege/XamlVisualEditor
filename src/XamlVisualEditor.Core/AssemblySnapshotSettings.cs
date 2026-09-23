using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XamlVisualEditor.Core;

/// <summary>Machine-local assembly snapshots for workspaces built by another process.</summary>
public sealed class AssemblySnapshotSettings
{
    public required string WorkspaceRoot { get; init; }
    public required string SourceDirectory { get; init; }
    public required string SnapshotDirectory { get; init; }
    public string? PreviewApplication { get; init; }

    public static AssemblySnapshotSettings? FindForWorkspace(string workspacePath, string? settingsPath = null)
    {
        settingsPath ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "XamlVisualEditor", "assembly-snapshots.json");
        if (!File.Exists(settingsPath)) return null;

        AssemblySnapshotSettings[] settings = JsonSerializer.Deserialize<AssemblySnapshotSettings[]>(
            SharedFileReader.ReadAllText(settingsPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? Array.Empty<AssemblySnapshotSettings>();
        AssemblySnapshotSettings? selected = settings
            .Where(setting => IsWithin(workspacePath, setting.WorkspaceRoot))
            .OrderByDescending(setting => setting.WorkspaceRoot.Length)
            .FirstOrDefault();
        if (selected is null) return null;
        if (!Path.IsPathFullyQualified(selected.WorkspaceRoot)
            || !Path.IsPathFullyQualified(selected.SourceDirectory)
            || !Path.IsPathFullyQualified(selected.SnapshotDirectory))
            throw new InvalidOperationException("Assembly snapshot paths must be absolute.");
        if (!Directory.Exists(selected.SnapshotDirectory))
            throw new DirectoryNotFoundException("Assembly snapshot directory not found: " + selected.SnapshotDirectory);
        if (IsWithin(selected.SnapshotDirectory, selected.SourceDirectory))
            throw new InvalidOperationException("The assembly snapshot must be outside the live output directory.");
        return selected;
    }

    public Dictionary<string, string> CreateMSBuildProperties() => new()
    {
        ["OutDir"] = Path.GetFullPath(SnapshotDirectory).TrimEnd(Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
        ["ReferencePath"] = Path.GetFullPath(SnapshotDirectory),
        ["AssemblySearchPaths"] = "{CandidateAssemblyFiles};" + Path.GetFullPath(SnapshotDirectory)
            + ";{HintPathFromItem};{TargetFrameworkDirectory};{RawFileName}"
    };

    public string ResolveAssemblyPath(string assemblyPath)
    {
        if (IsWithin(assemblyPath, SourceDirectory))
            return RequireSnapshotFile(Path.GetRelativePath(Path.GetFullPath(SourceDirectory),
                Path.GetFullPath(assemblyPath)));
        string candidate = Path.Combine(SnapshotDirectory, Path.GetFileName(assemblyPath));
        return File.Exists(candidate) ? Path.GetFullPath(candidate) : assemblyPath;
    }

    public string ResolveOutputAssembly(string? outputPath, string assemblyName) =>
        RequireSnapshotFile(string.IsNullOrWhiteSpace(outputPath) ? assemblyName + ".dll" : Path.GetFileName(outputPath));

    public string? ResolvePreviewApplication() =>
        string.IsNullOrWhiteSpace(PreviewApplication) ? null : RequireSnapshotFile(PreviewApplication);

    public IReadOnlyList<AssemblyReference> GetAssemblyReferences() =>
        Directory.EnumerateFiles(SnapshotDirectory, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(path => new AssemblyReference { Name = Path.GetFileNameWithoutExtension(path), Path = path })
            .ToArray();

    public void EnsureNoLiveReferences(IEnumerable<string> paths)
    {
        string? livePath = paths.FirstOrDefault(path => IsWithin(path, SourceDirectory));
        if (livePath is not null)
            throw new InvalidOperationException("The workspace still references live build output: " + livePath
                + ". Refresh the assembly snapshot or update its workspace configuration.");
    }

    private string RequireSnapshotFile(string relativePath)
    {
        string path = Path.GetFullPath(Path.Combine(SnapshotDirectory, relativePath));
        if (!IsWithin(path, SnapshotDirectory))
            throw new InvalidOperationException("Assembly path escapes the snapshot directory.");
        if (!File.Exists(path))
            throw new FileNotFoundException("Snapshot assembly missing. Refresh the snapshot before opening this workspace.", path);
        return path;
    }

    private static bool IsWithin(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return FileSystemPathComparison.Equals(fullPath, root)
            || fullPath.StartsWith(root + Path.DirectorySeparatorChar, FileSystemPathComparison.Comparison);
    }
}
