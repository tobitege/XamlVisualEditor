using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
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
    private const string REMOTE_WHEN = "git.hasRemote";
    private const string UPSTREAM_WHEN = "git.isRepository && git.hasUpstream";

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
        { GitCommands.COMMIT, GitStrings.CommitCommand, REPOSITORY_WHEN },
        { GitCommands.TOGGLE_REPOSITORY_VIEW, GitStrings.ToggleRepositoryViewCommand, null },
        { GitCommands.CHECKOUT, GitStrings.CheckoutCommand, REPOSITORY_WHEN },
        { GitCommands.CREATE_BRANCH, GitStrings.CreateBranchCommand, REPOSITORY_WHEN },
        { GitCommands.DELETE_BRANCH, GitStrings.DeleteBranchCommand, REPOSITORY_WHEN },
        { GitCommands.FETCH, GitStrings.FetchCommand, REMOTE_WHEN },
        { GitCommands.PULL, GitStrings.PullCommand, UPSTREAM_WHEN },
        { GitCommands.PUSH, GitStrings.PushCommand, REMOTE_WHEN },
        { GitCommands.SYNC, GitStrings.SyncCommand, UPSTREAM_WHEN },
        { GitCommands.TOGGLE_HISTORY_VIEW, GitStrings.ToggleHistoryViewCommand, null },
        { GitCommands.SHOW_HISTORY, GitStrings.ShowHistoryCommand, REPOSITORY_WHEN },
        { GitCommands.SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, REPOSITORY_WHEN },
        { GitCommands.TOGGLE_EXPLORER_DECORATIONS, GitStrings.ToggleExplorerDecorationsCommand, null },
        { GitCommands.TOGGLE_CHANGE_MARKERS, GitStrings.ToggleChangeMarkersCommand, null },
        { GitCommands.COMPARE_WITH_HEAD, GitStrings.CompareWithHeadCommand, REPOSITORY_WHEN },
        { GitCommands.COMPARE_WITH_INDEX, GitStrings.CompareWithIndexCommand, REPOSITORY_WHEN },
        { GitCommands.BLAME, GitStrings.BlameCommand, REPOSITORY_WHEN },
        { GitCommands.COMMIT_AMEND, GitStrings.CommitAmendCommand, REPOSITORY_WHEN },
        { GitCommands.STAGE_SELECTION, GitStrings.StageSelectionCommand, REPOSITORY_WHEN },
        { GitCommands.UNSTAGE_SELECTION, GitStrings.UnstageSelectionCommand, REPOSITORY_WHEN },
        { GitCommands.DISCARD_SELECTION, GitStrings.DiscardSelectionCommand, REPOSITORY_WHEN },
        { GitCommands.STASH, GitStrings.StashCommand, REPOSITORY_WHEN },
        { GitCommands.STASH_INCLUDE_UNTRACKED, GitStrings.StashIncludeUntrackedCommand, REPOSITORY_WHEN },
        { GitCommands.STASH_APPLY, GitStrings.StashApplyCommand, REPOSITORY_WHEN },
        { GitCommands.STASH_POP, GitStrings.StashPopCommand, REPOSITORY_WHEN },
        { GitCommands.STASH_DROP, GitStrings.StashDropCommand, REPOSITORY_WHEN }
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
        Assert.Equal(37, commandIds.Count);
        Assert.Equal(
            commandIds.Order(StringComparer.Ordinal),
            registration.Contributions.CommandPaletteItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.True(registration.Metadata.TryGet(GitCommands.SHOW_CHANGES, out CommandMetadata show));
        Assert.Equal("Ctrl+Shift+G", show.Keybinding);
        Assert.Single(registration.Metadata.GetAll().Values, metadata => metadata.Keybinding is not null);
    }

    [Fact]
    public async Task Menu_entries_are_in_view_panels_the_extensions_git_submenu_and_the_context_menus()
    {
        using Registration registration = await Registration.CreateAsync();
        IReadOnlyList<ExtensionMenuContribution> items = registration.Contributions.MenuItems;

        ExtensionMenuContribution[] viewItems = items.Where(item => item.Location == ExtensionMenuLocations.View).ToArray();
        Assert.Equal(3, viewItems.Length);
        ExtensionMenuContribution view = Assert.Single(viewItems, item => item.CommandId == GitCommands.TOGGLE_CHANGES_VIEW);
        Assert.Equal("views.bottom", view.Group);
        Assert.Equal(GitStrings.ChangesViewTitle, view.Title);
        Assert.Null(view.Submenu);
        ExtensionMenuContribution repositoryView = Assert.Single(viewItems, item => item.CommandId == GitCommands.TOGGLE_REPOSITORY_VIEW);
        Assert.Equal("views.left", repositoryView.Group);
        Assert.Equal(GitStrings.RepositoryViewTitle, repositoryView.Title);
        Assert.Null(repositoryView.Submenu);
        ExtensionMenuContribution historyView = Assert.Single(viewItems, item => item.CommandId == GitCommands.TOGGLE_HISTORY_VIEW);
        Assert.Equal("views.bottom", historyView.Group);
        Assert.Equal(GitStrings.HistoryViewTitle, historyView.Title);
        Assert.Null(historyView.Submenu);

        string[] extensionsIds =
        [
            GitCommands.SHOW_CHANGES, GitCommands.REFRESH, GitCommands.SHOW_OUTPUT, GitCommands.TOGGLE_AUTO_REFRESH,
            GitCommands.TOGGLE_EXPLORER_DECORATIONS, GitCommands.TOGGLE_CHANGE_MARKERS,
            GitCommands.STAGE_ALL, GitCommands.UNSTAGE_ALL, GitCommands.COMMIT, GitCommands.COMMIT_AMEND, GitCommands.INIT,
            GitCommands.CHECKOUT, GitCommands.CREATE_BRANCH, GitCommands.DELETE_BRANCH,
            GitCommands.FETCH, GitCommands.PULL, GitCommands.PUSH, GitCommands.SYNC, GitCommands.SHOW_HISTORY,
            GitCommands.STASH, GitCommands.STASH_INCLUDE_UNTRACKED, GitCommands.STASH_APPLY, GitCommands.STASH_POP,
            GitCommands.STASH_DROP
        ];
        ExtensionMenuContribution[] extensionsItems = items.Where(item => item.Location == ExtensionMenuLocations.Extensions).ToArray();
        Assert.Equal(extensionsIds.Order(StringComparer.Ordinal), extensionsItems.Select(item => item.CommandId).Order(StringComparer.Ordinal));
        Assert.All(extensionsItems, item => Assert.Equal("Git", item.Submenu));
        Assert.Equal(
            new[]
            {
                (GitCommands.TOGGLE_AUTO_REFRESH, "git.autoRefresh"),
                (GitCommands.TOGGLE_EXPLORER_DECORATIONS, "git.explorerDecorations"),
                (GitCommands.TOGGLE_CHANGE_MARKERS, "git.changeMarkers")
            },
            extensionsItems.Where(item => item.CheckedWhen is not null).Select(item => (item.CommandId, item.CheckedWhen!)));

        string[] fileIds =
        [
            GitCommands.BLAME, GitCommands.COMPARE_WITH_HEAD, GitCommands.COMPARE_WITH_INDEX, GitCommands.DISCARD,
            GitCommands.SHOW_FILE_HISTORY, GitCommands.STAGE, GitCommands.UNSTAGE
        ];
        string[] tabIds =
        [
            GitCommands.BLAME, GitCommands.COMPARE_WITH_HEAD, GitCommands.COMPARE_WITH_INDEX,
            GitCommands.SHOW_FILE_HISTORY, GitCommands.STAGE, GitCommands.UNSTAGE
        ];
        ExtensionMenuContribution[] contextItems = items
            .Where(item => item.Location is ExtensionMenuLocations.EditorContext or ExtensionMenuLocations.ExplorerContext
                or ExtensionMenuLocations.DocumentTabContext)
            .ToArray();
        Assert.Equal(fileIds, ContextIds(ExtensionMenuLocations.EditorContext));
        Assert.Equal(fileIds, ContextIds(ExtensionMenuLocations.ExplorerContext));
        Assert.Equal(tabIds, ContextIds(ExtensionMenuLocations.DocumentTabContext));
        Assert.All(contextItems, item => Assert.Equal("Git", item.Submenu));
        Assert.Equal(items.Count, viewItems.Length + extensionsItems.Length + contextItems.Length);

        IEnumerable<string> ContextIds(string location) => contextItems
            .Where(item => item.Location == location)
            .Select(item => item.CommandId)
            .Order(StringComparer.Ordinal);
    }

    [Fact]
    public async Task Compare_and_blame_open_one_document_per_file_and_version_and_refuse_folders()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            arrange: repository => repository.CommitFileIn(repository.Path, "src/b.txt", "b\n", "add src"));
        string file = Path.Combine(registration.Repository.Path, "a.txt");

        await registration.ExecuteAsync(GitCommands.COMPARE_WITH_INDEX, file);
        await registration.Commands.ExecuteAsync(GitCommands.COMPARE_WITH_INDEX, [file, 1], CancellationToken.None);
        await registration.ExecuteAsync(GitCommands.COMPARE_WITH_HEAD, file);
        await registration.ExecuteAsync(GitCommands.BLAME, file);
        await registration.ExecuteAsync(GitCommands.BLAME, Path.Combine(registration.Repository.Path, "src"));

        Assert.Equal(
            [
                ("git.compare.index:" + file, $"a.txt ({GitStrings.IndexName})", file),
                ("git.compare.index:" + file, $"a.txt ({GitStrings.IndexName})", file),
                ("git.compare.head:" + file, "a.txt (HEAD)", file),
                ("git.blame:" + file, string.Format(System.Globalization.CultureInfo.CurrentCulture, GitStrings.BlameTitleFormat, "a.txt"), file)
            ],
            registration.ViewHost.ShownDocuments);
        Assert.Equal(3, registration.ViewHost.DocumentViewModels.Count);
        Assert.Equal(GitFileRevision.Index, Assert.IsType<GitCompareDocumentViewModel>(
            registration.ViewHost.DocumentViewModels["git.compare.index:" + file]).Revision);
        Assert.Equal("a.txt", Assert.IsType<GitBlameDocumentViewModel>(
            registration.ViewHost.DocumentViewModels["git.blame:" + file]).RepositoryPath);
        Assert.Equal([GitStrings.FileTargetRequired], registration.Window.Infos);
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
    public async Task Stage_of_a_folder_stages_git_paths_of_its_changes_and_the_folder()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(GitCommands.STAGE, [Path.Combine(registration.RepositoryRoot, "src")], CancellationToken.None);

        (string command, string[] paths) = Assert.Single(registration.Git.Calls);
        Assert.Equal("stage", command);
        Assert.Equal(["src/a.cs", "src"], paths);
    }

    [Fact]
    public async Task Stage_with_a_path_that_differs_in_case_stages_the_changes_git_knows()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.CommitFileIn(repository.Path, "src/b.cs", "b\n", "add src");
            Directory.Move(Path.Combine(repository.Path, "src"), Path.Combine(repository.Path, "src-moving"));
            Directory.Move(Path.Combine(repository.Path, "src-moving"), Path.Combine(repository.Path, "Src"));
        });
        string root = registration.Repository.Path;
        // On a case-sensitive file system git sees Src as another folder; the case of git's paths cannot differ there.
        Assert.SkipUnless(File.Exists(Path.Combine(root, "src", "b.cs")), "The file system is case-sensitive.");
        File.WriteAllText(Path.Combine(root, "Src", "b.cs"), "changed\n");
        File.WriteAllText(Path.Combine(root, "Src", "new.cs"), "new\n");
        await registration.Session.RefreshAsync();

        await registration.ExecuteAsync(GitCommands.STAGE, Path.Combine(root, "Src", "b.cs"));
        Assert.Equal(
            ["src/b.cs"],
            registration.Session.Current.Changes.Where(GitCommandTarget.HasStagedChange).Select(change => change.Path));

        registration.Repository.RunGit("reset", "-q");
        await registration.Session.RefreshAsync();
        await registration.ExecuteAsync(GitCommands.STAGE, Path.Combine(root, "Src"));

        // Git may record the new file with the case of the folder in the index or on disk.
        Assert.Equal(
            ["src/b.cs", "src/new.cs"],
            registration.Session.Current.Changes
                .Where(GitCommandTarget.HasStagedChange)
                .Select(change => change.Path)
                .Order(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(registration.Session.Current.Changes, GitCommandTarget.HasWorkingTreeChange);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Unstage_of_a_rename_also_unstages_the_old_path_and_conflicts_are_never_unstaged()
    {
        using Registration registration = await Registration.CreateAsync(changes:
        [
            new GitFileChange { Path = "new.cs", OldPath = "old.cs", IndexStatus = GitChangeKind.Renamed, IsRenamed = true },
            new GitFileChange
            {
                Path = "conflict.cs",
                IndexStatus = GitChangeKind.Unmerged,
                WorkTreeStatus = GitChangeKind.Unmerged,
                ConflictKind = GitConflictKind.BothModified
            }
        ]);

        await registration.Commands.ExecuteAsync(GitCommands.UNSTAGE, [Path.Combine(registration.RepositoryRoot, "new.cs")], CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.UNSTAGE_ALL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.UNSTAGE, [Path.Combine(registration.RepositoryRoot, "conflict.cs")], CancellationToken.None);

        Assert.Equal(
            new[] { "unstage:new.cs,old.cs", "unstage:new.cs,old.cs" },
            registration.Git.Calls.Select(call => call.Command + ":" + string.Join(",", call.Paths)));
        Assert.Equal([string.Format(GitStrings.NoChangesToUnstageFormat, "conflict.cs")], registration.Window.Infos);
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
            new[] { "stage:.", "unstage:b.cs", "commit:" },
            registration.Git.Calls.Select(call => call.Command + ":" + string.Join(",", call.Paths.Order(StringComparer.Ordinal))));
        Assert.Equal("Message", registration.Git.CommitMessage);
        ProgressDialogRequest progress = Assert.Single(registration.Dialogs.Progress);
        Assert.Equal(GitStrings.CommitInputTitle, progress.Title);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Cancelling_the_commit_progress_dialog_cancels_the_commit()
    {
        using Registration registration = await Registration.CreateAsync();
        registration.Git.BlockCommit = true;
        registration.Dialogs.ProgressMode = ProgressDialogMode.Cancel;
        registration.Window.InputBoxResult = "Message";

        await registration.Commands.ExecuteAsync(GitCommands.COMMIT, null, CancellationToken.None);

        Assert.Equal("commit", Assert.Single(registration.Git.Calls).Command);
        Assert.Null(registration.Git.CommitMessage);
        Assert.Equal(string.Format(GitStrings.OperationCancelledFormat, GitStrings.CommitInputTitle), Assert.Single(registration.Window.Infos));
        Assert.Empty(registration.Window.Errors);
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

    [Fact]
    public async Task History_view_command_toggles_the_history_panel()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(GitCommands.TOGGLE_HISTORY_VIEW, null, CancellationToken.None);

        Assert.Equal([GitCommands.HISTORY_VIEW_ID], registration.ViewHost.Toggled);
    }

    [Fact]
    public async Task Show_history_shows_the_whole_repository_starting_at_head_or_at_the_revision_argument()
    {
        using Registration registration = await Registration.CreateAsync();
        registration.History.Show(new GitHistoryScope("src/a.cs"));

        await registration.Commands.ExecuteAsync(GitCommands.SHOW_HISTORY, null, CancellationToken.None);
        GitHistoryScope fromHead = registration.History.Scope;
        await registration.Commands.ExecuteAsync(GitCommands.SHOW_HISTORY, ["refs/tags/v1.0"], CancellationToken.None);

        Assert.Equal(GitHistoryScope.Repository, fromHead);
        Assert.Equal(new GitHistoryScope(Revision: "refs/tags/v1.0"), registration.History.Scope);
        Assert.Equal(string.Format(GitStrings.HistoryScopeRevisionFormat, GitStrings.HistoryScopeRepository, "v1.0"), registration.History.ScopeText);
        Assert.Equal([GitCommands.HISTORY_VIEW_ID, GitCommands.HISTORY_VIEW_ID], registration.ViewHost.Shown);
    }

    [Fact]
    public async Task Show_file_history_uses_the_path_argument_or_the_active_document_and_tells_folders_from_files()
    {
        using Registration registration = await Registration.CreateAsync();
        Directory.CreateDirectory(Path.Combine(registration.RepositoryRoot, "src"));
        try
        {
            await registration.Commands.ExecuteAsync(
                GitCommands.SHOW_FILE_HISTORY,
                [Path.Combine(registration.RepositoryRoot, "src")],
                CancellationToken.None);
            GitHistoryScope folder = registration.History.Scope;
            string folderText = registration.History.ScopeText;
            registration.Editor.Activate(new FakeEditorDocument(Path.Combine(registration.RepositoryRoot, "src", "deleted.cs"), string.Empty));
            await registration.Commands.ExecuteAsync(GitCommands.SHOW_FILE_HISTORY, null, CancellationToken.None);
            GitHistoryScope file = registration.History.Scope;
            await registration.Commands.ExecuteAsync(GitCommands.SHOW_FILE_HISTORY, [registration.RepositoryRoot], CancellationToken.None);

            Assert.Equal(new GitHistoryScope("src", IsDirectory: true), folder);
            Assert.Equal(string.Format(GitStrings.HistoryScopeFolderFormat, "src"), folderText);
            Assert.Equal(new GitHistoryScope("src/deleted.cs"), file);
            Assert.Equal(GitHistoryScope.Repository, registration.History.Scope);
            Assert.Equal(3, registration.ViewHost.Shown.Count);
        }
        finally
        {
            Directory.Delete(registration.RepositoryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Show_file_history_starts_at_the_revision_argument_and_sets_the_scope_before_the_panel_shows()
    {
        using Registration registration = await Registration.CreateAsync();
        List<GitHistoryScope> scopesWhenShown = new();
        registration.ViewHost.Showing = _ => scopesWhenShown.Add(registration.History.Scope);
        string sha = new('a', 40);

        await registration.Commands.ExecuteAsync(
            GitCommands.SHOW_FILE_HISTORY,
            [Path.Combine(registration.RepositoryRoot, "src", "a.cs"), sha],
            CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.SHOW_HISTORY, ["refs/tags/v1.0"], CancellationToken.None);

        Assert.Equal(
            new[] { new GitHistoryScope("src/a.cs", Revision: sha), new GitHistoryScope(Revision: "refs/tags/v1.0") },
            scopesWhenShown);
    }

    [Fact]
    public async Task Show_file_history_outside_the_repository_reports_it_and_shows_no_panel()
    {
        using Registration registration = await Registration.CreateAsync();
        string outside = Path.Combine(Path.GetTempPath(), "xve-outside-" + Guid.NewGuid().ToString("N"), "x.cs");

        await registration.Commands.ExecuteAsync(GitCommands.SHOW_FILE_HISTORY, [outside], CancellationToken.None);

        Assert.Empty(registration.ViewHost.Shown);
        Assert.Equal(GitHistoryScope.Repository, registration.History.Scope);
        Assert.Single(registration.Window.Infos);
    }

    [Fact]
    public async Task Repository_view_command_toggles_the_repository_panel()
    {
        using Registration registration = await Registration.CreateAsync();

        await registration.Commands.ExecuteAsync(GitCommands.TOGGLE_REPOSITORY_VIEW, null, CancellationToken.None);

        Assert.Equal([GitCommands.REPOSITORY_VIEW_ID], registration.ViewHost.Toggled);
    }

    [Fact]
    public async Task When_clauses_enable_branch_commands_in_a_repository_and_sync_commands_with_remote_and_upstream()
    {
        using Registration registration = await Registration.CreateAsync();
        GitRepositoryStatus withoutUpstream = new() { RepositoryRoot = "repo", BranchName = "main", Changes = [] };
        GitRepositoryStatus withUpstream = new() { RepositoryRoot = "repo", BranchName = "main", UpstreamName = "origin/main", Changes = [] };
        (GitSnapshot Snapshot, bool[] Enabled)[] cases =
        [
            (new GitSnapshot { State = GitSessionState.NotRepository }, [false, false, false, false, false, false]),
            (new GitSnapshot { State = GitSessionState.Repository, RepositoryRoot = "repo", Status = withoutUpstream }, [true, true, false, false, false, false]),
            (new GitSnapshot { State = GitSessionState.Repository, RepositoryRoot = "repo", Status = withoutUpstream, HasRemote = true }, [true, true, true, true, false, false]),
            (new GitSnapshot { State = GitSessionState.Repository, RepositoryRoot = "repo", Status = withUpstream, HasRemote = true }, [true, true, true, true, true, true])
        ];
        string[] commandIds = [GitCommands.CHECKOUT, GitCommands.DELETE_BRANCH, GitCommands.FETCH, GitCommands.PUSH, GitCommands.PULL, GitCommands.SYNC];

        foreach ((GitSnapshot snapshot, bool[] enabled) in cases)
        {
            ContextKeyRegistry contextKeys = new();
            GitExtensionEntry.UpdateContextKeys(contextKeys, snapshot);
            for (int i = 0; i < commandIds.Length; i++)
            {
                Assert.True(registration.Metadata.TryGet(commandIds[i], out CommandMetadata metadata));
                Assert.True(enabled[i] == EvaluateWhen(metadata.When, contextKeys), commandIds[i] + " in " + snapshot.State + ", remote " + snapshot.HasRemote);
            }
        }
    }

    [Fact]
    public async Task Checkout_picks_from_the_branches_with_the_current_one_marked_and_switches()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            arrange: repository => repository.RunGit("branch", "feature"));
        registration.Window.QuickPick = items => items.Single(item => item.Label == "feature");

        await registration.ExecuteAsync(GitCommands.CHECKOUT);

        (IReadOnlyList<QuickPickItem> items, QuickPickOptions options) = Assert.Single(registration.Window.QuickPicks);
        Assert.Equal(GitStrings.CheckoutPickTitle, options.Title);
        Assert.Equal(new[] { "feature", "main" }, items.Select(item => item.Label));
        Assert.Equal(GitStrings.CurrentBranchDescription, items[1].Description);
        Assert.Null(items[0].Description);
        Assert.Equal("init", items[0].Detail);
        Assert.Equal("feature", registration.CurrentBranch);
        Assert.Equal("feature", registration.Session.Current.BranchName);

        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/heads/feature");
        Assert.Equal(string.Format(GitStrings.AlreadyOnBranchFormat, "feature"), Assert.Single(registration.Window.Infos));
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_creates_a_tracking_branch_or_switches_to_the_local_one()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.AddBareRemote();
            repository.RunGit("push", "-q", "-u", "origin", "main");
            string clone = repository.CloneRemote("clone");
            repository.RunGitIn(clone, "switch", "-q", "-c", "topic");
            repository.CommitFileIn(clone, "t.txt", "t\n", "topic");
            repository.RunGitIn(clone, "push", "-q", "origin", "topic");
            repository.RunGit("fetch", "-q", "origin");
        });

        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");
        Assert.Equal("topic", registration.CurrentBranch);
        Assert.Equal("origin/topic", registration.Session.Current.UpstreamName);

        await registration.ExecuteAsync(GitCommands.CHECKOUT, "origin/main");
        Assert.Equal("main", registration.CurrentBranch);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Create_branch_asks_for_the_name_and_whether_to_switch()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync();

        registration.Window.InputBoxResult = " topic ";
        registration.Window.QuickPick = items => items.Single(item => item.Label == GitStrings.CreateOnly);
        await registration.ExecuteAsync(GitCommands.CREATE_BRANCH);
        Assert.Equal("main", registration.CurrentBranch);
        Assert.Contains("topic", registration.LocalBranches);
        Assert.Equal(string.Format(GitStrings.CreateBranchModeTitleFormat, "topic"), registration.Window.QuickPicks[0].Options.Title);

        registration.Window.InputBoxResult = "from-topic";
        registration.Window.QuickPick = items => items.Single(item => item.Label == GitStrings.CreateAndSwitch);
        await registration.ExecuteAsync(GitCommands.CREATE_BRANCH, "refs/heads/topic");
        Assert.Equal("from-topic", registration.CurrentBranch);

        registration.Window.InputBoxResult = "never";
        registration.Window.QuickPick = _ => null;
        await registration.ExecuteAsync(GitCommands.CREATE_BRANCH);
        Assert.DoesNotContain("never", registration.LocalBranches);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Delete_branch_refuses_the_current_branch_and_asks_twice_for_an_unmerged_one()
    {
        Queue<bool> answers = new([true, false, true, true]);
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            (_, _) => answers.Dequeue(),
            repository =>
            {
                repository.RunGit("switch", "-q", "-c", "unmerged");
                repository.CommitFileIn(repository.Path, "u.txt", "u\n", "only here");
                repository.RunGit("switch", "-q", "main");
            });

        await registration.ExecuteAsync(GitCommands.DELETE_BRANCH, "refs/heads/main");
        Assert.Equal(string.Format(GitStrings.CannotDeleteCurrentFormat, "main"), Assert.Single(registration.Window.Infos));
        Assert.Empty(registration.Dialogs.Requests);

        await registration.ExecuteAsync(GitCommands.DELETE_BRANCH, "unmerged");
        Assert.Contains("unmerged", registration.LocalBranches);
        ConfirmationDialogRequest[] confirmations = registration.Dialogs.Confirmations.ToArray();
        Assert.Equal(2, confirmations.Length);
        Assert.Equal(string.Format(GitStrings.DeleteBranchPromptFormat, "unmerged"), confirmations[0].Message);
        Assert.Equal(string.Format(GitStrings.DeleteUnmergedPromptFormat, "unmerged"), confirmations[1].Message);
        Assert.All(confirmations, confirmation => Assert.True(confirmation.IsDestructive));

        await registration.ExecuteAsync(GitCommands.DELETE_BRANCH, "unmerged");
        Assert.DoesNotContain("unmerged", registration.LocalBranches);
        Assert.Equal(string.Format(GitStrings.BranchDeletedFormat, "unmerged"), registration.Window.Infos[^1]);
        Assert.Empty(answers);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Delete_branch_without_argument_offers_only_the_other_local_branches()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            (_, _) => true,
            repository =>
            {
                repository.AddBareRemote();
                repository.RunGit("push", "-q", "-u", "origin", "main");
                repository.RunGit("branch", "merged");
            });
        registration.Window.QuickPick = items => items[0];

        await registration.ExecuteAsync(GitCommands.DELETE_BRANCH);

        Assert.Equal(new[] { "merged" }, Assert.Single(registration.Window.QuickPicks).Items.Select(item => item.Label));
        Assert.Equal(new[] { "main" }, registration.LocalBranches);
    }

    [Fact]
    public async Task Push_sets_the_upstream_after_a_confirmation_and_fetch_pull_and_sync_follow_the_remote()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            (_, _) => true,
            repository => repository.AddBareRemote());
        GitTestRepository repository = registration.Repository;

        await registration.ExecuteAsync(GitCommands.PUSH);
        ConfirmationDialogRequest confirmation = Assert.Single(registration.Dialogs.Confirmations);
        Assert.Equal(string.Format(GitStrings.PushSetUpstreamPromptFormat, "main", "origin"), confirmation.Message);
        Assert.False(confirmation.IsDestructive);
        ProgressDialogRequest progress = Assert.Single(registration.Dialogs.Progress);
        Assert.Equal(GitStrings.PushCommand, progress.Title);
        Assert.Equal(string.Format(GitStrings.PushSucceededFormat, "main", "origin/main"), registration.Window.Infos[^1]);
        Assert.Equal("origin/main", registration.Session.Current.UpstreamName);

        string clone = repository.CloneRemote("clone");
        repository.CommitFileIn(clone, "c.txt", "c\n", "from clone");
        repository.RunGitIn(clone, "push", "-q");
        await registration.ExecuteAsync(GitCommands.FETCH);
        Assert.Equal(GitStrings.FetchSucceeded, registration.Window.Infos[^1]);
        Assert.Equal(1, registration.Session.Current.BehindBy);

        await registration.ExecuteAsync(GitCommands.PULL);
        Assert.Equal(string.Format(GitStrings.PullFastForwardOneFormat, "main", "origin/main"), registration.Window.Infos[^1]);
        Assert.Equal(0, registration.Session.Current.BehindBy);

        await registration.ExecuteAsync(GitCommands.PULL);
        Assert.Equal(string.Format(GitStrings.PullUpToDateFormat, "main", "origin/main"), registration.Window.Infos[^1]);

        repository.CommitFileIn(repository.Path, "l.txt", "l\n", "local");
        await registration.ExecuteAsync(GitCommands.SYNC);
        Assert.Equal(string.Format(GitStrings.SyncSucceededFormat, "main", "origin/main"), registration.Window.Infos[^1]);
        Assert.Equal(repository.RunGit("rev-parse", "HEAD").Trim(), repository.RunGitIn(repository.RemotePath!, "rev-parse", "main").Trim());
        Assert.Equal(5, registration.Dialogs.Progress.Count());
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Remote_failures_show_the_classified_hint_and_the_decisive_git_line_with_masked_credentials()
    {
        ScriptedRemoteService remotes = new()
        {
            Respond = operation => operation switch
            {
                "fetch" => GitRemoteResult.Failed(
                    GitRemoteFailureKind.Authentication,
                    "remote: Invalid credentials\nfatal: Authentication failed for 'https://user:secret@example.com/r.git/'"),
                "pull" => GitRemoteResult.Failed(
                    GitRemoteFailureKind.Diverged,
                    "hint: Diverging branches can't be fast-forwarded, you need to either:\nhint:\nhint: \tgit merge --no-ff\n"
                    + "fatal: Not possible to fast-forward, aborting."),
                _ => GitRemoteResult.Failed(
                    GitRemoteFailureKind.Rejected,
                    "To https://example.com/r.git\n ! [rejected]        main -> main (non-fast-forward)\n"
                    + "error: failed to push some refs to 'https://example.com/r.git'\nhint: Updates were rejected because the tip of your current branch is behind")
            }
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");

        await registration.Commands.ExecuteAsync(GitCommands.FETCH, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.PUSH, null, CancellationToken.None);

        Assert.Equal(
            [
                string.Format(GitStrings.RemoteFailureFormat, GitStrings.AuthenticationFailedHint, "fatal: Authentication failed for 'https://***@example.com/r.git/'"),
                string.Format(GitStrings.RemoteFailureFormat, GitStrings.PullDivergedHint, "fatal: Not possible to fast-forward, aborting."),
                string.Format(GitStrings.RemoteFailureFormat, GitStrings.PushRejectedHint, "! [rejected]        main -> main (non-fast-forward)")
            ],
            registration.Window.Errors);
        Assert.Empty(registration.Window.Infos);
        Assert.Equal(["fetch", "pull", "push"], remotes.Calls);
    }

    [Fact]
    public async Task Failure_messages_point_to_the_git_output_channel_when_git_wrote_output()
    {
        const string timeout = "git fetch did not finish within 00:10:00.";
        ScriptedRemoteService remotes = new()
        {
            Respond = operation => operation == "pull"
                ? GitRemoteResult.Failed(GitRemoteFailureKind.Timeout, timeout)
                : GitRemoteResult.Failed(GitRemoteFailureKind.Other, "hint: try again\nfatal: something broke")
        };
        using Registration registration = await Registration.CreateAsync(
            remotes: remotes,
            upstream: "origin/main",
            output: new InMemoryWindow().CreateOutputChannel("Git"));
        registration.Git.StageError = "error: open(\"x\"): Permission denied\nfatal: adding files failed";

        await registration.Commands.ExecuteAsync(GitCommands.FETCH, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.STAGE, [Path.Combine(registration.RepositoryRoot, "new.txt")], CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);

        string suffix = Environment.NewLine + Environment.NewLine + GitStrings.SeeGitOutput;
        Assert.Equal(
            [
                string.Format(UiStrings.GitCommandFailed, "fatal: something broke") + suffix,
                string.Format(UiStrings.GitCommandFailed, "fatal: adding files failed") + suffix,
                string.Format(GitStrings.RemoteFailureFormat, GitStrings.TimeoutHint, timeout)
            ],
            registration.Window.Errors);
    }

    [Fact]
    public async Task A_pull_that_would_overwrite_local_changes_names_the_files()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.AddBareRemote();
            repository.RunGit("push", "-q", "-u", "origin", "main");
            string clone = repository.CloneRemote("clone");
            repository.CommitFileIn(clone, "a.txt", "remote\n", "remote change");
            repository.RunGitIn(clone, "push", "-q");
            repository.WriteFile("a.txt", "local\n");
        });

        await registration.ExecuteAsync(GitCommands.PULL);

        Assert.Equal(
            [
                string.Format(
                    UiStrings.GitCommandFailed,
                    "error: Your local changes to the following files would be overwritten by merge:" + Environment.NewLine + "    a.txt")
            ],
            registration.Window.Errors);
        Assert.Equal("local\n", File.ReadAllText(Path.Combine(registration.Repository.Path, "a.txt")));
    }

    [Fact]
    public async Task Pull_and_sync_run_although_the_status_shows_the_upstream_as_gone_because_it_can_be_stale()
    {
        ScriptedRemoteService remotes = new()
        {
            Respond = operation => operation == "pull"
                ? new GitRemoteResult { Message = "Fast-forward", PulledCommitCount = 2 }
                : GitRemoteResult.Succeeded(string.Empty)
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/feature", upstreamGone: true);

        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.SYNC, null, CancellationToken.None);

        Assert.Equal(["pull", "pull", "push"], remotes.Calls);
        Assert.Equal(
            [
                string.Format(GitStrings.PullFastForwardManyFormat, "main", "origin/feature", 2),
                string.Format(GitStrings.SyncSucceededFormat, "main", "origin/feature")
            ],
            registration.Window.Infos);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task A_pull_whose_upstream_is_gone_after_its_fetch_explains_it()
    {
        ScriptedRemoteService remotes = new()
        {
            Respond = _ => GitRemoteResult.Failed(
                GitRemoteFailureKind.UpstreamGone,
                "Your configuration specifies to merge with the ref 'refs/heads/feature' from the remote, "
                    + "but no such ref was fetched.")
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/feature");

        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);

        Assert.Equal(
            [
                string.Format(
                    GitStrings.RemoteFailureFormat,
                    GitStrings.UpstreamGoneHint,
                    "Your configuration specifies to merge with the ref 'refs/heads/feature' from the remote, "
                        + "but no such ref was fetched.")
            ],
            registration.Window.Errors);
    }

    [Fact]
    public async Task Pull_and_sync_of_a_branch_deleted_on_the_remote_explain_it_and_do_not_push_it_again()
    {
        // Default configuration: no fetch prunes, so origin/feature stays and the status shows no gone upstream.
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.AddBareRemote();
            repository.RunGit("push", "-q", "-u", "origin", "main");
            repository.RunGit("push", "-q", "origin", "main:feature");
            repository.RunGit("fetch", "-q", "origin");
            repository.RunGit("switch", "-q", "--track", "origin/feature");
            repository.RunGitIn(repository.RemotePath!, "branch", "-D", "feature");
        });
        GitTestRepository repository = registration.Repository;
        Assert.True(registration.Session.Current.HasUpstream);
        Assert.False(registration.Session.Current.IsUpstreamGone);
        string expected = string.Format(
            GitStrings.RemoteFailureFormat,
            GitStrings.UpstreamGoneHint,
            "Your configuration specifies to merge with the ref 'refs/heads/feature' from the remote, "
                + "but no such ref was fetched.");

        await registration.ExecuteAsync(GitCommands.PULL);
        await registration.ExecuteAsync(GitCommands.SYNC);

        Assert.Equal([expected, expected], registration.Window.Errors);
        Assert.Empty(registration.Window.Infos);
        Assert.Equal(string.Empty, repository.RunGit("ls-remote", "origin", "refs/heads/feature").Trim());
    }

    [Fact]
    public async Task Pull_and_sync_with_a_second_merge_value_do_not_push_a_first_one_deleted_on_the_remote()
    {
        // The fetch still marks refs/heads/main for merge; the upstream is the first value, refs/heads/feature.
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.AddBareRemote();
            repository.RunGit("push", "-q", "-u", "origin", "main");
            repository.RunGit("push", "-q", "origin", "main:feature");
            repository.RunGit("fetch", "-q", "origin");
            repository.RunGit("switch", "-q", "--track", "origin/feature");
            repository.RunGit("config", "--add", "branch.feature.merge", "refs/heads/main");
            repository.RunGitIn(repository.RemotePath!, "branch", "-D", "feature");
        });
        GitTestRepository repository = registration.Repository;
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        string expected = string.Format(
            GitStrings.RemoteFailureFormat,
            GitStrings.UpstreamGoneHint,
            "Your configuration specifies to merge with the ref 'refs/heads/feature' from the remote, "
                + "but no such ref was fetched.");

        await registration.ExecuteAsync(GitCommands.PULL);
        await registration.ExecuteAsync(GitCommands.SYNC);

        Assert.Equal([expected, expected], registration.Window.Errors);
        Assert.Empty(registration.Window.Infos);
        Assert.Equal(string.Empty, repository.RunGit("ls-remote", "origin", "refs/heads/feature").Trim());
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task A_pull_whose_commits_could_not_be_counted_reports_success_without_a_number()
    {
        ScriptedRemoteService remotes = new()
        {
            Respond = _ => new GitRemoteResult { Message = "Updating 1a2b3c4..5d6e7f8\nFast-forward", PulledCommitCount = null }
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");

        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);

        Assert.Equal([string.Format(GitStrings.PullSucceededFormat, "main", "origin/main")], registration.Window.Infos);
    }

    [Fact]
    public async Task Cancelling_the_progress_dialog_cancels_the_operation()
    {
        ScriptedRemoteService remotes = new() { Block = true };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");
        registration.Dialogs.ProgressMode = ProgressDialogMode.Cancel;

        await registration.Commands.ExecuteAsync(GitCommands.FETCH, null, CancellationToken.None);

        Assert.True(remotes.WasCancelled);
        Assert.Equal(string.Format(GitStrings.OperationCancelledFormat, GitStrings.FetchCommand), Assert.Single(registration.Window.Infos));
        Assert.Empty(registration.Window.Errors);
    }

    [Theory]
    [InlineData(ProgressDialogMode.CompleteAtOnce)]
    [InlineData(ProgressDialogMode.Fail)]
    public async Task A_progress_dialog_that_ends_without_cancel_or_cannot_be_shown_leaves_the_operation_running(ProgressDialogMode mode)
    {
        ScriptedRemoteService remotes = new();
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");
        registration.Dialogs.ProgressMode = mode;

        await registration.Commands.ExecuteAsync(GitCommands.FETCH, null, CancellationToken.None);

        Assert.False(remotes.WasCancelled);
        Assert.Equal([GitStrings.FetchSucceeded], registration.Window.Infos);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task An_operation_that_finished_although_it_was_cancelled_reports_its_result()
    {
        ScriptedRemoteService remotes = new() { Block = true, FinishWhenCancelled = true };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");
        registration.Dialogs.ProgressMode = ProgressDialogMode.Cancel;

        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);

        Assert.True(remotes.WasCancelled);
        Assert.Equal([string.Format(GitStrings.PullSucceededFormat, "main", "origin/main")], registration.Window.Infos);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Sync_reports_the_completed_pull_when_the_push_fails()
    {
        const string rejection = "! [rejected]        main -> main (fetch first)";
        ScriptedRemoteService remotes = new()
        {
            Respond = operation => operation == "push"
                ? GitRemoteResult.Failed(GitRemoteFailureKind.Rejected, rejection)
                : GitRemoteResult.Succeeded("Fast-forward")
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");

        await registration.Commands.ExecuteAsync(GitCommands.SYNC, null, CancellationToken.None);

        Assert.Equal(["pull", "push"], remotes.Calls);
        Assert.Equal(
            [
                string.Format(
                    GitStrings.SyncPushFailedFormat,
                    "main",
                    "origin/main",
                    string.Format(GitStrings.RemoteFailureFormat, GitStrings.PushRejectedHint, rejection))
            ],
            registration.Window.Errors);
        Assert.Empty(registration.Window.Infos);
    }

    [Fact]
    public async Task A_fetch_that_failed_for_one_remote_names_the_fetched_ones()
    {
        ScriptedRemoteService remotes = new()
        {
            Respond = _ => new GitRemoteResult
            {
                FailureKind = GitRemoteFailureKind.Network,
                Message = "broken: fatal: unable to access 'https://example.invalid/': Could not resolve host: example.invalid",
                FetchedRemotes = ["origin", "upstream"]
            }
        };
        using Registration registration = await Registration.CreateAsync(remotes: remotes, upstream: "origin/main");

        await registration.Commands.ExecuteAsync(GitCommands.FETCH, null, CancellationToken.None);

        Assert.Equal(
            [
                string.Format(
                    GitStrings.FetchPartlyFailedFormat,
                    "origin, upstream",
                    string.Format(
                        GitStrings.RemoteFailureFormat,
                        GitStrings.NetworkHint,
                        "broken: fatal: unable to access 'https://example.invalid/': Could not resolve host: example.invalid"))
            ],
            registration.Window.Errors);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_uses_the_local_branch_that_tracks_it_under_another_name()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            ArrangeRemoteTopic(repository);
            repository.RunGit("branch", "--track", "my-topic", "origin/topic");
        });

        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");

        Assert.Equal("my-topic", registration.CurrentBranch);
        Assert.DoesNotContain("topic", registration.LocalBranches);
        Assert.Empty(registration.Window.QuickPicks);
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Checkout_of_a_remote_branch_asks_when_a_local_branch_of_its_name_tracks_something_else()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            ArrangeRemoteTopic(repository);
            repository.RunGit("branch", "--track", "topic", "origin/main");
        });
        string remoteTopic = registration.Repository.RunGit("rev-parse", "origin/topic").Trim();

        registration.Window.QuickPick = _ => null;
        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");
        Assert.Equal("main", registration.CurrentBranch);
        (IReadOnlyList<QuickPickItem> items, QuickPickOptions options) = Assert.Single(registration.Window.QuickPicks);
        Assert.Equal(string.Format(GitStrings.LocalBranchExistsTitleFormat, "topic", "origin/topic"), options.Title);
        Assert.Equal(
            new[]
            {
                string.Format(GitStrings.SwitchToLocalBranchFormat, "topic"),
                string.Format(GitStrings.CreateBranchFromRemoteFormat, "origin/topic"),
                string.Format(GitStrings.CheckoutDetachedFormat, "origin/topic")
            },
            items.Select(item => item.Label));
        Assert.Equal(string.Format(GitStrings.TracksUpstreamFormat, "origin/main"), items[0].Description);

        registration.Window.QuickPick = choices => choices[2];
        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");
        Assert.Equal("HEAD", registration.CurrentBranch);
        Assert.Equal(remoteTopic, registration.Repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.True(registration.Session.Current.IsDetached);

        registration.Window.QuickPick = choices => choices[0];
        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");
        Assert.Equal("topic", registration.CurrentBranch);
        Assert.Equal("origin/main", registration.Session.Current.UpstreamName);

        registration.Window.InputBoxResult = "topic-2";
        registration.Window.QuickPick = choices => choices.Count == 3 ? choices[1] : choices.Single(item => item.Label == GitStrings.CreateAndSwitch);
        await registration.ExecuteAsync(GitCommands.CHECKOUT, "refs/remotes/origin/topic");
        Assert.Equal("topic-2", registration.CurrentBranch);
        Assert.Equal(remoteTopic, registration.Repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task A_branch_created_from_a_remote_branch_under_another_name_gets_its_upstream_on_the_first_push()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            (_, _) => true,
            ArrangeRemoteTopic);
        GitTestRepository repository = registration.Repository;
        registration.Window.InputBoxResult = "my-topic";
        registration.Window.QuickPick = items => items.Single(item => item.Label == GitStrings.CreateAndSwitch);

        await registration.ExecuteAsync(GitCommands.CREATE_BRANCH, "refs/remotes/origin/topic");
        Assert.Equal("my-topic", registration.CurrentBranch);
        Assert.False(registration.Session.Current.HasUpstream);

        repository.CommitFileIn(repository.Path, "mine.txt", "m\n", "mine");
        await registration.ExecuteAsync(GitCommands.PUSH);

        Assert.Equal(
            string.Format(GitStrings.PushSetUpstreamPromptFormat, "my-topic", "origin"),
            Assert.Single(registration.Dialogs.Confirmations).Message);
        Assert.Equal(string.Format(GitStrings.PushSucceededFormat, "my-topic", "origin/my-topic"), registration.Window.Infos[^1]);
        Assert.Equal("origin/my-topic", registration.Session.Current.UpstreamName);
        Assert.Equal(
            repository.RunGit("rev-parse", "HEAD").Trim(),
            repository.RunGitIn(repository.RemotePath!, "rev-parse", "my-topic").Trim());
        Assert.Empty(registration.Window.Errors);
    }

    [Theory]
    [InlineData("-m", "refs/heads/x1")]
    [InlineData("-D", "x1")]
    [InlineData("two words", null)]
    [InlineData("@{-1}", null)]
    public async Task An_invalid_branch_name_is_rejected_before_git_runs_and_changes_no_branch(string name, string? startPoint)
    {
        // The message has no git output, so it does not point to the output channel.
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            arrange: repository => repository.RunGit("branch", "x1"),
            output: new InMemoryWindow().CreateOutputChannel("Git"));
        registration.Window.InputBoxResult = name;
        registration.Window.QuickPick = items => items.Single(item => item.Label == GitStrings.CreateOnly);

        await registration.ExecuteAsync(GitCommands.CREATE_BRANCH, startPoint);

        Assert.Equal(new[] { "main", "x1" }, registration.LocalBranches);
        Assert.Equal("main", registration.CurrentBranch);
        Assert.Equal([string.Format(GitStrings.InvalidBranchNameFormat, name)], registration.Window.Errors);
        Assert.Empty(registration.Window.QuickPicks);
    }

    /// <summary>Adds the bare remote with main and the branch topic, which the local repository knows as origin/topic.</summary>
    private static void ArrangeRemoteTopic(GitTestRepository repository)
    {
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        string clone = repository.CloneRemote("clone");
        repository.RunGitIn(clone, "switch", "-q", "-c", "topic");
        repository.CommitFileIn(clone, "t.txt", "t\n", "topic");
        repository.RunGitIn(clone, "push", "-q", "origin", "topic");
        repository.RunGit("fetch", "-q", "origin");
    }

    [Fact]
    public async Task Pull_and_sync_without_upstream_explain_it_and_run_nothing()
    {
        ScriptedRemoteService remotes = new();
        using Registration registration = await Registration.CreateAsync(remotes: remotes);

        await registration.Commands.ExecuteAsync(GitCommands.PULL, null, CancellationToken.None);
        await registration.Commands.ExecuteAsync(GitCommands.SYNC, null, CancellationToken.None);

        Assert.Equal([GitStrings.NoUpstream, GitStrings.NoUpstream], registration.Window.Infos);
        Assert.Empty(remotes.Calls);
        Assert.Empty(registration.Dialogs.Requests);
    }

    [Fact]
    public async Task Stage_of_a_folder_also_stages_files_that_appeared_after_the_last_refresh()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            repository.WriteFile("src/one.txt", "1\n");
            repository.WriteFile("other.txt", "o\n");
        });
        registration.Repository.WriteFile("src/sub/two.txt", "2\n");

        await registration.ExecuteAsync(GitCommands.STAGE, Path.Combine(registration.Repository.Path, "src"));

        GitSnapshot snapshot = registration.Session.Current;
        Assert.Equal(
            new[] { "src/one.txt", "src/sub/two.txt" },
            snapshot.Changes.Where(GitCommandTarget.HasStagedChange).Select(change => change.Path).Order(StringComparer.Ordinal));
        Assert.True(GitCommandTarget.IsUntracked(Assert.Single(snapshot.Changes, change => change.Path == "other.txt")));
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Unstage_of_a_rename_leaves_the_index_equal_to_HEAD()
    {
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(
            arrange: repository => repository.RunGit("mv", "a.txt", "b.txt"));
        Assert.True(Assert.Single(registration.Session.Current.Changes).IsRenamed);

        await registration.ExecuteAsync(GitCommands.UNSTAGE, Path.Combine(registration.Repository.Path, "b.txt"));

        Assert.True(registration.Repository.TryRunGit(out _, "diff", "--cached", "--quiet"));
        Assert.DoesNotContain(registration.Session.Current.Changes, GitCommandTarget.HasStagedChange);
        Assert.Equal(" D a.txt\n?? b.txt\n", registration.Repository.RunGit("status", "--porcelain"));
        Assert.Empty(registration.Window.Errors);
    }

    [Fact]
    public async Task Stage_all_and_unstage_all_handle_more_paths_than_fit_on_a_command_line()
    {
        const int FILE_COUNT = 1600;
        using RepositoryRegistration registration = await RepositoryRegistration.CreateAsync(arrange: repository =>
        {
            for (int i = 0; i < FILE_COUNT; i++)
            {
                repository.WriteFile($"many/a-folder-name-that-makes-the-paths-long/file-{i:D4}-with-a-long-name.txt", "x\n");
            }
        });

        await registration.ExecuteAsync(GitCommands.STAGE_ALL);
        Assert.Equal(FILE_COUNT, registration.Session.Current.Changes.Count(GitCommandTarget.HasStagedChange));

        // Unstage passes every path; together they are far longer than the 32,767 characters of a command line.
        await registration.ExecuteAsync(GitCommands.UNSTAGE_ALL);
        Assert.DoesNotContain(registration.Session.Current.Changes, GitCommandTarget.HasStagedChange);
        Assert.Empty(registration.Window.Errors);
    }

    private static bool EvaluateWhen(string? when, ContextKeyRegistry contextKeys)
    {
        if (string.IsNullOrWhiteSpace(when))
        {
            return true;
        }

        foreach (string term in when.Split("&&", StringSplitOptions.TrimEntries))
        {
            bool negate = term.StartsWith('!');
            string key = negate ? term[1..] : term;
            bool value = contextKeys.TryGet(key, out bool set) && set;
            if (value == negate)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The Git commands over a real repository in a temporary folder.</summary>
    private sealed class RepositoryRegistration : IDisposable
    {
        private RepositoryRegistration(GitTestRepository repository, Func<string, object?, object?> respond, IOutputChannel? output)
        {
            Repository = repository;
            Git = new GitService(options: repository.ProcessOptions);
            Dialogs = new GitDialogHost(respond);
            Session = new GitSession(GitServiceSet.FromService(Git), new FakeWorkspaceInfo { WorkspacePath = repository.Path });
            GitCommandHandler handler = new(Session, ViewHost, Window, Dialogs, null, output, () => null, null);
            GitCommands.RegisterCommands(Commands, new CommandMetadataRegistry(), handler, new List<IDisposable>());
        }

        public RecordingExtensionViewHost ViewHost { get; } = new();

        public GitTestRepository Repository { get; }

        public GitService Git { get; }

        public GitSession Session { get; }

        public GitDialogHost Dialogs { get; }

        public CommandRegistry Commands { get; } = new();

        public RecordingWindow Window { get; } = new();

        public string CurrentBranch => Repository.RunGit("rev-parse", "--abbrev-ref", "HEAD").Trim();

        public string[] LocalBranches => Repository.RunGit("for-each-ref", "--format=%(refname:short)", "refs/heads")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        /// <summary>Creates a repository with one commit on main, arranges it and reads its status once.</summary>
        public static async Task<RepositoryRegistration> CreateAsync(
            Func<string, object?, object?>? respond = null,
            Action<GitTestRepository>? arrange = null,
            IOutputChannel? output = null)
        {
            GitTestRepository repository = GitTestRepository.Create("a.txt");
            try
            {
                arrange?.Invoke(repository);
                RepositoryRegistration registration = new(repository, respond ?? ((_, _) => false), output);
                await registration.Session.RefreshAsync();
                Assert.True(registration.Session.Current.IsRepository);
                return registration;
            }
            catch
            {
                repository.Dispose();
                throw;
            }
        }

        public Task ExecuteAsync(string commandId, string? argument = null) =>
            Commands.ExecuteAsync(commandId, argument is null ? null : [argument], CancellationToken.None);

        public void Dispose()
        {
            foreach (IDisposable document in ViewHost.DocumentViewModels.Values.OfType<IDisposable>())
            {
                document.Dispose();
            }

            Session.Dispose();
            Repository.Dispose();
        }
    }

    private sealed class Registration : IDisposable
    {
        private Registration(
            string repositoryRoot,
            GitFileChange[] changes,
            Func<string, object?, object?> respond,
            IGitRemoteService? remotes,
            string? upstream,
            bool upstreamGone,
            IOutputChannel? output)
        {
            RepositoryRoot = repositoryRoot;
            Git = new FakeGitService(repositoryRoot, changes);
            if (upstream is not null)
            {
                Git.Status = new GitRepositoryStatus
                {
                    RepositoryRoot = repositoryRoot,
                    BranchName = "main",
                    UpstreamName = upstream,
                    IsUpstreamGone = upstreamGone,
                    Changes = changes
                };
            }

            Dialogs = new GitDialogHost(respond);
            Session = new GitSession(
                new GitServiceSet(Git) { Remotes = remotes },
                new FakeWorkspaceInfo { WorkspacePath = repositoryRoot });
            History = new GitHistoryViewModel(Session, (_, _) => Task.CompletedTask);
            GitCommandHandler handler = new(Session, ViewHost, Window, Dialogs, Editor, output, () => null, null, () => History);
            GitCommands.RegisterCommands(Commands, Metadata, handler, Subscriptions);
            GitCommands.RegisterMenuAndPaletteItems(EXTENSION_ID, Contributions, Subscriptions);
        }

        public string RepositoryRoot { get; }

        public FakeGitService Git { get; }

        public GitSession Session { get; }

        public GitHistoryViewModel History { get; }

        public GitDialogHost Dialogs { get; }

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
            GitFileChange[]? changes = null,
            IGitRemoteService? remotes = null,
            string? upstream = null,
            bool upstreamGone = false,
            IOutputChannel? output = null)
        {
            string repositoryRoot = Path.Combine(Path.GetTempPath(), "xve-git-fake-" + Guid.NewGuid().ToString("N"));
            changes ??=
            [
                new GitFileChange { Path = "src/a.cs", WorkTreeStatus = GitChangeKind.Modified },
                new GitFileChange { Path = "b.cs", IndexStatus = GitChangeKind.Modified },
                new GitFileChange { Path = "new.txt", WorkTreeStatus = GitChangeKind.Untracked, IsUntracked = true }
            ];
            Registration registration = new(repositoryRoot, changes, respond ?? ((_, _) => false), remotes, upstream, upstreamGone, output);
            await registration.Session.RefreshAsync();
            Assert.True(registration.Session.Current.IsRepository);
            return registration;
        }

        public void Dispose()
        {
            History.Dispose();
            Session.Dispose();
        }
    }
}
