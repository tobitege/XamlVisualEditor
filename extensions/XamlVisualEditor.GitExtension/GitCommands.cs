using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// Ids of the Git commands, views and context keys, and the registration of the commands with their metadata,
/// menu entries and Command Palette entries.
/// </summary>
public static class GitCommands
{
    /// <summary>Command category and submenu title; "Git" is a name and is not translated.</summary>
    public const string CATEGORY = "Git";

    /// <summary>View id of the Git Changes panel; unchanged since the first version so saved layouts keep it.</summary>
    public const string CHANGES_VIEW_ID = "git.panel";

    /// <summary>Shows or hides the Git Changes panel.</summary>
    public const string TOGGLE_CHANGES_VIEW = "git.changes.toggleView";

    /// <summary>Shows the Git Changes panel.</summary>
    public const string SHOW_CHANGES = "git.changes.show";

    /// <summary>Reads the Git status again.</summary>
    public const string REFRESH = "git.refresh";

    /// <summary>Shows the "Git" output channel.</summary>
    public const string SHOW_OUTPUT = "git.showOutput";

    /// <summary>Creates a repository in the workspace folder.</summary>
    public const string INIT = "git.init";

    /// <summary>Switches the working folder watcher on or off.</summary>
    public const string TOGGLE_AUTO_REFRESH = "git.toggleAutoRefresh";

    /// <summary>Switches the Git status in the explorers on or off.</summary>
    public const string TOGGLE_EXPLORER_DECORATIONS = "git.toggleExplorerDecorations";

    /// <summary>Stages a file or folder: argument 0, otherwise the active document.</summary>
    public const string STAGE = "git.stage";

    /// <summary>Unstages a file or folder: argument 0, otherwise the active document.</summary>
    public const string UNSTAGE = "git.unstage";

    /// <summary>Discards the working file changes of a file or folder after a confirmation.</summary>
    public const string DISCARD = "git.discard";

    /// <summary>Stages all changes.</summary>
    public const string STAGE_ALL = "git.stageAll";

    /// <summary>Unstages all changes.</summary>
    public const string UNSTAGE_ALL = "git.unstageAll";

    /// <summary>
    /// Commits the staged changes with the message of the panel or an entered message; amends the last commit instead
    /// while the Amend box of the panel is checked.
    /// </summary>
    public const string COMMIT = "git.commit";

    /// <summary>Shows the Git Changes panel with the Amend box checked; the commit button then amends the last commit.</summary>
    public const string COMMIT_AMEND = "git.commitAmend";

    /// <summary>
    /// Stages the lines selected in the diff of the Git Changes panel, or the hunk at the cursor, of a working tree
    /// change.
    /// </summary>
    public const string STAGE_SELECTION = "git.stageSelection";

    /// <summary>Unstages the lines selected in the diff of the Git Changes panel, or the hunk at the cursor, of a staged change.</summary>
    public const string UNSTAGE_SELECTION = "git.unstageSelection";

    /// <summary>
    /// Discards the lines selected in the diff of the Git Changes panel, or the hunk at the cursor, of a working tree
    /// change after a confirmation.
    /// </summary>
    public const string DISCARD_SELECTION = "git.discardSelection";

    /// <summary>Stashes the changes of the tracked files with an entered message.</summary>
    public const string STASH = "git.stash";

    /// <summary>Stashes the changes of the tracked files and the untracked files with an entered message.</summary>
    public const string STASH_INCLUDE_UNTRACKED = "git.stashIncludeUntracked";

    /// <summary>Applies the stash with the commit id in argument 0, otherwise one picked from a list, and keeps it.</summary>
    public const string STASH_APPLY = "git.stashApply";

    /// <summary>Applies the stash with the commit id in argument 0, otherwise one picked from a list, and removes it.</summary>
    public const string STASH_POP = "git.stashPop";

    /// <summary>Removes the stash with the commit id in argument 0, otherwise one picked from a list, after a confirmation.</summary>
    public const string STASH_DROP = "git.stashDrop";

    /// <summary>
    /// Opens a document tab with the files and diffs of the stash with the commit id in argument 0, otherwise of one
    /// picked from a list; untracked files of the stash show as added.
    /// </summary>
    public const string STASH_SHOW = "git.stashShow";

    /// <summary>
    /// Creates a branch with an entered name at the commit the stash with the commit id in argument 0, otherwise one
    /// picked from a list, was made on, switches to it, applies the stash and removes it.
    /// </summary>
    public const string STASH_BRANCH = "git.stashBranch";

