using System;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;

namespace XamlVisualEditor.Tests.UI;

public sealed class WorkspaceResponsivenessTests
{
    [AvaloniaTheory]
    [InlineData(".csproj")]
    [InlineData(".slnx")]
    [InlineData(".sln")]
    public async Task FileAndWorkspaceCommandsOpenSeparatelyAndCloseWorkspaceState(string extension)
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-file-menu-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string workspacePath = Path.Combine(root, "Test" + extension);
        string codePath = Path.Combine(root, "Code.cs");
        string recentStore = Path.Combine(root, "recent.json");
        await File.WriteAllTextAsync(workspacePath, "<Project />");
        await File.WriteAllTextAsync(codePath, "class Example { }");
        XamlVisualEditor.App.Services.WorkspaceInfoService workspaceInfo = new();
        RecordingCatalogUpdater catalog = new();
        using MainWindowViewModel main = new(
            workspaceService: new StubWorkspaceService(), workspaceInfoUpdater: workspaceInfo,
            metadataService: new StubMetadataService(), dotNetCli: new ControllableDotNetCli(), controlCatalogUpdater: catalog,
            recentFilesPath: recentStore);
        using IDisposable fileHandler = main.OpenFileInteraction.RegisterHandler(context => context.SetOutput(workspacePath));
        using IDisposable workspaceHandler = main.OpenWorkspaceInteraction.RegisterHandler(context => context.SetOutput(new WorkspaceOpenRequest(workspacePath)));

        Assert.False(await main.CloseActiveDocumentCommand.CanExecute.FirstAsync());
        Assert.False(await main.CloseWorkspaceCommand.CanExecute.FirstAsync());
        await main.OpenDocumentCommand.Execute().FirstAsync();
        Assert.Equal(workspacePath, Assert.Single(main.Documents).FilePath);
        Assert.False(main.HasWorkspace);

        await main.OpenWorkspaceCommand.Execute().FirstAsync();
        Assert.True(main.HasWorkspace);
        Assert.Equal(workspacePath, workspaceInfo.WorkspacePath);
        Assert.Equal(workspacePath, Assert.Single(main.RecentWorkspaces).FilePath);
        Assert.Equal(workspacePath, Assert.Single(main.RecentDocuments).FilePath);
        Assert.True(catalog.UpdateCount > 0);
        Assert.Single(main.WorkspaceProjects);
        Assert.NotNull(main.ActiveProject);
        await main.OpenFileAsync(codePath);
        Assert.Equal(2, main.Documents.Count);

        await main.CloseActiveDocumentCommand.Execute().FirstAsync();
        Assert.Equal(workspacePath, Assert.Single(main.Documents).FilePath);
        Assert.True(main.HasWorkspace);
        Assert.True(await main.CloseWorkspaceCommand.CanExecute.FirstAsync());
        await main.CloseWorkspaceCommand.Execute().FirstAsync();

        Assert.Empty(main.Documents);
        Assert.Null(main.ActiveDocument);
        Assert.False(main.HasWorkspace);
        Assert.Null(workspaceInfo.WorkspacePath);
        Assert.Empty(main.WorkspaceProjects);
        Assert.Null(main.ActiveProject);
        Assert.Null(main.ActiveProjectPath);
        Assert.False(main.SolutionExplorer.IsVisible);
        Assert.False(main.IsWorkspaceLoading);
        Assert.False(await main.CloseWorkspaceCommand.CanExecute.FirstAsync());
        Assert.False(await main.CloseActiveDocumentCommand.CanExecute.FirstAsync());

