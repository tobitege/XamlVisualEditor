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

    /// <summary>Commits the staged changes with the message of the panel or an entered message.</summary>
    public const string COMMIT = "git.commit";

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

    /// <summary>Context key: the working folder watcher is switched on.</summary>
    public const string AUTO_REFRESH_CONTEXT_KEY = GitSettingsStore.AUTO_REFRESH_KEY;

    /// <summary>Context key: the explorers show the Git status.</summary>
    public const string EXPLORER_DECORATIONS_CONTEXT_KEY = GitSettingsStore.EXPLORER_DECORATIONS_KEY;

    private const string SUBMENU_GROUP = "git";
    private const string VIEW_GROUP = "1_view";
    private const string CHANGES_GROUP = "2_changes";
    private const string REPOSITORY_GROUP = "3_repository";
    private const string BRANCHES_GROUP = "4_branches";
    private const string SYNC_GROUP = "5_sync";
    private const string HISTORY_GROUP = "6_history";
    private const string REPOSITORY_WHEN = IS_REPOSITORY_CONTEXT_KEY;
    private const string REMOTE_WHEN = HAS_REMOTE_CONTEXT_KEY;
    private const string UPSTREAM_WHEN = IS_REPOSITORY_CONTEXT_KEY + " && " + HAS_UPSTREAM_CONTEXT_KEY;

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
                ExtensionsItem(STAGE_ALL, GitStrings.StageAllCommand, CHANGES_GROUP, 50),
                ExtensionsItem(UNSTAGE_ALL, GitStrings.UnstageAllCommand, CHANGES_GROUP, 60),
                ExtensionsItem(COMMIT, GitStrings.CommitCommand, CHANGES_GROUP, 70),
                ExtensionsItem(INIT, GitStrings.InitCommand, REPOSITORY_GROUP, 80),
                new ExtensionMenuContribution(TOGGLE_REPOSITORY_VIEW, GitStrings.RepositoryViewTitle, ExtensionMenuLocations.View, "views.left", 50),
                ExtensionsItem(CHECKOUT, GitStrings.CheckoutCommand, BRANCHES_GROUP, 90),
                ExtensionsItem(CREATE_BRANCH, GitStrings.CreateBranchCommand, BRANCHES_GROUP, 100),
                ExtensionsItem(DELETE_BRANCH, GitStrings.DeleteBranchCommand, BRANCHES_GROUP, 110),
                ExtensionsItem(FETCH, GitStrings.FetchCommand, SYNC_GROUP, 120),
                ExtensionsItem(PULL, GitStrings.PullCommand, SYNC_GROUP, 130),
                ExtensionsItem(PUSH, GitStrings.PushCommand, SYNC_GROUP, 140),
                ExtensionsItem(SYNC, GitStrings.SyncCommand, SYNC_GROUP, 150),
                new ExtensionMenuContribution(TOGGLE_HISTORY_VIEW, GitStrings.HistoryViewTitle, ExtensionMenuLocations.View, "views.bottom", 55),
                ExtensionsItem(SHOW_HISTORY, GitStrings.ShowHistoryCommand, HISTORY_GROUP, 160),
                ContextItem(ExtensionMenuLocations.EditorContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.EditorContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.EditorContext, DISCARD, GitStrings.DiscardCommand, 30),
                ContextItem(ExtensionMenuLocations.EditorContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40),
                ContextItem(ExtensionMenuLocations.ExplorerContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.ExplorerContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.ExplorerContext, DISCARD, GitStrings.DiscardCommand, 30),
                ContextItem(ExtensionMenuLocations.ExplorerContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, STAGE, GitStrings.StageCommand, 10),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, UNSTAGE, GitStrings.UnstageCommand, 20),
                ContextItem(ExtensionMenuLocations.DocumentTabContext, SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, 40)
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
                new ExtensionCommandPaletteContribution(SHOW_FILE_HISTORY, GitStrings.ShowFileHistoryCommand, CATEGORY)
            ]));

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
