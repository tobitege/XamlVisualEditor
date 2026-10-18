using System.Reactive.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

/// <summary>The Git Changes panel during a merge that stopped with every conflict kind.</summary>
public sealed class GitConflictPanelTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    [AvaloniaFact]
    public async Task A_stopped_merge_lists_its_conflicts_shows_the_banner_and_fills_in_the_prepared_message()
    {
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        using Fixture fixture = await Fixture.CreateAsync(repository);
        GitPanelViewModel panel = fixture.Panel;

        Assert.Equal(9, panel.ConflictItems.Count);
        Assert.Equal(GitStrings.ConflictBothModified, panel.ConflictItems.Single(entry => entry.Path == "content.txt").KindLabel);
        Assert.Equal(GitStrings.ConflictDeletedByThem, panel.ConflictItems.Single(entry => entry.Path == "deleted-by-them.txt").KindLabel);
        Assert.DoesNotContain(panel.UnstagedItems, entry => entry.Path == "content.txt");
        Assert.Contains(panel.UnstagedItems, entry => entry.Path == "local.txt");
        Assert.Equal(GitStrings.OperationMerging + " · " + string.Format(GitStrings.ConflictCountFormat, 9), panel.OperationSummary);
        Assert.True(panel.IsMerging);
        Assert.False(panel.CanAmend);
        Assert.Equal("Merge branch 'feature'", await panel.WhenAnyValue(x => x.CommitMessage).FirstAsync(message => !string.IsNullOrEmpty(message)).Timeout(WaitLimit));
        Assert.False(await panel.CommitCommand.CanExecute.FirstAsync());
        Assert.False(await panel.ContinueCommand.CanExecute.FirstAsync());
        Assert.True(await panel.AbortCommand.CanExecute.FirstAsync());

        panel.SelectedUnstagedChange = panel.UnstagedItems.Single(entry => entry.Path == "local.txt");
        panel.SelectedConflict = panel.ConflictItems.Single(entry => entry.Path == "content.txt");
        await panel.AcceptOursCommand.Execute();

        Assert.Null(panel.SelectedUnstagedChange);
        Assert.Equal((GitCommands.ACCEPT_OURS, (object?)"content.txt"), (fixture.Executed.Single().CommandId, fixture.Executed.Single().Arguments![0]));
    }

    [AvaloniaFact]
    public async Task Resolving_every_conflict_enables_commit_and_ending_the_merge_clears_the_prepared_message()
    {
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        using Fixture fixture = await Fixture.CreateAsync(repository);
        GitPanelViewModel panel = fixture.Panel;
        await panel.WhenAnyValue(x => x.CommitMessage).FirstAsync(message => !string.IsNullOrEmpty(message)).Timeout(WaitLimit);

        string[] conflicted = panel.ConflictItems.Select(entry => entry.Path).ToArray();
        Assert.True(await fixture.Git.TakeSideAsync(repository.Path, conflicted, GitConflictSide.Ours, CancellationToken.None));
        await fixture.RefreshAsync();

        Assert.False(panel.HasConflicts);
        Assert.Equal(GitStrings.OperationMerging + " · " + GitStrings.MergeReadyToCommit, panel.OperationSummary);
        Assert.True(await panel.CommitCommand.CanExecute.FirstAsync());

        repository.RunGit("merge", "--abort");
        await fixture.RefreshAsync();

        Assert.False(panel.IsOperationInProgress);
        Assert.Equal(string.Empty, panel.CommitMessage);
        Assert.True(panel.CanAmend);
    }

    [AvaloniaFact]
    public async Task A_stopped_cherry_pick_fills_in_no_message_and_offers_continue_instead_of_commit()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "a.txt", "feature\n", "Feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "a.txt", "main\n", "Main");
        Assert.False(repository.TryRunGit(out _, "cherry-pick", "feature"));
        using Fixture fixture = await Fixture.CreateAsync(repository);
        GitPanelViewModel panel = fixture.Panel;

        Assert.True(await fixture.Git.TakeSideAsync(repository.Path, ["a.txt"], GitConflictSide.Theirs, CancellationToken.None));
        await fixture.RefreshAsync();
        Assert.True(string.IsNullOrEmpty(panel.CommitMessage));
        panel.CommitMessage = "Typed";

        Assert.Equal(GitStrings.OperationCherryPicking + " · " + GitStrings.OperationReadyToContinue, panel.OperationSummary);
        Assert.Equal(1, panel.StagedCount);
        Assert.False(panel.IsCommitAllowed);
        Assert.False(panel.CanAmend);
        Assert.False(await panel.CommitCommand.CanExecute.FirstAsync());
        Assert.True(await panel.ContinueCommand.CanExecute.FirstAsync());
    }

    [AvaloniaFact]
    public async Task The_view_shows_the_banner_the_conflicts_and_the_hint_instead_of_a_diff()
    {
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        using Fixture fixture = await Fixture.CreateAsync(repository);
        GitPanelView view = new() { DataContext = fixture.Panel };
        Window window = new() { Content = view, Width = 1100, Height = 700 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.True(view.FindControl<Border>("GitOperationBanner")!.IsVisible);
            Assert.True(view.FindControl<DockPanel>("GitConflictPane")!.IsVisible);
            Assert.False(view.FindControl<TextBlock>("GitConflictHint")!.IsVisible);

            fixture.Panel.SelectedConflict = fixture.Panel.ConflictItems[0];
            Dispatcher.UIThread.RunJobs();

            Assert.True(view.FindControl<TextBlock>("GitConflictHint")!.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class Fixture : IDisposable
    {
        private Fixture(GitTestRepository repository)
        {
            Git = new GitService(options: repository.ProcessOptions);
            Session = new GitSession(GitServiceSet.FromService(Git), new TestWorkspaceInfo(repository.Path));
            Panel = new GitPanelViewModel(Session, (commandId, arguments) =>
            {
                Executed.Add((commandId, arguments));
                return Task.CompletedTask;
            });
        }

        public GitService Git { get; }

        public GitSession Session { get; }

        public GitPanelViewModel Panel { get; }

        public List<(string CommandId, IReadOnlyList<object?>? Arguments)> Executed { get; } = new();

        public static async Task<Fixture> CreateAsync(GitTestRepository repository)
        {
            Fixture fixture = new(repository);
            await fixture.RefreshAsync();
            await fixture.Panel.WhenAnyValue(x => x.IsOperationInProgress).FirstAsync(inProgress => inProgress).Timeout(WaitLimit);
            return fixture;
        }

        // The panel applies snapshots through the UI dispatcher.
        public async Task RefreshAsync()
        {
            await Panel.RefreshAsync().WaitAsync(WaitLimit);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Panel.Dispose();
            Session.Dispose();
        }
    }
}
