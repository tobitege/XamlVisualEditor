using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcOfflineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tfvc-offline-" + Guid.NewGuid().ToString("N"));

    public TfvcOfflineTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void ReconcileOutputParser_ResolvesRelativeAndAbsoluteFolderHeaders()
    {
        string output =
            "Ausstehend Hinzufügen: RootFile.cs\r\n" +
            "Sub\\Folder:\r\n" +
            "Ausstehend Hinzufügen: Nested.cs\r\n" +
            Path.Combine(_root, "Other") + ":\r\n" +
            "delete: Removed.cs\r\n";

        IReadOnlyCollection<string> paths = TfvcReconcileOutputParser.ParseCandidatePaths(output, _root);

        Assert.Equal(
            new[]
            {
                Path.Combine(_root, "RootFile.cs"),
                Path.Combine(_root, "Sub", "Folder", "Nested.cs"),
                Path.Combine(_root, "Other", "Removed.cs")
            }.OrderBy(path => path),
            paths.OrderBy(path => path));
    }

    [Fact]
    public async Task GoOffline_KeepsMappingsOfLastConnectionWithoutServerCalls()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        InMemoryExtensionStorage storage = new();
        using TfvcSession session = TfvcSessionTests.CreateSession(client, storage, new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);

        client.WorkspacesFailure = new TfCommandException("workspaces", new TfCommandResult(100, string.Empty, "TF400324"));
        using TfvcSession restarted = TfvcSessionTests.CreateSession(client, storage, new FakeWorkspaceInfo());
        await restarted.InitializeAsync(CancellationToken.None);
        Assert.True(restarted.ConnectionFailed);

        int queries = client.PendingChangeQueries;
        await restarted.GoOfflineAsync(CancellationToken.None);

        Assert.True(restarted.IsOffline);
        Assert.False(restarted.IsConnected);
        Assert.Equal(TfvcStrings.OfflineStatus, restarted.StatusMessage);
        Assert.NotNull(restarted.FindWorkspaceForLocalPath(Path.Combine(_root, "File.cs")));
        await restarted.RefreshPendingChangesAsync(CancellationToken.None);
        Assert.Equal(queries, client.PendingChangeQueries);
    }

    [Fact]
    public async Task OfflineEdit_RemovesWriteProtectionOnlyAfterConfirmation()
    {
        string confirmedPath = CreateReadOnlyFile("Confirmed.cs");
        string declinedPath = CreateReadOnlyFile("Declined.cs");
        (TfvcSession session, TfvcAutoCheckOutService service, ScriptedDialogHost dialogs) = await CreateOfflineAsync(
            (_, viewModel) => viewModel is ConfirmationDialogRequest request && request.Message.Contains("Confirmed.cs", StringComparison.Ordinal));
        using (session)
        using (service)
        {
            FakeEditorDocument confirmed = new(confirmedPath, "changed");
            FakeEditorDocument declined = new(declinedPath, "changed");

            Assert.Equal(TfvcFileStateKind.Offline, (await service.GetStateAsync(confirmedPath, CancellationToken.None)).Kind);
            await service.HandleDocumentChangedAsync(confirmed, CancellationToken.None);
            await service.HandleDocumentChangedAsync(declined, CancellationToken.None);
            await service.HandleDocumentChangedAsync(declined, CancellationToken.None);

            Assert.False(File.GetAttributes(confirmedPath).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.GetAttributes(declinedPath).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal(new[] { confirmedPath }, session.OfflineEdits);
            Assert.Equal(2, dialogs.Requests.Count);
        }
    }

    [Fact]
    public async Task Reconciler_ClassifiesEditsAddsAndDeletesAndPendsTheSelection()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        FakeTfvcServerClient server = new();
        FakeWorkspaceInfo workspaceInfo = new() { WorkspacePath = _root };
        using TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), workspaceInfo, server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);

        string edited = CreateFile("Edited.cs");
        string added = CreateFile("Added.cs");
        string recorded = CreateFile("Recorded.cs");
        string deleted = Path.Combine(_root, "Deleted.cs");
        server.ExistingItems.Add("$/Product/Edited.cs");
        server.ExistingItems.Add("$/Product/Recorded.cs");
        client.LocalChangeCandidates.AddRange([edited, added, deleted]);
        await session.RecordOfflineEditAsync(recorded, CancellationToken.None);
        TfvcOfflineReconciler reconciler = new(session, workspaceInfo, new NullExtensionLogger());

        IReadOnlyList<TfvcOfflineChange> changes = await reconciler.DetectAsync(CancellationToken.None);
        await reconciler.ApplyAsync(changes, CancellationToken.None);

        Assert.Equal(new[] { _root }, client.LocalChangeScopes);
        Assert.Equal(TfvcOfflineChangeKind.Add, changes.Single(change => change.LocalPath == added).Kind);
        Assert.Equal(TfvcOfflineChangeKind.Delete, changes.Single(change => change.LocalPath == deleted).Kind);
        Assert.Equal(TfvcOfflineChangeKind.Edit, changes.Single(change => change.LocalPath == edited).Kind);
        Assert.Equal(TfvcOfflineChangeKind.Edit, changes.Single(change => change.LocalPath == recorded).Kind);
        Assert.Equal(new[] { edited, recorded }, client.CheckOuts.Single());
        Assert.Equal(new[] { added }, client.Adds.Single());
        Assert.Equal(new[] { deleted }, client.Deletes.Single());
        Assert.Empty(session.OfflineEdits);
    }

    [Fact]
    public async Task Coordinator_OffersOfflineWorkAndReturnsToItWhenGoingOnlineFails()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        InMemoryExtensionStorage storage = new();
        using (TfvcSession first = TfvcSessionTests.CreateSession(client, storage, new FakeWorkspaceInfo()))
        {
            await first.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        }

        client.WorkspacesFailure = new TfCommandException("workspaces", new TfCommandResult(100, string.Empty, "TF400324"));
        FakeWorkspaceInfo workspaceInfo = new();
        using TfvcSession session = TfvcSessionTests.CreateSession(client, storage, workspaceInfo);
        ScriptedDialogHost dialogs = new((dialogId, _) => dialogId == ConfirmationDialogRequest.DialogId);
        RecordingWindow window = new();
        using TfvcOfflineCoordinator coordinator = new(
            session,
            new TfvcOfflineReconciler(session, workspaceInfo, new NullExtensionLogger()),
            window,
            dialogs,
            new NullExtensionLogger());
        await session.InitializeAsync(CancellationToken.None);

        await coordinator.HandleInitializedAsync(CancellationToken.None);
        Assert.True(session.IsOffline);

        await coordinator.GoOnlineAsync(CancellationToken.None);
        Assert.True(session.IsOffline);
        Assert.Single(window.Errors);

        client.WorkspacesFailure = null;
        await coordinator.GoOnlineAsync(CancellationToken.None);
        Assert.True(session.IsConnected);
        Assert.Empty(window.Errors.Skip(1));

        // Without an editor workspace, the mapping root is not searched.
        Assert.Empty(client.LocalChangeScopes);
    }

    [Fact]
    public async Task Coordinator_ProposesLocalChangesOnlyOnceWhenGoingOnline()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        FakeWorkspaceInfo workspaceInfo = new();
        using TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), workspaceInfo);
        ScriptedDialogHost dialogs = new((_, _) => null);
        using TfvcOfflineCoordinator coordinator = new(
            session,
            new TfvcOfflineReconciler(session, workspaceInfo, new NullExtensionLogger()),
            new RecordingWindow(),
            dialogs,
            new NullExtensionLogger());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        await coordinator.GoOfflineAsync(CancellationToken.None);
        await session.RecordOfflineEditAsync(CreateFile("Offline.cs"), CancellationToken.None);

        await coordinator.GoOnlineAsync(CancellationToken.None);
        await coordinator.GoOfflineAsync(CancellationToken.None);
        await coordinator.GoOnlineAsync(CancellationToken.None);

        // The cancelled proposal is not repeated: the file stays a local change to pend by hand.
        Assert.Single(dialogs.Requests, request => request.DialogId == TfvcReconcileDialogViewModel.DIALOG_ID);
        Assert.Empty(session.OfflineEdits);
        Assert.Empty(client.Adds);
        Assert.Empty(client.CheckOuts);
    }

    private async Task<(TfvcSession Session, TfvcAutoCheckOutService Service, ScriptedDialogHost Dialogs)> CreateOfflineAsync(
        Func<string, object?, object?> respond)
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo());
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        await session.GoOfflineAsync(CancellationToken.None);
        ScriptedDialogHost dialogs = new(respond);
        TfvcAutoCheckOutService service = new(session, new FakeEditorServices(), new RecordingWindow(), new NullExtensionLogger(),
            _ => "original", dialogHost: dialogs);
        return (session, service, dialogs);
    }

    private string CreateFile(string name)
    {
        string path = Path.Combine(_root, name);
        File.WriteAllText(path, "original");
        return path;
    }

    private string CreateReadOnlyFile(string name)
    {
        string path = CreateFile(name);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    public void Dispose()
    {
        // Only the files this test created are made writable again, so the temporary folder can be removed.
        foreach (string path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }
}
