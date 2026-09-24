using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.DataGridSorting;
using Xunit;
using XamlVisualEditor.TfvcExtension.Models;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.Tests.Unit.Tfvc;

public sealed class TfvcHistoryViewModelTests
{
    private const string AuthorId = "01c607f8-9e4e-46a0-9120-9660001ee287";

    [Fact]
    public void ReadChangesets_ReadsAuthorIdentity()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"value":[{"changesetId":42,"author":{"id":"a-1","displayName":"Test User","uniqueName":"DOMAIN\\test"},"createdDate":"2026-09-01T10:00:00Z","comment":"Fix"}]}
            """);

        TfvcChangesetInfo changeset = Assert.Single(TfvcServerClient.ReadChangesets(document.RootElement));

        Assert.Equal(42, changeset.Id);
        Assert.Equal("Test User", changeset.Author);
        Assert.Equal("a-1", changeset.AuthorId);
        Assert.Equal(@"DOMAIN\test", changeset.AuthorUniqueName);
        Assert.Equal("Fix", changeset.Comment);
    }

    [Fact]
    public async Task History_SortsByChangesetDescendingByDefault()
    {
        (TfvcSession session, _, _) = await CreateSessionAsync();
        using (session)
        {
            TfvcHistoryViewModel viewModel = new(session);

            SortingDescriptor descriptor = Assert.Single(viewModel.SortingModel.Descriptors);
            Assert.Equal(ListSortDirection.Descending, descriptor.Direction);
            Assert.Equal(nameof(TfvcChangesetRowViewModel.Id), descriptor.PropertyPath);
        }
    }

    [Fact]
    public async Task History_FiltersByAuthorAndCollectsKnownAuthors()
    {
        (TfvcSession session, _, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        {
            TfvcHistoryViewModel viewModel = new(session);
            await viewModel.ShowAsync("$/Product", CancellationToken.None);

            Assert.Equal(3, viewModel.Changesets.Count);
            Assert.Equal(["Other User", "Test User"], viewModel.KnownAuthors);

            viewModel.AuthorText = " Other User ";
            await viewModel.SearchCommand.Execute().ToTask();

            Assert.Equal("Other User", server.AuthorFilters[^1]);
            TfvcChangesetRowViewModel row = Assert.Single(viewModel.Changesets);
            Assert.Equal(2, row.Id);

            viewModel.SearchText = "nothing";
            await viewModel.SearchCommand.Execute().ToTask();
            Assert.Empty(viewModel.Changesets);
            Assert.Equal("Other User", server.AuthorFilters[^1]);

            await viewModel.ClearSearchCommand.Execute().ToTask();
            Assert.Null(viewModel.AuthorText);
            Assert.Null(server.AuthorFilters[^1]);
            Assert.Equal(3, viewModel.Changesets.Count);
        }
    }

    [Fact]
    public async Task History_ShowsDetailsOfSelectedChangesetAndReplacesUpdatedRow()
    {
        (TfvcSession session, _, _) = await CreateSessionAsync();
        using (session)
        {
            TfvcChangesetInfo? shown = null;
            TfvcHistoryViewModel viewModel = new(session, changeset =>
            {
                shown = changeset;
                return Task.CompletedTask;
            });
            await viewModel.ShowAsync("$/Product", CancellationToken.None);
            viewModel.SelectedChangeset = viewModel.Changesets[0];

            await viewModel.ShowChangesetDetailsCommand.Execute().ToTask();
            Assert.Equal(3, shown?.Id);

            viewModel.UpdateChangeset(shown! with { Comment = "Revised" });

            Assert.Equal("Revised", viewModel.Changesets[0].Comment);
            Assert.Same(viewModel.Changesets[0], viewModel.SelectedChangeset);
        }
    }

    [Fact]
    public async Task ChangesetDetails_AuthorCanSaveRevisedComment()
    {
        (TfvcSession session, FakeTfvcClient client, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        {
            server.ChangesetChanges[3] = [new TfvcChangesetChange("$/Product/a.cs", "edit")];
            TfvcChangesetDetailsViewModel viewModel = new(session);
            TfvcChangesetInfo? updated = null;
            viewModel.ChangesetUpdated += (_, changeset) => updated = changeset;

            await viewModel.ShowAsync(server.History[0], CancellationToken.None);

            Assert.True(viewModel.IsActive);
            Assert.True(viewModel.CanEditComment);
            Assert.Equal("$/Product/a.cs", Assert.Single(viewModel.Changes).ServerPath);
            Assert.False(await viewModel.SaveCommand.CanExecute.FirstAsync());

            viewModel.Comment = "  Revised comment  ";
            Assert.True(await viewModel.SaveCommand.CanExecute.FirstAsync());
            await viewModel.SaveCommand.Execute().ToTask();

            Assert.Equal((3, "Revised comment"), Assert.Single(client.ChangesetCommentUpdates));
            Assert.Equal("Revised comment", updated?.Comment);
            Assert.Equal("Revised comment", viewModel.Changeset?.Comment);
            Assert.Contains("3", viewModel.Message);

            await viewModel.CloseCommand.Execute().ToTask();
            Assert.False(viewModel.IsActive);
        }
    }

    [Fact]
    public async Task ChangesetDetails_OtherAuthorCannotEditComment()
    {
        (TfvcSession session, _, FakeTfvcServerClient server) = await CreateSessionAsync();
        using (session)
        {
            TfvcChangesetDetailsViewModel viewModel = new(session);

            await viewModel.ShowAsync(server.History[1], CancellationToken.None);
            viewModel.Comment = "Changed";

            Assert.False(viewModel.CanEditComment);
            Assert.False(await viewModel.SaveCommand.CanExecute.FirstAsync());
        }
    }

    private static async Task<(TfvcSession Session, FakeTfvcClient Client, FakeTfvcServerClient Server)> CreateSessionAsync()
    {
        FakeTfvcClient client = new() { Workspaces = [TfvcTestData.CreateWorkspace()] };
        FakeTfvcServerClient server = new() { CurrentUserId = AuthorId };
        DateTimeOffset date = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        server.History.Add(new TfvcChangesetInfo(3, "Test User", date, "Third", AuthorId));
        server.History.Add(new TfvcChangesetInfo(2, "Other User", date, "Second", "other-id"));
        server.History.Add(new TfvcChangesetInfo(1, "Test User", date, "First", AuthorId));
        TfvcSession session = TfvcSessionTests.CreateSession(client, new InMemoryExtensionStorage(), new FakeWorkspaceInfo(), server);
        await session.ApplySettingsAsync(new TfvcSettings(TfvcTestData.CollectionUrl, null), CancellationToken.None);
        return (session, client, server);
    }
}
