using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.Tests.Shared;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>Renaming branches, managing remotes and showing, branching and clearing stashes through the commands.</summary>
public sealed class GitRepositoryCommandsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task Rename_branch_offers_the_current_branch_first_and_refuses_an_invalid_name()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.RunGit("branch", "other"));
        fixture.Window.QuickPick = items => items[0];
        fixture.Window.InputBoxResult = "bad..name";

        await fixture.ExecuteAsync(GitCommands.RENAME_BRANCH);

        (IReadOnlyList<QuickPickItem> items, QuickPickOptions options) = Assert.Single(fixture.Window.QuickPicks);
        Assert.Equal(GitStrings.RenameBranchPickTitle, options.Title);
        Assert.Equal(["main", "other"], items.Select(item => item.Label));
        Assert.Equal("main", Assert.Single(fixture.Window.InputBoxes).Value);
        Assert.Equal([string.Format(GitStrings.InvalidBranchNameFormat, "bad..name")], fixture.Window.Errors);
        Assert.Equal("main", CurrentBranch(fixture.Repository));

        fixture.Window.InputBoxResult = "renamed";
        await fixture.ExecuteAsync(GitCommands.RENAME_BRANCH, "refs/heads/main");

        Assert.Equal("renamed", CurrentBranch(fixture.Repository));
        Assert.Equal("renamed", fixture.Session.Current.BranchName);
    }

    [AvaloniaFact]
    public async Task Remotes_are_added_changed_renamed_and_removed_after_a_confirmation()
    {
        bool confirm = false;
        using GitPanelCommandFixture fixture = await CreateAsync(_ => { }, (_, _) => confirm);
        using GitRepositoryPanelViewModel panel = new(
            fixture.Session,
            (commandId, arguments) => fixture.Commands.ExecuteAsync(commandId, arguments, CancellationToken.None)) { IsShown = true };
        fixture.Window.InputBoxResults.Enqueue(null);

        await fixture.ExecuteAsync(GitCommands.ADD_REMOTE);

        Assert.Equal(GitRemote.DEFAULT_NAME, Assert.Single(fixture.Window.InputBoxes).Value);
        Assert.Equal(string.Empty, fixture.Repository.RunGit("remote").Trim());

        fixture.Window.InputBoxResults.Enqueue("--upload-pack=echo");
        await fixture.ExecuteAsync(GitCommands.ADD_REMOTE);
        Assert.Equal([string.Format(GitStrings.InvalidRemoteNameFormat, "--upload-pack=echo")], fixture.Window.Infos);
        Assert.Equal(2, fixture.Window.InputBoxes.Count);

        fixture.Window.InputBoxResults.Enqueue("origin");
        fixture.Window.InputBoxResults.Enqueue("https://user:token@example.com/repo.git");
        await fixture.ExecuteAsync(GitCommands.ADD_REMOTE);
        // The panel reads the remotes again after the command, as after every command of the session.
        await WaitUntilAsync(() => panel.Remotes.Select(remote => remote.UrlText).SequenceEqual(["https://***@example.com/repo.git"]));
        fixture.Window.InputBoxResults.Enqueue("https://example.com/other.git");
        await fixture.ExecuteAsync(GitCommands.SET_REMOTE_URL, "origin");
        fixture.Window.QuickPick = items => items[0];
        fixture.Window.InputBoxResults.Enqueue("upstream");
        await fixture.ExecuteAsync(GitCommands.RENAME_REMOTE);

        Assert.Equal("https://user:token@example.com/repo.git", fixture.Window.InputBoxes[^2].Value);
        Assert.EndsWith(GitStrings.RemoteUrlShowsCredentials, fixture.Window.InputBoxes[^2].Prompt, StringComparison.Ordinal);
        Assert.Equal(("origin", "https://example.com/other.git"), (fixture.Window.QuickPicks[^1].Items[0].Label, fixture.Window.QuickPicks[^1].Items[0].Description));
        Assert.Equal("https://example.com/other.git", fixture.Repository.RunGit("remote", "get-url", "upstream").Trim());
        Assert.True(fixture.Session.Current.HasRemote);
        await WaitUntilAsync(() => panel.Remotes.Select(remote => remote.Name).SequenceEqual(["upstream"]));

        await fixture.ExecuteAsync(GitCommands.REMOVE_REMOTE, "upstream");
        Assert.Equal("upstream", fixture.Repository.RunGit("remote").Trim());
        confirm = true;
        await fixture.ExecuteAsync(GitCommands.REMOVE_REMOTE, "upstream");

        ConfirmationDialogRequest[] confirmations = fixture.Dialogs.Confirmations.ToArray();
        Assert.Equal(2, confirmations.Length);
        Assert.Contains("upstream", confirmations[0].Message);
        Assert.Equal(string.Empty, fixture.Repository.RunGit("remote").Trim());
        Assert.False(fixture.Session.Current.HasRemote);
        Assert.Empty(fixture.Window.Errors);
        await WaitUntilAsync(() => panel.Remotes.Count == 0 && panel.RemotesMessage == GitStrings.NoRemote);
    }

    [AvaloniaFact]
    public async Task Show_opens_the_stash_with_its_tracked_and_untracked_files_and_their_diffs()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.WriteFile("a.txt", "first\n");
            repository.WriteFile("new.txt", "brand new\n");
            repository.RunGit("stash", "push", "-q", "--include-untracked", "-m", "first");
        });
        fixture.Window.QuickPick = items => items[0];

        await fixture.ExecuteAsync(GitCommands.STASH_SHOW);

        (string id, string title, string? toolTip) = Assert.Single(fixture.ViewHost.ShownDocuments);
        string shortSha = fixture.Repository.RunGit("rev-parse", "--short", "stash@{0}").Trim();
        Assert.Equal((string.Format(GitStrings.StashTitleFormat, shortSha), "On main: first"), (title, toolTip));
        using GitCommitDetailsViewModel details = Assert.IsType<GitCommitDetailsViewModel>(fixture.ViewHost.DocumentViewModels[id]);
        await details.WhenAnyValue(x => x.FilesHeader)
            .FirstAsync(header => header == string.Format(GitStrings.CommitFilesFormat, 2))
            .Timeout(WaitLimit);
        Assert.Equal(
            [("a.txt", GitChangeKind.Modified), ("new.txt", GitChangeKind.Added)],
            details.Files.Select(file => (file.Path, file.Change.Kind)));
        Assert.Contains(details.Commit!.ParentShas[0][..7], details.MergeText, StringComparison.Ordinal);

        details.SelectedFile = details.Files[1];
        IReadOnlyList<DiffLine> lines = await details.WhenAnyValue(x => x.DiffLines)
            .FirstAsync(diff => diff.Any(line => line.Text.Contains("brand new", StringComparison.Ordinal)))
            .Timeout(WaitLimit);
        Assert.NotEmpty(lines);

        // The history of a tracked file starts at the commit the stash was made on; an untracked file has none.
        List<IReadOnlyList<object?>?> histories = new();
        using GitCommitDetailsViewModel withHistory = GitCommitDetailsViewModel.ForStash(
            fixture.Session,
            (_, arguments) =>
            {
                histories.Add(arguments);
                return Task.CompletedTask;
            },
            details.Commit);
        await withHistory.WhenAnyValue(x => x.FilesHeader)
            .FirstAsync(header => header == string.Format(GitStrings.CommitFilesFormat, 2))
            .Timeout(WaitLimit);
        Assert.True(await withHistory.ShowFileHistoryCommand.CanExecute.FirstAsync());
        await withHistory.ShowFileHistoryCommand.Execute();
        Assert.Equal(details.Commit.ParentShas[0], Assert.Single(histories)![1]);
        withHistory.SelectedFile = withHistory.Files[1];
        Assert.False(await withHistory.ShowFileHistoryCommand.CanExecute.FirstAsync());

        // Showing the stash again activates its tab instead of opening a second one.
        await fixture.ExecuteAsync(GitCommands.STASH_SHOW);
        Assert.Equal(2, fixture.ViewHost.ShownDocuments.Count);
        Assert.Same(details, Assert.Single(fixture.ViewHost.DocumentViewModels).Value);
    }

    [AvaloniaFact]
    public async Task Show_of_a_stash_without_untracked_files_lists_its_tracked_changes_only()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.WriteFile("a.txt", "first\n");
            repository.WriteFile("new.txt", "not stashed\n");
            repository.RunGit("stash", "push", "-q", "-m", "first");
        });
        fixture.Window.QuickPick = items => items[0];

        await fixture.ExecuteAsync(GitCommands.STASH_SHOW);

        using GitCommitDetailsViewModel details = Assert.IsType<GitCommitDetailsViewModel>(Assert.Single(fixture.ViewHost.DocumentViewModels).Value);
        await details.WhenAnyValue(x => x.FilesHeader)
            .FirstAsync(header => header == string.Format(GitStrings.CommitFilesFormat, 1))
            .Timeout(WaitLimit);
        Assert.Equal([("a.txt", GitChangeKind.Modified)], details.Files.Select(file => (file.Path, file.Change.Kind)));
        Assert.Equal(2, details.Commit!.ParentShas.Count);
    }

    [AvaloniaFact]
    public async Task Branch_from_a_stash_that_cannot_be_applied_says_that_the_branch_exists_and_keeps_the_stash()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.WriteFile("new.txt", "stashed\n");
            repository.RunGit("stash", "push", "-q", "--include-untracked", "-m", "first");
            repository.WriteFile("new.txt", "local\n");
        });
        fixture.Window.QuickPick = items => items[0];
        fixture.Window.InputBoxResult = "from-stash";

        await fixture.ExecuteAsync(GitCommands.STASH_BRANCH);

        Assert.Equal([string.Format(GitStrings.StashBranchStoppedFormat, "from-stash", "stash@{0}")], fixture.Window.Infos);
        Assert.Equal("from-stash", CurrentBranch(fixture.Repository));
        Assert.True(fixture.Repository.TryRunGit(out _, "rev-parse", "--verify", "--quiet", "refs/stash"));
        Assert.Equal("local\n", fixture.Repository.ReadFile("new.txt"));
    }

    [AvaloniaFact]
    public async Task Branch_from_a_stash_asks_for_a_valid_name_and_clear_asks_first()
    {
        bool confirm = false;
        using GitPanelCommandFixture fixture = await CreateAsync(StashOneChange, (_, _) => confirm);
        fixture.Window.QuickPick = items => items[0];
        fixture.Window.InputBoxResult = "bad..name";

        await fixture.ExecuteAsync(GitCommands.STASH_BRANCH);

        Assert.Equal([string.Format(GitStrings.InvalidBranchNameFormat, "bad..name")], fixture.Window.Errors);
        Assert.Equal("1", fixture.Repository.RunGit("rev-list", "--walk-reflogs", "--count", "refs/stash").Trim());

        fixture.Window.InputBoxResult = "from-stash";
        await fixture.ExecuteAsync(GitCommands.STASH_BRANCH);

        Assert.Equal("from-stash", CurrentBranch(fixture.Repository));
        Assert.Equal("first\n", fixture.Repository.ReadFile("a.txt"));
        Assert.False(fixture.Repository.TryRunGit(out _, "rev-parse", "--verify", "--quiet", "refs/stash"));

        fixture.Repository.RunGit("stash", "push", "-q", "-m", "again");
        await fixture.ExecuteAsync(GitCommands.STASH_CLEAR);
        Assert.True(fixture.Repository.TryRunGit(out _, "rev-parse", "--verify", "--quiet", "refs/stash"));
        confirm = true;
        await fixture.ExecuteAsync(GitCommands.STASH_CLEAR);

        ConfirmationDialogRequest[] confirmations = fixture.Dialogs.Confirmations.ToArray();
        Assert.Equal(2, confirmations.Length);
        Assert.Equal(string.Format(GitStrings.StashClearPromptFormat, 1), confirmations[0].Message);
        Assert.False(fixture.Repository.TryRunGit(out _, "rev-parse", "--verify", "--quiet", "refs/stash"));
    }

    [AvaloniaFact]
    public async Task Clear_of_stashes_that_changed_while_the_question_was_open_clears_nothing()
    {
        GitPanelCommandFixture? fixture = null;
        // Another stash is made while the question is open.
        fixture = await CreateAsync(StashOneChange, (_, _) =>
        {
            fixture!.Repository.WriteFile("a.txt", "second\n");
            fixture.Repository.RunGit("stash", "push", "-q", "-m", "second");
            return true;
        });
        using (fixture)
        {
            await fixture.ExecuteAsync(GitCommands.STASH_CLEAR);

            Assert.Equal([string.Format(GitStrings.StashesChangedFormat, 0, 1)], fixture.Window.Infos);
            Assert.Equal("2", fixture.Repository.RunGit("rev-list", "--walk-reflogs", "--count", "refs/stash").Trim());
        }
    }

    // The panel reads its lists in the background and applies them on the UI thread, which runs while this waits.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime end = DateTime.UtcNow + WaitLimit;
        while (!condition())
        {
            if (DateTime.UtcNow > end)
            {
                throw new TimeoutException("The condition did not become true.");
            }

            await Task.Delay(20);
        }
    }

    private static string CurrentBranch(GitTestRepository repository) =>
        repository.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim();

    private static void StashOneChange(GitTestRepository repository)
    {
        repository.WriteFile("a.txt", "first\n");
        repository.RunGit("stash", "push", "-q", "-m", "first");
    }

    private static async Task<GitPanelCommandFixture> CreateAsync(
        Action<GitTestRepository> arrange,
        Func<string, object?, object?>? respond = null)
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        try
        {
            arrange(repository);
            return await GitPanelCommandFixture.CreateAsync(repository, respond);
        }
        catch
        {
            repository.Dispose();
            throw;
        }
    }
}
