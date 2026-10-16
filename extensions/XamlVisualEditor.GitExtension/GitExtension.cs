using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// Git integration: the Git Changes panel, a status bar item with branch and changes, the "Git" output channel,
/// context keys and the Git commands. All commands are registered on activation, also without git or repository;
/// their <see cref="CommandMetadata.When"/> conditions disable them.
/// </summary>
public sealed class GitExtension : IXveExtension
{
    private readonly GitServiceSet _services;
    private readonly IWorkspaceInfo _workspaceInfo;
    private readonly IDialogHost _dialogHost;

    /// <summary>Creates the extension with the capabilities <paramref name="gitService"/> implements.</summary>
    public GitExtension(IGitService gitService, IWorkspaceInfo workspaceInfo, IDialogHost dialogHost)
        : this(gitService, GitServiceSet.FromService(gitService), workspaceInfo, dialogHost)
    {
    }

    /// <summary>
    /// Creates the extension with explicit git services. <paramref name="gitService"/> replaces the basic service of
    /// <paramref name="services"/> when the two differ.
    /// </summary>
    public GitExtension(IGitService gitService, GitServiceSet services, IWorkspaceInfo workspaceInfo, IDialogHost dialogHost)
    {
        _services = ReferenceEquals(services.Service, gitService)
            ? services
            : new GitServiceSet(gitService) { Repository = services.Repository, CommandLog = services.CommandLog };
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

        // The panel has one view model, created when it is first shown and disposed with the extension.
        Lazy<GitPanelViewModel> panel = new(() => new GitPanelViewModel(session, _dialogHost));
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

        ExtensionViewContribution view = new(
            GitCommands.CHANGES_VIEW_ID,
            GitStrings.ChangesViewTitle,
            ExtensionViewType.Custom,
            ExtensionViewLocation.Bottom,
            40);
        context.Subscriptions.Add(context.Contributions.RegisterViews(context.ExtensionId, new[] { view }));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            GitCommands.CHANGES_VIEW_ID,
            new DelegateViewProvider(() => panel.Value)));
        GitCommands.RegisterCommands(context.Commands, context.CommandMetadata, handler, context.Subscriptions);
        GitCommands.RegisterMenuAndPaletteItems(context.ExtensionId, context.Contributions, context.Subscriptions);

        IContextKeys? contextKeys = context.ContextKeys;
        CancellationTokenSource lifetime = new();
        IDisposable snapshotSubscription = session.Snapshots.Subscribe(snapshot => Dispatcher.UIThread.Post(() =>
        {
            if (lifetime.IsCancellationRequested)
            {
                return;
            }

            GitStatusBar.Update(statusBarItem, snapshot);
            UpdateContextKeys(contextKeys, snapshot);
        }));

        context.Subscriptions.Add(new ActionDisposable(() =>
        {
            lifetime.Cancel();
            snapshotSubscription.Dispose();
            if (commandLog is not null)
            {
                commandLog.CommandCompleted -= commandLogHandler;
            }

            if (panel.IsValueCreated)
            {
                panel.Value.Dispose();
            }

            session.Dispose();
            statusBarItem.Dispose();
            outputChannel.Dispose();
            lifetime.Dispose();
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
