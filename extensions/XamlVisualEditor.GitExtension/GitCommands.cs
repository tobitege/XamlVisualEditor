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

    private const string SUBMENU_GROUP = "git";
    private const string VIEW_GROUP = "1_view";
    private const string CHANGES_GROUP = "2_changes";
    private const string REPOSITORY_GROUP = "3_repository";
    private const string REPOSITORY_WHEN = IS_REPOSITORY_CONTEXT_KEY;

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

        void Register(string commandId, Func<CommandContext, Task> execute, CommandMetadata metadata)
        {
            subscriptions.Add(commands.Register(commandId, execute));
            subscriptions.Add(commandMetadata.Register(commandId, metadata));
        }
    }

    /// <summary>
    /// Registers the menu and Command Palette entries: the panel under View › Panels, the repository commands in
    /// the Extensions › Git submenu and the file commands in the Git submenu of the editor context menu.
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
                ExtensionsItem(STAGE_ALL, GitStrings.StageAllCommand, CHANGES_GROUP, 50),
                ExtensionsItem(UNSTAGE_ALL, GitStrings.UnstageAllCommand, CHANGES_GROUP, 60),
                ExtensionsItem(COMMIT, GitStrings.CommitCommand, CHANGES_GROUP, 70),
                ExtensionsItem(INIT, GitStrings.InitCommand, REPOSITORY_GROUP, 80),
                EditorContextItem(STAGE, GitStrings.StageCommand, 10),
                EditorContextItem(UNSTAGE, GitStrings.UnstageCommand, 20),
                EditorContextItem(DISCARD, GitStrings.DiscardCommand, 30)
            ]));
        subscriptions.Add(contributions.RegisterCommandPaletteItems(
            extensionId,
            [
                new ExtensionCommandPaletteContribution(TOGGLE_CHANGES_VIEW, GitStrings.ToggleChangesViewCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_CHANGES, GitStrings.ShowChangesCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(REFRESH, GitStrings.RefreshCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(SHOW_OUTPUT, GitStrings.ShowOutputCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(TOGGLE_AUTO_REFRESH, GitStrings.ToggleAutoRefreshCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(INIT, GitStrings.InitCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STAGE, GitStrings.StageCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(UNSTAGE, GitStrings.UnstageCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(DISCARD, GitStrings.DiscardCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(STAGE_ALL, GitStrings.StageAllCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(UNSTAGE_ALL, GitStrings.UnstageAllCommand, CATEGORY),
                new ExtensionCommandPaletteContribution(COMMIT, GitStrings.CommitCommand, CATEGORY)
            ]));

        static ExtensionMenuContribution ExtensionsItem(string commandId, string title, string submenuGroup, int priority) =>
            new(commandId, title, ExtensionMenuLocations.Extensions, SUBMENU_GROUP, priority)
            {
                Submenu = CATEGORY,
                SubmenuGroup = submenuGroup
            };

        static ExtensionMenuContribution EditorContextItem(string commandId, string title, int priority) =>
            new(commandId, title, ExtensionMenuLocations.EditorContext, SUBMENU_GROUP, priority)
            {
                Submenu = CATEGORY
            };
    }
}