    /// <summary>Removes every stash after a confirmation.</summary>
    public const string STASH_CLEAR = "git.stashClear";

    /// <summary>View id of the Git Repository panel with the branches.</summary>
    public const string REPOSITORY_VIEW_ID = "git.repository";

    /// <summary>Shows or hides the Git Repository panel.</summary>
    public const string TOGGLE_REPOSITORY_VIEW = "git.repository.toggleView";

    /// <summary>Switches to the branch in argument 0 (full or short name), otherwise to one picked from a list.</summary>
    public const string CHECKOUT = "git.checkout";

    /// <summary>Creates a branch at argument 0, otherwise at HEAD, with an entered name, and optionally switches to it.</summary>
    public const string CREATE_BRANCH = "git.createBranch";

    /// <summary>Deletes the local branch in argument 0, otherwise one picked from a list, after a confirmation.</summary>
    public const string DELETE_BRANCH = "git.deleteBranch";

    /// <summary>Renames the local branch in argument 0, otherwise one picked from a list, to an entered name.</summary>
    public const string RENAME_BRANCH = "git.renameBranch";

    /// <summary>Adds a remote with an entered name and URL.</summary>
    public const string ADD_REMOTE = "git.addRemote";

    /// <summary>Renames the remote in argument 0, otherwise one picked from a list, to an entered name.</summary>
    public const string RENAME_REMOTE = "git.renameRemote";

    /// <summary>Changes the URL of the remote in argument 0, otherwise of one picked from a list, to an entered URL.</summary>
    public const string SET_REMOTE_URL = "git.setRemoteUrl";

    /// <summary>
    /// Removes the remote in argument 0, otherwise one picked from a list, with its remote-tracking branches after a
    /// confirmation.
    /// </summary>
    public const string REMOVE_REMOTE = "git.removeRemote";

    /// <summary>Merges the branch in argument 0 (full or short name), otherwise one picked from a list, into the current branch.</summary>
    public const string MERGE = "git.merge";

    /// <summary>Aborts the merge, rebase, cherry-pick, revert or patch application in progress after a confirmation.</summary>
    public const string ABORT = "git.abort";

    /// <summary>
    /// Continues the rebase, cherry-pick, revert or patch application in progress; a merge is completed with
    /// <see cref="COMMIT"/>.
    /// </summary>
    public const string CONTINUE = "git.continue";

    /// <summary>
    /// Skips the current step of the rebase, cherry-pick, revert or patch application in progress after a confirmation,
    /// for example a step that became empty after its conflicts were resolved.
    /// </summary>
    public const string SKIP = "git.skip";

    /// <summary>Applies the changes of the commit in argument 0 to the current branch as a new commit.</summary>
    public const string CHERRY_PICK = "git.cherryPick";

    /// <summary>Undoes the changes of the commit in argument 0 on the current branch with a new commit.</summary>
    public const string REVERT = "git.revert";

    /// <summary>
    /// Resolves the conflicted files of argument 0 (a file or folder), otherwise of the active document, with the
    /// version of the current branch after a confirmation; a file that side deleted is deleted.
    /// </summary>
    public const string ACCEPT_OURS = "git.acceptOurs";

    /// <summary>
    /// Resolves the conflicted files of argument 0 (a file or folder), otherwise of the active document, with the
    /// version of the merged commit after a confirmation; a file that side deleted is deleted.
    /// </summary>
    public const string ACCEPT_THEIRS = "git.acceptTheirs";

    /// <summary>
    /// Marks the conflicted files of argument 0 (a file or folder), otherwise of the active document, as resolved by
    /// staging them; asks first when a saved file still contains conflict markers.
    /// </summary>
    public const string MARK_RESOLVED = "git.markResolved";

    /// <summary>
    /// Replaces the conflict block at the 1-based line in argument 1 of the file in argument 0, otherwise at the caret
    /// of the active document, by the side picked from a list, in the editor buffer. A click on a conflict mark runs it.
    /// </summary>
    public const string RESOLVE_CONFLICT = "git.resolveConflict";

    /// <summary>Fetches the branches of all remotes.</summary>
    public const string FETCH = "git.fetch";

    /// <summary>Fast-forwards the current branch to its upstream.</summary>
    public const string PULL = "git.pull";

    /// <summary>Pushes the current branch; without upstream it asks to set one.</summary>
    public const string PUSH = "git.push";

