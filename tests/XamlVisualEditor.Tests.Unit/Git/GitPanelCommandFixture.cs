using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>
/// The Git commands and the Git Changes panel over a real repository: the panel runs the registered commands, and the
/// handler reads the panel, as in the editor. Dialogs are answered by a callback.
/// </summary>
internal sealed class GitPanelCommandFixture : IDisposable
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(10);

    private GitPanelCommandFixture(GitTestRepository repository, Func<string, object?, object?> respond)
    {
        Repository = repository;
        Dialogs = new GitDialogHost(respond);
        Session = new GitSession(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FakeWorkspaceInfo { WorkspacePath = repository.Path });
        Panel = new GitPanelViewModel(Session, (commandId, arguments) => Commands.ExecuteAsync(commandId, arguments, CancellationToken.None));
        GitCommandHandler handler = new(Session, ViewHost, Window, Dialogs, null, null, () => Panel, null);
        GitCommands.RegisterCommands(Commands, new CommandMetadataRegistry(), handler, new List<IDisposable>());
    }

    public GitTestRepository Repository { get; }

    public GitSession Session { get; }

    public GitPanelViewModel Panel { get; }

    public GitDialogHost Dialogs { get; }

    public CommandRegistry Commands { get; } = new();

    public RecordingWindow Window { get; } = new();

    public RecordingExtensionViewHost ViewHost { get; } = new();

    /// <summary>Creates the fixture over <paramref name="repository"/>, which it disposes, and shows the first status.</summary>
    public static async Task<GitPanelCommandFixture> CreateAsync(
        GitTestRepository repository,
        Func<string, object?, object?>? respond = null)
    {
        GitPanelCommandFixture fixture = new(repository, respond ?? ((_, _) => false));
        await fixture.Session.RefreshAsync();
        await fixture.Panel.WhenAnyValue(x => x.IsRepository).FirstAsync(isRepository => isRepository).Timeout(WaitLimit);
        return fixture;
    }

    /// <summary>Selects the staged or working tree entry of <paramref name="path"/> and returns its shown diff.</summary>
    public async Task<string> SelectAsync(string path, bool staged)
    {
        Task<string> shown = NextDiffAsync(text => text.Length > 0);
        IReadOnlyList<GitChangeEntryViewModel> items = staged ? Panel.StagedItems : Panel.UnstagedItems;
        GitChangeEntryViewModel entry = Assert.Single(items, item => item.Path == path);
        if (staged)
        {
            Panel.SelectedStagedChange = entry;
        }
        else
        {
            Panel.SelectedUnstagedChange = entry;
        }

        return await shown;
    }

    /// <summary>Returns the next shown diff text that matches <paramref name="predicate"/>, the current one included.</summary>
    public Task<string> NextDiffAsync(Func<string, bool> predicate) =>
        Panel.WhenAnyValue(x => x.DiffText).FirstAsync(predicate).Timeout(WaitLimit).ToTask();

    /// <summary>Returns the 0-based index of <paramref name="line"/> in the shown diff.</summary>
    public int LineOf(string line) => Array.IndexOf(Panel.DiffText.Split('\n'), line);

    public Task ExecuteAsync(string commandId, string? argument = null) =>
        Commands.ExecuteAsync(commandId, argument is null ? null : [argument], CancellationToken.None);

    public void Dispose()
    {
        Panel.Dispose();
        Session.Dispose();
        Repository.Dispose();
    }
}
