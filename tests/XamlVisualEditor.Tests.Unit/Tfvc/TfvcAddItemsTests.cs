using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcAddItemsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tfvc-add-items-" + Guid.NewGuid().ToString("N"));

    public TfvcAddItemsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Sub"));
    }

    [Fact]
    public async Task Scanner_ListsUnversionedFilesAndFoldersPerLevel()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        {
            string newFile = CreateFile("New.cs");
            string inner = CreateFile(Path.Combine("Sub", "Inner.cs"));
            string edited = CreateFile("Edited.cs");
            server.ExistingItems.Add("$/Product/Edited.cs");
            client.LocalChangeCandidates.AddRange([newFile, inner, edited, Path.Combine(_root, "Gone.cs")]);
            TfvcAddItemsScanner scanner = new(session, session.CurrentWorkspace!, _root);

            await scanner.ScanAsync(CancellationToken.None);
            IReadOnlyList<TfvcAddEntry> rootEntries = await scanner.ListAsync(_root, CancellationToken.None);
            IReadOnlyList<TfvcAddEntry> subEntries = await scanner.ListAsync(Path.Combine(_root, "Sub"), CancellationToken.None);

            Assert.Equal(
                new[] { (Path.Combine(_root, "New.cs"), false), (Path.Combine(_root, "Sub"), true) },
                rootEntries.Select(entry => (entry.LocalPath, entry.IsFolder)).OrderBy(entry => entry.LocalPath));
            Assert.Equal(inner, Assert.Single(subEntries).LocalPath);
            Assert.Equal("$/Product/Sub", scanner.GetServerPath(Path.Combine(_root, "Sub")));
        }
    }

    [Fact]
    public async Task Dialog_KeepsSelectionAcrossFolders()
    {
        (TfvcSession session, FakeTfvcClient client, _) = await CreateSessionAsync();
        using (session)
        {
            string newFile = CreateFile("New.cs");
            string inner = CreateFile(Path.Combine("Sub", "Inner.cs"));
            client.LocalChangeCandidates.AddRange([newFile, inner]);
            TfvcAddItemsScanner scanner = new(session, session.CurrentWorkspace!, _root);
            await scanner.ScanAsync(CancellationToken.None);
            TfvcAddItemsDialogViewModel dialog = new(scanner, (_, _) => null);
            IReadOnlyList<TfvcAddEntry>? result = null;
            dialog.CloseInteraction.RegisterHandler(context =>
            {
                result = context.Input;
                context.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
            });

            await dialog.ShowFolderAsync(_root, CancellationToken.None);
            Assert.False(dialog.CanGoUp);
            Assert.Equal("Sub", dialog.Items[0].Name);
            dialog.Items.Single(item => item.Name == "New.cs").IsSelected = true;
            dialog.SelectedItem = dialog.Items.Single(item => item.Name == "Sub");
            await dialog.OpenSelectedCommand.Execute().ToTask();
            Assert.True(dialog.CanGoUp);
            Assert.Equal("$/Product/Sub", dialog.TargetServerFolder);
            Assert.Single(dialog.Items).IsSelected = true;
            await dialog.UpCommand.Execute().ToTask();
            Assert.True(dialog.Items.Single(item => item.Name == "New.cs").IsSelected);
            await dialog.FinishCommand.Execute().ToTask();

            Assert.Equal(new[] { inner, newFile }.OrderBy(path => path), result!.Select(entry => entry.LocalPath).OrderBy(path => path));
        }
    }

    [Fact]
    public async Task Explorer_AddsSelectedFoldersRecursivelyAndOtherFilesSingly()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        server.Children["$/"] = [new TfvcServerItem("$/Product", true, 30, null)];
        server.Children["$/Product"] = [];
        string newFile = CreateFile("New.cs");
        string inner = CreateFile(Path.Combine("Sub", "Inner.cs"));
        client.LocalChangeCandidates.AddRange([newFile, inner]);
        ScriptedDialogHost dialogs = new((dialogId, viewModel) =>
        {
            if (dialogId != TfvcAddItemsDialogViewModel.DIALOG_ID || viewModel is not TfvcAddItemsDialogViewModel dialog)
            {
                return null;
            }

            // The folder and a file inside it are both selected; the file comes with its folder.
            List<TfvcAddEntry> selection = dialog.Items.Select(item => item.Entry).ToList();
            selection.Add(new TfvcAddEntry(inner, false, DateTime.Now, 1));
            return (IReadOnlyList<TfvcAddEntry>)selection;
        });
        using (session)
        using (SourceControlExplorerViewModel explorer = new(
            session,
            new TfvcAutoCheckOutService(session, new FakeEditorServices(), new RecordingWindow(), new NullExtensionLogger()),
            new FakeEditorServices(),
            dialogs,
            null))
        {
            await explorer.NavigateToAsync("$/Product", CancellationToken.None);

            await explorer.AddItemsToFolderCommand.Execute().ToTask();

            Assert.Equal(new[] { Path.Combine(_root, "Sub") }, Assert.Single(client.FolderAdds));
            Assert.Equal(new[] { newFile }, Assert.Single(client.Adds));
        }
    }

    private async Task<(TfvcSession Session, FakeTfvcClient Client, FakeTfvcServerClient Server)> CreateSessionAsync()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace(localRoot: _root)] };
        FakeTfvcServerClient server = new();
        TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        return (session, client, server);
    }

    private string CreateFile(string relativePath)
    {
        string path = Path.Combine(_root, relativePath);
        File.WriteAllText(path, "content");
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }
}
