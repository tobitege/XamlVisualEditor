using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcSessionTests
{
    [Fact]
    public async Task InitializeAsync_WithoutSettingsStaysDisconnected()
    {
        FakeTfvcClient client = new();
        using TfvcSession session = CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo());

        await session.InitializeAsync(CancellationToken.None);

        Assert.False(session.IsConnected);
        Assert.NotNull(session.StatusMessage);
        Assert.Equal(0, client.PendingChangeQueries);
    }

    [Fact]
    public async Task ApplySettingsAsync_ConnectsPersistsAndSelectsWorkspaceOfEditor()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "TFVC workspaces map Windows paths, and tf.exe exists only on Windows.");
        TfvcWorkspace other = TfvcTestData.CreateWorkspace("OTHER", @"C:\other");
        TfvcWorkspace product = TfvcTestData.CreateWorkspace();
        FakeTfvcClient client = new()
        {
            Workspaces = [other, product],
            PendingChanges = [TfvcTestData.CreateChange("$/Product/a.cs", @"C:\src\Product\a.cs")]
        };
        InMemoryExtensionStorage storage = new();
        FakeWorkspaceInfo workspaceInfo = new() { WorkspacePath = @"C:\src\Product\app\app.csproj" };
        using TfvcSession session = CreateSession(client, storage, workspaceInfo);

        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl + "/", @" ""C:\tf\tf.exe"" "), CancellationToken.None);

        Assert.True(session.IsConnected);
        Assert.Same(product, session.CurrentWorkspace);
        Assert.Single(session.PendingChanges);
        Assert.Equal(TfvcTestData.CollectionUrl, session.Settings.CollectionUrl);
        Assert.Equal(@"C:\tf\tf.exe", client.ConfiguredTfExePath);

        TfvcSettings stored = await new TfvcSettingsStore(storage).LoadSettingsAsync(CancellationToken.None);
        Assert.Equal(TfvcTestData.CollectionUrl, stored.CollectionUrl);
    }

    [Fact]
    public async Task SetExcludedAsync_PersistsExclusionsPerWorkspace()
    {
        TfvcPendingChange change = TfvcTestData.CreateChange("$/Product/a.cs", @"C:\src\Product\a.cs");
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace()], PendingChanges = [change] };
        InMemoryExtensionStorage storage = new();
        using TfvcSession session = CreateSession(client, storage, new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        int notifications = 0;
        session.PendingChangesChanged += (_, _) => notifications++;

        await session.SetExcludedAsync(["$/Product/a.cs"], true, CancellationToken.None);

        Assert.True(session.IsExcluded(change));
        Assert.Equal(1, notifications);
        IReadOnlyList<string> stored = await new TfvcSettingsStore(storage).LoadExcludedChangesAsync("DEV01", CancellationToken.None);
        Assert.Equal(["$/Product/a.cs"], stored);

        using TfvcSession reloaded = CreateSession(client, storage, new FakeWorkspaceInfo());
        await reloaded.InitializeAsync(CancellationToken.None);
        Assert.True(reloaded.IsExcluded(change));
    }

    [Fact]
    public async Task EditorWorkspaceChange_SelectsMappingWorkspace()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "TFVC workspaces map Windows paths, and tf.exe exists only on Windows.");
        TfvcWorkspace first = TfvcTestData.CreateWorkspace("FIRST", @"C:\first");
        TfvcWorkspace second = TfvcTestData.CreateWorkspace("SECOND", @"C:\second");
        FakeTfvcClient client = new() { Workspaces = [first, second] };
        FakeWorkspaceInfo workspaceInfo = new();
        using TfvcSession session = CreateSession(client, new InMemoryExtensionStorage(), workspaceInfo);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        Assert.Same(first, session.CurrentWorkspace);

        workspaceInfo.Change(@"C:\second\app.sln");

        Assert.Same(second, session.CurrentWorkspace);
    }

    [Fact]
    public async Task FindPendingChange_MatchesLocalAndServerPathsIgnoringCase()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "TFVC workspaces map Windows paths, and tf.exe exists only on Windows.");
        FakeTfvcClient client = new()
        {
            Workspaces = [TfvcTestData.CreateWorkspace()],
            PendingChanges = [TfvcTestData.CreateChange("$/Product/a.cs", @"C:\src\Product\a.cs")]
        };
        using TfvcSession session = CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);

        Assert.NotNull(session.FindPendingChangeByLocalPath(@"c:\SRC\product\A.cs"));
        Assert.NotNull(session.FindPendingChangeByServerPath("$/product/a.CS"));
        Assert.Null(session.FindPendingChangeByServerPath("$/Product/b.cs"));
        Assert.Same(session.CurrentWorkspace, session.FindWorkspaceForLocalPath(@"C:\src\Product\x\y.cs"));
    }

    [Fact]
    public async Task GoOfflineDuringConnect_KeepsCachedWorkspacesWhenConnectFails()
    {
        TfvcWorkspace workspace = TfvcTestData.CreateWorkspace();
        FakeTfvcClient client = new() { Workspaces = [workspace] };
        using TfvcSession session = CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        TaskCompletionSource<IReadOnlyList<TfvcWorkspace>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.WorkspacesGate = gate;

        Task connect = session.ConnectAsync(CancellationToken.None);
        Assert.False(connect.IsCompleted);
        await session.GoOfflineAsync(CancellationToken.None);
        gate.SetException(new TimeoutException("tf workspaces did not finish."));
        await connect;

        Assert.True(session.IsOffline);
        Assert.False(session.ConnectionFailed);
        Assert.Same(workspace, Assert.Single(session.Workspaces));
        Assert.Null(session.CurrentWorkspace);
        Assert.Equal(TfvcStrings.OfflineStatus, session.StatusMessage);
        Assert.False(session.IsBusy);
    }

    [Fact]
    public async Task GoOfflineDuringConnect_StaysOfflineWhenConnectSucceeds()
    {
        TfvcWorkspace workspace = TfvcTestData.CreateWorkspace();
        FakeTfvcClient client = new() { Workspaces = [workspace] };
        using TfvcSession session = CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        int queriesBeforeConnect = client.PendingChangeQueries;
        TaskCompletionSource<IReadOnlyList<TfvcWorkspace>> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.WorkspacesGate = gate;

        Task connect = session.ConnectAsync(CancellationToken.None);
        await session.GoOfflineAsync(CancellationToken.None);
        gate.SetResult([TfvcTestData.CreateWorkspace("OTHER", @"C:\other")]);
        await connect;

        Assert.True(session.IsOffline);
        Assert.False(session.IsConnected);
        Assert.Null(session.CurrentWorkspace);
        Assert.Same(workspace, Assert.Single(session.Workspaces));
        Assert.Equal(TfvcStrings.OfflineStatus, session.StatusMessage);
        Assert.Equal(queriesBeforeConnect, client.PendingChangeQueries);
    }

    internal static TfvcSession CreateSession(FakeTfvcClient client, InMemoryExtensionStorage storage, FakeWorkspaceInfo workspaceInfo, FakeTfvcServerClient? server = null)
    {
        return new TfvcSession(client, server ?? new FakeTfvcServerClient(), new TfvcSettingsStore(storage), workspaceInfo, new NullExtensionLogger());
    }
}
