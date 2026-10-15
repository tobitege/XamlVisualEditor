using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.ComponentModel;
using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Markup.Xaml;
using Avalonia.Media;



namespace XamlVisualEditor.Workspace;

/// <summary>
/// Loads and manages MSBuild workspace and project information.
/// </summary>
public sealed class WorkspaceService : IWorkspaceService, IDisposable
{
    private static readonly TimeSpan ProjectLoadTimeout = TimeSpan.FromSeconds(30);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private readonly ILogger<WorkspaceService> _logger;

    public WorkspaceService(ILogger<WorkspaceService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceService>.Instance;
    }

    /// <summary>
    /// Ensures MSBuild is located and registered.
    /// </summary>
    private void EnsureMSBuildRegistered()
    {
        EnsureDotnetRoot();
        if (!MSBuildLocator.IsRegistered)
        {
            IReadOnlyList<VisualStudioInstance> instances = MSBuildLocator.QueryVisualStudioInstances()
                .OrderByDescending(i => i.Version)
                .ToList();

            LogMsBuildInstances(instances);

            VisualStudioInstance? instance = instances.FirstOrDefault();
            if (instance is not null)
            {
                _logger.LogInformation("MSBuild selected: {Name} {Version} at {Path}",
                    instance.Name,
                    instance.Version,
                    instance.MSBuildPath);
            }

            VisualStudioInstance? instanceToRegister = instances.FirstOrDefault();
            if (instanceToRegister is not null)
            {
                MSBuildLocator.RegisterInstance(instanceToRegister);
            }
            else
            {
                _logger.LogInformation("MSBuild instances not found. Falling back to defaults.");
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private void EnsureDotnetRoot()
    {
        string? dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrWhiteSpace(dotnetRoot))
        {
            return;
        }

        string? arm64Root = Environment.GetEnvironmentVariable("DOTNET_ROOT_ARM64");
        if (!string.IsNullOrWhiteSpace(arm64Root))
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", arm64Root);
            _logger.LogInformation("DOTNET_ROOT set to {Path} from DOTNET_ROOT_ARM64", arm64Root);
        }
    }

    private void LogMsBuildInstances(IReadOnlyList<VisualStudioInstance> instances)
    {
        if (instances.Count == 0)
        {
            _logger.LogInformation("MSBuild instances: none");
            return;
        }

        _logger.LogInformation("MSBuild instances:");
        foreach (VisualStudioInstance instance in instances)
        {
            _logger.LogInformation("- {Name} {Version} at {Path}",
                instance.Name,
                instance.Version,
                instance.MSBuildPath);
        }
    }

    /// <inheritdoc />
    public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
    {
        return Task.Run(() => LoadSolutionCoreAsync(solutionPath, ct), ct);
    }

    private async Task<WorkspaceModel> LoadSolutionCoreAsync(string solutionPath, CancellationToken ct)
    {
        IDisposable? workspaceFailed = null;
        try
        {
            EnsureMSBuildRegistered();

            _workspace = MSBuildWorkspace.Create();
            workspaceFailed = _workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);
            _logger.LogInformation("Loading solution: {Path}", solutionPath);
            Progress<ProjectLoadProgress> progress = new(p =>
                _logger.LogInformation("Workspace load: {Progress}", FormatProgress(p)));
            _solution = await _workspace.OpenSolutionAsync(solutionPath, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            workspaceFailed?.Dispose();
        }

        List<ProjectModel> projects = new();
        IReadOnlyDictionary<string, string> projectFolders = ParseSolutionFolders(solutionPath);

        foreach (Project project in _solution.Projects)
        {
            Dictionary<string, XamlFileModel> xamlFiles = new(FileSystemPathComparison.Comparer);
            Dictionary<string, ProjectFileModel> files = new(FileSystemPathComparison.Comparer);
            List<AssemblyReference> references = new();

            if (!string.IsNullOrWhiteSpace(project.FilePath))
            {
                AddProjectFile(files, project.FilePath, project.FilePath, project.Name + ".csproj");
            }

            // Find XAML files
            foreach (AdditionalDocument additional in project.AdditionalDocuments)
            {
                string filePath = additional.FilePath ?? additional.Name;
                AddProjectFile(files, project.FilePath, filePath, additional.Name);
                AddXamlFile(xamlFiles, filePath, additional.Name);
            }

            // Also check documents for XAML
            foreach (Document doc in project.Documents)
            {
                string filePath = doc.FilePath ?? doc.Name;
                AddProjectFile(files, project.FilePath, filePath, doc.Name);
                AddXamlFile(xamlFiles, filePath, doc.Name);
            }

            foreach (XamlFileModel file in LoadXamlFromProjectFile(project.FilePath))
            {
                AddXamlFile(xamlFiles, file.FilePath, file.RelativePath);
                AddProjectFile(files, project.FilePath, file.FilePath, file.RelativePath);
            }

            // Collect assembly references
            foreach (MetadataReference metaRef in project.MetadataReferences)
            {
                if (metaRef is PortableExecutableReference peRef && peRef.FilePath is not null)
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(peRef.FilePath);
                    references.Add(new AssemblyReference { Name = name, Path = peRef.FilePath });
                }
            }

            projects.Add(new ProjectModel
            {
                Name = project.Name,
                ProjectPath = project.FilePath ?? string.Empty,
                XamlFiles = xamlFiles.Values.ToList(),
                Files = files.Values.ToList(),
                References = references,
                OutputAssemblyPath = project.OutputFilePath,
                TargetFramework = TryGetTargetFrameworkFromOutputPath(project.OutputFilePath),
                IsExecutable = IsExecutableProject(project)
            });
        }

        return new WorkspaceModel
        {
            Projects = projects,
            ProjectFolders = projectFolders
        };
    }

