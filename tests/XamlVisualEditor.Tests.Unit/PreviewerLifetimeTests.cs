using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerLifetimeTests : IDisposable
{
    private const string Xaml = "<UserControl xmlns=\"https://github.com/avaloniaui\" />";

    private static readonly WorkspaceModel s_emptyWorkspace = new() { Projects = Array.Empty<ProjectModel>() };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-PreviewerLifetime-" + Guid.NewGuid().ToString("N"));

    public PreviewerLifetimeTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void Disposing_the_service_ends_every_host_when_one_does_not_exit_in_time()
    {
        using Process first = StartIdleProcess();
        using Process second = StartIdleProcess();
        ConcurrentQueue<string> levels = new();
        PreviewerLaunchService service = new() { WaitForProcessExit = (_, _) => false };
        service.AddRunningInstance("First.axaml", s_emptyWorkspace, null, Process.GetProcessById(first.Id), null,
            (level, _) => levels.Enqueue(level));
        service.AddRunningInstance("Second.axaml", s_emptyWorkspace, null, Process.GetProcessById(second.Id), null,
            (level, _) => levels.Enqueue(level));

        service.Dispose();

        Assert.True(first.WaitForExit(10000));
        Assert.True(second.WaitForExit(10000));
        Assert.Equal(2, levels.Count(level => level == "Warning"));
        Assert.False(service.TryGetSession("First.axaml", out _));
    }

    [Fact]
    public async Task Stopping_a_previewer_does_not_wait_for_its_host_on_the_calling_thread()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using Process host = StartIdleProcess();
        using ManualResetEventSlim release = new();
        TaskCompletionSource<bool> waitStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using PreviewerLaunchService service = new()
        {
            WaitForProcessExit = (process, timeout) =>
            {
                waitStarted.TrySetResult(true);
                release.Wait(TimeSpan.FromSeconds(30));
                return process.WaitForExit(timeout);
            }
        };
        service.AddRunningInstance("View.axaml", s_emptyWorkspace, null, Process.GetProcessById(host.Id), null);

        Stopwatch elapsed = Stopwatch.StartNew();
        service.StopPreviewer("View.axaml");
        elapsed.Stop();

        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5), $"StopPreviewer blocked for {elapsed.Elapsed}.");
        release.Set();
        Assert.True(host.WaitForExit(10000));
    }

    [AvaloniaFact(Timeout = 60000)]
    public async Task Only_the_active_and_recently_active_documents_keep_a_previewer()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using MainWindowViewModel main = new(recentFilesPath: Path.Combine(_root, "recent.json"));
        List<DesignerDocumentViewModel> documents = new();
        for (int index = 0; index < MainWindowViewModel.MaxLivePreviewers + 2; index++)
        {
            string path = Path.Combine(_root, $"View{index}.axaml");
            await File.WriteAllTextAsync(path, Xaml, cancellationToken);
            await main.OpenFileAsync(path).WaitAsync(cancellationToken);
            documents.Add(Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument));
        }

        foreach (DesignerDocumentViewModel document in documents)
        {
            main.PreviewerLaunchService.AddRunningInstance(document.FilePath, s_emptyWorkspace, null, null,
                new PreviewerTcpSession(document.FilePath, log: null));
        }

        // Activation order before: 5, 4, 3, 2, 1, 0. Activating 0 moves 2 and 1 beyond the limit.
        main.ActiveDocument = documents[0];
        Assert.Equal(new[] { true, false, false, true, true, true }, documents.Select(HasPreviewer));
        Assert.Null(documents[1].PreviewerSession);
        Assert.Null(documents[2].PreviewerSession);
        Assert.NotNull(documents[0].PreviewerSession);

        main.ActiveDocument = documents[2];
        Assert.Equal(new[] { true, false, false, false, true, true }, documents.Select(HasPreviewer));

        bool HasPreviewer(DesignerDocumentViewModel document) =>
            main.PreviewerLaunchService.TryGetSession(document.FilePath, out _);
    }

    [AvaloniaFact(Timeout = 60000)]
    public async Task Exporting_a_preview_keeps_the_live_previewer_of_the_document()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string projectPath = Path.Combine(_root, "Views.csproj");
        await File.WriteAllTextAsync(projectPath, "<Project />", cancellationToken);
        string xamlPath = Path.Combine(_root, "View.axaml");
        await File.WriteAllTextAsync(xamlPath, Xaml, cancellationToken);
        string output = Path.Combine(_root, "missing-output");
        WorkspaceModel workspace = new()
        {
            Projects = new[]
            {
                new ProjectModel
                {
                    Name = "Views", ProjectPath = projectPath,
                    OutputAssemblyPath = Path.Combine(output, "Views.dll"),
                    XamlFiles = new[] { new XamlFileModel { FilePath = xamlPath, RelativePath = "View.axaml" } },
                    Files = Array.Empty<ProjectFileModel>(), References = Array.Empty<AssemblyReference>()
                }
            }
        };
        using MainWindowViewModel main = new(
            workspaceService: new FixedWorkspaceService(workspace),
            metadataService: new TypeMetadataService(),
            recentFilesPath: Path.Combine(_root, "recent.json"),
            workspaceSessions: new WorkspaceSessionStore(Path.Combine(_root, "sessions.json")));
        PreviewerTrustDecision trust = PreviewerTrustDecision.Cancel;
        using IDisposable trustHandler = main.PreviewerTrustInteraction.RegisterHandler(context => context.SetOutput(trust));

        await main.OpenFileAsync(projectPath).WaitAsync(cancellationToken);
        Assert.True(main.HasWorkspace);
        await main.OpenFileAsync(xamlPath).WaitAsync(cancellationToken);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDocument);
        PreviewerTcpSession session = new(xamlPath, log: null);
        main.PreviewerLaunchService.AddRunningInstance(xamlPath, workspace, projectPath, null, session);
        document.PreviewerSession = session;
        trust = PreviewerTrustDecision.AllowOnce;

        XamlVisualEditor.Extensions.PreviewExportResult result = await main.ExportPreviewAsync(
            xamlPath, Path.Combine(_root, "View.png"), timeoutMs: 300).WaitAsync(cancellationToken);

        Assert.False(result.Success);
        Assert.Equal(UiStrings.PreviewerFrameTimeout, result.Error);
        Assert.True(main.PreviewerLaunchService.TryGetSession(xamlPath, out PreviewerTcpSession? live));
        Assert.Same(session, live);
        Assert.Same(session, document.PreviewerSession);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static Process StartIdleProcess()
    {
        ProcessStartInfo startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("ping", "-n 60 127.0.0.1")
            : new ProcessStartInfo("sleep", "60");
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        return Process.Start(startInfo) ?? throw new InvalidOperationException("The idle process did not start.");
    }

    private sealed class FixedWorkspaceService(WorkspaceModel workspace) : IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) =>
            Task.FromResult(workspace);

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) => workspace;
    }
}