    /// <summary>Pulls and then pushes the current branch.</summary>
    public const string SYNC = "git.sync";

    /// <summary>View id of the Git History panel.</summary>
    public const string HISTORY_VIEW_ID = "git.history";

    /// <summary>Shows or hides the Git History panel.</summary>
    public const string TOGGLE_HISTORY_VIEW = "git.history.toggleView";

    /// <summary>
    /// Shows the history of the whole repository in the Git History panel, starting at the commit, branch or tag in
    /// argument 0, otherwise at HEAD.
    /// </summary>
    public const string SHOW_HISTORY = "git.showHistory";

    /// <summary>
    /// Shows the history of a file or folder in the Git History panel: argument 0, otherwise the active document,
    /// starting at the commit, branch or tag in argument 1, otherwise at HEAD. The history of a file follows its
    /// renames.
    /// </summary>
    public const string SHOW_FILE_HISTORY = "git.showFileHistory";

    /// <summary>Switches the Git change marks in the editors on or off.</summary>
    public const string TOGGLE_CHANGE_MARKERS = "git.toggleChangeMarkers";

    /// <summary>Compares a file, argument 0, otherwise the active document, with its version in HEAD in a document tab.</summary>
    public const string COMPARE_WITH_HEAD = "git.compareWithHead";

    /// <summary>
    /// Compares a file, argument 0, otherwise the active document, with its staged version in a document tab, and
    /// shows the change at the 1-based line in argument 1, if any. A click on a change mark in an editor runs it.
    /// </summary>
    public const string COMPARE_WITH_INDEX = "git.compareWithIndex";

    /// <summary>Shows the commit of every line of a saved file, argument 0, otherwise the active document, in a document tab.</summary>
    public const string BLAME = "git.blame";

    /// <summary>Context key: git can be started.</summary>
    public const string AVAILABLE_CONTEXT_KEY = "git.available";

    /// <summary>Context key: the workspace is inside a repository.</summary>
    public const string IS_REPOSITORY_CONTEXT_KEY = "git.isRepository";

    /// <summary>Context key: the repository has a remote.</summary>
    public const string HAS_REMOTE_CONTEXT_KEY = "git.hasRemote";

    /// <summary>Context key: the current branch has an upstream branch.</summary>
    public const string HAS_UPSTREAM_CONTEXT_KEY = "git.hasUpstream";

    /// <summary>Context key: a merge waits to be committed or aborted.</summary>
    public const string IS_MERGING_CONTEXT_KEY = "git.isMerging";

    /// <summary>Context key: a file has an unresolved merge conflict.</summary>
    public const string HAS_CONFLICTS_CONTEXT_KEY = "git.hasConflicts";

    /// <summary>Context key: a merge, rebase, cherry-pick, revert or patch application is in progress.</summary>
    public const string OPERATION_IN_PROGRESS_CONTEXT_KEY = "git.operationInProgress";

    /// <summary>Context key: the working folder watcher is switched on.</summary>
    public const string AUTO_REFRESH_CONTEXT_KEY = GitSettingsStore.AUTO_REFRESH_KEY;

    /// <summary>Context key: the explorers show the Git status.</summary>
    public const string EXPLORER_DECORATIONS_CONTEXT_KEY = GitSettingsStore.EXPLORER_DECORATIONS_KEY;

    /// <summary>Context key: the editors show the Git change marks.</summary>
    public const string CHANGE_MARKERS_CONTEXT_KEY = GitSettingsStore.CHANGE_MARKERS_KEY;

    private const string SUBMENU_GROUP = "git";
    private const string VIEW_GROUP = "1_view";
    private const string CHANGES_GROUP = "2_changes";
    private const string REPOSITORY_GROUP = "3_repository";
    private const string BRANCHES_GROUP = "4_branches";
    private const string SYNC_GROUP = "5_sync";
    private const string HISTORY_GROUP = "6_history";
    private const string STASH_GROUP = "7_stash";
    private const string REMOTES_GROUP = "8_remotes";
    private const string REPOSITORY_WHEN = IS_REPOSITORY_CONTEXT_KEY;
    private const string REMOTE_WHEN = HAS_REMOTE_CONTEXT_KEY;
    private const string UPSTREAM_WHEN = IS_REPOSITORY_CONTEXT_KEY + " && " + HAS_UPSTREAM_CONTEXT_KEY;
    // Stash and merge are refused during an operation: a stash would end a resolved merge without its merge commit.
    private const string NO_OPERATION_WHEN = IS_REPOSITORY_CONTEXT_KEY + " && !" + OPERATION_IN_PROGRESS_CONTEXT_KEY;
    private const string OPERATION_WHEN = IS_REPOSITORY_CONTEXT_KEY + " && " + OPERATION_IN_PROGRESS_CONTEXT_KEY;
    private const string CONTINUE_WHEN = OPERATION_WHEN + " && !" + IS_MERGING_CONTEXT_KEY;
    private const string CONFLICTS_WHEN = IS_REPOSITORY_CONTEXT_KEY + " && " + HAS_CONFLICTS_CONTEXT_KEY;

