using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// Git integration: the Git Changes and Git Repository panels, a status bar item with branch and changes, the "Git" output channel,
/// context keys and the Git commands. All commands are registered on activation, also without git or repository;
/// their <see cref="CommandMetadata.When"/> conditions disable them.
/// </summary>
public sealed class GitExtension : IXveExtension
{
    private readonly GitServiceSet _services;
    private readonly IWorkspaceInfo _workspaceInfo;
    private readonly IDialogHost _dialogHost;

    /// <summary>
    /// Creates the extension with the capabilities <paramref name="gitService"/> implements, such as
    /// <see cref="IGitRepositoryService"/> and <see cref="IGitBranchService"/>.
    /// </summary>
    public GitExtension(IGitService gitService, IWorkspaceInfo workspaceInfo, IDialogHost dialogHost)
    {
        _services = GitServiceSet.FromService(gitService);
        _workspaceInfo = workspaceInfo;
        _dialogHost = dialogHost;
    }

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        IOutputChannel outputChannel = context.Window.CreateOutputChannel(GitCommands.CATEGORY);
        GitOutputLogger logger = new(context.Logger, outputChannel);
        EventHandler<GitCommandLogEntry> commandLogHandler = (_, entry) => logger.LogCommand(entry);
        IGitCommandLog? commandLog = _services.CommandLog;
        if (commandLog is not null)
        {
            commandLog.CommandCompleted += commandLogHandler;
        }

        GitSession session = new(_services, _workspaceInfo, new GitSettingsStore(context.Storage), logger);

        ICommands commands = context.Commands;
        Func<string, IReadOnlyList<object?>?, Task> executeCommand =
            (commandId, arguments) => commands.ExecuteAsync(commandId, arguments, CancellationToken.None);

        // The panel has one view model, created when it is first shown and disposed with the extension.
        Lazy<GitPanelViewModel> panel = new(() => new GitPanelViewModel(session, executeCommand));
        GitCommandHandler handler = new(
            session,
            context.ViewHost,
            context.Window,
            _dialogHost,
            context.Editor,
            outputChannel,
            () => panel.IsValueCreated ? panel.Value : null,
            logger);

        IStatusBarItem statusBarItem = context.Window.CreateStatusBarItem(StatusBarAlignment.Left, GitStatusBar.PRIORITY);
        statusBarItem.CommandId = GitCommands.SHOW_CHANGES;
        statusBarItem.Hide();

        Lazy<GitRepositoryPanelViewModel> repositoryPanel = new(() => new GitRepositoryPanelViewModel(session, executeCommand));

        ExtensionViewContribution[] views =
        [
            new(GitCommands.CHANGES_VIEW_ID, GitStrings.ChangesViewTitle, ExtensionViewType.Custom, ExtensionViewLocation.Bottom, 40),
            new(GitCommands.REPOSITORY_VIEW_ID, GitStrings.RepositoryViewTitle, ExtensionViewType.Custom, ExtensionViewLocation.Left, 50)
        ];
        context.Subscriptions.Add(context.Contributions.RegisterViews(context.ExtensionId, views));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            GitCommands.CHANGES_VIEW_ID,
            new DelegateViewProvider(() => panel.Value)));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            GitCommands.REPOSITORY_VIEW_ID,
            new DelegateViewProvider(() => repositoryPanel.Value)));
        GitCommands.RegisterCommands(context.Commands, context.CommandMetadata, handler, context.Subscriptions);
        GitCommands.RegisterMenuAndPaletteItems(context.ExtensionId, context.Contributions, context.Subscriptions);

        IContextKeys? contextKeys = context.ContextKeys;
        IDisposable snapshotSubscription = session.Snapshots
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot =>
            {
                GitStatusBar.Update(statusBarItem, snapshot);
                UpdateContextKeys(contextKeys, snapshot);
            });

        context.Subscriptions.Add(new ActionDisposable(() =>
        {
            snapshotSubscription.Dispose();
            if (commandLog is not null)
            {
                commandLog.CommandCompleted -= commandLogHandler;
            }

            if (panel.IsValueCreated)
            {
                panel.Value.Dispose();
            }

            if (repositoryPanel.IsValueCreated)
            {
                repositoryPanel.Value.Dispose();
            }

            session.Dispose();
            statusBarItem.Dispose();
            outputChannel.Dispose();
        }));

        _ = session.StartAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>Sets the Git context keys from a snapshot.</summary>
    internal static void UpdateContextKeys(IContextKeys? contextKeys, GitSnapshot snapshot)
    {
        contextKeys?.Set(
        [
            new KeyValuePair<string, bool>(GitCommands.AVAILABLE_CONTEXT_KEY, snapshot.IsGitAvailable),
            new KeyValuePair<string, bool>(GitCommands.IS_REPOSITORY_CONTEXT_KEY, snapshot.IsRepository),
            new KeyValuePair<string, bool>(GitCommands.HAS_REMOTE_CONTEXT_KEY, snapshot.IsRepository && snapshot.HasRemote),
            new KeyValuePair<string, bool>(GitCommands.HAS_UPSTREAM_CONTEXT_KEY, snapshot.HasUpstream),
            new KeyValuePair<string, bool>(GitCommands.IS_MERGING_CONTEXT_KEY, snapshot.Operation == GitRepositoryOperation.Merging),
            new KeyValuePair<string, bool>(GitCommands.HAS_CONFLICTS_CONTEXT_KEY, snapshot.HasConflicts),
            new KeyValuePair<string, bool>(GitCommands.AUTO_REFRESH_CONTEXT_KEY, snapshot.IsWorkingTreeWatcherEnabled)
        ]);
    }

    private sealed class DelegateViewProvider : ICustomViewProvider
    {
        private readonly Func<object> _factory;

        public DelegateViewProvider(Func<object> factory)
        {
            _factory = factory;
        }

        public object? CreateViewModel() => _factory();
    }

    private sealed class ActionDisposable : IDisposable
    {
        private Action? _action;

        public ActionDisposable(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _action, null)?.Invoke();
        }
    }
}