        int previousUpdates = catalog.UpdateCount;
        await main.OpenRecentCommand.Execute(Assert.Single(main.RecentWorkspaces)).FirstAsync();
        Assert.True(main.HasWorkspace);
        Assert.Equal(workspacePath, workspaceInfo.WorkspacePath);
        Assert.True(catalog.UpdateCount > previousUpdates);
        using MainWindowViewModel restored = new(recentFilesPath: recentStore);
        Assert.Equal(workspacePath, Assert.Single(restored.RecentWorkspaces).FilePath);
    }

    [AvaloniaFact]
    public async Task WorkspaceRegistersMetadataOffTheUiThreadWithoutEnumeratingToolboxControls()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-metadata-thread-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "Test.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string outputPath = Path.Combine(root, "Test.dll");
        File.Copy(typeof(TypeMetadata).Assembly.Location, outputPath);
        StubWorkspaceService workspaceService = new()
        {
            WorkspaceOverride = new WorkspaceModel
            {
                Projects = new[]
                {
                    new ProjectModel
                    {
                        Name = "Test", ProjectPath = projectPath, OutputAssemblyPath = outputPath,
                        XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                        References = Array.Empty<AssemblyReference>()
                    }
                }
            }
        };
        StubMetadataService metadata = new();
        ControllableDotNetCli cli = new();
        using MainWindowViewModel main = new(workspaceService: workspaceService, metadataService: metadata, dotNetCli: cli);
        int uiThread = Environment.CurrentManagedThreadId;

        await main.OpenFileAsync(projectPath);

        Assert.NotNull(metadata.LoadThread);
        Assert.Null(metadata.CatalogThread);
        Assert.NotEqual(uiThread, metadata.LoadThread);
        Assert.Contains(outputPath, metadata.LoadedAssemblies);
        Assert.Empty(cli.Calls);
        Assert.True(main.HasWorkspace);
    }

    [AvaloniaFact]
    public async Task MissingSnapshotDoesNotBuildOrLoadLiveReferenceAssemblies()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-missing-snapshot-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "Test.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        string snapshotPath = Path.Combine(root, "missing-snapshot");
        StubWorkspaceService workspaceService = new()
        {
            WorkspaceOverride = new WorkspaceModel
            {
                Projects = new[]
                {
                    new ProjectModel
                    {
                        Name = "Test", ProjectPath = projectPath,
                        AssemblySnapshotDirectory = snapshotPath,
                        OutputAssemblyPath = Path.Combine(snapshotPath, "Test.dll"),
                        XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                        References = new[]
                        {
                            new AssemblyReference { Name = "LiveReference", Path = typeof(TypeMetadata).Assembly.Location }
                        }
                    }
                }
            }
        };
        ControllableDotNetCli dotNetCli = new();
        StubMetadataService metadata = new();
        using MainWindowViewModel viewModel = new(
            workspaceService: workspaceService, metadataService: metadata, dotNetCli: dotNetCli);

        await viewModel.OpenFileAsync(projectPath);

        Assert.True(viewModel.HasWorkspace);
        Assert.False(viewModel.IsWorkspaceLoading);
        Assert.Empty(dotNetCli.Calls);
        Assert.Empty(metadata.LoadedAssemblies);
    }

    [AvaloniaFact]
    public async Task Workspace_Load_Uses_Nonblocking_Cli_And_Leaves_Shell_Responsive()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-workspace-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "Test.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ControllableDotNetCli dotNetCli = new();
        dotNetCli.BlockNextCall();
        using MainWindowViewModel viewModel = new(
            workspaceService: new StubWorkspaceService(),
            metadataService: new StubMetadataService(),
            dotNetCli: dotNetCli);

        try
        {
            Task loadTask = viewModel.OpenFileAsync(projectPath);
            await dotNetCli.BlockedCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsWorkspaceLoading);
            Assert.Single(dotNetCli.Calls);
            Assert.Equal(new[] { "restore", projectPath }, dotNetCli.Calls[0].Arguments);
            Assert.Equal(root, dotNetCli.Calls[0].WorkingDirectory);

            await Dispatcher.UIThread.InvokeAsync(
                () => viewModel.AboutCommand.Execute().Subscribe(),
                DispatcherPriority.Background);
            Assert.Contains("Avalonia-based", viewModel.StatusText, StringComparison.Ordinal);

            dotNetCli.ReleaseBlockedCall();
            await loadTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(viewModel.IsWorkspaceLoading);
            Assert.True(viewModel.HasWorkspace);
            Assert.Equal(2, dotNetCli.Calls.Count);
            Assert.Equal(new[] { "build", projectPath }, dotNetCli.Calls[1].Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Workspace_Commands_Are_Single_Flight_And_Use_Exact_Arguments()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-commands-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string projectPath = Path.Combine(root, "Test.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        ControllableDotNetCli dotNetCli = new();
        using MainWindowViewModel viewModel = new(
            workspaceService: new StubWorkspaceService(),
            metadataService: new StubMetadataService(),
            dotNetCli: dotNetCli);

        try
        {
            await viewModel.OpenFileAsync(projectPath);
            dotNetCli.Calls.Clear();
            dotNetCli.BlockNextCall();

            IWorkspaceCommands commands = viewModel;
            Task rebuildTask = commands.RebuildWorkspaceAsync(CancellationToken.None);
            await dotNetCli.BlockedCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(viewModel.IsWorkspaceCommandRunning);
            Assert.Single(dotNetCli.Calls);
            Assert.Equal(
                new[] { "build", projectPath, "-t:Rebuild" },
                dotNetCli.Calls[0].Arguments);

            await commands.CleanWorkspaceAsync(CancellationToken.None);
            Assert.Single(dotNetCli.Calls);

            dotNetCli.ReleaseBlockedCall();
            await rebuildTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(viewModel.IsWorkspaceCommandRunning);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class ControllableDotNetCli : IDotNetCli
    {
        private TaskCompletionSource<DotNetCliResult> _blockedCallCompletion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _blockNextCall;

        public TaskCompletionSource<object?> BlockedCallStarted { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<DotNetCliCall> Calls { get; } = new();

        public Task<DotNetCliResult> RunAsync(
            IReadOnlyList<string> args,
            string? workingDirectory,
            CancellationToken ct = default)
        {
            Calls.Add(new DotNetCliCall(new List<string>(args), workingDirectory));
            if (_blockNextCall)
            {
                _blockNextCall = false;
                BlockedCallStarted.TrySetResult(null);
                return _blockedCallCompletion.Task.WaitAsync(ct);
            }

            return Task.FromResult(new DotNetCliResult(0, "Build succeeded", string.Empty));
        }

        public void BlockNextCall()
        {
            _blockNextCall = true;
            BlockedCallStarted = new TaskCompletionSource<object?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _blockedCallCompletion = new TaskCompletionSource<DotNetCliResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public void ReleaseBlockedCall()
        {
            _blockedCallCompletion.TrySetResult(
                new DotNetCliResult(0, "Command succeeded", string.Empty));
        }
    }

    private sealed record DotNetCliCall(IReadOnlyList<string> Arguments, string? WorkingDirectory);

    private sealed class RecordingCatalogUpdater : IControlCatalogUpdater
    {
        public int UpdateCount { get; private set; }
        public void SetWorkspaceAssemblies(IEnumerable<string> assemblyPaths) => UpdateCount++;
    }

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public WorkspaceModel? WorkspaceOverride { get; init; }

        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default)
        {
            return Task.FromResult(WorkspaceOverride ?? CreateWorkspace(solutionPath));
        }

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default)
        {
            return Task.FromResult(WorkspaceOverride ?? CreateWorkspace(projectPath));
        }

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath)
        {
            return CreateWorkspace(xamlFilePath);
        }

        private static WorkspaceModel CreateWorkspace(string projectPath)
        {
            return new WorkspaceModel
            {
                Projects = new[]
                {
                    new ProjectModel
                    {
                        Name = "Test",
                        ProjectPath = projectPath,
                        XamlFiles = Array.Empty<XamlFileModel>(),
                        Files = Array.Empty<ProjectFileModel>(),
                        References = Array.Empty<AssemblyReference>(),
                        OutputAssemblyPath = null
                    }
                }
            };
        }
    }

    private sealed class StubMetadataService : ITypeMetadataService
    {
        public List<string> LoadedAssemblies { get; } = new();
        public int? LoadThread { get; private set; }
        public int? CatalogThread { get; private set; }

        public TypeMetadata? GetType(string xmlNamespace, string typeName) => null;

        public IReadOnlyList<TypeMetadata> GetAvailableTypes(string? xmlNamespace = null)
        {
            CatalogThread = Environment.CurrentManagedThreadId;
            return Array.Empty<TypeMetadata>();
        }

        public IReadOnlyList<PropertyMetadata> GetProperties(TypeMetadata type) =>
            Array.Empty<PropertyMetadata>();

        public IReadOnlyList<EventMetadata> GetEvents(TypeMetadata type) =>
            Array.Empty<EventMetadata>();

        public IReadOnlyList<string> GetAvailableNamespaces() => Array.Empty<string>();

        public void LoadAssembly(string assemblyPath)
        {
            LoadedAssemblies.Add(assemblyPath);
        }

        public void LoadAssemblies(IEnumerable<string> assemblyPaths)
        {
            LoadThread = Environment.CurrentManagedThreadId;
            LoadedAssemblies.AddRange(assemblyPaths);
        }

        public Type? ResolveClrType(TypeMetadata type) => null;
    }
}
