using System.Collections.Generic;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcPanelViewModelTests
{
    [Fact]
    public void TreeBuilder_CollapsesFolderChainsLikeVisualStudio()
    {
        TfvcPendingChange[] changes =
        [
            TfvcTestData.CreateChange("$/Product/app/src/core/Core.csproj", @"C:\src\Product\app\src\core\Core.csproj"),
            TfvcTestData.CreateChange("$/Product/app/src/core/readme.md", @"C:\src\Product\app\src\core\readme.md"),
            TfvcTestData.CreateChange("$/Product/app/src/ui/readme.md", @"C:\src\Product\app\src\ui\readme.md"),
            TfvcTestData.CreateChange("$/Product/tools/Tests/Unit/A.cs", @"C:\src\Product\tools\Tests\Unit\A.cs")
        ];

        IReadOnlyList<PendingChangeNodeViewModel> roots = PendingChangeTreeBuilder.Build(changes);

        PendingChangeNodeViewModel root = Assert.Single(roots);
        Assert.Equal(@"C:\src\Product", root.Name);
        Assert.Equal([@"app\src", @"tools\Tests\Unit"], [root.Children[0].Name, root.Children[1].Name]);
        PendingChangeNodeViewModel appSrc = root.Children[0];
        Assert.Equal(["core", "ui"], [appSrc.Children[0].Name, appSrc.Children[1].Name]);
        Assert.Equal(["Core.csproj", "readme.md"], [appSrc.Children[0].Children[0].Name, appSrc.Children[0].Children[1].Name]);
        Assert.Equal("A.cs", Assert.Single(root.Children[1].Children).Name);
        Assert.Equal(4, root.GetChanges().Count);
    }

    [Fact]
    public void TreeBuilder_GroupsUnmappedChangesByServerPath()
    {
        IReadOnlyList<PendingChangeNodeViewModel> roots = PendingChangeTreeBuilder.Build(
            [TfvcTestData.CreateChange("$/Product/src/deleted.cs", null, "Delete")]);

        PendingChangeNodeViewModel root = Assert.Single(roots);
        Assert.Equal("$/Product/src", root.Name);
        PendingChangeNodeViewModel file = Assert.Single(root.Children);
        Assert.Equal("deleted.cs", file.Name);
        Assert.False(file.IsFolder);
        Assert.StartsWith("[", file.ChangeLabel);
    }

    [Fact]
    public async Task PendingChanges_SplitsIncludedAndExcludedAndChecksInIncludedOnly()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        using (PendingChangesViewModel viewModel = CreatePendingChangesViewModel(session, client))
        {
            await session.SetExcludedAsync(["$/Product/b.cs"], true, CancellationToken.None);

            Assert.Equal(1, viewModel.IncludedCount);
            Assert.Equal(1, viewModel.ExcludedCount);

            server.WorkItems[17] = new TfvcWorkItemInfo(17, "Task", "Port the form", "Active");
            viewModel.WorkItemIdText = "#17";
            await viewModel.AddWorkItemCommand.Execute().ToTask();
            Assert.Single(viewModel.WorkItems);

            viewModel.Comment = "  Update a  ";
            await viewModel.CheckInCommand.Execute().ToTask();

            (string comment, IReadOnlyList<string> items) = Assert.Single(client.CheckIns);
            Assert.Equal("Update a", comment);
            Assert.Equal(["$/Product/a.cs"], items);
            Assert.Equal((17, 4711), Assert.Single(server.Links));
            Assert.Empty(viewModel.WorkItems);
            Assert.Null(viewModel.Comment);
            Assert.Contains("4711", viewModel.OperationMessage);
        }
    }

    [Fact]
    public async Task PendingChanges_CheckInRequiresComment()
    {
        (TfvcSession session, FakeTfvcClient client, _) = await CreateSessionAsync();
        using (session)
        using (PendingChangesViewModel viewModel = CreatePendingChangesViewModel(session, client))
        {
            viewModel.Comment = "   ";

            await viewModel.CheckInCommand.Execute().ToTask();

            Assert.Empty(client.CheckIns);
            Assert.False(string.IsNullOrEmpty(viewModel.OperationMessage));
        }
    }

    [Fact]
    public void FormatSize_UsesBinaryUnits()
    {
        Assert.EndsWith(" B", SourceControlItemViewModel.FormatSize(512));
        Assert.EndsWith(" KB", SourceControlItemViewModel.FormatSize(2048));
        Assert.EndsWith(" MB", SourceControlItemViewModel.FormatSize(5L * 1024 * 1024));
    }

    [Fact]
    public async Task PendingChanges_UndoSelectedAsksForConfirmation()
    {
        (TfvcSession session, FakeTfvcClient client, _) = await CreateSessionAsync();
        ConfirmingDialogHost dialogHost = new();
        using (session)
        using (PendingChangesViewModel viewModel = CreatePendingChangesViewModel(session, client, dialogHost))
        {
            viewModel.SelectedIncludedRow = viewModel.IncludedModel.Flattened[0];

            await viewModel.UndoSelectedCommand.Execute().ToTask();

            Assert.Equal(1, dialogHost.Confirmations);
            Assert.Equal(["$/Product/a.cs", "$/Product/b.cs"], Assert.Single(client.Undos));
        }
    }

    [Fact]
    public async Task PendingChanges_CompareShowsDiff()
    {
        (TfvcSession session, FakeTfvcClient client, _) = await CreateSessionAsync();
        using (session)
        using (PendingChangesViewModel viewModel = CreatePendingChangesViewModel(session, client))
        {
            PendingChangeNodeViewModel root = viewModel.IncludedRoots[0];
            viewModel.SelectedIncludedRow = root.Children[0];

            await viewModel.CompareSelectedCommand.Execute().ToTask();

            Assert.True(viewModel.IsDiffVisible);
            Assert.Equal(5, viewModel.DiffLines.Count);
            Assert.Equal(TfvcDiffLineKind.Added, viewModel.DiffLines[4].Kind);
        }
    }

    [Fact]
    public async Task PendingChanges_SuccessfulCompareClearsPreviousError()
    {
        (TfvcSession session, FakeTfvcClient client, _) = await CreateSessionAsync();
        using (session)
        using (PendingChangesViewModel viewModel = CreatePendingChangesViewModel(session, client))
        {
            viewModel.SelectedIncludedRow = viewModel.IncludedRoots[0].Children[0];
            client.DifferenceFailure = new TfCommandException("difference", new TfCommandResult(100, string.Empty, "TF400324: unavailable"));

            await viewModel.CompareSelectedCommand.Execute().ToTask();
            Assert.Contains("TF400324", viewModel.OperationMessage);

            client.DifferenceFailure = null;
            await viewModel.CompareSelectedCommand.Execute().ToTask();

            Assert.Null(viewModel.OperationMessage);
            Assert.True(viewModel.IsDiffVisible);
        }
    }

    [Fact]
    public async Task SourceControlExplorer_NavigatesToServerPathAndShowsWorkspaceState()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        server.Children["$/"] = [new TfvcServerItem("$/Product", true, 30, null)];
        server.Children["$/Product"] = [new TfvcServerItem("$/Product/a.cs", false, 29, null), new TfvcServerItem("$/Product/c.cs", false, 28, null)];
        using (session)
        using (SourceControlExplorerViewModel viewModel = new(
            session,
            new TfvcAutoCheckOutService(session, new FakeEditorServices(), new RecordingWindow(), new NullExtensionLogger()),
            new FakeEditorServices(),
            new ConfirmingDialogHost(),
            null))
        {
            await viewModel.NavigateToAsync("$/Product/a.cs", CancellationToken.None);

            SourceControlItemViewModel? selected = viewModel.SelectedItem;
            Assert.NotNull(selected);
            Assert.Equal("$/Product/a.cs", selected.ServerPath);
            Assert.Equal(@"C:\src\Product\a.cs", selected.LocalPath);
            Assert.NotEmpty(selected.PendingChangeText);
            Assert.Equal("29", selected.ChangesetText);
            Assert.Equal("$/Product/a.cs", viewModel.PathText);
            Assert.Empty(client.CheckOuts);
        }
    }

    [Fact]
    public async Task SourceControlExplorer_SessionChangesDuringRootLoadDoNotStartAnotherLoad()
    {
        (TfvcSession session, _, FakeTfvcServerClient server) = await CreateSessionAsync();
        server.Children["$/"] = [new TfvcServerItem("$/Product", true, 30, null)];
        server.ChildrenGate = new TaskCompletionSource();
        using (session)
        using (SourceControlExplorerViewModel viewModel = CreateExplorer(session))
        {
            for (int i = 0; i < 3; i++)
            {
                await session.RefreshPendingChangesAsync(CancellationToken.None);
            }

            server.ChildrenGate.SetResult();
            await Task.Yield();

            Assert.Equal(["$/"], server.ChildrenRequests);
            Assert.Equal("$/", Assert.Single(viewModel.Roots).ServerPath);
        }
    }

    [Fact]
    public async Task SourceControlExplorer_FocusShowsOnlyFolderAndIsRestored()
    {
        InMemoryExtensionStorage storage = new();
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace()] };
        FakeTfvcServerClient server = new();
        server.Children["$/"] = [new TfvcServerItem("$/Product", true, 30, null), new TfvcServerItem("$/Other", true, 20, null)];
        server.Children["$/Product"] = [new TfvcServerItem("$/Product/src", true, 29, null)];
        server.Children["$/Product/src"] = [new TfvcServerItem("$/Product/src/a.cs", false, 28, null)];
        using TfvcSession session = TfvcSessionTests.CreateSession(client, storage, new FakeWorkspaceInfo(), server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);

        using (SourceControlExplorerViewModel viewModel = CreateExplorer(session))
        {
            await viewModel.NavigateToAsync("$/Product/src", CancellationToken.None);
            await viewModel.FocusSelectedCommand.Execute().ToTask();

            Assert.Equal("$/Product/src", viewModel.FocusedPath);
            SourceControlItemViewModel root = Assert.Single(viewModel.Roots);
            Assert.Equal("$/Product/src", root.ServerPath);
            Assert.Equal("a.cs", Assert.Single(root.Children).Name);
        }

        server.ChildrenRequests.Clear();
        server.ItemRequests.Clear();
        using (SourceControlExplorerViewModel reopened = CreateExplorer(session))
        {
            await reopened.NavigateToAsync("$/Product/src/a.cs", CancellationToken.None);
            Assert.Equal("$/Product/src", reopened.FocusedPath);
            Assert.Equal("$/Product/src", Assert.Single(reopened.Roots).ServerPath);
            Assert.Equal(["$/Product/src"], server.ItemRequests);
            Assert.Equal(["$/Product/src"], server.ChildrenRequests);

            await reopened.NavigateToAsync("$/Other", CancellationToken.None);
            Assert.Null(reopened.FocusedPath);
            Assert.Equal("$/", Assert.Single(reopened.Roots).ServerPath);
            Assert.Equal("$/Other", reopened.SelectedItem?.ServerPath);
        }

        Assert.Null(await session.LoadExplorerFocusAsync(CancellationToken.None));
    }

    private static SourceControlExplorerViewModel CreateExplorer(TfvcSession session)
    {
        return new SourceControlExplorerViewModel(
            session,
            new TfvcAutoCheckOutService(session, new FakeEditorServices(), new RecordingWindow(), new NullExtensionLogger()),
            new FakeEditorServices(),
            new ConfirmingDialogHost(),
            null);
    }

    private static PendingChangesViewModel CreatePendingChangesViewModel(TfvcSession session, FakeTfvcClient client, ConfirmingDialogHost? dialogHost = null)
    {
        FakeEditorServices editor = new();
        return new PendingChangesViewModel(
            session,
            new TfvcAutoCheckOutService(session, editor, new RecordingWindow(), new NullExtensionLogger()),
            editor,
            new RecordingWindow(),
            dialogHost ?? new ConfirmingDialogHost(),
            null);
    }

    private static async Task<(TfvcSession Session, FakeTfvcClient Client, FakeTfvcServerClient Server)> CreateSessionAsync()
    {
        FakeTfvcClient client = new()
        {
            Workspaces = [TfvcTestData.CreateWorkspace()],
            PendingChanges =
            [
                TfvcTestData.CreateChange("$/Product/a.cs", @"C:\src\Product\a.cs"),
                TfvcTestData.CreateChange("$/Product/b.cs", @"C:\src\Product\b.cs", "Add")
            ]
        };
        FakeTfvcServerClient server = new();
        TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        return (session, client, server);
    }
}
