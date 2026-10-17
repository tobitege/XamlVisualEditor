using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Headless.XUnit;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.Tests.Shared;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitPatchCommandsTests
{
    private const string Twelve = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\n";
    private const string TwoChanges = "l1\nX2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nY11\nl12\n";

    [AvaloniaFact]
    public async Task Stage_selection_stages_the_selected_lines_and_reloads_the_diff()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("f.txt", TwoChanges));
        string shown = await fixture.SelectAsync("f.txt", staged: false);
        Assert.False(((ICommand)fixture.Panel.StageSelectionCommand).CanExecute(null));

        fixture.Panel.SelectedDiffRange = new DiffLineRange(fixture.LineOf("+Y11"), 1);
        Assert.True(((ICommand)fixture.Panel.StageSelectionCommand).CanExecute(null));
        Task<string> reloaded = fixture.NextDiffAsync(text => text != shown);
        await fixture.Panel.StageSelectionCommand.Execute().ToTask();

        Assert.Equal(Twelve.Replace("l12\n", "Y11\nl12\n"), fixture.Repository.RunGit("show", ":f.txt"));
        Assert.DoesNotContain("+Y11", await reloaded, StringComparison.Ordinal);
        Assert.Empty(fixture.Window.Infos);
        Assert.Empty(fixture.Window.Errors);
    }

    [AvaloniaFact]
    public async Task Without_a_selection_the_hunk_at_the_cursor_is_unstaged()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.WriteFile("f.txt", TwoChanges);
            repository.RunGit("add", "--", "f.txt");
        });
        await fixture.SelectAsync("f.txt", staged: true);
        Assert.False(((ICommand)fixture.Panel.UnstageSelectionCommand).CanExecute(null));
        Assert.False(((ICommand)fixture.Panel.StageSelectionCommand).CanExecute(null));

        fixture.Panel.CurrentHunkIndex = 1;
        await fixture.Panel.UnstageSelectionCommand.Execute().ToTask();

        Assert.Equal(Twelve.Replace("l2\n", "X2\n"), fixture.Repository.RunGit("show", ":f.txt"));
        Assert.Equal(TwoChanges, File.ReadAllText(Path.Combine(fixture.Repository.Path, "f.txt")));
    }

    [AvaloniaFact]
    public async Task Discard_selection_asks_first_and_keeps_the_file_when_declined()
    {
        bool confirm = false;
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("f.txt", TwoChanges), (_, _) => confirm);
        await fixture.SelectAsync("f.txt", staged: false);
        fixture.Panel.CurrentHunkIndex = 0;

        await fixture.Panel.DiscardSelectionCommand.Execute().ToTask();
        Assert.Equal(TwoChanges, File.ReadAllText(Path.Combine(fixture.Repository.Path, "f.txt")));
        ConfirmationDialogRequest request = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Equal(string.Format(GitStrings.DiscardSelectionPromptFormat, "f.txt"), request.Message);
        Assert.True(request.IsDestructive);

        confirm = true;
        await fixture.Panel.DiscardSelectionCommand.Execute().ToTask();

        Assert.Equal(Twelve.Replace("l11\n", "Y11\n"), File.ReadAllText(Path.Combine(fixture.Repository.Path, "f.txt")));
        Assert.Equal(Twelve, fixture.Repository.RunGit("show", ":f.txt"));
    }

    [AvaloniaFact]
    public async Task A_changed_diff_is_reported_and_shown_again_instead_of_applied()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("f.txt", TwoChanges));
        string shown = await fixture.SelectAsync("f.txt", staged: false);
        fixture.Panel.CurrentHunkIndex = 0;
        fixture.Repository.WriteFile("f.txt", TwoChanges.Replace("l4\n", "X4\n"));
        Task<string> reloaded = fixture.NextDiffAsync(text => text != shown);

        await fixture.Panel.StageSelectionCommand.Execute().ToTask();

        Assert.Equal([GitStrings.DiffChanged], fixture.Window.Infos);
        Assert.Equal(Twelve, fixture.Repository.RunGit("show", ":f.txt"));
        Assert.Contains("+X4", await reloaded, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Selection_commands_from_the_palette_without_a_selection_say_so()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.WriteFile("neu.txt", "new\n"));

        await fixture.ExecuteAsync(GitCommands.STAGE_SELECTION);
        await fixture.SelectAsync("neu.txt", staged: false);
        fixture.Panel.CurrentHunkIndex = 0;
        await fixture.ExecuteAsync(GitCommands.STAGE_SELECTION);

        Assert.False(((ICommand)fixture.Panel.StageSelectionCommand).CanExecute(null));
        Assert.Equal([GitStrings.NoDiffSelection, GitStrings.NoDiffSelection], fixture.Window.Infos);
        Assert.Equal("?? neu.txt\n", fixture.Repository.RunGit("status", "--porcelain"));
    }

    [AvaloniaFact]
    public async Task Amend_fills_in_the_last_message_and_unchecking_clears_it_only_while_unchanged()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository => repository.RunGit("commit", "-q", "--amend", "-m", "Größe\n\nKörper"));
        GitPanelViewModel panel = fixture.Panel;
        Assert.True(panel.CanAmend);

        Task<string?> filled = panel.WhenAnyValue(x => x.CommitMessage).FirstAsync(message => message == "Größe\n\nKörper").Timeout(TimeSpan.FromSeconds(10)).ToTask();
        panel.IsAmend = true;
        await filled;
        panel.IsAmend = false;
        Assert.Equal(string.Empty, panel.CommitMessage);

        panel.CommitMessage = "own";
        panel.IsAmend = true;
        panel.IsAmend = false;
        Assert.Equal("own", panel.CommitMessage);
    }

    [AvaloniaFact]
    public async Task Amend_ends_with_its_filled_in_message_when_another_commit_becomes_head()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(repository =>
        {
            repository.WriteFile("f.txt", TwoChanges);
            repository.RunGit("add", "--", "f.txt");
        });
        GitPanelViewModel panel = fixture.Panel;
        Task<string?> filled = panel.WhenAnyValue(x => x.CommitMessage).FirstAsync(message => message == "init").Timeout(TimeSpan.FromSeconds(10)).ToTask();
        panel.IsAmend = true;
        await filled;

        // A commit made outside the editor moves HEAD.
        fixture.Repository.RunGit("commit", "-q", "-m", "elsewhere");
        Task<int> applied = panel.WhenAnyValue(x => x.StagedCount).FirstAsync(count => count == 0).Timeout(TimeSpan.FromSeconds(10)).ToTask();
        await fixture.Session.RefreshAsync();
        await applied;

        Assert.False(panel.IsAmend);
        Assert.Equal(string.Empty, panel.CommitMessage);
    }

    [AvaloniaFact]
    public async Task Amend_replaces_the_last_commit_and_asks_first_when_the_upstream_contains_it()
    {
        bool confirm = false;
        using GitPanelCommandFixture fixture = await CreateAsync(
            repository =>
            {
                repository.AddBareRemote();
                repository.RunGit("push", "-q", "-u", "origin", "main");
            },
            (_, _) => confirm);
        GitPanelViewModel panel = fixture.Panel;
        string head = fixture.Repository.RunGit("rev-parse", "HEAD");
        panel.CommitMessage = "Geändert";
        panel.IsAmend = true;
        Assert.True(((ICommand)panel.CommitCommand).CanExecute(null));

        await panel.CommitCommand.Execute().ToTask();
        Assert.Equal(head, fixture.Repository.RunGit("rev-parse", "HEAD"));
        ConfirmationDialogRequest request = Assert.Single(fixture.Dialogs.Confirmations);
        Assert.Equal(string.Format(GitStrings.AmendPushedPromptFormat, "main", "origin/main"), request.Message);
        Assert.True(request.IsDestructive);

        confirm = true;
        await panel.CommitCommand.Execute().ToTask();

        Assert.Equal("Geändert", fixture.Repository.RunGit("log", "-1", "--format=%B").TrimEnd('\n'));
        Assert.Equal("1", fixture.Repository.RunGit("rev-list", "--count", "HEAD").Trim());
        Assert.False(panel.IsAmend);
        Assert.Equal(string.Empty, panel.CommitMessage);
    }

    [AvaloniaFact]
    public async Task Commit_amend_shows_the_panel_with_the_amend_box_checked()
    {
        using GitPanelCommandFixture fixture = await CreateAsync(_ => { });

        await fixture.ExecuteAsync(GitCommands.COMMIT_AMEND);

        Assert.Equal([GitCommands.CHANGES_VIEW_ID], fixture.ViewHost.Shown);
        Assert.True(fixture.Panel.IsAmend);
    }

    private static async Task<GitPanelCommandFixture> CreateAsync(
        Action<GitTestRepository> arrange,
        Func<string, object?, object?>? respond = null)
    {
        GitTestRepository repository = GitTestRepository.Create();
        try
        {
            repository.CommitFileIn(repository.Path, "f.txt", Twelve, "init");
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
