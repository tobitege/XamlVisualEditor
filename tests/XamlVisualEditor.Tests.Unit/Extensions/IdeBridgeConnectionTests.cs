using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Extensions.Hosting.IdeBridge;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class IdeBridgeConnectionTests
{
    [Fact]
    public async Task RequestHandlerSendsResult()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IdeBridgeJsonRpcConnection connection = pipe.CreateConnection();

        connection.RegisterRequestHandler("ping", (_, _) => Task.FromResult<object?>(new { pong = true }));
        connection.Start(cts.Token);

        await IdeBridgeMessageFraming.WriteMessageAsync(pipe.ServerWriter, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "ping",
            @params = new { }
        }, cts.Token);

        using JsonDocument response = await IdeBridgeMessageFraming.ReadMessageAsync(pipe.ServerReader, cts.Token);
        JsonElement root = response.RootElement;
        Assert.Equal(1, root.GetProperty("id").GetInt32());
        Assert.True(root.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task NotificationIsRaised()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IdeBridgeJsonRpcConnection connection = pipe.CreateConnection();

        TaskCompletionSource<(string Method, JsonElement? Params)> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (method, parameters) => tcs.TrySetResult((method, parameters));
        connection.Start(cts.Token);

        await IdeBridgeMessageFraming.WriteMessageAsync(pipe.ServerWriter, new
        {
            jsonrpc = "2.0",
            method = "workspace.changed",
            @params = new { workspaceId = "w1" }
        }, cts.Token);

        await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(2), cts.Token));
        Assert.True(tcs.Task.IsCompleted, "Expected notification was not received.");
        var result = await tcs.Task;
        Assert.Equal("workspace.changed", result.Method);
    }

    [Fact]
    public async Task NonStringMethodIsAnsweredWithInvalidRequestAndTheConnectionKeepsReading()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IdeBridgeJsonRpcConnection connection = pipe.CreateConnection();

        connection.RegisterRequestHandler("ping", (_, _) => Task.FromResult<object?>(new { pong = true }));
        connection.Start(cts.Token);

        await IdeBridgeMessageFraming.WriteMessageAsync(pipe.ServerWriter, new { jsonrpc = "2.0", id = 3, method = 42 }, cts.Token);
        await IdeBridgeMessageFraming.WriteMessageAsync(pipe.ServerWriter, new { jsonrpc = "2.0", id = 4, method = "ping" }, cts.Token);

        using JsonDocument invalid = await IdeBridgeMessageFraming.ReadMessageAsync(pipe.ServerReader, cts.Token);
        Assert.Equal(3, invalid.RootElement.GetProperty("id").GetInt32());
        Assert.Equal(-32600, invalid.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.False(invalid.RootElement.TryGetProperty("result", out _));
        using JsonDocument ping = await IdeBridgeMessageFraming.ReadMessageAsync(pipe.ServerReader, cts.Token);
        Assert.Equal(4, ping.RootElement.GetProperty("id").GetInt32());
        Assert.True(ping.RootElement.GetProperty("result").GetProperty("pong").GetBoolean());
    }

    [Fact]
    public async Task RevokedConsentEndsTheLiveSessionAndTheNextInitializePromptsAgain()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IdeBridgeJsonRpcConnection connection = pipe.CreateConnection();
        string workspaceId = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xve-bridge-revocation"));
        ConsentWindow window = new();
        IdeBridgePermissionService permissions = new(new InMemorySettingsStore(), window);
        IdeBridgeSessionRegistry registry = new();
        permissions.PermissionsRevoked += registry.Revoke;
        new IdeBridgeHandshakeHandler(new IdeBridgeSessionService(permissions, new FixedWorkspaceInfo(workspaceId)), registry)
            .Register(connection);
        connection.Start(cts.Token);

        string first = await InitializeAsync(pipe, 1, cts.Token);
        Assert.True(registry.TryGet(connection, out IdeBridgeSessionInfo session));
        Assert.Equal(first, session.SessionToken);
        Assert.Equal(workspaceId, session.WorkspaceId);
        Assert.True(session.Capabilities.Write);

        await permissions.ClearPermissionAsync(workspaceId, CancellationToken.None);
        Assert.False(registry.TryGet(connection, out _));

        string second = await InitializeAsync(pipe, 2, cts.Token);
        Assert.Equal(2, window.QuickPickCount);
        Assert.NotEqual(first, second);
        Assert.True(registry.TryGet(connection, out IdeBridgeSessionInfo renewed));
        Assert.Equal(second, renewed.SessionToken);

        await permissions.ClearAllAsync(CancellationToken.None);
        Assert.False(registry.TryGet(connection, out _));
        Assert.Empty(await permissions.GetPermissionsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task RevocationRequestedWhileTheSessionIsAuthorizedEndsThatSession()
    {
        using DuplexPipe pipe = new();
        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        await using IdeBridgeJsonRpcConnection connection = pipe.CreateConnection();
        string workspaceId = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "xve-bridge-revocation"));
        InMemorySettingsStore settings = new();
        IdeBridgePermissionService permissions = new(settings, new ConsentWindow());
        IdeBridgeSessionRegistry registry = new();
        permissions.PermissionsRevoked += registry.Revoke;
        new IdeBridgeHandshakeHandler(new IdeBridgeSessionService(permissions, new FixedWorkspaceInfo(workspaceId)), registry)
            .Register(connection);
        bool requested = false;
        Task? revocation = null;
        bool revokedDuringAuthorization = true;
        // The granted consent is saved under the consent lock, right before the session is registered.
        settings.SectionChanged += (_, _) =>
        {
            if (requested) return;
            requested = true;
            revocation = permissions.ClearPermissionAsync(workspaceId, CancellationToken.None);
            revokedDuringAuthorization = revocation.IsCompleted;
        };
        connection.Start(cts.Token);

        string token = await InitializeAsync(pipe, 1, cts.Token);

        Assert.False(string.IsNullOrEmpty(token));
        Assert.False(revokedDuringAuthorization);
        Assert.NotNull(revocation);
        await revocation;
        Assert.False(registry.TryGet(connection, out _));
        Assert.Empty(await permissions.GetPermissionsAsync(CancellationToken.None));
    }

    private static async Task<string> InitializeAsync(DuplexPipe pipe, int id, CancellationToken ct)
    {
        await IdeBridgeMessageFraming.WriteMessageAsync(pipe.ServerWriter, new
        {
            jsonrpc = "2.0",
            id,
            method = IdeBridgeProtocol.InitializeMethod,
            @params = new BridgeInitializeParams(null, "tests", "1")
        }, ct);

        using JsonDocument response = await IdeBridgeMessageFraming.ReadMessageAsync(pipe.ServerReader, ct);
        Assert.Equal(id, response.RootElement.GetProperty("id").GetInt32());
        Assert.False(response.RootElement.TryGetProperty("error", out JsonElement error), error.ToString());
        return response.RootElement.GetProperty("result").GetProperty("sessionToken").GetString()!;
    }

    private sealed class FixedWorkspaceInfo : IWorkspaceInfo
    {
        public FixedWorkspaceInfo(string workspacePath) => WorkspacePath = workspacePath;

        public string? WorkspacePath { get; }

        public event EventHandler<WorkspaceChangedEventArgs> WorkspaceChanged
        {
            add { }
            remove { }
        }
    }

    private sealed class ConsentWindow : IWindow
    {
        public int QuickPickCount { get; private set; }

        public Task ShowInformationMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowWarningMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ShowErrorMessageAsync(string message, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

        public Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, QuickPickOptions options, CancellationToken cancellationToken)
        {
            QuickPickCount++;
            return Task.FromResult<QuickPickItem?>(items.First(item => item.Label == UiStrings.AllowFullAccess));
        }

        public IOutputChannel CreateOutputChannel(string name) => new InMemoryOutputChannel(name);

        public Task<IReadOnlyList<OutputChannelInfo>> GetOutputChannelsAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<OutputChannelInfo>>(Array.Empty<OutputChannelInfo>());

#pragma warning disable CS0067
        public event EventHandler<OutputChannelEventArgs>? OutputChannelCreated;

        public event EventHandler<OutputChannelEventArgs>? OutputChannelRemoved;

        public event EventHandler<OutputChannelMessageEventArgs>? OutputChannelMessage;

        public event EventHandler<OutputChannelClearedEventArgs>? OutputChannelCleared;

        public event EventHandler<OutputChannelEventArgs>? OutputChannelShowRequested;
#pragma warning restore CS0067

        public IStatusBarItem CreateStatusBarItem(StatusBarAlignment alignment, int priority) => new InMemoryStatusBarItem();
    }

    private sealed class DuplexPipe : IDisposable
    {
        private readonly Pipe _clientToServer = new();
        private readonly Pipe _serverToClient = new();

        public Stream ServerReader => _clientToServer.Reader.AsStream();
        public Stream ServerWriter => _serverToClient.Writer.AsStream();

        public IdeBridgeJsonRpcConnection CreateConnection()
        {
            return new IdeBridgeJsonRpcConnection(_serverToClient.Reader.AsStream(), _clientToServer.Writer.AsStream());
        }

        public void Dispose()
        {
            _clientToServer.Reader.Complete();
            _clientToServer.Writer.Complete();
            _serverToClient.Reader.Complete();
            _serverToClient.Writer.Complete();
        }
    }
}
