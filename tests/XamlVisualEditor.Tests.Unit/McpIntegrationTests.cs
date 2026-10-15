using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Extensions.Hosting.Mcp;
using XamlVisualEditor.McpExtension;
using Xunit;

#pragma warning disable CS0067

namespace XamlVisualEditor.Tests.Unit;

public sealed class McpIntegrationTests
{
    private const int PortAttempts = 5;

    [Fact]
    public async Task FirstConnectPersistsConsentAndReconnectIsGrantedWithoutPrompt()
    {
        InMemorySettingsStore settings = new();
        string workspacePath = "/tmp/xve";
        string workspaceId = System.IO.Path.GetFullPath(workspacePath);
        StubWindow window = new(XamlVisualEditor.Core.Localization.UiStrings.AllowReadOnly);
        McpSessionRegistry registry = new();
        McpRequestRouter router = CreateHandshakeRouter(settings, window, new StubWorkspaceInfo(workspacePath), registry, out _);

        McpInitializeResult first = await InitializeAsync(router);
        Assert.Equal(1, window.QuickPickCount);
        Dictionary<string, McpWorkspacePermissionState>? persisted = settings.Get<Dictionary<string, McpWorkspacePermissionState>>("mcp.permissions");
        Assert.NotNull(persisted);
        Assert.Equal(McpAccessLevel.ReadOnly, persisted![workspaceId].AccessLevel);

        McpInitializeResult second = await InitializeAsync(router);
        Assert.Equal(1, window.QuickPickCount);
        Assert.NotEqual(first.SessionToken, second.SessionToken);
        Assert.Equal(McpAccessLevel.ReadOnly, registry.Resolve(new McpRequestContext(null, second.SessionToken))?.AccessLevel);
        Assert.NotNull(registry.Resolve(new McpRequestContext(null, first.SessionToken)));
    }

