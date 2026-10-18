using Avalonia.Headless.XUnit;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.Tests.Shared;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitStashCommandsTests
{
    [AvaloniaFact]
    public async Task Stash_asks_for_a_message_and_stashes_only_when_there_is_something_to_stash()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("neu.txt", "new\n"));
        fixture.Window.InputBoxResult = " Grüße ";

        await fixture.ExecuteAsync(GitCommands.STASH);
        Assert.Equal([GitStrings.NothingToStash], fixture.Window.Infos);

        await fixture.ExecuteAsync(GitCommands.STASH_INCLUDE_UNTRACKED);

        Assert.Equal("stash@{0}: On main: Grüße\n", fixture.Repository.RunGit("stash", "list"));
        Assert.False(File.Exists(Path.Combine(fixture.Repository.Path, "neu.txt")));
        Assert.Single(fixture.Window.Infos);
    }

    [AvaloniaFact]
    public async Task Stash_of_a_folder_stashes_its_changes_and_leaves_the_others_and_staged_stashes_only_the_index()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.CommitFileIn(repository.Path, "src/b.txt", "b\n", "Add b");
            repository.WriteFile("a.txt", "changed\n");
            repository.WriteFile("src/b.txt", "changed\n");
            repository.WriteFile("src/new.txt", "new\n");
        });
        fixture.Window.InputBoxResult = "src";

        await fixture.ExecuteAsync(GitCommands.STASH_STAGED);
        Assert.Equal([GitStrings.NothingStagedToStash], fixture.Window.Infos);
        await fixture.ExecuteAsync(GitCommands.STASH_PATHS, Path.Combine(fixture.Repository.Path, "src"));

        Assert.Equal("stash@{0}: On main: src\n", fixture.Repository.RunGit("stash", "list"));
        Assert.Equal("b\n", fixture.Repository.ReadFile("src/b.txt"));
        Assert.Null(fixture.Repository.ReadFile("src/new.txt"));
        Assert.Equal("changed\n", fixture.Repository.ReadFile("a.txt"));

        fixture.Repository.RunGit("add", "a.txt");
        await fixture.Session.RefreshAsync();
        fixture.Window.InputBoxResult = "index";
        await fixture.ExecuteAsync(GitCommands.STASH_STAGED);

        Assert.Equal("stash@{0}: On main: index", fixture.Repository.RunGit("stash", "list").Split('\n')[0]);
        Assert.Equal(string.Empty, fixture.Repository.RunGit("status", "--porcelain").Trim());
        Assert.Single(fixture.Window.Infos);
    }

    [AvaloniaFact]
    public async Task A_cancelled_message_stashes_nothing()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("a.txt", "changed\n"));
        fixture.Window.InputBoxResult = null;

        await fixture.ExecuteAsync(GitCommands.STASH);

        Assert.Equal(string.Empty, fixture.Repository.RunGit("stash", "list"));
        Assert.Equal("changed\n", File.ReadAllText(Path.Combine(fixture.Repository.Path, "a.txt")));
    }

    [AvaloniaFact]
    public async Task Drop_asks_first_and_a_declined_drop_keeps_the_stash()
    {
        bool confirm = false;
        using GitPanelCommandFixture fixture = await CreateAsync(StashOneChange, (_, _) => confirm);
        string id = fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim();

        await fixture.ExecuteAsync(GitCommands.STASH_DROP, id);
        Assert.Equal(id, fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim());
        ConfirmationDialogRequest request = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Equal(string.Format(GitStrings.StashDropPromptFormat, "stash@{0}", "On main: first"), request.Message);
        Assert.True(request.IsDestructive);

        confirm = true;
        await fixture.ExecuteAsync(GitCommands.STASH_DROP, id);

        Assert.Equal(string.Empty, fixture.Repository.RunGit("stash", "list"));
        Assert.Empty(fixture.Window.Errors);
    }

    [AvaloniaFact]
    public async Task Apply_keeps_the_stash_and_pop_removes_it()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(StashOneChange);
        string id = fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim();

        await fixture.ExecuteAsync(GitCommands.STASH_APPLY, id);
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(fixture.Repository.Path, "a.txt")));
        Assert.Equal(id, fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim());

        fixture.Repository.RunGit("checkout", "--", "a.txt");
        await fixture.Session.RefreshAsync();
        await fixture.ExecuteAsync(GitCommands.STASH_POP, id);

        Assert.Equal("first\n", File.ReadAllText(Path.Combine(fixture.Repository.Path, "a.txt")));
        Assert.Equal(string.Empty, fixture.Repository.RunGit("stash", "list"));
        Assert.Empty(fixture.Window.Infos);
        Assert.Empty(fixture.Window.Errors);
    }

    [AvaloniaFact]
    public async Task A_pop_with_conflicts_keeps_the_stash_and_says_so()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            StashOneChange(repository);
            repository.CommitFileIn(repository.Path, "a.txt", "committed\n", "conflicting");
        });
        string id = fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim();

        await fixture.ExecuteAsync(GitCommands.STASH_POP, id);

        Assert.Equal([GitStrings.StashConflicts], fixture.Window.Infos);
        Assert.Empty(fixture.Window.Errors);
        Assert.Equal(id, fixture.Repository.RunGit("rev-parse", "stash@{0}").Trim());
        Assert.True(fixture.Session.Current.HasConflicts);
    }

    [AvaloniaFact]
    public async Task Palette_commands_pick_the_stash_from_a_list_or_say_there_is_none()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(_ => { });
        await fixture.ExecuteAsync(GitCommands.STASH_APPLY);
        Assert.Equal([GitStrings.NoStashes], fixture.Window.Infos);

        StashOneChange(fixture.Repository);
        fixture.Window.QuickPick = items => items[0];
        await fixture.ExecuteAsync(GitCommands.STASH_POP);
        await fixture.ExecuteAsync(GitCommands.STASH_DROP, "0123456789012345678901234567890123456789");

        (IReadOnlyList<QuickPickItem> items, QuickPickOptions options) = Assert.Single(fixture.Window.QuickPicks);
        // The title is the command without the ellipsis that marks it in the menus.
        Assert.EndsWith("…", GitStrings.StashPopCommand, StringComparison.Ordinal);
        Assert.DoesNotContain("…", options.Title, StringComparison.Ordinal);
        Assert.StartsWith(options.Title, GitStrings.StashPopCommand, StringComparison.Ordinal);
        Assert.Equal(("stash@{0}", "On main: first"), (items[0].Label, items[0].Description));
        Assert.Equal("first\n", File.ReadAllText(Path.Combine(fixture.Repository.Path, "a.txt")));
        Assert.Equal(
            [GitStrings.NoStashes, string.Format(GitStrings.StashNotFoundFormat, "0123456789012345678901234567890123456789")],
            fixture.Window.Infos);
    }

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
