using System.Collections.Concurrent;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Extensions.Hosting.IdeBridge;
using XamlVisualEditor.IdeBridgeExtension;
using Xunit;

#pragma warning disable CS0067 // Events required by interfaces for test doubles.

namespace XamlVisualEditor.Tests.UI;

public sealed class IdeBridgePanelViewModelTests
{
    [AvaloniaFact]
    public async Task ControllerChangesFromABackgroundThreadUpdateThePanelOnTheUiThread()
    {
        InMemoryWindow window = new();
        InMemorySettingsStore settings = new();
        IdeBridgePermissionService permissions = new(settings, window);
        FixedWorkspaceInfo workspaceInfo = new();
        await using IdeBridgeRuntimeController controller = new(settings, permissions, new CommandRegistry(), new InMemoryWorkspace(),
            workspaceInfo, window, new EmptyEditorServices(), new EmptyDiagnostics(), new EmptyTerminal());
        using IdeBridgePanelViewModel viewModel = new(controller, permissions, workspaceInfo);
        ConcurrentQueue<bool> onUiThread = new();
        viewModel.PropertyChanged += (_, _) => onUiThread.Enqueue(Dispatcher.UIThread.CheckAccess());
        viewModel.ActivityLog.CollectionChanged += (_, _) => onUiThread.Enqueue(Dispatcher.UIThread.CheckAccess());

        await Task.Run(() => controller.ApplySettingsAsync(new IdeBridgeSettings { Enabled = false, Transport = "tcp" }, CancellationToken.None));
        Dispatcher.UIThread.RunJobs();

        Assert.False(viewModel.IsEnabled);
        Assert.Equal("tcp", viewModel.SelectedTransport);
        Assert.NotEmpty(onUiThread);
        Assert.All(onUiThread, value => Assert.True(value));
    }

    private sealed class FixedWorkspaceInfo : IWorkspaceInfo
    {
        public string? WorkspacePath => null;

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;
    }

    private sealed class EmptyEditorServices : IEditorServices
    {
        public IEditorDocument? ActiveDocument => null;

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => Array.Empty<IEditorDocument>();

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct)
            => Task.FromResult<IEditorDocument?>(null);

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class EmptyDiagnostics : IDiagnosticsService
    {
        public event EventHandler<DiagnosticsChannelsChangedEventArgs>? ChannelsChanged;

        public event EventHandler<DiagnosticsChannelPublishedEventArgs>? DiagnosticsChannelPublished;

        public event EventHandler<DiagnosticsSnapshotPublishedEventArgs>? DiagnosticsSnapshotPublished;

        public event EventHandler<DiagnosticsPublishedEventArgs>? DiagnosticsPublished;

        public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DiagnosticsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());

        public Task<IReadOnlyList<DiagnosticsDocumentSnapshot>> GetDiagnosticsSnapshotAsync(DiagnosticsQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagnosticsDocumentSnapshot>>(Array.Empty<DiagnosticsDocumentSnapshot>());

        public Task<IReadOnlyList<DiagnosticsChannelInfo>> GetChannelsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<DiagnosticsChannelInfo>>(Array.Empty<DiagnosticsChannelInfo>());
    }

    private sealed class EmptyTerminal : ITerminalBridge
    {
        public event EventHandler<TerminalChangedEventArgs>? TerminalCreated;

        public event EventHandler<TerminalChangedEventArgs>? TerminalClosed;

        public event EventHandler<ActiveTerminalChangedEventArgs>? ActiveTerminalChanged;

        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

        public event EventHandler<TerminalExitEventArgs>? TerminalExited;

        public event EventHandler<TerminalDimensionsChangedEventArgs>? TerminalDimensionsChanged;

        public Task<TerminalInfo> CreateAsync(TerminalCreateRequest request, CancellationToken ct)
            => Task.FromResult(new TerminalInfo(Guid.NewGuid(), "terminal"));

        public Task SendTextAsync(Guid terminalId, string text, CancellationToken ct) => Task.CompletedTask;

        public Task<IReadOnlyList<TerminalInfo>> GetTerminalsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TerminalInfo>>(Array.Empty<TerminalInfo>());

        public Task<Guid?> GetActiveTerminalIdAsync(CancellationToken ct) => Task.FromResult<Guid?>(null);

        public Task<bool> CloseAsync(Guid terminalId, CancellationToken ct) => Task.FromResult(false);

        public Task<TaskExecutionResult> RunTaskAsync(TaskExecutionRequest request, CancellationToken ct)
            => Task.FromResult(new TaskExecutionResult(request.TaskId, 0, Array.Empty<string>(), Array.Empty<TaskProblemMatch>()));
    }
}
