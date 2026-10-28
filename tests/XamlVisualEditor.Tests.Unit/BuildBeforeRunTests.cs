using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class BuildBeforeRunTests : IDisposable
{
    private const string SavedCode = "class Program { static void Main() { } }";
    private const string EditedCode = "class Program { static void Main() { int changed = 1; } }";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "xve-build-before-run-" + Guid.NewGuid().ToString("N"));

    [AvaloniaFact(Timeout = 30000)]
    public async Task Build_Saves_The_Changed_Documents_Before_Dotnet_Runs()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, string codePath) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            TextDocumentViewModel code = await OpenEditedCodeAsync(main, codePath, cancellationToken);

            await ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellationToken);

            RecordingDotNetCli.Call build = Assert.Single(cli.Calls, call => call.Arguments[0] == "build");
            Assert.Equal(EditedCode, build.CodeOnDisk);
            Assert.False(code.IsModified);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Debug_Saves_And_Builds_Although_An_Output_Exists_And_Stops_When_The_Build_Fails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, string codePath) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            await OpenEditedCodeAsync(main, codePath, cancellationToken);
            cli.Fail = true;

            await ((IWorkspaceCommands)main).StartDebugAsync(cancellationToken);

            RecordingDotNetCli.Call build = Assert.Single(cli.Calls, call => call.Arguments[0] == "build");
            Assert.Equal(EditedCode, build.CodeOnDisk);
            Assert.Equal(string.Format(UiStrings.DotnetCommandFailed, "build"), main.StatusText);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Run_Saves_And_Builds_Although_An_Output_Exists_And_Stops_When_The_Build_Fails()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, string codePath) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            await OpenEditedCodeAsync(main, codePath, cancellationToken);
            cli.Fail = true;

            await ((IWorkspaceCommands)main).StartRunAsync(cancellationToken);

            RecordingDotNetCli.Call build = Assert.Single(cli.Calls, call => call.Arguments[0] == "build");
            Assert.Equal(EditedCode, build.CodeOnDisk);
            Assert.Equal(string.Format(UiStrings.DotnetCommandFailed, "build"), main.StatusText);
            Assert.False(main.IsRunActive);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_Document_That_Cannot_Be_Saved_Stops_The_Build()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, string codePath) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            TextDocumentViewModel code = await OpenEditedCodeAsync(main, codePath, cancellationToken);
            File.SetAttributes(codePath, FileAttributes.ReadOnly);

            await ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellationToken);

            Assert.Empty(cli.Calls);
            Assert.True(code.IsModified);
            Assert.Equal(SavedCode, File.ReadAllText(codePath));
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Without_Saving_The_Build_Leaves_The_Documents_Unsaved()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, string codePath) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            TextDocumentViewModel code = await OpenEditedCodeAsync(main, codePath, cancellationToken);
            main.SaveBehavior = SaveBehavior.NoSaving;

            await ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellationToken);

            RecordingDotNetCli.Call build = Assert.Single(cli.Calls, call => call.Arguments[0] == "build");
            Assert.Equal(SavedCode, build.CodeOnDisk);
            Assert.True(code.IsModified);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task While_Run_Builds_Neither_Debug_Nor_A_Workspace_Build_Starts()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, _) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cli.Gate = release.Task;
            cli.Fail = true;

            Task run = ((IWorkspaceCommands)main).StartRunAsync(cancellationToken);
            await cli.FirstCall.WaitAsync(cancellationToken);
            Assert.True(main.IsWorkspaceCommandRunning);
            // The commands take their new enabled state on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ((IWorkspaceCommands)main).StartDebugAsync(cancellationToken);
            Assert.Equal(string.Format(UiStrings.DotnetCommandRunning, "build"), main.StatusText);
            await ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellationToken);
            Assert.Equal(UiStrings.WorkspaceBusy, main.StatusText);

            release.SetResult();
            await run.WaitAsync(cancellationToken);
            Assert.Single(cli.Calls);
            Assert.False(main.IsWorkspaceCommandRunning);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task While_A_Workspace_Build_Runs_Run_Does_Not_Start()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, _) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            cli.Gate = release.Task;

            Task build = ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellationToken);
            await cli.FirstCall.WaitAsync(cancellationToken);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            await ((IWorkspaceCommands)main).StartRunAsync(cancellationToken);
            Assert.Equal(string.Format(UiStrings.DotnetCommandRunning, "build"), main.StatusText);

            release.SetResult();
            await build.WaitAsync(cancellationToken);
            Assert.Single(cli.Calls);
        }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task A_Canceled_Build_Reports_The_Cancellation()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, _) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            cli.Gate = Task.Delay(Timeout.Infinite, cancellationToken);
            using CancellationTokenSource cancellation = new();

            Task build = ((IWorkspaceCommands)main).BuildWorkspaceAsync(cancellation.Token);
            await cli.FirstCall.WaitAsync(cancellationToken);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => build);
            Assert.Equal(string.Format(UiStrings.DotnetCommandCanceled, "build"), main.StatusText);
            Assert.False(main.IsWorkspaceCommandRunning);
        }
    }

    [Fact]
    public async Task The_Dotnet_Runner_Passes_A_Cancellation_On()
    {
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new DotNetCliRunner().RunAsync(new[] { "--version" }, null, cancellation.Token));
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Save_All_Reports_A_Canceled_Save_As()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(_root);
        string untitledPath = Path.Combine(_root, "Untitled-1.txt");
        File.WriteAllText(untitledPath, string.Empty);
        using MainWindowViewModel main = new(
            workspaceService: new FixedWorkspaceService(new WorkspaceModel { Projects = Array.Empty<ProjectModel>() }),
            metadataService: new TypeMetadataService(),
            dotNetCli: new RecordingDotNetCli(untitledPath),
            recentFilesPath: Path.Combine(_root, "recent.json"));
        await main.OpenFileAsync(untitledPath).WaitAsync(cancellationToken);
        Assert.False(main.HasWorkspace);
        TextDocumentViewModel untitled = Assert.IsType<TextDocumentViewModel>(main.ActiveDocument);
        untitled.Document.Text = "draft";
        using IDisposable handler = main.SaveFileInteraction.RegisterHandler(context => context.SetOutput(null));

        await main.SaveAllCommand.Execute().ToTask(cancellationToken);

        Assert.Equal(UiStrings.SaveCanceled, main.StatusText);
        Assert.True(untitled.IsModified);
    }

    [Fact]
    public void A_Failed_Command_Shows_The_Compiler_Errors_Beside_A_Notice()
    {
        DotNetCliResult result = new(1, "Program.cs(7,22): error CS1002: ; expected\n", "MSBuild server not available.\n");

        string output = MainWindowViewModel.FormatFailureOutput(result);

        Assert.Contains("error CS1002", output);
        Assert.Contains("MSBuild server not available.", output);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task The_Previewer_Build_Fails_With_The_Reason()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        (MainWindowViewModel main, RecordingDotNetCli cli, _) = await OpenProjectAsync(cancellationToken);
        using (main)
        {
            cli.Fail = true;

            InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(
                () => main.BuildForPreviewerAsync("build"));

            Assert.Equal(string.Format(UiStrings.DotnetCommandFailed, "build"), failure.Message);
        }
    }

    [Theory]
    [InlineData(null, null, "run --no-build --project \"App.csproj\"")]
    [InlineData(null, "--verbose", "run --no-build --project \"App.csproj\" -- --verbose")]
    [InlineData("net9.0", "--verbose", "run --no-build --project \"App.csproj\" --framework net9.0 -- --verbose")]
    public void Run_Starts_The_Built_Output_Of_The_Target_Framework(string? targetFramework, string? programArguments,
        string expected)
    {
        ProjectModel project = new()
        {
            Name = "App", ProjectPath = "App.csproj", TargetFramework = targetFramework,
            XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
            References = Array.Empty<AssemblyReference>()
        };

        Assert.Equal(expected, MainWindowViewModel.BuildRunArguments(project, programArguments));
    }

    public void Dispose()
    {
        string codePath = Path.Combine(_root, "Program.cs");
        if (File.Exists(codePath))
        {
            File.SetAttributes(codePath, FileAttributes.Normal);
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<(MainWindowViewModel Main, RecordingDotNetCli Cli, string CodePath)> OpenProjectAsync(
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        string projectPath = Path.Combine(_root, "App.csproj");
        string codePath = Path.Combine(_root, "Program.cs");
        string outputPath = Path.Combine(_root, "bin", "App.dll");
        File.WriteAllText(projectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(codePath, SavedCode);
        File.WriteAllText(outputPath, "an output of an earlier build");
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "App", ProjectPath = projectPath, OutputAssemblyPath = outputPath, IsExecutable = true,
                    XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                    References = Array.Empty<AssemblyReference>()
                }
            }
        };
        DebuggerServiceRegistry debuggers = new();
        debuggers.Register(new DebuggerServiceRegistration("test", "Test Debugger", new RefusingDebuggerService()), makeDefault: true);
        RecordingDotNetCli cli = new(codePath);
        MainWindowViewModel main = new(
            workspaceService: new FixedWorkspaceService(workspace),
            metadataService: new TypeMetadataService(),
            debuggerRegistry: debuggers,
            dotNetCli: cli,
            recentFilesPath: Path.Combine(_root, "recent.json"));
        await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);
        Assert.True(main.HasWorkspace);
        cli.Calls.Clear();
        return (main, cli, codePath);
    }

    private static async Task<TextDocumentViewModel> OpenEditedCodeAsync(MainWindowViewModel main, string codePath,
        CancellationToken cancellationToken)
    {
        await main.OpenFileAsync(codePath).WaitAsync(cancellationToken);
        TextDocumentViewModel code = Assert.IsType<TextDocumentViewModel>(main.ActiveDocument);
        code.Document.Text = EditedCode;
        Assert.True(code.IsModified);
        return code;
    }

    // Records what each dotnet call finds on disk, which is what the build compiles.
    private sealed class RecordingDotNetCli(string codePath) : IDotNetCli
    {
        public sealed record Call(IReadOnlyList<string> Arguments, string CodeOnDisk);

        private readonly TaskCompletionSource _firstCall = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Call> Calls { get; } = new();

        public bool Fail { get; set; }

        // Holds every call until it completes, as a build that takes its time.
        public Task? Gate { get; set; }

        public Task FirstCall => _firstCall.Task;

        public async Task<DotNetCliResult> RunAsync(IReadOnlyList<string> args, string? workingDirectory, CancellationToken ct = default)
        {
            Calls.Add(new Call(args.ToArray(), File.ReadAllText(codePath)));
            _firstCall.TrySetResult();
            if (Gate is not null)
            {
                await Gate.WaitAsync(ct);
            }
            return Fail
                ? new DotNetCliResult(1, "Program.cs(1,1): error CS1002: ; expected", string.Empty)
                : new DotNetCliResult(0, "Build succeeded", string.Empty);
        }
    }

    private sealed class RefusingDebuggerService : IDebuggerService
    {
        public Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken ct = default) =>
            throw new InvalidOperationException("The test does not launch a debug session.");

        public Task<IDebugSession> AttachAsync(DebugAttachOptions options, CancellationToken ct = default) =>
            throw new InvalidOperationException("The test does not attach a debug session.");
    }

    private sealed class FixedWorkspaceService(WorkspaceModel workspace) : IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) => Task.FromResult(workspace);

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) => Task.FromResult(workspace);

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) => workspace;
    }
}