    /// <inheritdoc />
    public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        return Task.Run(() => LoadProjectCoreAsync(projectPath, ct), ct);
    }

    private async Task<WorkspaceModel> LoadProjectCoreAsync(string projectPath, CancellationToken ct)
    {
        IDisposable? workspaceFailed = null;
        Project? project = null;
        try
        {
            EnsureMSBuildRegistered();

            _workspace = MSBuildWorkspace.Create();
            workspaceFailed = _workspace.RegisterWorkspaceFailedHandler(OnWorkspaceFailed);
            _logger.LogInformation("Loading project: {Path}", projectPath);
            Progress<ProjectLoadProgress> progress = new(p =>
                _logger.LogInformation("Workspace load: {Progress}", FormatProgress(p)));
            using CancellationTokenSource timeout = new(ProjectLoadTimeout);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                project = await _workspace.OpenProjectAsync(projectPath, progress, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "MSBuild workspace load timed out after {Seconds} seconds. Using lightweight project metadata.",
                    ProjectLoadTimeout.TotalSeconds);
                _workspace.Dispose();
                _workspace = null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "MSBuild workspace load failed: {Message}. Using lightweight project metadata.",
                    ex.Message);
                _workspace.Dispose();
                _workspace = null;
                project = null;
            }
        }
        finally
        {
            workspaceFailed?.Dispose();
        }

        if (project is null)
        {
            return CreateFallbackWorkspace(projectPath);
        }

        try
        {
            Dictionary<string, XamlFileModel> xamlFiles = new(FileSystemPathComparison.Comparer);
            Dictionary<string, ProjectFileModel> files = new(FileSystemPathComparison.Comparer);
            List<AssemblyReference> references = new();

            if (!string.IsNullOrWhiteSpace(project.FilePath))
            {
                AddProjectFile(files, project.FilePath, project.FilePath, project.Name + ".csproj");
            }

            foreach (Document doc in project.Documents)
            {
                string filePath = doc.FilePath ?? doc.Name;
                AddProjectFile(files, project.FilePath, filePath, doc.Name);
                AddXamlFile(xamlFiles, filePath, doc.Name);
            }

            foreach (XamlFileModel file in LoadXamlFromProjectFile(project.FilePath))
            {
                AddXamlFile(xamlFiles, file.FilePath, file.RelativePath);
                AddProjectFile(files, project.FilePath, file.FilePath, file.RelativePath);
            }

            foreach (MetadataReference metaRef in project.MetadataReferences)
            {
                if (metaRef is PortableExecutableReference peRef && peRef.FilePath is not null)
                {
                    string name = System.IO.Path.GetFileNameWithoutExtension(peRef.FilePath);
                    references.Add(new AssemblyReference { Name = name, Path = peRef.FilePath });
                }
            }

            ProjectModel projectModel = new()
            {
                Name = project.Name,
                ProjectPath = project.FilePath ?? string.Empty,
                XamlFiles = xamlFiles.Values.ToList(),
                Files = files.Values.ToList(),
                References = references,
                OutputAssemblyPath = project.OutputFilePath,
                TargetFramework = TryGetTargetFrameworkFromOutputPath(project.OutputFilePath),
                IsExecutable = IsExecutableProject(project)
            };

            return new WorkspaceModel
            {
                Projects = new List<ProjectModel> { projectModel },
                ProjectFolders = new Dictionary<string, string>(FileSystemPathComparison.Comparer)
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "MSBuild project metadata read failed: {Message}. Using lightweight project metadata.",
                ex.Message);
            _workspace?.Dispose();
            _workspace = null;
            return CreateFallbackWorkspace(projectPath);
        }
    }

    internal static WorkspaceModel CreateFallbackWorkspace(string projectPath)
    {
        string projectName = Path.GetFileNameWithoutExtension(projectPath);
        string assemblyName = projectName;
        string? targetFramework = null;
        bool isExecutable = false;

        try
        {
            XDocument document = XDocument.Load(projectPath, LoadOptions.None);
            IEnumerable<XElement> properties = document.Descendants();
            assemblyName = properties.FirstOrDefault(element => element.Name.LocalName == "AssemblyName")?.Value
                ?.Trim() ?? projectName;
            targetFramework = properties.FirstOrDefault(element => element.Name.LocalName == "TargetFramework")?.Value
                ?.Trim();
            string? outputType = properties.FirstOrDefault(element => element.Name.LocalName == "OutputType")?.Value
                ?.Trim();
            isExecutable = string.Equals(outputType, "Exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(outputType, "WinExe", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
        }

        string? outputAssemblyPath = FindFallbackOutputAssembly(projectPath, assemblyName, targetFramework);
        ProjectModel project = new()
        {
            Name = projectName,
            ProjectPath = projectPath,
            XamlFiles = Array.Empty<XamlFileModel>(),
            Files = new[]
            {
                new ProjectFileModel
                {
                    FilePath = projectPath,
                    RelativePath = Path.GetFileName(projectPath)
                }
            },
            References = Array.Empty<AssemblyReference>(),
            OutputAssemblyPath = outputAssemblyPath,
            TargetFramework = targetFramework,
            IsExecutable = isExecutable
        };

        return new WorkspaceModel
        {
            Projects = new[] { project },
            ProjectFolders = new Dictionary<string, string>(FileSystemPathComparison.Comparer)
        };
    }

    private static string? FindFallbackOutputAssembly(
        string projectPath,
        string assemblyName,
        string? targetFramework)
    {
        string? projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        string fileName = assemblyName + ".dll";
        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            foreach (string configuration in new[] { "Debug", "Release" })
            {
                string candidate = Path.Combine(
                    projectDirectory,
                    "bin",
                    configuration,
                    targetFramework,
                    fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        DirectoryInfo? ancestor = new(projectDirectory);
        for (int depth = 0; ancestor is not null && depth < 4; depth++, ancestor = ancestor.Parent)
        {
            string buildDirectory = Path.Combine(ancestor.FullName, "build");
            if (!Directory.Exists(buildDirectory))
            {
                continue;
            }

            try
            {
                string? match = Directory.EnumerateFiles(buildDirectory, fileName, SearchOption.AllDirectories)
                    .FirstOrDefault(path => !path.Contains(
                        Path.DirectorySeparatorChar + "ref" + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(match))
                {
                    return match;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    /// <inheritdoc />
    public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath)
    {
        string fileName = System.IO.Path.GetFileName(xamlFilePath);
        XamlFileModel xamlFile = new() { FilePath = xamlFilePath, RelativePath = fileName };
        ProjectModel project = new()
        {
            Name = fileName,
            ProjectPath = string.Empty,
            XamlFiles = new List<XamlFileModel> { xamlFile },
            Files = new List<ProjectFileModel>
            {
                new()
                {
                    FilePath = xamlFilePath,
                    RelativePath = fileName
                }
            },
            References = Array.Empty<AssemblyReference>(),
            TargetFramework = null,
            IsExecutable = false
        };

        return new WorkspaceModel
        {
            Projects = new List<ProjectModel> { project },
            ProjectFolders = new Dictionary<string, string>(FileSystemPathComparison.Comparer)
        };
    }

    public void Dispose()
    {
        _workspace?.Dispose();
    }

    private static bool IsExecutableProject(Project project)
    {
        OutputKind? outputKind = project.CompilationOptions?.OutputKind;
        return outputKind == OutputKind.ConsoleApplication
               || outputKind == OutputKind.WindowsApplication
               || outputKind == OutputKind.WindowsRuntimeApplication;
    }

    private static string? TryGetTargetFrameworkFromOutputPath(string? outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(outputFilePath))
        {
            return null;
        }

        string normalized = outputFilePath.Replace('\\', '/');
        string[] parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length - 2; i++)
        {
            if (!string.Equals(parts[i], "bin", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(parts[i + 1], "Debug", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(parts[i + 1], "Release", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string tfm = parts[i + 2];
            return string.IsNullOrWhiteSpace(tfm) ? null : tfm;
        }

        return null;
    }

    private static void AddXamlFile(
        Dictionary<string, XamlFileModel> xamlFiles,
        string filePath,
        string relativePath)
    {
        if (!filePath.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) &&
            !filePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!xamlFiles.ContainsKey(filePath))
        {
            xamlFiles[filePath] = new XamlFileModel
            {
                FilePath = filePath,
                RelativePath = relativePath
            };
        }
    }

    private static void AddProjectFile(
        Dictionary<string, ProjectFileModel> files,
        string? projectPath,
        string filePath,
        string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return;
        }

        if (files.ContainsKey(filePath))
        {
            return;
        }

        string relative = GetRelativePath(projectPath, filePath, fallbackName);
        files[filePath] = new ProjectFileModel
        {
            FilePath = filePath,
            RelativePath = relative
        };
    }

    private static string GetRelativePath(string? projectPath, string filePath, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return fallbackName;
        }

        string? projectDir = System.IO.Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return fallbackName;
        }

        try
        {
            return System.IO.Path.GetRelativePath(projectDir, filePath);
        }
        catch
        {
            return fallbackName;
        }
    }

    private static IReadOnlyDictionary<string, string> ParseSolutionFolders(string solutionPath)
    {
        if (string.IsNullOrWhiteSpace(solutionPath) || !System.IO.File.Exists(solutionPath))
        {
            return new Dictionary<string, string>(FileSystemPathComparison.Comparer);
        }

        if (solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            return ParseSolutionFoldersFromSlnx(solutionPath);
        }

        const string solutionFolderGuid = "{66A26720-8FB5-11D2-AA7E-00C04F688DDE}";
        string solutionDir = System.IO.Path.GetDirectoryName(solutionPath) ?? string.Empty;
        Dictionary<string, string> projectPaths = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> folderNames = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> nested = new(StringComparer.OrdinalIgnoreCase);

        string[] lines;
        try
        {
            lines = System.IO.File.ReadAllLines(solutionPath);
        }
        catch
        {
            return new Dictionary<string, string>(FileSystemPathComparison.Comparer);
        }

        bool inNested = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryParseProjectLine(line, out string? typeGuid, out string? name, out string? path, out string? projectGuid))
                {
                    continue;
                }

                if (string.Equals(typeGuid, solutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(projectGuid) && !string.IsNullOrWhiteSpace(name))
                    {
                        folderNames[projectGuid] = name;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(projectGuid) && !string.IsNullOrWhiteSpace(path))
                {
                    string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, path));
                    projectPaths[projectGuid] = fullPath;
                }

                continue;
            }

            if (line.TrimStart().StartsWith("GlobalSection(NestedProjects)", StringComparison.OrdinalIgnoreCase))
            {
                inNested = true;
                continue;
            }

            if (inNested)
            {
                if (line.TrimStart().StartsWith("EndGlobalSection", StringComparison.OrdinalIgnoreCase))
                {
                    inNested = false;
                    continue;
                }

                string[] parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2)
                {
                    nested[parts[0]] = parts[1];
                }
            }
        }

        Dictionary<string, string> result = new(FileSystemPathComparison.Comparer);
        foreach (KeyValuePair<string, string> project in projectPaths)
        {
            string projectGuid = project.Key;
            string projectPath = project.Value;
            List<string> folders = new();

            string? parent = nested.TryGetValue(projectGuid, out string? parentGuid) ? parentGuid : null;
            while (!string.IsNullOrWhiteSpace(parent) && folderNames.TryGetValue(parent, out string? folderName))
            {
                folders.Add(folderName);
                parent = nested.TryGetValue(parent, out string? next) ? next : null;
            }

            if (folders.Count > 0)
            {
                folders.Reverse();
                result[projectPath] = string.Join('/', folders);
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string> ParseSolutionFoldersFromSlnx(string solutionPath)
    {
        Dictionary<string, string> result = new(FileSystemPathComparison.Comparer);
        string solutionDir = System.IO.Path.GetDirectoryName(solutionPath) ?? string.Empty;

        XDocument doc;
        try
        {
            doc = XDocument.Load(solutionPath);
        }
        catch
        {
            return result;
        }

        XElement? root = doc.Root;
        if (root is null)
        {
            return result;
        }

        foreach (XElement project in root.Elements("Project"))
        {
            string? projectPath = (string?)project.Attribute("Path");
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                continue;
            }

            string? folder = NormalizeSolutionFolderName((string?)project.Attribute("Folder"));
            if (string.IsNullOrWhiteSpace(folder))
            {
                continue;
            }

            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, projectPath));
            result[fullPath] = folder;
        }

        foreach (XElement folder in root.Elements("Folder"))
        {
            VisitSlnxFolder(folder, solutionDir, string.Empty, result);
        }

        return result;
    }

    private static void VisitSlnxFolder(
        XElement folder,
        string solutionDir,
        string parentPath,
        Dictionary<string, string> result)
    {
        string? name = NormalizeSolutionFolderName((string?)folder.Attribute("Name"));
        string currentPath = CombineSolutionFolderPath(parentPath, name);

        foreach (XElement project in folder.Elements("Project"))
        {
            string? projectPath = (string?)project.Attribute("Path");
            if (string.IsNullOrWhiteSpace(projectPath))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentPath))
            {
                continue;
            }

            string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(solutionDir, projectPath));
            result[fullPath] = currentPath;
        }

        foreach (XElement childFolder in folder.Elements("Folder"))
        {
            VisitSlnxFolder(childFolder, solutionDir, currentPath, result);
        }
    }

    private static string? NormalizeSolutionFolderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string trimmed = name.Replace('\\', '/').Trim().Trim('/');
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static string CombineSolutionFolderPath(string parent, string? child)
    {
        if (string.IsNullOrWhiteSpace(child))
        {
            return parent;
        }

        if (string.IsNullOrWhiteSpace(parent))
        {
            return child;
        }

        return parent + "/" + child;
    }

    private static bool TryParseProjectLine(
        string line,
        out string? typeGuid,
        out string? name,
        out string? path,
        out string? projectGuid)
    {
        typeGuid = null;
        name = null;
        path = null;
        projectGuid = null;

        int typeStart = line.IndexOf('"');
        if (typeStart < 0)
        {
            return false;
        }

        int typeEnd = line.IndexOf('"', typeStart + 1);
        if (typeEnd < 0)
        {
            return false;
        }

        typeGuid = line.Substring(typeStart + 1, typeEnd - typeStart - 1);

        string[] parts = line.Split('=', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            return false;
        }

        string[] fields = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length < 3)
        {
            return false;
        }

        name = TrimQuotes(fields[0]);
        path = TrimQuotes(fields[1]);
        projectGuid = TrimQuotes(fields[2]);
        return true;
    }

    private static string TrimQuotes(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }

    internal IReadOnlyList<XamlFileModel> LoadXamlFromProjectFile(string? projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !System.IO.File.Exists(projectPath))
        {
            return Array.Empty<XamlFileModel>();
        }

        string? projectDir = System.IO.Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(projectDir))
        {
            return Array.Empty<XamlFileModel>();
        }

        List<XamlFileModel> results = new();
        XDocument doc;
        try
        {
            doc = XDocument.Load(projectPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read project '{Path}': {Message}", projectPath, ex.Message);
            return results;
        }

        IEnumerable<XElement> items = doc.Descendants()
            .Where(e => string.Equals(e.Name.LocalName, "AvaloniaXaml", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name.LocalName, "Page", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name.LocalName, "ApplicationDefinition", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name.LocalName, "None", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(e.Name.LocalName, "Content", StringComparison.OrdinalIgnoreCase));

        foreach (XElement item in items)
        {
            string? include = item.Attribute("Include")?.Value ?? item.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            foreach (string file in ResolveItemFiles(projectDir, include, item.Attribute("Exclude")?.Value))
            {
                string rel = System.IO.Path.GetRelativePath(projectDir, file);
                results.Add(new XamlFileModel { FilePath = file, RelativePath = rel });
            }
        }

        // An SDK-style project includes its XAML files by default glob, whatever other items it lists; explicit items
        // such as a None update of a text file must not hide them.
        if (results.Count == 0 || UsesDefaultXamlItems(doc))
        {
            string[] removed = doc.Descendants()
                .Where(e => string.Equals(e.Name.LocalName, "AvaloniaXaml", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(e.Name.LocalName, "Page", StringComparison.OrdinalIgnoreCase))
                .SelectMany(e => SplitPatterns(e.Attribute("Remove")?.Value))
                .ToArray();
            foreach (string file in EnumerateProjectXamlFiles(projectDir))
            {
                string rel = System.IO.Path.GetRelativePath(projectDir, file);
                if (removed.Any(pattern => GlobMatch(rel.Replace('\\', '/'), pattern)))
                {
                    continue;
                }

                results.Add(new XamlFileModel { FilePath = file, RelativePath = rel });
            }
        }

        return results;
    }

    // SDK-style projects glob their items unless EnableDefaultItems or the Avalonia switch EnableDefaultAvaloniaItems
    // turns that off.
    internal static bool UsesDefaultXamlItems(XDocument project)
    {
        XElement? root = project.Root;
        bool sdkStyle = root?.Attribute("Sdk") is not null
            || root?.Elements().Any(e => string.Equals(e.Name.LocalName, "Sdk", StringComparison.OrdinalIgnoreCase)) == true;
        return sdkStyle && !project.Descendants().Any(e =>
            (string.Equals(e.Name.LocalName, "EnableDefaultItems", StringComparison.OrdinalIgnoreCase)
             || string.Equals(e.Name.LocalName, "EnableDefaultAvaloniaItems", StringComparison.OrdinalIgnoreCase))
            && string.Equals(e.Value.Trim(), "false", StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> EnumerateProjectXamlFiles(string projectDir)
    {
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(projectDir, "*.xaml", System.IO.SearchOption.AllDirectories)
                .Concat(System.IO.Directory.EnumerateFiles(projectDir, "*.axaml", System.IO.SearchOption.AllDirectories));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to enumerate XAML files in '{Directory}': {Message}", projectDir, ex.Message);
            yield break;
        }

        foreach (string file in files)
        {
            if (IsIgnoredProjectPath(file))
            {
                continue;
            }

            yield return file;
        }
    }

    private static bool IsIgnoredProjectPath(string path)
    {
        return path.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\bin\\", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/obj/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\obj\\", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/.git/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\.git\\", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/.vs/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("\\.vs\\", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> ResolveItemFiles(string projectDir, string include, string? exclude)
    {
        string normalized = include.Replace('\\', '/');
        if (!HasWildcard(normalized))
        {
            string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(projectDir, include));
            if (System.IO.File.Exists(candidate))
            {
                yield return candidate;
            }

            yield break;
        }

        string searchRoot = GetSearchRoot(projectDir, normalized);
        IEnumerable<string> files;
        try
        {
            files = System.IO.Directory.EnumerateFiles(searchRoot, "*.xaml", System.IO.SearchOption.AllDirectories)
                .Concat(System.IO.Directory.EnumerateFiles(searchRoot, "*.axaml", System.IO.SearchOption.AllDirectories));
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to enumerate '{Root}': {Message}", searchRoot, ex.Message);
            yield break;
        }

        string pattern = normalized;
        string[] excludes = SplitPatterns(exclude);

        foreach (string file in files)
        {
            string rel = System.IO.Path.GetRelativePath(projectDir, file).Replace('\\', '/');
            if (!GlobMatch(rel, pattern))
            {
                continue;
            }

            if (excludes.Length > 0 && excludes.Any(ex => GlobMatch(rel, ex)))
            {
                continue;
            }

            yield return file;
        }
    }

    private static string GetSearchRoot(string projectDir, string pattern)
    {
        int wildcardIndex = pattern.IndexOfAny(new[] { '*', '?' });
        if (wildcardIndex <= 0)
        {
            return projectDir;
        }

        string prefix = pattern[..wildcardIndex];
        string combined = System.IO.Path.Combine(projectDir, prefix);
        string? dir = System.IO.Path.GetDirectoryName(combined);
        return string.IsNullOrEmpty(dir) ? projectDir : dir;
    }

    private static string[] SplitPatterns(string? patterns)
    {
        if (string.IsNullOrWhiteSpace(patterns))
        {
            return Array.Empty<string>();
        }

        return patterns.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Replace('\\', '/'))
            .ToArray();
    }

    private static bool HasWildcard(string value)
    {
        return value.Contains('*') || value.Contains('?');
    }

    private static bool GlobMatch(string text, string pattern)
    {
        string regex = Regex.Escape(pattern)
            .Replace("\\*\\*", ".*")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]");

        return Regex.IsMatch(text, "^" + regex + "$", RegexOptions.IgnoreCase);
    }

    private void OnWorkspaceFailed(WorkspaceDiagnosticEventArgs e)
    {
        if (e.Diagnostic is not null)
        {
            _logger.LogWarning("Workspace diagnostic [{Kind}]: {Message}",
                e.Diagnostic.Kind,
                e.Diagnostic.Message);
        }
    }

    private static string FormatProgress(ProjectLoadProgress progress)
    {
        string operation = progress.Operation.ToString();
        string filePath = progress.FilePath ?? string.Empty;
        string fileName = string.IsNullOrWhiteSpace(filePath)
            ? string.Empty
            : System.IO.Path.GetFileName(filePath);
        string tfm = string.IsNullOrWhiteSpace(progress.TargetFramework)
            ? string.Empty
            : $" ({progress.TargetFramework})";

        if (string.IsNullOrWhiteSpace(fileName))
        {
            return operation;
        }

        return $"{operation}: {fileName}{tfm}";
    }
}

/// <summary>
/// Provides type metadata from loaded assemblies for intellisense and property editing.
/// </summary>
public sealed class TypeMetadataService : ITypeMetadataService
{
    /// <summary>Reads only the selected assemblies and the Avalonia base types for the toolbox.</summary>
    public static IReadOnlyList<TypeMetadata> ReadControlTypes(IReadOnlyList<string> assemblyPaths,
        Action<string> reportFailure, Action<int, int, string> reportProgress, CancellationToken cancellationToken,
        bool includeProjectControls = true)
    {
        Dictionary<string, (string Path, bool IsStandard)> selected = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in assemblyPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                AssemblyTypeCatalog.AssemblyIdentity? identity = AssemblyTypeCatalog.ReadAssemblyIdentity(path);
                if (identity is not null) selected.TryAdd(identity.Name, (path, identity.IsStandardAssembly));
            }
            catch (BadImageFormatException) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                reportFailure($"Cannot read control metadata from '{path}': {error.Message}");
            }
        }

        // Project assemblies replace the base catalog; old and new types are not merged.
        string[] standardPaths = { typeof(AvaloniaObject).Assembly.Location, typeof(Control).Assembly.Location };
        bool hasProjectFramework = standardPaths.Any(path => selected.ContainsKey(Path.GetFileNameWithoutExtension(path)));
        foreach (string path in standardPaths)
        {
            AssemblyTypeCatalog.AssemblyIdentity identity = AssemblyTypeCatalog.ReadAssemblyIdentity(path)
                ?? throw new BadImageFormatException($"XVE base assembly contains no .NET metadata: {path}");
            if (!hasProjectFramework)
                selected.TryAdd(identity.Name, (path, identity.IsStandardAssembly));
            else if (!selected.ContainsKey(identity.Name))
                reportFailure($"The project dependency '{identity.Name}' is missing. Restore the project references.");
        }
        string[] paths = selected.Values.Where(assembly => includeProjectControls || assembly.IsStandard)
            .Select(assembly => assembly.Path).ToArray();
        return AssemblyTypeCatalog.Read(paths, reportFailure, cancellationToken, reportProgress);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, TypeMetadata> _cache = new(StringComparer.Ordinal);
    private readonly object _catalogGate = new();
    private readonly Dictionary<string, IReadOnlyList<TypeMetadata>> _namespaceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _registeredAssemblyPaths = new(StringComparer.OrdinalIgnoreCase);
    private Lazy<IReadOnlyList<TypeMetadata>>? _typeCatalog;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<System.Reflection.Assembly, Type[]> _exportedTypesCache = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Assembly, FieldInfo[]> _attachedPropertyFieldsCache = new();
    private readonly List<System.Reflection.Assembly> _loadedAssemblies = new();
    private readonly HashSet<string> _loadedAssemblyNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<TypeMetadataService> _logger;

    private readonly Dictionary<string, List<XmlnsDefinition>> _xmlnsMappings = new(StringComparer.OrdinalIgnoreCase);

    public TypeMetadataService(ILogger<TypeMetadataService>? logger = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TypeMetadataService>.Instance;

        // The AppDomain scan below only sees assemblies that happen to be loaded
        // already; seed the core Avalonia assemblies so the default xmlns mappings
        // do not depend on load order.
        AddAssembly(typeof(AvaloniaObject).Assembly);
        AddAssembly(typeof(Control).Assembly);
        AddAssembly(typeof(AvaloniaXamlLoader).Assembly);

        foreach (System.Reflection.Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string? name = asm.GetName().Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (name.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("XamlVisualEditor", StringComparison.OrdinalIgnoreCase))
            {
                AddAssembly(asm);
            }
        }
    }

    /// <inheritdoc />
    public TypeMetadata? GetType(string xmlNamespace, string typeName)
    {
        IReadOnlyList<TypeMetadata> types = GetAvailableTypes(xmlNamespace);
        for (int index = 0; index < types.Count; index++)
        {
            TypeMetadata type = types[index];
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal)
                || string.Equals(type.FullName, typeName, StringComparison.Ordinal))
                return type;
        }

        if (TryResolveType(xmlNamespace, typeName, out Type? resolved))
        {
            string key = resolved!.Assembly.FullName + "|" + resolved.FullName;
            if (_cache.TryGetValue(key, out TypeMetadata? cached))
            {
                return cached;
            }
            TypeMetadata meta = BuildMetadata(resolved!);
            _cache[key] = meta;
            return meta;
        }

        return null;
    }

    /// <inheritdoc />
    public IReadOnlyList<TypeMetadata> GetAvailableTypes(string? xmlNamespace = null)
    {
        if (string.IsNullOrWhiteSpace(xmlNamespace))
        {
            return GetTypeCatalog();
        }

        lock (_catalogGate)
        {
            if (_namespaceTypes.TryGetValue(xmlNamespace, out IReadOnlyList<TypeMetadata>? cached))
                return cached;

            IReadOnlyList<TypeMetadata> filtered = FilterAvailableTypes(xmlNamespace);
            _namespaceTypes.Add(xmlNamespace, filtered);
            return filtered;
        }
    }

    private IReadOnlyList<TypeMetadata> FilterAvailableTypes(string xmlNamespace)
    {
        IReadOnlyList<TypeMetadata> types = GetTypeCatalog();
        if (TryParseClrNamespace(xmlNamespace, out string? clrNamespace, out string? assemblyName))
        {
            return types.Where(type => string.Equals(type.ClrNamespace, clrNamespace, StringComparison.Ordinal)
                && (string.IsNullOrEmpty(assemblyName)
                    || string.Equals(type.AssemblyName, assemblyName, StringComparison.OrdinalIgnoreCase))).ToArray();
        }
        if (_xmlnsMappings.TryGetValue(xmlNamespace, out List<XmlnsDefinition>? mappings))
        {
            return types.Where(type => mappings.Any(mapping =>
                string.Equals(type.AssemblyName, mapping.AssemblyName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(type.ClrNamespace, mapping.ClrNamespace, StringComparison.Ordinal))).ToArray();
        }
        return Array.Empty<TypeMetadata>();
    }

    private IReadOnlyList<TypeMetadata> GetTypeCatalog()
    {
        Lazy<IReadOnlyList<TypeMetadata>> catalog;
        lock (_catalogGate)
        {
            if (_typeCatalog is null)
            {
                string[] paths = _loadedAssemblies.Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                    .Select(assembly => assembly.Location).Concat(_registeredAssemblyPaths.Values)
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                _typeCatalog = new Lazy<IReadOnlyList<TypeMetadata>>(() =>
                    AssemblyTypeCatalog.Read(paths, message => _logger.LogWarning("{CatalogError}", message)));
            }
            catalog = _typeCatalog;
        }
        return catalog.Value;
    }

    /// <inheritdoc />
    public IReadOnlyList<PropertyMetadata> GetProperties(TypeMetadata type)
    {
        Type? clrType = ResolveClrType(type);
        if (clrType is null)
        {
            return Array.Empty<PropertyMetadata>();
        }

        List<PropertyMetadata> properties = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach ((Avalonia.AvaloniaProperty prop, Type ownerType, System.Reflection.FieldInfo field) in GetAvaloniaProperties(clrType))
        {
            string name = prop.IsAttached ? $"{ownerType.Name}.{prop.Name}" : prop.Name;
            if (!seen.Add(name))
            {
                continue;
            }

            (string category, string? description) = GetCategoryAndDescription(field);
            if (category == "Misc" && ownerType.GetProperty(prop.Name) is { } clrProperty)
            {
                (string wrapperCategory, string? wrapperDescription) = GetCategoryAndDescription(clrProperty);
                if (wrapperCategory != "Misc") category = wrapperCategory;
                description ??= wrapperDescription;
            }

            properties.Add(new PropertyMetadata
            {
                Name = name,
                TypeFullName = prop.PropertyType.FullName ?? prop.PropertyType.Name,
                Kind = MapValueKind(prop.PropertyType),
                IsReadOnly = prop.IsReadOnly,
                DefaultValue = TryGetDefaultValue(prop, ownerType),
                ClrType = prop.PropertyType,
                IsAttached = prop.IsAttached,
                OwnerType = prop.IsAttached ? ownerType.FullName : null,
                DeclaringTypeName = ownerType.Name,
                Category = category,
                Description = description
            });
        }

        foreach (PropertyMetadata attached in GetAttachedProperties())
        {
            if (!seen.Add(attached.Name))
            {
                continue;
            }

            properties.Add(attached);
        }

        foreach (System.Reflection.PropertyInfo prop in clrType.GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (prop.GetIndexParameters().Length > 0)
            {
                continue;
            }

            if (!seen.Add(prop.Name))
            {
                continue;
            }

            (string category, string? description) = GetCategoryAndDescription(prop);

            properties.Add(new PropertyMetadata
            {
                Name = prop.Name,
                TypeFullName = prop.PropertyType.FullName ?? prop.PropertyType.Name,
                Kind = MapValueKind(prop.PropertyType),
                IsReadOnly = !prop.CanWrite,
                ClrType = prop.PropertyType,
                DeclaringTypeName = prop.DeclaringType?.Name,
                Category = category,
                Description = description
            });
        }

        return properties;
    }

    /// <inheritdoc />
    public IReadOnlyList<EventMetadata> GetEvents(TypeMetadata type)
    {
        Type? clrType = ResolveClrType(type);
        if (clrType is null)
        {
            return Array.Empty<EventMetadata>();
        }

        List<EventMetadata> events = new();
        foreach (System.Reflection.EventInfo evt in clrType.GetEvents(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            events.Add(new EventMetadata
            {
                Name = evt.Name,
                HandlerTypeFullName = evt.EventHandlerType?.FullName ?? "EventHandler"
            });
        }

        return events;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetAvailableNamespaces()
    {
        return _xmlnsMappings.Keys.ToList();
    }

    /// <summary>
    /// Resolves a type by full CLR name.
    /// </summary>
    public TypeMetadata? GetTypeMetadata(string fullTypeName)
    {
        TypeMetadata? catalogType = GetTypeCatalog().FirstOrDefault(type =>
            string.Equals(type.FullName, fullTypeName, StringComparison.Ordinal));
        if (catalogType is not null) return catalogType;

        if (_cache.TryGetValue(fullTypeName, out TypeMetadata? cached))
        {
            return cached;
        }

        // Search loaded assemblies
        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            if (TryGetTypeSafe(asm, fullTypeName, out Type? type) && type is not null)
            {
                TypeMetadata meta = BuildMetadata(type);
                _cache[fullTypeName] = meta;
                return meta;
            }
        }

        return null;
    }

    /// <summary>
    /// Registers assembly metadata without loading project code into the editor process.
    /// </summary>
    public void LoadAssembly(string assemblyPath)
    {
        if (!ShouldLoadAssembly(assemblyPath, out string? assemblyName) || assemblyName is null)
        {
            return;
        }

        try
        {
            var namespaces = AssemblyTypeCatalog.ReadXmlnsDefinitions(assemblyPath);
            lock (_catalogGate)
            {
                if (!_registeredAssemblyPaths.TryAdd(assemblyName, assemblyPath)) return;
                foreach (var (xmlNamespace, clrNamespace) in namespaces)
                {
                    if (!_xmlnsMappings.TryGetValue(xmlNamespace, out List<XmlnsDefinition>? mappings))
                        _xmlnsMappings[xmlNamespace] = mappings = new();
                    XmlnsDefinition mapping = new(xmlNamespace, clrNamespace, assemblyName);
                    if (!mappings.Contains(mapping)) mappings.Add(mapping);
                }
                _typeCatalog = null;
                _namespaceTypes.Clear();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to read assembly metadata '{Path}': {Message}", assemblyPath, ex.Message);
        }
    }

    /// <summary>
    /// Loads multiple assemblies for metadata lookup.
    /// </summary>
    public void LoadAssemblies(IEnumerable<string> assemblyPaths)
    {
        foreach (string path in assemblyPaths)
        {
            LoadAssembly(path);
        }
    }

    private bool ShouldLoadAssembly(string assemblyPath, out string? assemblyName)
    {
        assemblyName = null;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath))
        {
            return false;
        }

        if (Path.GetFileName(assemblyPath).EndsWith(".resources.dll", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string extension = Path.GetExtension(assemblyPath);
        if (!extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (IsReferenceAssemblyPath(assemblyPath))
        {
            return false;
        }

        if (!IsManagedAssembly(assemblyPath))
        {
            return false;
        }

        try
        {
            assemblyName = System.Reflection.AssemblyName.GetAssemblyName(assemblyPath).Name;
        }
        catch
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(assemblyName))
        {
            return false;
        }

        return true;
    }

    private static bool IsReferenceAssemblyPath(string assemblyPath)
    {
        string normalized = assemblyPath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        string marker = Path.DirectorySeparatorChar.ToString();
        return normalized.Contains(marker + "ref" + marker, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(marker + "refint" + marker, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedAssembly(string assemblyPath)
    {
        try
        {
            using FileStream stream = File.OpenRead(assemblyPath);
            using PEReader reader = new(stream);
            return reader.HasMetadata && reader.PEHeaders?.CorHeader is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Type? ResolveClrType(TypeMetadata type)
    {
        EnsureAssemblyLoaded(type.AssemblyName);
        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            if (!string.IsNullOrWhiteSpace(type.AssemblyName))
            {
                string? asmName = asm.GetName().Name;
                if (!string.Equals(asmName, type.AssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (TryGetTypeSafe(asm, type.FullName, out Type? resolved))
            {
                return resolved;
            }
        }

        return null;
    }

    private void EnsureAssemblyLoaded(string? assemblyName)
    {
        string? assemblyPath;
        lock (_catalogGate)
        {
            if (string.IsNullOrWhiteSpace(assemblyName) || _loadedAssemblyNames.Contains(assemblyName)
                || !_registeredAssemblyPaths.TryGetValue(assemblyName, out assemblyPath)) return;
        }
        try
        {
            AddAssembly(AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to load requested designer assembly '{Path}'", assemblyPath);
        }
    }

    private Type? FindClrType(string fullTypeName)
    {
        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            if (TryGetTypeSafe(asm, fullTypeName, out Type? type))
            {
                return type;
            }
        }

        return null;
    }

    private static TypeMetadata BuildMetadata(Type type)
    {
        string clrNamespace = type.Namespace ?? string.Empty;
        AssemblyName identity = type.Assembly.GetName();
        string assemblyName = identity.Name ?? string.Empty;

        return new TypeMetadata
        {
            FullName = type.FullName ?? type.Name,
            Name = type.Name,
            XmlNamespace = $"clr-namespace:{clrNamespace};assembly={assemblyName}",
            ClrNamespace = clrNamespace,
            AssemblyName = assemblyName,
            AssemblyVersion = identity.Version?.ToString(),
            IsControl = typeof(Control).IsAssignableFrom(type),
            IsStyle = typeof(Avalonia.Styling.Styles).IsAssignableFrom(type),
            IsStandardControl = AssemblyTypeCatalog.IsStandardAssembly(assemblyName, identity.GetPublicKey()),
            IsPanel = typeof(Panel).IsAssignableFrom(type),
            IsContentControl = typeof(ContentControl).IsAssignableFrom(type),
            IsItemsControl = typeof(ItemsControl).IsAssignableFrom(type),
            IsTopLevel = typeof(TopLevel).IsAssignableFrom(type),
            IsInstantiable = type.IsPublic && !type.IsAbstract && !type.ContainsGenericParameters
                && type.GetConstructor(Type.EmptyTypes) is not null
        };
    }

    private static PropertyKind InferKind(Type propertyType)
    {
        return MapValueKind(propertyType);
    }

    private void AddAssembly(System.Reflection.Assembly asm)
    {
        string? name = asm.GetName().Name;
        lock (_catalogGate)
        {
            if (string.IsNullOrWhiteSpace(name) || !_loadedAssemblyNames.Add(name))
            {
                return;
            }

            // The catalog reads assembly files by path; an assembly loaded from an already
            // registered or loaded path does not change its content.
            bool addsCatalogPath = !asm.IsDynamic && !string.IsNullOrEmpty(asm.Location)
                && !_registeredAssemblyPaths.Values.Contains(asm.Location, StringComparer.OrdinalIgnoreCase)
                && !_loadedAssemblies.Any(loaded => !loaded.IsDynamic
                    && string.Equals(loaded.Location, asm.Location, StringComparison.OrdinalIgnoreCase));
            _loadedAssemblies.Add(asm);
            bool addsMappings = AddXmlnsDefinitions(asm);
            if (addsCatalogPath) _typeCatalog = null;
            if (addsCatalogPath || addsMappings) _namespaceTypes.Clear();
        }
    }

    private bool AddXmlnsDefinitions(System.Reflection.Assembly asm)
    {
        string? asmName = asm.GetName().Name;
        if (string.IsNullOrWhiteSpace(asmName))
        {
            return false;
        }

        bool added = false;
        foreach (System.Reflection.CustomAttributeData attr in asm.GetCustomAttributesData())
        {
            if (!IsXmlnsDefinitionAttribute(attr.AttributeType))
            {
                continue;
            }

            if (attr.ConstructorArguments.Count < 2)
            {
                continue;
            }

            string? xmlNamespace = attr.ConstructorArguments[0].Value as string;
            string? clrNamespace = attr.ConstructorArguments[1].Value as string;
            if (string.IsNullOrWhiteSpace(xmlNamespace) || string.IsNullOrWhiteSpace(clrNamespace))
            {
                continue;
            }

            if (!_xmlnsMappings.TryGetValue(xmlNamespace, out List<XmlnsDefinition>? list))
            {
                list = new List<XmlnsDefinition>();
                _xmlnsMappings[xmlNamespace] = list;
            }

            XmlnsDefinition mapping = new(xmlNamespace, clrNamespace, asmName);
            if (list.Contains(mapping)) continue;
            list.Add(mapping);
            added = true;
        }

        return added;
    }

    private static bool IsXmlnsDefinitionAttribute(Type attributeType)
    {
        return string.Equals(attributeType.Name, "XmlnsDefinitionAttribute", StringComparison.Ordinal)
            || attributeType.FullName?.EndsWith(".XmlnsDefinitionAttribute", StringComparison.Ordinal) == true;
    }

    private bool TryGetLoadedAssembly(string assemblyName, out System.Reflection.Assembly? assembly)
    {
        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            string? name = asm.GetName().Name;
            if (string.Equals(name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                assembly = asm;
                return true;
            }
        }

        assembly = null;
        return false;
    }

    private IEnumerable<(Avalonia.AvaloniaProperty Prop, Type OwnerType, System.Reflection.FieldInfo Field)> GetAvaloniaProperties(Type type)
    {
        Type? current = type;
        while (current is not null)
        {
            foreach (System.Reflection.FieldInfo field in current.GetFields(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.FlattenHierarchy))
            {
                if (!typeof(Avalonia.AvaloniaProperty).IsAssignableFrom(field.FieldType))
                {
                    continue;
                }

                if (field.GetValue(null) is Avalonia.AvaloniaProperty prop)
                {
                    yield return (prop, prop.OwnerType ?? current, field);
                }
            }

            current = current.BaseType;
        }
    }

    private static (string Category, string? Description) GetCategoryAndDescription(System.Reflection.MemberInfo member)
    {
        string category = "Misc";
        string? description = null;

        if (member.GetCustomAttribute<CategoryAttribute>() is { } cat)
        {
            if (!string.IsNullOrWhiteSpace(cat.Category))
            {
                category = cat.Category;
            }
        }

        if (member.GetCustomAttribute<DescriptionAttribute>() is { } desc)
        {
            description = desc.Description;
        }

        return (category, description);
    }

    private IReadOnlyList<PropertyMetadata> GetAttachedProperties()
    {
        List<PropertyMetadata> attached = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            foreach (FieldInfo field in GetAttachedPropertyFieldsCached(asm))
            {
                try
                {
                    Avalonia.AvaloniaProperty? prop = field.GetValue(null) as Avalonia.AvaloniaProperty;
                    if (prop is null || !prop.IsAttached)
                    {
                        continue;
                    }

                    Type type = field.DeclaringType!;
                    string name = $"{type.Name}.{prop.Name}";
                    if (!seen.Add(name))
                    {
                        continue;
                    }

                    (string category, string? description) = GetCategoryAndDescription(field);

                    attached.Add(new PropertyMetadata
                    {
                        Name = name,
                        TypeFullName = prop.PropertyType.FullName ?? prop.PropertyType.Name,
                        Kind = MapValueKind(prop.PropertyType),
                        IsReadOnly = prop.IsReadOnly,
                        DefaultValue = TryGetDefaultValue(prop, type),
                        ClrType = prop.PropertyType,
                        IsAttached = true,
                        OwnerType = type.FullName,
                        Category = category,
                        Description = description
                    });
                }
                catch
                {
                    continue;
                }
            }
        }

        return attached;
    }

    private FieldInfo[] GetAttachedPropertyFieldsCached(Assembly assembly)
    {
        return _attachedPropertyFieldsCache.GetOrAdd(assembly, asm =>
        {
            List<FieldInfo> fields = new();
            try
            {
                if (asm.IsDynamic || string.IsNullOrWhiteSpace(asm.Location))
                {
                    return Array.Empty<FieldInfo>();
                }

                using FileStream stream = File.OpenRead(asm.Location);
                foreach (AttachedPropertyField candidate in AttachedPropertyDiscovery.Read(stream))
                {
                    if (!TryGetTypeSafe(asm, candidate.OwnerTypeName, out Type? owner))
                    {
                        continue;
                    }

                    try
                    {
                        FieldInfo? field = owner!.GetField(candidate.FieldName,
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
                        if (field is not null && typeof(AvaloniaProperty).IsAssignableFrom(field.FieldType)
                            && IsAttachedPropertyCandidate(field))
                        {
                            fields.Add(field);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Failed to resolve property field '{Owner}.{Field}': {Message}",
                            candidate.OwnerTypeName, candidate.FieldName, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to read attached-property metadata from '{Assembly}': {Message}",
                    asm.FullName, ex.Message);
            }

            return fields.ToArray();
        });
    }

    private static bool IsAttachedPropertyCandidate(FieldInfo field)
    {
        // Reading a field runs its owner's initializer. Do not initialize ordinary controls
        // merely to discover whether their properties are attached.
        if (field.FieldType.IsGenericType &&
            field.FieldType.GetGenericTypeDefinition() == typeof(AttachedProperty<>)) return true;
        Type? owner = field.DeclaringType;
        if (owner is null) return false;
        string name = field.Name.EndsWith("Property", StringComparison.Ordinal) ? field.Name[..^8] : field.Name;
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
        return owner.GetMember("Get" + name, MemberTypes.Method, flags)
            .Concat(owner.GetMember("Set" + name, MemberTypes.Method, flags)).OfType<MethodInfo>()
            .Any(method =>
            {
                ParameterInfo[] parameters = method.GetParameters();
                return !method.ContainsGenericParameters && parameters.Length == (method.Name.StartsWith("Get", StringComparison.Ordinal) ? 1 : 2)
                    && typeof(AvaloniaObject).IsAssignableFrom(parameters[0].ParameterType);
            });
    }

    private static object? TryGetDefaultValue(Avalonia.AvaloniaProperty prop, Type ownerType)
    {
        try
        {
            System.Reflection.MethodInfo? getDefault = prop.GetType().GetMethod("GetDefaultValue", new[] { typeof(Type) });
            if (getDefault is not null)
            {
                return getDefault.Invoke(prop, new object?[] { ownerType });
            }

            System.Reflection.MethodInfo? getMetadata = prop.GetType().GetMethod("GetMetadata", new[] { typeof(Type) });
            if (getMetadata is not null)
            {
                object? metadata = getMetadata.Invoke(prop, new object?[] { ownerType });
                if (metadata is not null)
                {
                    System.Reflection.PropertyInfo? defaultProp = metadata.GetType().GetProperty("DefaultValue");
                    if (defaultProp is not null)
                    {
                        return defaultProp.GetValue(metadata);
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static PropertyKind MapValueKind(Type propertyType)
    {
        if (propertyType == typeof(string))
        {
            return PropertyKind.String;
        }

        if (propertyType == typeof(bool) || propertyType == typeof(bool?))
        {
            return PropertyKind.Boolean;
        }

        if (propertyType.IsEnum)
        {
            return PropertyKind.Enum;
        }

        if (propertyType == typeof(Avalonia.Thickness))
        {
            return PropertyKind.Thickness;
        }

        if (propertyType == typeof(Avalonia.CornerRadius))
        {
            return PropertyKind.CornerRadius;
        }

        if (propertyType == typeof(Avalonia.Point))
        {
            return PropertyKind.Point;
        }

        if (propertyType == typeof(Avalonia.Size))
        {
            return PropertyKind.Size;
        }

        if (propertyType == typeof(Avalonia.Rect))
        {
            return PropertyKind.Rect;
        }

        if (propertyType == typeof(Avalonia.Controls.GridLength))
        {
            return PropertyKind.GridLength;
        }

        if (propertyType == typeof(Avalonia.Media.Color))
        {
            return PropertyKind.Color;
        }

        if (typeof(Avalonia.Media.IBrush).IsAssignableFrom(propertyType))
        {
            return PropertyKind.Brush;
        }

        if (propertyType == typeof(Avalonia.Media.FontFamily))
        {
            return PropertyKind.FontFamily;
        }

        if (propertyType == typeof(Avalonia.Media.FontWeight))
        {
            return PropertyKind.FontWeight;
        }

        if (propertyType == typeof(Avalonia.Media.FontStyle))
        {
            return PropertyKind.FontStyle;
        }

        if (propertyType == typeof(TimeSpan) || propertyType == typeof(TimeSpan?))
        {
            return PropertyKind.TimeSpan;
        }

        if (propertyType == typeof(Uri))
        {
            return PropertyKind.Uri;
        }

        if (typeof(Avalonia.Controls.Templates.IDataTemplate).IsAssignableFrom(propertyType) ||
            typeof(Avalonia.Controls.Templates.IControlTemplate).IsAssignableFrom(propertyType))
        {
            return PropertyKind.Template;
        }

        if (typeof(Avalonia.Markup.Xaml.MarkupExtension).IsAssignableFrom(propertyType))
        {
            return PropertyKind.MarkupExtension;
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType) && propertyType != typeof(string))
        {
            return PropertyKind.Collection;
        }

        if (propertyType.IsPrimitive)
        {
            return PropertyKind.Numeric;
        }

        switch (Type.GetTypeCode(propertyType))
        {
            case TypeCode.Byte:
            case TypeCode.SByte:
            case TypeCode.Int16:
            case TypeCode.Int32:
            case TypeCode.Int64:
            case TypeCode.UInt16:
            case TypeCode.UInt32:
            case TypeCode.UInt64:
            case TypeCode.Single:
            case TypeCode.Double:
            case TypeCode.Decimal:
                return PropertyKind.Numeric;
        }

        return PropertyKind.Object;
    }

    private bool TryResolveType(string xmlNamespace, string typeName, out Type? resolved)
    {
        resolved = null;

        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(xmlNamespace) &&
            _xmlnsMappings.TryGetValue(xmlNamespace, out List<XmlnsDefinition>? mappings))
        {
            foreach (XmlnsDefinition mapping in mappings)
            {
                string fullName = typeName.Contains('.')
                    ? typeName
                    : string.IsNullOrWhiteSpace(mapping.ClrNamespace)
                        ? typeName
                        : mapping.ClrNamespace + "." + typeName;

                foreach (System.Reflection.Assembly asm in _loadedAssemblies)
                {
                    string? asmName = asm.GetName().Name;
                    if (!string.Equals(asmName, mapping.AssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (TryGetTypeSafe(asm, fullName, out Type? match))
                    {
                        resolved = match;
                        return true;
                    }
                }
            }
        }

        if (TryParseClrNamespace(xmlNamespace, out string? clrNamespace, out string? assemblyName))
        {
            string fullName = typeName.Contains('.')
                ? typeName
                : string.IsNullOrEmpty(clrNamespace)
                    ? typeName
                    : clrNamespace + "." + typeName;

            foreach (System.Reflection.Assembly asm in _loadedAssemblies)
            {
                if (!string.IsNullOrEmpty(assemblyName))
                {
                    string? asmName = asm.GetName().Name;
                    if (!string.Equals(asmName, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                if (TryGetTypeSafe(asm, fullName, out Type? match))
                {
                    resolved = match;
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(xmlNamespace))
        {
            return false;
        }

        foreach (System.Reflection.Assembly asm in _loadedAssemblies)
        {
            foreach (Type type in GetExportedTypesCached(asm))
            {
                if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = type;
                    return true;
                }
            }
        }

        return false;
    }

    // GetExportedTypes exactly once per assembly: the call is expensive and fails
    // permanently for legacy assemblies with framework references (WPF/WinForms/
    // AspNetCore). Without the cache every hover/completion type lookup re-logs the
    // same warnings.
    private Type[] GetExportedTypesCached(System.Reflection.Assembly assembly)
    {
        return _exportedTypesCache.GetOrAdd(assembly, asm =>
        {
            try
            {
                return asm.GetExportedTypes();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Failed to enumerate exported types from '{Assembly}': {Message}",
                    asm.FullName,
                    ex.Message);
                return Type.EmptyTypes;
            }
        });
    }

    private readonly record struct XmlnsDefinition(string XmlNamespace, string ClrNamespace, string AssemblyName);

    private bool TryGetTypeSafe(System.Reflection.Assembly asm, string typeName, out Type? type)
    {
        type = null;
        try
        {
            type = asm.GetType(typeName, throwOnError: false);
            return type is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Failed to resolve type '{TypeName}' from '{Assembly}': {Message}",
                typeName,
                asm.FullName,
                ex.Message);
            return false;
        }
    }

    private static bool TryParseClrNamespace(string xmlNamespace, out string? clrNamespace, out string? assemblyName)
    {
        clrNamespace = null;
        assemblyName = null;

        if (string.IsNullOrWhiteSpace(xmlNamespace))
        {
            return false;
        }

        string value = xmlNamespace.Trim();
        if (value.StartsWith("clr-namespace:", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = value["clr-namespace:".Length..];
            ParseNamespaceParts(remainder, out clrNamespace, out assemblyName);
            return true;
        }

        if (value.StartsWith("using:", StringComparison.OrdinalIgnoreCase))
        {
            string remainder = value["using:".Length..];
            ParseNamespaceParts(remainder, out clrNamespace, out assemblyName);
            return true;
        }

        return false;
    }

    private static void ParseNamespaceParts(string value, out string? clrNamespace, out string? assemblyName)
    {
        clrNamespace = null;
        assemblyName = null;

        string[] parts = value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length > 0)
        {
            clrNamespace = parts[0];
        }

        foreach (string part in parts)
        {
            if (part.StartsWith("assembly=", StringComparison.OrdinalIgnoreCase))
            {
                assemblyName = part["assembly=".Length..];
            }
        }
    }
}
