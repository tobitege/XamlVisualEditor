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
            string editedInner = CreateFile(Path.Combine("Sub", "EditedInner.cs"));
            server.ExistingItems.Add("$/Product/Edited.cs");
            server.ExistingItems.Add("$/Product/Sub/EditedInner.cs");
            client.LocalChangeCandidates.AddRange([newFile, inner, edited, editedInner, Path.Combine(_root, "Gone.cs")]);
            TfvcAddItemsScanner scanner = new(session, session.CurrentWorkspace!, _root);

            await scanner.ScanAsync(CancellationToken.None);
            IReadOnlyList<TfvcAddEntry> rootEntries = scanner.List(_root);
            IReadOnlyList<TfvcAddEntry> subEntries = scanner.List(Path.Combine(_root, "Sub"));

            Assert.Equal(
                new[] { (Path.Combine(_root, "New.cs"), false), (Path.Combine(_root, "Sub"), true) },
                rootEntries.Select(entry => (entry.LocalPath, entry.IsFolder)).OrderBy(entry => entry.LocalPath));
            Assert.Equal(inner, Assert.Single(subEntries).LocalPath);
            Assert.Equal("$/Product/Sub", scanner.GetServerPath(Path.Combine(_root, "Sub")));

            // One recursive query of the start folder replaces a query per listed file.
            Assert.Equal(["$/Product"], server.RecursiveItemRequests);
            Assert.Empty(server.ItemRequests);
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

            dialog.ShowFolder(_root);
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
    public async Task Dialog_FolderCheckSelectsItsItemsAndAClearedItemStaysOut()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        {
            string sub = Path.Combine(_root, "Sub");
            string inner = CreateFile(Path.Combine("Sub", "Inner.cs"));
            string cleared = CreateFile(Path.Combine("Sub", "Cleared.cs"));
            Directory.CreateDirectory(Path.Combine(sub, "Deep"));
            string deeper = CreateFile(Path.Combine("Sub", "Deep", "Deeper.cs"));
            Directory.CreateDirectory(Path.Combine(_root, "Versioned"));
            string versionedInner = CreateFile(Path.Combine("Versioned", "Added.cs"));
            server.ExistingItems.Add("$/Product/Versioned");
            client.LocalChangeCandidates.AddRange([sub, inner, cleared, deeper, versionedInner]);
            TfvcAddItemsScanner scanner = new(session, session.CurrentWorkspace!, _root);
            await scanner.ScanAsync(CancellationToken.None);
            TfvcAddItemsDialogViewModel dialog = new(scanner, (_, _) => null);

            dialog.ShowFolder(_root);
            dialog.Items.Single(item => item.Name == "Sub").IsSelected = true;
            dialog.ShowFolder(sub);
            Assert.All(dialog.Items, item => Assert.True(item.IsSelected));
            dialog.Items.Single(item => item.Name == "Cleared.cs").IsSelected = false;
            dialog.ShowFolder(_root);

            // Not every item below the folder is selected any more, so the folder shows as cleared.
            Assert.False(dialog.Items.Single(item => item.Name == "Sub").IsSelected);
            Assert.False(dialog.Items.Single(item => item.Name == "Versioned").IsSelected);
            Assert.Equal(new[] { sub, deeper, inner }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), dialog.GetSelection().Select(entry => entry.LocalPath));

            // Checking a versioned folder selects the items to add below it, not the folder itself.
            dialog.Items.Single(item => item.Name == "Versioned").IsSelected = true;
            Assert.Contains(versionedInner, dialog.GetSelection().Select(entry => entry.LocalPath));
            Assert.DoesNotContain(Path.Combine(_root, "Versioned"), dialog.GetSelection().Select(entry => entry.LocalPath));
        }
    }

    [Fact]
    public async Task Explorer_AddsOnlyTheSelectedItemsWithoutRecursion()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        server.Children["$/"] = [new TfvcServerItem("$/Product", true, 30, null)];
        server.Children["$/Product"] = [];
        string sub = Path.Combine(_root, "Sub");
        string newFile = CreateFile("New.cs");
        string inner = CreateFile(Path.Combine("Sub", "Inner.cs"));
        string cleared = CreateFile(Path.Combine("Sub", "Cleared.cs"));
        client.LocalChangeCandidates.AddRange([sub, newFile, inner, cleared]);
        ScriptedDialogHost dialogs = new((dialogId, viewModel) =>
        {
            if (dialogId != TfvcAddItemsDialogViewModel.DIALOG_ID || viewModel is not TfvcAddItemsDialogViewModel dialog)
            {
                return null;
            }

            // The user checks the folder, opens it and clears one file in it.
            dialog.Items.Single(item => item.Name == "Sub").IsSelected = true;
            dialog.ShowFolder(sub);
            dialog.Items.Single(item => item.Name == "Cleared.cs").IsSelected = false;
            return dialog.GetSelection();
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

            Assert.Equal(new[] { sub, inner }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase), Assert.Single(client.Adds));
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
