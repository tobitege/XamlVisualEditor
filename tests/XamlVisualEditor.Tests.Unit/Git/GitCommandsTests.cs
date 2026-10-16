using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
using FakeEditorServices = XamlVisualEditor.Tests.Unit.Tfvc.FakeEditorServices;
using GitExtensionEntry = XamlVisualEditor.GitExtension.GitExtension;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitCommandsTests
{
    private const string EXTENSION_ID = "git.test";
    private const string REPOSITORY_WHEN = "git.isRepository";

    public static TheoryData<string, string, string?> CommandMetadataCases => new()
    {
        { GitCommands.TOGGLE_CHANGES_VIEW, GitStrings.ToggleChangesViewCommand, null },
        { GitCommands.SHOW_CHANGES, GitStrings.ShowChangesCommand, null },
        { GitCommands.REFRESH, GitStrings.RefreshCommand, "git.available" },
        { GitCommands.SHOW_OUTPUT, GitStrings.ShowOutputCommand, null },
        { GitCommands.TOGGLE_AUTO_REFRESH, GitStrings.ToggleAutoRefreshCommand, null },
        { GitCommands.INIT, GitStrings.InitCommand, "hasWorkspace && git.available && !git.isRepository" },
        { GitCommands.STAGE, GitStrings.StageCommand, REPOSITORY_WHEN },
        { GitCommands.UNSTAGE, GitStrings.UnstageCommand, REPOSITORY_WHEN },
        { GitCommands.DISCARD, GitStrings.DiscardCommand, REPOSITORY_WHEN },
        { GitCommands.STAGE_ALL, GitStrings.StageAllCommand, REPOSITORY_WHEN },
        { GitCommands.UNSTAGE_ALL, GitStrings.UnstageAllCommand, REPOSITORY_WHEN },
        { GitCommands.COMMIT, GitStrings.CommitCommand, REPOSITORY_WHEN }
    };

    [Theory]
    [MemberData(nameof(CommandMetadataCases))]
    public async Task Command_is_registered_with_metadata_and_a_palette_item(string commandId, string title, string? when)
    {
        using Registration registration = await Registration.CreateAsync();

        Assert.Contains(commandId, await registration.Commands.GetCommandsAsync(CancellationToken.None));
        Assert.True(registration.Metadata.TryGet(commandId, out CommandMetadata metadata));
        Assert.Equal(title, metadata.Title);
        Assert.Equal("Git", metadata.Category);
        Assert.Equal(when, metadata.When);
        ExtensionCommandPaletteContribution paletteItem = Assert.Single(
            registration.Contributions.CommandPaletteItems,
            item => item.CommandId == commandId);
        Assert.Equal(title, paletteItem.Title);
        Assert.Equal("Git", paletteItem.Category);
    }

    [Fact]
    public async Task Every_registered_command_has_one_palette_item_and_show_has_its_keybinding()
    {
        using Registration registration = await Registration.CreateAsync();

        IReadOnlyList<string> commandIds = await registration.Commands.GetCommandsAsync(CancellationToken.None);
        Assert.Equal(12, commandIds.Count);
        Assert.Equal(
            commandIds.Order(StringComparer.Ordinal),
            registration.Contributions.CommandPaletteItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.True(registration.Metadata.TryGet(GitCommands.SHOW_CHANGES, out CommandMetadata show));
        Assert.Equal("Ctrl+Shift+G", show.Keybinding);
        Assert.Single(registration.Metadata.GetAll().Values, metadata => metadata.Keybinding is not null);
    }

    [Fact]
    public async Task Menu_entries_are_in_view_panels_the_extensions_git_submenu_and_the_editor_context_menu()
    {
        using Registration registration = await Registration.CreateAsync();
        IReadOnlyList<ExtensionMenuContribution> items = registration.Contributions.MenuItems;

        ExtensionMenuContribution view = Assert.Single(items, item => item.Location == ExtensionMenuLocations.View);
        Assert.Equal(GitCommands.TOGGLE_CHANGES_VIEW, view.CommandId);
        Assert.Equal("views.bottom", view.Group);
        Assert.Equal(GitStrings.ChangesViewTitle, view.Title);
        Assert.Null(view.Submenu);

        string[] extensionsIds =
        [
            GitCommands.SHOW_CHANGES, GitCommands.REFRESH, GitCommands.SHOW_OUTPUT, GitCommands.TOGGLE_AUTO_REFRESH,
            GitCommands.STAGE_ALL, GitCommands.UNSTAGE_ALL, GitCommands.COMMIT, GitCommands.INIT
        ];
        ExtensionMenuContribution[] extensionsItems = items.Where(item => item.Location == ExtensionMenuLocations.Extensions).ToArray();
        Assert.Equal(extensionsIds.Order(StringComparer.Ordinal), extensionsItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.All(extensionsItems, item => Assert.Equal("Git", item.Submenu));
        Assert.Equal("git.autoRefresh", Assert.Single(extensionsItems, item => item.CheckedWhen is not null).CheckedWhen);
        Assert.Equal(GitCommands.TOGGLE_AUTO_REFRESH, Assert.Single(extensionsItems, item => item.CheckedWhen is not null).CommandId);

        ExtensionMenuContribution[] editorItems = items.Where(item => item.Location == ExtensionMenuLocations.EditorContext).ToArray();
        Assert.Equal(
            new[] { GitCommands.DISCARD, GitCommands.STAGE, GitCommands.UNSTAGE },
            editorItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.All(editorItems, item => Assert.Equal("Git", item.Submenu));
        Assert.Equal(items.Count, 1 + extensionsItems.Length + editorItems.Length);
    }

    [Fact]
    public async Task View_commands_toggle_and_show_the_changes_panel()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(GitCommands.TOGGLE_CHANGES_VIEW, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.SHOW_CHANGES, null, CancellationToken.None);

        Assert.Equal([GitCommands.CHANGES_VIEW_ID], registration.ViewHost.Toggled);
        Assert.Equal([GitCommands.CHANGES_VIEW_ID], registration.ViewHost.Shown);
    }

    [Fact]
    public async Task Stage_uses_the_path_argument_and_falls_back_to_the_active_document()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(
            GitCommands.STAGE,
            [Path.Combine(registration.RepositoryRoot, "src", "a.cs")],
            CancellationToken.None);
        registration.Editor.Activate(new FakeEditorDocument(Path.Combine(registration.RepositoryRoot, "new.txt"), string.Empty));
        await registration.Commands.ExecuteAsync(GitCommands.STAGE, null, CancellationToken.None);

        Assert.Equal(
            new[] { ("stage", "src/a.cs"), ("stage", "new.txt") },
            registration.Git.Calls.Select(call => (call.Command, Assert.Single(call.Paths))));
    }

    [Fact]
    public async Task Stage_of_a_folder_stages_its_changed_files()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(GitCommands.STAGE, [Path.Combine(registration.RepositoryRoot, "src")], CancellationToken.None);

        (string command, string[] paths) = Assert.Single(registration.Git.Calls);
        Assert.Equal("stage", command);
        Assert.Equal(["src/a.cs"], paths);
    }

    [Fact]
    public async Task Path_commands_report_paths_without_changes_and_outside_the_repository()
    {
        using Registration registration = await Registration.CreateAsync();
        string outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N"), "x.txt");

        await registration.Commands.ExecuteAsync(GitCommands.UNSTAGE, [Path.Combine(registration.RepositoryRoot, "src", "a.cs")], CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.STAGE, [outside], CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.STAGE, null, CancellationToken.None);

        Assert.Empty(registration.Git.Calls);
        Assert.Equal(
            [
                string.Format(GitStrings.NoChangesToUnstageFormat, "src/a.cs"),
                string.Format(GitStrings.PathOutsideRepositoryFormat, outside, registration.RepositoryRoot),
                GitStrings.NoTargetPath
            ],
            registration.Window.Infos);
    }

    [Fact]
    public async Task Discard_asks_for_confirmation_and_never_discards_untracked_files()
    {
        bool confirm = false;
        using Registration registration = await Registration.CreateAsync((_, _) => confirm);
        string file = Path.Combine(registration.RepositoryRoot, "src", "a.cs");

        await registration.Commands.ExecuteAsync(GitCommands.DISCARD, [file], CancellationToken.None);
        Assert.Empty(registration.Git.Calls);
        ConfirmationDialogRequest request = Assert.IsType<ConfirmationDialogRequest>(Assert.Single(registration.Dialogs.Requests).ViewModel);
        Assert.Equal(string.Format(UiStrings.GitDiscardWorkingChangesPrompt, "src/a.cs"), request.Message);
        Assert.True(request.IsDestructive);

        confirm = true;
        await registration.Commands.ExecuteAsync(GitCommands.DISCARD, [file], CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.DISCARD, [Path.Combine(registration.RepositoryRoot, "new.txt")], CancellationToken.None);

        (string command, string[] paths) = Assert.Single(registration.Git.Calls);
        Assert.Equal("discard", command);
        Assert.Equal(["src/a.cs"], paths);
        Assert.Equal(2, registration.Dialogs.Requests.Count);
        Assert.Equal(string.Format(GitStrings.NoChangesToDiscardFormat, "new.txt"), Assert.Single(registration.Window.Infos));
    }

    [Fact]
    public async Task Stage_all_unstage_all_and_commit_work_on_the_whole_repository()
    {
        using Registration registration = await Registration.CreateAsync();
        registration.Window.InputBoxResult = "  Message  ";

        await registration.Commands.ExecuteAsync(GitCommands.STAGE_ALL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.UNSTAGE_ALL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.COMMIT, null, CancellationToken.None);

        Assert.Equal(
            new[] { "stage:new.txt,src/a.cs", "unstage:b.cs", "commit:" },
            registration.Git.Calls.Select(call => call.Command + ":" + string.Join(",", call.Paths.Order(StringComparer.Ordinal))));
        Assert.Equal("Message", registration.Git.CommitMessage);
    }

    [Fact]
    public async Task Commit_without_staged_changes_or_message_runs_nothing()
    {
        using Registration registration = await Registration.CreateAsync(changes:
        [
            new GitFileChange { Path = "src/a.cs", WorkTreeStatus = GitChangeKind.Modified }
        ]);

        await registration.Commands.ExecuteAsync(GitCommands.COMMIT, null, CancellationToken.None);
        Assert.Equal([GitStrings.NothingToCommit], registration.Window.Infos);

        using Registration staged = await Registration.CreateAsync();
        staged.Window.InputBoxResult = "   ";
        await staged.Commands.ExecuteAsync(GitCommands.COMMIT, null, CancellationToken.None);

        Assert.Empty(registration.Git.Calls);
        Assert.Empty(staged.Git.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sub")]
    public async Task Init_in_a_folder_of_an_existing_repository_runs_no_git_init_and_says_so(string subFolder)
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string folder = subFolder.Length == 0 ? repository.Path : Path.Combine(repository.Path, subFolder);
        Directory.CreateDirectory(folder);
        GitService git = new(options: repository.ProcessOptions);
        List<string> commands = [];
        git.CommandCompleted += (_, entry) => commands.Add(string.Join(" ", entry.Arguments));
        using GitSession session = new(GitServiceSet.FromService(git), new FakeWorkspaceInfo { WorkspacePath = folder });
        RecordingWindow window = new();
        GitCommandHandler handler = new(
            session,
            new RecordingExtensionViewHost(),
            window,
            new ScriptedDialogHost((_, _) => false),
            null,
            null,
            () => null,
            null);
        CommandRegistry registry = new();
        List<IDisposable> subscriptions = [];
        GitCommands.RegisterCommands(registry, new CommandMetadataRegistry(), handler, subscriptions);

        await registry.ExecuteAsync(GitCommands.INIT, null, CancellationToken.None);
        string repositoryRoot = Path.GetFullPath((await git.GetRepositoryRootAsync(repository.Path))!);

        Assert.DoesNotContain(commands, command => command.StartsWith("init", StringComparison.Ordinal));
        if (subFolder.Length > 0)
        {
            Assert.False(Directory.Exists(Path.Combine(folder, ".git")));
        }

        Assert.Equal(
            string.Format(GitStrings.InitAlreadyRepositoryFormat, folder, repositoryRoot),
            Assert.Single(window.Infos));
        Assert.Empty(window.Errors);
    }

    [Fact]
    public async Task Toggle_auto_refresh_switches_the_working_tree_watcher_and_the_context_key()
    {
        using Registration registration = await Registration.CreateAsync();
        ContextKeyRegistry contextKeys = new();

        await registration.Commands.ExecuteAsync(GitCommands.TOGGLE_AUTO_REFRESH, null, CancellationToken.None);
        GitExtensionEntry.UpdateContextKeys(contextKeys, registration.Session.Current);

        Assert.True(registration.Session.Current.IsWorkingTreeWatcherEnabled);
        Assert.True(contextKeys.TryGet(GitCommands.AUTO_REFRESH_CONTEXT_KEY, out bool autoRefresh) && autoRefresh);
        Assert.True(contextKeys.TryGet(GitCommands.IS_REPOSITORY_CONTEXT_KEY, out bool isRepository) && isRepository);
        Assert.True(contextKeys.TryGet(GitCommands.AVAILABLE_CONTEXT_KEY, out bool available) && available);
        Assert.True(contextKeys.TryGet(GitCommands.HAS_REMOTE_CONTEXT_KEY, out bool hasRemote) && !hasRemote);

        await registration.Commands.ExecuteAsync(GitCommands.TOGGLE_AUTO_REFRESH, null, CancellationToken.None);
        Assert.False(registration.Session.Current.IsWorkingTreeWatcherEnabled);
    }

    [Fact]
    public void Context_keys_follow_upstream_merge_and_conflicts()
    {
        ContextKeyRegistry contextKeys = new();
        GitSnapshot snapshot = new()
        {
            State = GitSessionState.Repository,
            RepositoryRoot = "repo",
            Status = new GitRepositoryStatus
            {
                RepositoryRoot = "repo",
                BranchName = "main",
                UpstreamName = "origin/main",
                Operation = GitRepositoryOperation.Merging,
                Changes = [new GitFileChange { Path = "a.cs", ConflictKind = GitConflictKind.BothModified }]
            },
            HasRemote = true
        };

        GitExtensionEntry.UpdateContextKeys(contextKeys, snapshot);

        foreach (string key in new[]
                 {
                     GitCommands.HAS_UPSTREAM_CONTEXT_KEY, GitCommands.IS_MERGING_CONTEXT_KEY,
                     GitCommands.HAS_CONFLICTS_CONTEXT_KEY, GitCommands.HAS_REMOTE_CONTEXT_KEY
                 })
        {
            Assert.True(contextKeys.TryGet(key, out bool value) && value, key);
        }

        GitExtensionEntry.UpdateContextKeys(contextKeys, new GitSnapshot { State = GitSessionState.GitUnavailable });
        Assert.True(contextKeys.TryGet(GitCommands.AVAILABLE_CONTEXT_KEY, out bool available) && !available);
        Assert.True(contextKeys.TryGet(GitCommands.IS_REPOSITORY_CONTEXT_KEY, out bool isRepository) && !isRepository);
    }

    [Fact]
    public async Task Disposing_the_subscriptions_removes_commands_metadata_and_contributions()
    {
        using Registration registration = await Registration.CreateAsync();

        foreach (IDisposable subscription in registration.Subscriptions)
        {
            subscription.Dispose();
        }

        Assert.Empty(await registration.Commands.GetCommandsAsync(CancellationToken.None));
        Assert.Empty(registration.Metadata.GetAll());
        Assert.Empty(registration.Contributions.MenuItems);
        Assert.Empty(registration.Contributions.CommandPaletteItems);
    }

    private sealed class Registration : IDisposable
    {
        private Registration(string repositoryRoot, GitFileChange[] changes, Func<string, object?, object?> respond)
        {
            RepositoryRoot = repositoryRoot;
            Git = new FakeGitService(repositoryRoot, changes);
            Dialogs = new ScriptedDialogHost(respond);
            Session = new GitSession(GitServiceSet.FromService(Git), new FakeWorkspaceInfo { WorkspacePath = repositoryRoot });
            GitCommandHandler handler = new(Session, ViewHost, Window, Dialogs, Editor, null, () => null, null);
            GitCommands.RegisterCommands(Commands, Metadata, handler, Subscriptions);
            GitCommands.RegisterMenuAndPaletteItems(EXTENSION_ID, Contributions, Subscriptions);
        }

        public string RepositoryRoot { get; }

        public FakeGitService Git { get; }

        public GitSession Session { get; }

        public ScriptedDialogHost Dialogs { get; }

        public CommandRegistry Commands { get; } = new();

        public CommandMetadataRegistry Metadata { get; } = new();

        public ExtensionContributionRegistry Contributions { get; } = new();

        public RecordingExtensionViewHost ViewHost { get; } = new();

        public RecordingWindow Window { get; } = new();

        public FakeEditorServices Editor { get; } = new();

        public List<IDisposable> Subscriptions { get; } = new();

        /// <summary>
        /// Creates a session over a repository that does not exist on disk, with an unstaged change in src/a.cs, a
        /// staged change in b.cs and the untracked file new.txt unless <paramref name="changes"/> are given.
        /// </summary>
        public static async Task<Registration> CreateAsync(
            Func<string, object?, object?>? respond = null,
            GitFileChange[]? changes = null)
        {
            string repositoryRoot = Path.Combine(Path.GetTempPath(), "xve-git-fake-" + Guid.NewGuid().ToString("N"));
            changes ??=
            [
                new GitFileChange { Path = "src/a.cs", WorkTreeStatus = GitChangeKind.Modified },
                new GitFileChange { Path = "b.cs", IndexStatus = GitChangeKind.Modified },
                new GitFileChange { Path = "new.txt", WorkTreeStatus = GitChangeKind.Untracked, IsUntracked = true }
            ];
            Registration registration = new(repositoryRoot, changes, respond ?? ((_, _) => false));
            await registration.Session.RefreshAsync();
            Assert.True(registration.Session.Current.IsRepository);
            return registration;
        }

        public void Dispose()
        {
            Session.Dispose();
        }
    }
}