    /// <summary>Registers the command handlers and their metadata.</summary>
    internal static void RegisterCommands(
        ICommands commands,
        ICommandMetadataRegistry commandMetadata,
        GitCommandHandler handler,
        IList<IDisposable> subscriptions)
    {
        Register(TOGGLE_CHANGES_VIEW, handler.ToggleChangesViewAsync,
            new CommandMetadata(GitStrings.ToggleChangesViewCommand, CATEGORY, Priority: 10));
        Register(SHOW_CHANGES, handler.ShowChangesAsync,
            new CommandMetadata(GitStrings.ShowChangesCommand, CATEGORY, Keybinding: "Ctrl+Shift+G", Priority: 20));
        Register(REFRESH, handler.RefreshAsync,
            new CommandMetadata(GitStrings.RefreshCommand, CATEGORY, When: AVAILABLE_CONTEXT_KEY, Priority: 30));
        Register(SHOW_OUTPUT, handler.ShowOutputAsync,
            new CommandMetadata(GitStrings.ShowOutputCommand, CATEGORY, Priority: 40));
        Register(TOGGLE_AUTO_REFRESH, handler.ToggleAutoRefreshAsync,
            new CommandMetadata(GitStrings.ToggleAutoRefreshCommand, CATEGORY, Priority: 50));
        Register(INIT, handler.InitAsync,
            new CommandMetadata(GitStrings.InitCommand, CATEGORY,
                When: "hasWorkspace && " + AVAILABLE_CONTEXT_KEY + " && !" + IS_REPOSITORY_CONTEXT_KEY, Priority: 60));
        Register(STAGE, handler.StageAsync,
            new CommandMetadata(GitStrings.StageCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 70));
        Register(UNSTAGE, handler.UnstageAsync,
            new CommandMetadata(GitStrings.UnstageCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 80));
        Register(DISCARD, handler.DiscardAsync,
            new CommandMetadata(GitStrings.DiscardCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 90));
        Register(STAGE_ALL, handler.StageAllAsync,
            new CommandMetadata(GitStrings.StageAllCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 100));
        Register(UNSTAGE_ALL, handler.UnstageAllAsync,
            new CommandMetadata(GitStrings.UnstageAllCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 110));
        Register(COMMIT, handler.CommitAsync,
            new CommandMetadata(GitStrings.CommitCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 120));
        Register(TOGGLE_REPOSITORY_VIEW, handler.ToggleRepositoryViewAsync,
            new CommandMetadata(GitStrings.ToggleRepositoryViewCommand, CATEGORY, Priority: 130));
        Register(CHECKOUT, handler.CheckoutAsync,
            new CommandMetadata(GitStrings.CheckoutCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 140));
        Register(CREATE_BRANCH, handler.CreateBranchAsync,
            new CommandMetadata(GitStrings.CreateBranchCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 150));
        Register(DELETE_BRANCH, handler.DeleteBranchAsync,
            new CommandMetadata(GitStrings.DeleteBranchCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 160));
        Register(FETCH, handler.FetchAsync,
            new CommandMetadata(GitStrings.FetchCommand, CATEGORY, When: REMOTE_WHEN, Priority: 170));
        Register(PULL, handler.PullAsync,
            new CommandMetadata(GitStrings.PullCommand, CATEGORY, When: UPSTREAM_WHEN, Priority: 180));
        Register(PUSH, handler.PushAsync,
            new CommandMetadata(GitStrings.PushCommand, CATEGORY, When: REMOTE_WHEN, Priority: 190));
        Register(SYNC, handler.SyncAsync,
            new CommandMetadata(GitStrings.SyncCommand, CATEGORY, When: UPSTREAM_WHEN, Priority: 200));
        Register(TOGGLE_HISTORY_VIEW, handler.ToggleHistoryViewAsync,
            new CommandMetadata(GitStrings.ToggleHistoryViewCommand, CATEGORY, Priority: 210));
        Register(SHOW_HISTORY, handler.ShowHistoryAsync,
            new CommandMetadata(GitStrings.ShowHistoryCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 220));
        Register(SHOW_FILE_HISTORY, handler.ShowFileHistoryAsync,
            new CommandMetadata(GitStrings.ShowFileHistoryCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 230));
        Register(TOGGLE_EXPLORER_DECORATIONS, handler.ToggleExplorerDecorationsAsync,
            new CommandMetadata(GitStrings.ToggleExplorerDecorationsCommand, CATEGORY, Priority: 240));
        Register(TOGGLE_CHANGE_MARKERS, handler.ToggleChangeMarkersAsync,
            new CommandMetadata(GitStrings.ToggleChangeMarkersCommand, CATEGORY, Priority: 250));
        Register(COMPARE_WITH_HEAD, handler.CompareWithHeadAsync,
            new CommandMetadata(GitStrings.CompareWithHeadCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 260));
        Register(COMPARE_WITH_INDEX, handler.CompareWithIndexAsync,
            new CommandMetadata(GitStrings.CompareWithIndexCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 270));
        Register(BLAME, handler.BlameAsync,
            new CommandMetadata(GitStrings.BlameCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 280));
        Register(COMMIT_AMEND, handler.CommitAmendAsync,
            new CommandMetadata(GitStrings.CommitAmendCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 290));
        Register(STAGE_SELECTION, handler.StageSelectionAsync,
            new CommandMetadata(GitStrings.StageSelectionCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 300));
        Register(UNSTAGE_SELECTION, handler.UnstageSelectionAsync,
            new CommandMetadata(GitStrings.UnstageSelectionCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 310));
        Register(DISCARD_SELECTION, handler.DiscardSelectionAsync,
            new CommandMetadata(GitStrings.DiscardSelectionCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 320));
        Register(STASH, handler.StashAsync,
            new CommandMetadata(GitStrings.StashCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 330));
        Register(STASH_INCLUDE_UNTRACKED, handler.StashIncludeUntrackedAsync,
            new CommandMetadata(GitStrings.StashIncludeUntrackedCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 340));
        Register(STASH_APPLY, handler.StashApplyAsync,
            new CommandMetadata(GitStrings.StashApplyCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 350));
        Register(STASH_POP, handler.StashPopAsync,
            new CommandMetadata(GitStrings.StashPopCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 360));
        Register(STASH_DROP, handler.StashDropAsync,
            new CommandMetadata(GitStrings.StashDropCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 370));
        Register(MERGE, handler.MergeAsync,
            new CommandMetadata(GitStrings.MergeCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 380));
        Register(ABORT, handler.AbortAsync,
            new CommandMetadata(GitStrings.AbortCommand, CATEGORY, When: OPERATION_WHEN, Priority: 390));
        Register(CONTINUE, handler.ContinueAsync,
            new CommandMetadata(GitStrings.ContinueCommand, CATEGORY, When: CONTINUE_WHEN, Priority: 400));
        Register(ACCEPT_OURS, handler.AcceptOursAsync,
            new CommandMetadata(GitStrings.AcceptOursCommand, CATEGORY, When: CONFLICTS_WHEN, Priority: 410));
        Register(ACCEPT_THEIRS, handler.AcceptTheirsAsync,
            new CommandMetadata(GitStrings.AcceptTheirsCommand, CATEGORY, When: CONFLICTS_WHEN, Priority: 420));
        Register(MARK_RESOLVED, handler.MarkResolvedAsync,
            new CommandMetadata(GitStrings.MarkResolvedCommand, CATEGORY, When: CONFLICTS_WHEN, Priority: 430));
        Register(RESOLVE_CONFLICT, handler.ResolveConflictAsync,
            new CommandMetadata(GitStrings.ResolveConflictCommand, CATEGORY, When: CONFLICTS_WHEN, Priority: 440));
        Register(RENAME_BRANCH, handler.RenameBranchAsync,
            new CommandMetadata(GitStrings.RenameBranchCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 450));
        Register(STASH_SHOW, handler.StashShowAsync,
            new CommandMetadata(GitStrings.StashShowCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 460));
        Register(STASH_BRANCH, handler.StashBranchAsync,
            new CommandMetadata(GitStrings.StashBranchCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 470));
        Register(STASH_CLEAR, handler.StashClearAsync,
            new CommandMetadata(GitStrings.StashClearCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 480));
        Register(ADD_REMOTE, handler.AddRemoteAsync,
            new CommandMetadata(GitStrings.AddRemoteCommand, CATEGORY, When: REPOSITORY_WHEN, Priority: 490));
        Register(RENAME_REMOTE, handler.RenameRemoteAsync,
            new CommandMetadata(GitStrings.RenameRemoteCommand, CATEGORY, When: REMOTE_WHEN, Priority: 500));
        Register(SET_REMOTE_URL, handler.SetRemoteUrlAsync,
            new CommandMetadata(GitStrings.SetRemoteUrlCommand, CATEGORY, When: REMOTE_WHEN, Priority: 510));
        Register(REMOVE_REMOTE, handler.RemoveRemoteAsync,
            new CommandMetadata(GitStrings.RemoveRemoteCommand, CATEGORY, When: REMOTE_WHEN, Priority: 520));
        Register(SKIP, handler.SkipAsync,
            new CommandMetadata(GitStrings.SkipCommand, CATEGORY, When: CONTINUE_WHEN, Priority: 530));
        Register(CHERRY_PICK, handler.CherryPickAsync,
            new CommandMetadata(GitStrings.CherryPickCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 540));
        Register(REVERT, handler.RevertAsync,
            new CommandMetadata(GitStrings.RevertCommand, CATEGORY, When: NO_OPERATION_WHEN, Priority: 550));

        void Register(string commandId, Func<CommandContext, Task> execute, CommandMetadata metadata)
        {
            subscriptions.Add(commands.Register(commandId, execute));
            subscriptions.Add(commandMetadata.Register(commandId, metadata));
        }
    }

