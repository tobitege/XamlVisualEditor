using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcAutoCheckOutServiceTests
{
    private const string FilePath = @"C:\src\Product\src\App.cs";
    private const string ServerPath = "$/Product/src/App.cs";

    [Fact]
    public async Task ActivatingDocument_DeterminesStateBeforeAnyChange()
    {
        (TfvcSession session, FakeTfvcClient client, _, FakeEditorServices editor, TfvcAutoCheckOutService service, _) = await CreateAsync();
        using (session)
        using (service)
        {
            FakeEditorDocument document = new(FilePath, "original");

            editor.Activate(document);
            TfvcFileState state = await service.GetStateAsync(FilePath, CancellationToken.None);

            Assert.Equal(TfvcFileStateKind.CheckedIn, state.Kind);
            Assert.Equal(ServerPath, state.ServerPath);
            Assert.Equal(TfvcFileStateKind.CheckedIn, service.ActiveDocumentState.Kind);
            Assert.True(editor.ManagedWriteProtection[FilePath]);
            Assert.Equal(1, client.FileStatusQueries);
            Assert.Empty(client.CheckOuts);
        }
    }

    [Fact]
    public async Task DocumentActivatedBeforeConnection_IsEvaluatedAfterConnecting()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace()] };
        FakeTfvcServerClient server = new();
        server.ExistingItems.Add(ServerPath);
        using TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
        FakeEditorServices editor = new();
        editor.Activate(new FakeEditorDocument(FilePath, "original"));
        using TfvcAutoCheckOutService service = new(session, editor, new RecordingWindow(), new NullExtensionLogger(), _ => "original");
        service.Start();
        Assert.Equal(TfvcFileStateKind.Unknown, service.ActiveDocumentState.Kind);

        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);

        Assert.Equal(TfvcFileStateKind.CheckedIn, service.ActiveDocumentState.Kind);
        Assert.False(service.IsCheckingActiveDocument);
    }

    [Fact]
    public async Task FailedFileStatus_UsesReadOnlyAttributeAndRetriesOtherwise()
    {
        string root = Path.Combine(Path.GetTempPath(), "tfvc-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string readOnlyFile = Path.Combine(root, "ReadOnly.cs");
        string writableFile = Path.Combine(root, "Writable.cs");
        File.WriteAllText(readOnlyFile, "a");
        File.WriteAllText(writableFile, "b");
        File.SetAttributes(readOnlyFile, FileAttributes.ReadOnly);
        try
        {
            FakeTfvcClient client = new()
            {
                Workspaces = [TfvcTestData.CreateWorkspace(localRoot: root)],
                FileStatusFailure = new TfCommandException("status", new TfCommandResult(100, string.Empty, "TF400324"))
            };
            FakeTfvcServerClient server = new();
            server.ExistingItems.Add("$/Product/ReadOnly.cs");
            server.ExistingItems.Add("$/Product/Writable.cs");
            using TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
            await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
            FakeEditorServices editor = new();
            using TfvcAutoCheckOutService service = new(session, editor, new RecordingWindow(), new NullExtensionLogger(), retryDelay: TimeSpan.FromMilliseconds(300));
            service.Start();

            Assert.Equal(TfvcFileStateKind.CheckedIn, (await service.GetStateAsync(readOnlyFile, CancellationToken.None)).Kind);
            await Assert.ThrowsAsync<TfCommandException>(() => service.GetStateAsync(writableFile, CancellationToken.None));

            editor.Activate(new FakeEditorDocument(writableFile, "b"));
            Assert.Equal(TfvcFileStateKind.Unknown, service.ActiveDocumentState.Kind);
            client.FileStatusFailure = null;
            for (int attempt = 0; attempt < 100 && service.ActiveDocumentState.Kind != TfvcFileStateKind.CheckedIn; attempt++)
            {
                await Task.Delay(30);
            }

            Assert.Equal(TfvcFileStateKind.CheckedIn, service.ActiveDocumentState.Kind);
        }
        finally
        {
            File.SetAttributes(readOnlyFile, FileAttributes.Normal);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FirstChange_ChecksOutVersionedFileOnce()
    {
        (TfvcSession session, FakeTfvcClient client, _, FakeEditorServices editor, TfvcAutoCheckOutService service, _) = await CreateAsync();
        using (session)
        using (service)
        {
            FakeEditorDocument document = new(FilePath, "original");
            editor.Activate(document);

            document.Text = "changed";
            await service.HandleDocumentChangedAsync(document, CancellationToken.None);
            document.Text = "changed again";
            await service.HandleDocumentChangedAsync(document, CancellationToken.None);

            Assert.Equal([FilePath], Assert.Single(client.CheckOuts));
            Assert.Equal(TfvcFileStateKind.Pending, (await service.GetStateAsync(FilePath, CancellationToken.None)).Kind);
            Assert.True(client.PendingChangeQueries >= 2);
        }
    }

    [Fact]
    public async Task ChangeEventWithDiskContent_DoesNotCheckOut()
    {
        (TfvcSession session, FakeTfvcClient client, _, FakeEditorServices editor, TfvcAutoCheckOutService service, _) = await CreateAsync("same\r\ntext");
        using (session)
        using (service)
        {
            FakeEditorDocument document = new(FilePath, "same\ntext");
            editor.Activate(document);

            document.Edit("same\ntext");
            await service.HandleDocumentChangedAsync(document, CancellationToken.None);

            Assert.Empty(client.CheckOuts);
        }
    }

    [Fact]
    public async Task PendingOrUnversionedFiles_AreNotCheckedOut()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server, FakeEditorServices editor, TfvcAutoCheckOutService service, _) = await CreateAsync();
        using (session)
        using (service)
        {
            client.PendingChangesByLocalPath[FilePath] = [TfvcTestData.CreateChange(ServerPath, FilePath)];
            FakeEditorDocument pending = new(FilePath, "changed");
            const string newFile = @"C:\src\Product\src\New.cs";
            FakeEditorDocument unversioned = new(newFile, "changed");
            editor.Activate(pending);
            editor.Activate(unversioned);

            await service.HandleDocumentChangedAsync(pending, CancellationToken.None);
            await service.HandleDocumentChangedAsync(unversioned, CancellationToken.None);

            Assert.Empty(client.CheckOuts);
            Assert.Equal(TfvcFileStateKind.NotVersioned, (await service.GetStateAsync(newFile, CancellationToken.None)).Kind);
            Assert.DoesNotContain("$/Product/src/New.cs", server.ExistingItems);
        }
    }

    [Fact]
    public async Task FilesOutsideWorkspace_AreIgnored()
    {
        (TfvcSession session, FakeTfvcClient client, _, _, TfvcAutoCheckOutService service, _) = await CreateAsync();
        using (session)
        using (service)
        {
            FakeEditorDocument document = new(@"D:\other\a.cs", "changed");

            await service.HandleDocumentChangedAsync(document, CancellationToken.None);

            Assert.Equal(TfvcFileStateKind.NotInWorkspace, (await service.GetStateAsync(document.FilePath, CancellationToken.None)).Kind);
            Assert.Equal(0, client.FileStatusQueries);
            Assert.Empty(client.CheckOuts);
        }
    }

    [Fact]
    public async Task FailedCheckOut_ReportsErrorOnceUntilInvalidated()
    {
        (TfvcSession session, FakeTfvcClient client, _, FakeEditorServices editor, TfvcAutoCheckOutService service, RecordingWindow window) = await CreateAsync();
        using (session)
        using (service)
        {
            client.CheckOutFailure = new TfCommandException("checkout", new TfCommandResult(100, string.Empty, "TF10152: locked"));
            FakeEditorDocument document = new(FilePath, "changed");
            editor.Activate(document);

            await service.HandleDocumentChangedAsync(document, CancellationToken.None);
            await service.HandleDocumentChangedAsync(document, CancellationToken.None);
            service.Invalidate(FilePath);
            await service.HandleDocumentChangedAsync(document, CancellationToken.None);

            Assert.Equal(2, client.CheckOuts.Count);
            Assert.Equal(2, window.Errors.Count);
            Assert.Contains("App.cs", window.Errors[0]);
        }
    }

    private static async Task<(TfvcSession Session, FakeTfvcClient Client, FakeTfvcServerClient Server, FakeEditorServices Editor, TfvcAutoCheckOutService Service, RecordingWindow Window)> CreateAsync(
        string diskText = "original")
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace()] };
        FakeTfvcServerClient server = new();
        server.ExistingItems.Add(ServerPath);
        TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        FakeEditorServices editor = new();
        RecordingWindow window = new();
        Func<string, string?> readFile = path => string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase) ? diskText : null;
        TfvcAutoCheckOutService service = new(session, editor, window, new NullExtensionLogger(), readFile);
        service.Start();
        return (session, client, server, editor, service, window);
    }
}