    [Fact]
    public async Task RevokedConsentEndsLiveSessionsAndPromptsAgain()
    {
        InMemorySettingsStore settings = new();
        string workspacePath = "/tmp/xve";
        StubWindow window = new(XamlVisualEditor.Core.Localization.UiStrings.AllowFullAccess);
        McpSessionRegistry registry = new();
        McpRequestRouter router = CreateHandshakeRouter(settings, window, new StubWorkspaceInfo(workspacePath), registry, out McpPermissionService permissions);
        permissions.PermissionsRevoked += registry.Revoke;

        McpInitializeResult first = await InitializeAsync(router);
        await permissions.ClearPermissionAsync(System.IO.Path.GetFullPath(workspacePath), CancellationToken.None);
        Assert.Null(registry.Resolve(new McpRequestContext(null, first.SessionToken)));
        Assert.Equal(0, registry.Count);

        McpInitializeResult second = await InitializeAsync(router);
        Assert.Equal(2, window.QuickPickCount);
        Assert.NotNull(registry.Resolve(new McpRequestContext(null, second.SessionToken)));

        await permissions.ClearAllAsync(CancellationToken.None);
        Assert.Null(registry.Resolve(new McpRequestContext(null, second.SessionToken)));
        Assert.Empty(await permissions.GetPermissionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task DeniedConsentIsNotPersistedAndFailsInitialize()
    {
        InMemorySettingsStore settings = new();
        StubWindow window = new(XamlVisualEditor.Core.Localization.UiStrings.Deny);
        McpSessionRegistry registry = new();
        McpRequestRouter router = CreateHandshakeRouter(settings, window, new StubWorkspaceInfo("/tmp/xve"), registry, out McpPermissionService permissions);

        await Assert.ThrowsAsync<McpJsonRpcException>(() => InitializeAsync(router));
        await Assert.ThrowsAsync<McpJsonRpcException>(() => InitializeAsync(router));
        Assert.Equal(2, window.QuickPickCount);
        Assert.Empty(await permissions.GetPermissionsAsync(CancellationToken.None));
        Assert.Equal(0, registry.Count);
    }

    private static McpRequestRouter CreateHandshakeRouter(
        InMemorySettingsStore settings,
        IWindow window,
        IWorkspaceInfo workspaceInfo,
        McpSessionRegistry registry,
        out McpPermissionService permissions)
    {
        permissions = new McpPermissionService(settings, window);
        McpRequestRouter router = new(registry);
        new McpHandshakeHandler(new McpSessionService(permissions, workspaceInfo), registry).Register(router);
        return router;
    }

    private static async Task<McpInitializeResult> InitializeAsync(McpRequestRouter router)
    {
        JsonElement parameters = JsonSerializer.SerializeToElement(
            new McpInitializeParams(null, new McpClientInfo("tests", "1"), null), McpMessageFraming.SerializerOptions);
        object? result = await router.DispatchAsync(McpProtocol.InitializeMethod, new McpRequestContext(null, null), parameters, CancellationToken.None);
        return Assert.IsType<McpInitializeResult>(result);
    }

    [Fact]
    public async Task ToolsCallHonorsWritePermission()
    {
        StubCommands commands = new();
        InMemoryWorkspace workspace = new();
        StubWorkspaceInfo workspaceInfo = new("/tmp/xve");
        StubWindow window = new();
        StubEditorServices editor = new();
        StubDiagnostics diagnostics = new();
        StubTerminalBridge terminal = new();
        InMemorySettingsStore settings = new();

        McpToolCatalog catalog = new(commands, workspace, workspaceInfo, window, editor, diagnostics, terminal, settings);
        McpToolsHandler tools = new(catalog);
        McpSessionRegistry registry = new();
        McpRequestRouter router = new(registry);
        tools.Register(router);

        registry.Set("token-read", new McpSessionInfo("/tmp/xve", McpAccessLevel.ReadOnly, "token-read"));

        McpToolCallParams call = new("xve.workspace.writeFile", JsonSerializer.SerializeToElement(new { path = "test.txt", content = "hi" }));
        JsonElement parameters = JsonSerializer.SerializeToElement(call, McpMessageFraming.SerializerOptions);

        await Assert.ThrowsAsync<McpJsonRpcException>(() =>
            router.DispatchAsync(McpProtocol.ToolsCallMethod, new McpRequestContext(null, "token-read"), parameters, CancellationToken.None));
    }

    [Fact]
    public async Task RuntimeControllerKeepsHttpServerAliveAfterStartupTokenCancellation()
    {
        InMemorySettingsStore settings = new();
        StubWorkspaceInfo workspaceInfo = new("/tmp/xve");
        StubWindow window = new();
        StubCommands commands = new();
        InMemoryWorkspace workspace = new();
        StubEditorServices editor = new();
        StubDiagnostics diagnostics = new();
        StubTerminalBridge terminal = new();
        McpPermissionService permissions = new(settings, window);

        await using McpRuntimeController controller = new(
            settings,
            permissions,
            commands,
            workspace,
            workspaceInfo,
            window,
            editor,
            diagnostics,
            terminal);

        using CancellationTokenSource startupToken = new();
        int port = await InitializeOnAvailablePortAsync(controller, settings, candidate => new McpSettings(true, "http", candidate, "/mcp/"), startupToken.Token);
        startupToken.Cancel();

        using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(2) };
        HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/mcp/");
        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task InitializeAutoGrantsWhenWindowIsNonInteractive()
    {
        InMemorySettingsStore settings = new();
        string workspacePath = "/tmp/xve";
        StubWorkspaceInfo workspaceInfo = new(workspacePath);
        McpPermissionService permissionService = new(settings, new InMemoryWindow());
        McpSessionService sessionService = new(permissionService, workspaceInfo);

        McpInitializeOutcome outcome = await sessionService.InitializeAsync(
            new McpInitializeParams(null, new McpClientInfo("tests", "1"), null),
            CancellationToken.None);

        Assert.Equal(McpAccessLevel.Full, outcome.AccessLevel);
        Assert.False(string.IsNullOrWhiteSpace(outcome.Result.SessionToken));

        string workspaceId = System.IO.Path.GetFullPath(workspacePath);
        Dictionary<string, McpWorkspacePermissionState>? persisted = settings.Get<Dictionary<string, McpWorkspacePermissionState>>("mcp.permissions");
        Assert.NotNull(persisted);
        Assert.True(persisted!.TryGetValue(workspaceId, out McpWorkspacePermissionState? state));
        Assert.Equal(McpAccessLevel.Full, state!.AccessLevel);
    }

    [Fact]
    public async Task StdioAcceptsStandardJsonLinesAndReturnsStandardPromptContent()
    {
        InMemorySettingsStore settings = new();
        StubWorkspaceInfo workspaceInfo = new("/tmp/xve");
        StubEditorServices editor = new();
        StubDiagnostics diagnostics = new();
        McpSessionRegistry registry = new();
        McpRequestRouter router = new(registry);
        new McpHandshakeHandler(new McpSessionService(new McpPermissionService(settings, new InMemoryWindow()), workspaceInfo), registry).Register(router);
        new McpToolsHandler(new McpToolCatalog(new StubCommands(), new InMemoryWorkspace(), workspaceInfo,
            new StubWindow(), editor, diagnostics, new StubTerminalBridge(), settings)).Register(router);
        new McpPromptsHandler(workspaceInfo, editor, diagnostics).Register(router);
        string input = """
            {invalid json}
            {"jsonrpc":"2.0","id":"init","method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":"test","version":"1"},"capabilities":{}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":"tools","method":"tools/list"}
            {"jsonrpc":"2.0","id":"prompt","method":"prompts/get","params":{"name":"ide-context"}}
            {"jsonrpc":"2.0","id":"ping","method":"ping"}
            """ + "\n";
        using MemoryStream inputStream = new(Encoding.UTF8.GetBytes(input));
        using MemoryStream output = new();
        await using McpJsonRpcConnection connection = new(inputStream, output, router);
        TaskCompletionSource disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Disconnected += error => { if (error is null) disconnected.TrySetResult(); else disconnected.TrySetException(error); };
        connection.Start(CancellationToken.None);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        using JsonDocument malformed = JsonDocument.Parse(lines[0]);
        Assert.Equal(JsonValueKind.Null, malformed.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(-32700, malformed.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        using JsonDocument initialized = JsonDocument.Parse(lines[1]);
        Assert.Equal(McpProtocol.ProtocolVersion, initialized.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.False(initialized.RootElement.GetProperty("result").GetProperty("capabilities").TryGetProperty("logging", out _));
        using JsonDocument tools = JsonDocument.Parse(lines[2]);
        Assert.NotEmpty(tools.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        using JsonDocument prompt = JsonDocument.Parse(lines[3]);
        JsonElement message = prompt.RootElement.GetProperty("result").GetProperty("messages")[0];
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("text", message.GetProperty("content").GetProperty("type").GetString());
        Assert.True(registry.Resolve(new McpRequestContext(connection, null)) is not null);
    }

    [Fact]
    public async Task HttpLifecycleUsesSessionHeaderAndHonorsRevocationAndOrigin()
    {
        InMemorySettingsStore settings = new();
        StubWorkspaceInfo workspaceInfo = new("/tmp/xve");
        McpPermissionService permissions = new(settings, new InMemoryWindow());
        await using McpRuntimeController controller = new(settings, permissions, new StubCommands(), new InMemoryWorkspace(),
            workspaceInfo, new StubWindow(), new StubEditorServices(), new StubDiagnostics(), new StubTerminalBridge());
        int port = await InitializeOnAvailablePortAsync(controller, settings, candidate => new McpSettings(true, "http", candidate), CancellationToken.None);
        using HttpClient client = new() { BaseAddress = new Uri($"http://127.0.0.1:{port}/mcp/"), Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        using HttpResponseMessage initialize = await PostAsync(client, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","clientInfo":{"name":"test","version":"1"},"capabilities":{}}}""");
        Assert.Equal(HttpStatusCode.OK, initialize.StatusCode);
        string sessionId = Assert.Single(initialize.Headers.GetValues("Mcp-Session-Id"));
        Assert.Equal(1, controller.ConnectionCount);
        client.DefaultRequestHeaders.Add("Mcp-Session-Id", sessionId);
        client.DefaultRequestHeaders.Add("MCP-Protocol-Version", McpProtocol.ProtocolVersion);
        using HttpResponseMessage notification = await PostAsync(client, """{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        Assert.Equal(HttpStatusCode.Accepted, notification.StatusCode);
        Assert.Equal(string.Empty, await notification.Content.ReadAsStringAsync());
        const string listTools = """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""";
        using HttpResponseMessage tools = await PostAsync(client, listTools);
        using JsonDocument toolsBody = JsonDocument.Parse(await tools.Content.ReadAsStringAsync());
        Assert.NotEmpty(toolsBody.RootElement.GetProperty("result").GetProperty("tools").EnumerateArray());
        client.DefaultRequestHeaders.Add("Origin", "https://untrusted.example");
        using HttpResponseMessage foreignOrigin = await PostAsync(client, listTools);
        Assert.Equal(HttpStatusCode.Forbidden, foreignOrigin.StatusCode);
        client.DefaultRequestHeaders.Remove("Origin");
        using HttpResponseMessage malformed = await PostAsync(client, "{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await permissions.ClearPermissionAsync(Path.GetFullPath(workspaceInfo.WorkspacePath!), CancellationToken.None);
        Assert.Equal(0, controller.ConnectionCount);
        using HttpResponseMessage revoked = await PostAsync(client, listTools);
        Assert.Equal(HttpStatusCode.NotFound, revoked.StatusCode);
        Assert.Null(controller.LastError);
    }

    [Fact]
    public async Task OccupiedPortLeavesTheControllerStoppedAndCanBeRetried()
    {
        InMemorySettingsStore settings = new();
        (HttpListener occupiedListener, int port) = OccupyAvailablePort();
        using HttpListener occupied = occupiedListener;
        await settings.UpdateAsync("mcp", new McpSettings(true, "http", port), SettingsTarget.User, CancellationToken.None);
        StubWorkspaceInfo workspaceInfo = new("/tmp/xve");
        McpPermissionService permissions = new(settings, new InMemoryWindow());
        await using McpRuntimeController controller = new(settings, permissions, new StubCommands(), new InMemoryWorkspace(),
            workspaceInfo, new StubWindow(), new StubEditorServices(), new StubDiagnostics(), new StubTerminalBridge());
        await controller.InitializeAsync(CancellationToken.None);
        Assert.False(controller.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(controller.LastError));
        occupied.Stop();
        await controller.RestartAsync(CancellationToken.None);
        Assert.True(controller.IsRunning);
        Assert.Null(controller.LastError);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string json) =>
        client.PostAsync(string.Empty, new StringContent(json, Encoding.UTF8, "application/json"));

    /// <summary>
    /// Starts the controller on a free loopback port. Another process can take the probed port before the
    /// server binds it, so a failed start is retried on a newly probed port.
    /// </summary>
    private static async Task<int> InitializeOnAvailablePortAsync(McpRuntimeController controller, InMemorySettingsStore settings,
        Func<int, McpSettings> createSettings, CancellationToken ct)
    {
        int port = GetAvailablePort();
        await settings.UpdateAsync("mcp", createSettings(port), SettingsTarget.User, CancellationToken.None);
        await controller.InitializeAsync(ct);
        for (int attempt = 1; !controller.IsRunning && attempt < PortAttempts; attempt++)
        {
            port = GetAvailablePort();
            await controller.ApplySettingsAsync(createSettings(port), ct);
        }

        return port;
    }

    /// <summary>Binds an HTTP listener to a free loopback port, retrying when the probed port was taken meanwhile.</summary>
    private static (HttpListener Listener, int Port) OccupyAvailablePort()
    {
        for (int attempt = 1; ; attempt++)
        {
            int port = GetAvailablePort();
            HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/mcp/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException) when (attempt < PortAttempts)
            {
                listener.Close();
            }
        }
    }

    private static int GetAvailablePort()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class StubWorkspaceInfo : IWorkspaceInfo
    {
        public StubWorkspaceInfo(string? workspacePath)
        {
            WorkspacePath = workspacePath;
        }

        public string? WorkspacePath { get; }

        public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged;
    }

    private sealed class StubCommands : ICommands
    {
        public IDisposable Register(string commandId, Func<CommandContext, Task> handler)
        {
            return new DummyDisposable();
        }

        public Task ExecuteAsync(string commandId, IReadOnlyList<object?>? args, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> GetCommandsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
        }
    }

    private sealed class StubWindow : IWindow
    {
        private readonly string? _quickPickSelection;

        public StubWindow(string? quickPickSelection = null)
        {
            _quickPickSelection = quickPickSelection;
        }

        public int QuickPickCount { get; private set; }

        public Task ShowInformationMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowWarningMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ShowErrorMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions options, CancellationToken cancellationToken)
        {
            QuickPickCount++;
            if (string.IsNullOrWhiteSpace(_quickPickSelection))
            {
                return Task.FromResult<QuickPickItem?>(null);
            }

            QuickPickItem? item = items.FirstOrDefault(candidate =>
                string.Equals(candidate.Label, _quickPickSelection, StringComparison.OrdinalIgnoreCase));

            return Task.FromResult(item);
        }
        public IOutputChannel CreateOutputChannel(string name) => new InMemoryOutputChannel(name);

        public Task<IReadOnlyList<OutputChannelInfo>> GetOutputChannelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OutputChannelInfo>>(Array.Empty<OutputChannelInfo>());

        public event EventHandler<OutputChannelEventArgs>? OutputChannelCreated;

        public event EventHandler<OutputChannelEventArgs>? OutputChannelRemoved;

        public event EventHandler<OutputChannelMessageEventArgs>? OutputChannelMessage;

        public event EventHandler<OutputChannelClearedEventArgs>? OutputChannelCleared;

        public IStatusBarItem CreateStatusBarItem(StatusBarAlignment alignment, int priority) => new InMemoryStatusBarItem();
    }

    private sealed class StubEditorServices : IEditorServices
    {
        private readonly List<IEditorDocument> _documents = new();

        public IEditorDocument? ActiveDocument => _documents.FirstOrDefault();

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => _documents;

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct)
        {
            StubEditorDocument doc = new(filePath);
            _documents.Add(doc);
            return Task.FromResult<IEditorDocument?>(doc);
        }

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct)
        {
            StubEditorDocument doc = new(filePath);
            _documents.Add(doc);
            return Task.FromResult<IEditorDocument?>(doc);
        }

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;
    }

    private sealed class StubEditorDocument : IEditorDocument
    {
        private string _text = string.Empty;

        public StubEditorDocument(string filePath)
        {
            FilePath = filePath;
        }

        public string FilePath { get; }

        public string? LanguageId => "text";

        public int CaretOffset { get; set; }

        public int SelectionStart { get; set; }

        public int SelectionLength { get; set; }

        public Task<string> GetTextAsync(CancellationToken ct)
        {
            return Task.FromResult(_text);
        }

        public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct)
        {
            _text = _text + string.Join("", edits.Select(e => e.NewText));
            return Task.CompletedTask;
        }

        public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

        public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;
    }

    private sealed class StubDiagnostics : IDiagnosticsService
    {
        public event EventHandler<DiagnosticsChannelsChangedEventArgs>? ChannelsChanged;

        public event EventHandler<DiagnosticsChannelPublishedEventArgs>? DiagnosticsChannelPublished;

        public event EventHandler<DiagnosticsSnapshotPublishedEventArgs>? DiagnosticsSnapshotPublished;

        public event EventHandler<DiagnosticsPublishedEventArgs>? DiagnosticsPublished;

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(string? filePath, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());
        }

        public Task<IReadOnlyList<LanguageDiagnostic>> GetDiagnosticsAsync(DiagnosticsQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<LanguageDiagnostic>>(Array.Empty<LanguageDiagnostic>());
        }

        public Task<IReadOnlyList<DiagnosticsDocumentSnapshot>> GetDiagnosticsSnapshotAsync(
            DiagnosticsQuery query,
            CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<DiagnosticsDocumentSnapshot>>(Array.Empty<DiagnosticsDocumentSnapshot>());
        }

        public Task<IReadOnlyList<DiagnosticsChannelInfo>> GetChannelsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<DiagnosticsChannelInfo>>(Array.Empty<DiagnosticsChannelInfo>());
        }

        public event EventHandler<DiagnosticsChangedEventArgs>? DiagnosticsChanged;
    }

    private sealed class StubTerminalBridge : ITerminalBridge
    {
        public event EventHandler<TerminalChangedEventArgs>? TerminalCreated;
        public event EventHandler<TerminalChangedEventArgs>? TerminalClosed;
        public event EventHandler<ActiveTerminalChangedEventArgs>? ActiveTerminalChanged;
        public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;
        public event EventHandler<TerminalExitEventArgs>? TerminalExited;
        public event EventHandler<TerminalDimensionsChangedEventArgs>? TerminalDimensionsChanged;

        public Task<TerminalInfo> CreateAsync(TerminalCreateRequest request, CancellationToken ct)
        {
            return Task.FromResult(new TerminalInfo(Guid.NewGuid(), request.Title ?? "terminal"));
        }

        public Task SendTextAsync(Guid terminalId, string text, CancellationToken ct)
        {
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<TerminalInfo>> GetTerminalsAsync(CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<TerminalInfo>>(Array.Empty<TerminalInfo>());
        }

        public Task<Guid?> GetActiveTerminalIdAsync(CancellationToken ct)
        {
            return Task.FromResult<Guid?>(null);
        }

        public Task<bool> CloseAsync(Guid terminalId, CancellationToken ct)
        {
            return Task.FromResult(false);
        }

        public Task<TaskExecutionResult> RunTaskAsync(TaskExecutionRequest request, CancellationToken ct)
        {
            return Task.FromResult(new TaskExecutionResult(
                request.TaskId,
                0,
                Array.Empty<string>(),
                Array.Empty<TaskProblemMatch>()));
        }
    }

    private sealed class DummyDisposable : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

#pragma warning restore CS0067