    /// <summary>
    /// Registers the menu and Command Palette entries: the panels under View › Panels, the repository commands in
    /// the Extensions › Git submenu and the file commands in the Git submenu of the editor, explorer and document tab
    /// context menus.
    /// </summary>
    internal static void RegisterMenuAndPaletteItems(
        string extensionId,
        IExtensionContributionRegistry contributions,
        IList<IDisposable> subscriptions)
    {
        subscriptions.Add(contributions.RegisterMenuItems(
            extensionId,
            [
                new ExtensionMenuContribution(TOGGLE_CHANGES_VIEW, GitStrings.ChangesViewTitle, ExtensionMenuLocations.View, "views.bottom", 50),
                ExtensionsItem(SHOW_CHANGES, GitStrings.ShowChangesCommand, VIEW_GROUP, 10),
                ExtensionsItem(REFRESH, GitStrings.RefreshCommand, VIEW_GROUP, 20),
                ExtensionsItem(SHOW_OUTPUT, GitStrings.ShowOutputCommand, VIEW_GROUP, 30),
                ExtensionsItem(TOGGLE_AUTO_REFRESH, GitStrings.ToggleAutoRefreshCommand, VIEW_GROUP, 40) with
                {
                    CheckedWhen = AUTO_REFRESH_CONTEXT_KEY
                },
                ExtensionsItem(TOGGLE_EXPLORER_DECORATIONS, GitStrings.ToggleExplorerDecorationsCommand, VIEW_GROUP, 45) with
                {
                    CheckedWhen = EXPLORER_DECORATIONS_CONTEXT_KEY
                },
                ExtensionsItem(TOGGLE_CHANGE_MARKERS, GitStrings.ToggleChangeMarkersCommand, VIEW_GROUP, 46) with
                {
                    CheckedWhen = CHANGE_MARKERS_CONTEXT_KEY
                },
                ExtensionsItem(STAGE_ALL, GitStrings.StageAllCommand, CHANGES_GROUP, 50),
                ExtensionsItem(UNSTAGE_ALL, GitStrings.UnstageAllCommand, CHANGES_GROUP, 60),
                ExtensionsItem(COMMIT, GitStrings.CommitCommand, CHANGES_GROUP, 70),
                ExtensionsItem(COMMIT_AMEND, GitStrings.CommitAmendCommand, CHANGES_GROUP, 75),
                ExtensionsItem(INIT, GitStrings.InitCommand, REPOSITORY_GROUP, 80),
                new ExtensionMenuContribution(TOGGLE_REPOSITORY_VIEW, GitStrings.RepositoryViewTitle, ExtensionMenuLocations.View, "views.left", 50),
                ExtensionsItem(CHECKOUT, GitStrings.CheckoutCommand, BRANCHES_GROUP, 90),
                ExtensionsItem(CREATE_BRANCH, GitStrings.CreateBranchCommand, BRANCHES_GROUP, 100),
                ExtensionsItem(RENAME_BRANCH, GitStrings.RenameBranchCommand, BRANCHES_GROUP, 105),
                ExtensionsItem(DELETE_BRANCH, GitStrings.DeleteBranchCommand, BRANCHES_GROUP, 110),
                ExtensionsItem(MERGE, GitStrings.MergeCommand, BRANCHES_GROUP, 112),
                ExtensionsItem(CONTINUE, GitStrings.ContinueCommand, BRANCHES_GROUP, 114),
                ExtensionsItem(SKIP, GitStrings.SkipCommand, BRANCHES_GROUP, 115),
                ExtensionsItem(ABORT, GitStrings.AbortCommand, BRANCHES_GROUP, 116),
                ExtensionsItem(FETCH, GitStrings.FetchCommand, SYNC_GROUP, 120),
                ExtensionsItem(PULL, GitStrings.PullCommand, SYNC_GROUP, 130),
                ExtensionsItem(PUSH, GitStrings.PushCommand, SYNC_GROUP, 140),
                ExtensionsItem(SYNC, GitStrings.SyncCommand, SYNC_GROUP, 150),
                new ExtensionMenuContribution(TOGGLE_HISTORY_VIEW, GitStrings.HistoryViewTitle, ExtensionMenuLocations.View, "views.bottom", 55),
                ExtensionsItem(SHOW_HISTORY, GitStrings.ShowHistoryCommand, HISTORY_GROUP, 160),
                ExtensionsItem(STASH, GitStrings.StashCommand, STASH_GROUP, 170),
                ExtensionsItem(STASH_INCLUDE_UNTRACKED, GitStrings.StashIncludeUntrackedCommand, STASH_GROUP, 180),
                ExtensionsItem(STASH_APPLY, GitStrings.StashApplyCommand, STASH_GROUP, 190),
                ExtensionsItem(STASH_POP, GitStrings.StashPopCommand, STASH_GROUP, 200),
                ExtensionsItem(STASH_SHOW, GitStrings.StashShowCommand, STASH_GROUP, 185),
                ExtensionsItem(STASH_BRANCH, GitStrings.StashBranchCommand, STASH_GROUP, 205),
                ExtensionsItem(STASH_DROP, GitStrings.StashDropCommand, STASH_GROUP, 210),
                ExtensionsItem(STASH_CLEAR, GitStrings.StashClearCommand, STASH_GROUP, 215),
                ExtensionsItem(ADD_REMOTE, GitStrings.AddRemoteCommand, REMOTES_GROUP, 220),
                ExtensionsItem(RENAME_REMOTE, GitStrings.RenameRemoteCommand, REMOTES_GROUP, 230),
                ExtensionsItem(SET_REMOTE_URL, GitStrings.SetRemoteUrlCommand, REMOTES_GROUP, 240),
                ExtensionsItem(REMOVE_REMOTE, GitStrings.RemoveRemoteCommand, REMOTES_GROUP, 250),
                ContextItem(ExtensionMenuLocations.EditorContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.EditorContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.EditorContext, DISCARD, GitStrings.DiscardCommand, 30),
                ContextItem(ExtensionMenuLocations.EditorContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40),
                .. FileDocumentItems(ExtensionMenuLocations.EditorContext),
                ContextItem(ExtensionMenuLocations.EditorContext, RESOLVE_CONFLICT, GitStrings.ResolveConflictCommand, 80),
                .. ConflictItems(ExtensionMenuLocations.EditorContext),
                ContextItem(ExtensionMenuLocations.ExplorerContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.ExplorerContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.ExplorerContext, DISCARD, GitStrings.DiscardCommand, 30),
                ContextItem(ExtensionMenuLocations.ExplorerContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40),
                .. FileDocumentItems(ExtensionMenuLocations.ExplorerContext),
                .. ConflictItems(ExtensionMenuLocations.ExplorerContext),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40),
                .. FileDocumentItems(ExtensionMenuLocations.DocumentTabContext)
            ]));
        subscriptions.Add(contributions.RegisterCommandPaletteItems(
            extensionId,
            [
                new ExtensionCommandPaletteContribution(TOGGLE_CHANGES_VIEW, GitStrings.ToggleChangesViewCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_CHANGES, GitStrings.ShowChangesCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(REFRESH, GitStrings.RefreshCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_OUTPUT, GitStrings.ShowOutputCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_AUTO_REFRESH, GitStrings.ToggleAutoRefreshCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_EXPLORER_DECORATIONS, GitStrings.ToggleExplorerDecorationsCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(INIT, GitStrings.InitCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STAGE, GitStrings.StageCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(UNSTAGE, GitStrings.UnstageCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(DISCARD, GitStrings.DiscardCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STAGE_ALL, GitStrings.StageAllCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(UNSTAGE_ALL, GitStrings.UnstageAllCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(COMMIT, GitStrings.CommitCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_REPOSITORY_VIEW, GitStrings.ToggleRepositoryViewCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(CHECKOUT, GitStrings.CheckoutCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(CREATE_BRANCH, GitStrings.CreateBranchCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(DELETE_BRANCH, GitStrings.DeleteBranchCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(FETCH, GitStrings.FetchCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(PULL, GitStrings.PullCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(PUSH, GitStrings.PushCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SYNC, GitStrings.SyncCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_HISTORY_VIEW, GitStrings.ToggleHistoryViewCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_HISTORY, GitStrings.ShowHistoryCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_CHANGE_MARKERS, GitStrings.ToggleChangeMarkersCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(COMPARE_WITH_HEAD, GitStrings.CompareWithHeadCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(COMPARE_WITH_INDEX, GitStrings.CompareWithIndexCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(BLAME, GitStrings.BlameCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(COMMIT_AMEND, GitStrings.CommitAmendCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STAGE_SELECTION, GitStrings.StageSelectionCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(UNSTAGE_SELECTION, GitStrings.UnstageSelectionCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(DISCARD_SELECTION, GitStrings.DiscardSelectionCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH, GitStrings.StashCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_INCLUDE_UNTRACKED, GitStrings.StashIncludeUntrackedCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_APPLY, GitStrings.StashApplyCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_POP, GitStrings.StashPopCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_DROP, GitStrings.StashDropCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(MERGE, GitStrings.MergeCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(CONTINUE, GitStrings.ContinueCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SKIP, GitStrings.SkipCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(ABORT, GitStrings.AbortCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(ACCEPT_OURS, GitStrings.AcceptOursCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(ACCEPT_THEIRS, GitStrings.AcceptTheirsCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(MARK_RESOLVED, GitStrings.MarkResolvedCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(RESOLVE_CONFLICT, GitStrings.ResolveConflictCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(RENAME_BRANCH, GitStrings.RenameBranchCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_SHOW, GitStrings.StashShowCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_BRANCH, GitStrings.StashBranchCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STASH_CLEAR, GitStrings.StashClearCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(ADD_REMOTE, GitStrings.AddRemoteCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(RENAME_REMOTE, GitStrings.RenameRemoteCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SET_REMOTE_URL, GitStrings.SetRemoteUrlCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(REMOVE_REMOTE, GitStrings.RemoveRemoteCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(CHERRY_PICK, GitStrings.CherryPickCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(REVERT, GitStrings.RevertCommand, CATEGORY)
            ]));

        // The document commands of a file, after its file commands.
        static ExtensionMenuContribution[] FileDocumentItems(string location) =>
        [
            ContextItem(location, COMPARE_WITH_HEAD, GitStrings.CompareWithHeadCommand, 50),
            ContextItem(location, COMPARE_WITH_INDEX, GitStrings.CompareWithIndexCommand, 60),
            ContextItem(location, BLAME, GitStrings.BlameCommand, 70)
        ];

        // The conflict commands of a file or folder, last.
        static ExtensionMenuContribution[] ConflictItems(string location) =>
        [
            ContextItem(location, ACCEPT_OURS, GitStrings.AcceptOursCommand, 90),
            ContextItem(location, ACCEPT_THEIRS, GitStrings.AcceptTheirsCommand, 100),
            ContextItem(location, MARK_RESOLVED, GitStrings.MarkResolvedCommand, 110)
        ];

        static ExtensionMenuContribution ExtensionsItem(string commandId, string title, string submenuGroup, int priority) =>
            new(commandId, title, ExtensionMenuLocations.Extensions, SUBMENU_GROUP, priority)
            {
                Submenu = CATEGORY,
                SubmenuGroup = submenuGroup
            };

        static ExtensionMenuContribution ContextItem(string location, string commandId, string title, int priority) =>
            new(commandId, title, location, SUBMENU_GROUP, priority)
            {
                Submenu = CATEGORY
            };
    }
}
