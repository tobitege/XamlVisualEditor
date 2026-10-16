using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Services;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.TfvcExtension;

/// <summary>
/// Team Foundation Version Control integration: a source control explorer panel, a pending changes
/// panel and the automatic check-out of versioned files on their first modification in the editor.
/// </summary>
/// <remarks>
/// All operations run as the current Windows user through tf.exe and the TFVC REST API; the only
/// required setting is the project collection URL.
/// </remarks>
public sealed class TfvcExtension : IXveExtension
{
    /// <summary>View id of the pending changes panel.</summary>
    public const string PENDING_CHANGES_VIEW_ID = "tfvc.pendingChanges";

    /// <summary>View id of the source control explorer panel.</summary>
    public const string SOURCE_CONTROL_EXPLORER_VIEW_ID = "tfvc.sourceControlExplorer";

    /// <summary>View id of the history panel.</summary>
    public const string HISTORY_VIEW_ID = "tfvc.history";

    /// <summary>Command that switches to offline work.</summary>
    public const string WORK_OFFLINE_COMMAND_ID = "tfvc.workOffline";

    /// <summary>Command that connects again after offline work.</summary>
    public const string GO_ONLINE_COMMAND_ID = "tfvc.goOnline";

    /// <summary>Context key that is true while TFVC works offline.</summary>
    public const string OFFLINE_CONTEXT_KEY = "tfvc.offline";

    /// <summary>Context key that is true when a collection URL is configured.</summary>
    public const string CONFIGURED_CONTEXT_KEY = "tfvc.configured";

    /// <inheritdoc />
    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        // tf.exe exists only on Windows; on other systems the extension contributes no panels, menus or output channel.
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

        IOutputChannel outputChannel = context.Window.CreateOutputChannel("TFVC");
        TfvcOutputLogger logger = new(context.Logger, outputChannel);
        TfvcServerClient serverClient = new();
        TfvcSession session = new(
            new TfvcClient(new TfCommandRunner(new TfExeLocator(), logger)),
            serverClient,
            new TfvcSettingsStore(context.Storage),
            context.WorkspaceInfo,
            logger);
        TfvcAutoCheckOutService autoCheckOut = new(session, context.Editor, context.Window, logger, dialogHost: context.DialogHost);
        TfvcOfflineCoordinator offline = new(
            session,
            new TfvcOfflineReconciler(session, context.WorkspaceInfo, logger),
            context.Window,
            context.DialogHost,
            logger);
        IStatusBarItem statusBarItem = context.Window.CreateStatusBarItem(StatusBarAlignment.Left, 40);
        statusBarItem.Tooltip = TfvcStrings.PendingChangesTitle;

        EventHandler statusHandler = (_, _) => Dispatcher.UIThread.Post(() => UpdateStatusBar(statusBarItem, autoCheckOut));
        autoCheckOut.ActiveDocumentStateChanged += statusHandler;
        EventHandler contextKeysHandler = (_, _) => Dispatcher.UIThread.Post(() => UpdateContextKeys(context.ContextKeys, session));
        session.StateChanged += contextKeysHandler;
        UpdateContextKeys(context.ContextKeys, session);
        RegisterOfflineCommands(context, offline);
        context.Subscriptions.Add(context.DialogHost.RegisterDialog(
            TfvcReconcileDialogViewModel.DIALOG_ID,
            viewModel => new Views.TfvcReconcileDialog { DataContext = viewModel }));
        context.Subscriptions.Add(context.DialogHost.RegisterDialog(
            TfvcAddItemsDialogViewModel.DIALOG_ID,
            viewModel => new Views.TfvcAddItemsDialog { DataContext = viewModel }));

        TfvcChangesetDetailsViewModel changesetDetails = new(session);
        TfvcHistoryViewModel history = new(session, async changeset =>
        {
            await context.ViewHost.ShowAsync(PENDING_CHANGES_VIEW_ID, CancellationToken.None);
            await changesetDetails.ShowAsync(changeset, CancellationToken.None);
        });
        EventHandler<Models.TfvcChangesetInfo> changesetUpdatedHandler = (_, changeset) => history.UpdateChangeset(changeset);
        changesetDetails.ChangesetUpdated += changesetUpdatedHandler;
        Func<string, Task> showHistory = async path =>
        {
            await context.ViewHost.ShowAsync(HISTORY_VIEW_ID, CancellationToken.None);
            await history.ShowAsync(path, CancellationToken.None);
        };

        ExtensionViewContribution[] views =
        [
            new(PENDING_CHANGES_VIEW_ID, TfvcStrings.PendingChangesTitle, ExtensionViewType.Custom, ExtensionViewLocation.Right, 20),
            new(SOURCE_CONTROL_EXPLORER_VIEW_ID, TfvcStrings.SourceControlExplorerTitle, ExtensionViewType.Custom, ExtensionViewLocation.Left, 40),
            new(HISTORY_VIEW_ID, TfvcStrings.HistoryTitle, ExtensionViewType.Custom, ExtensionViewLocation.Bottom, 60)
        ];

        context.Subscriptions.Add(context.Contributions.RegisterViews(context.ExtensionId, views));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            PENDING_CHANGES_VIEW_ID,
            new DelegateViewProvider(() => new PendingChangesViewModel(
                session,
                autoCheckOut,
                context.Editor,
                context.Window,
                context.DialogHost,
                context.SystemIcons,
                showHistory,
                changesetDetails))));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            SOURCE_CONTROL_EXPLORER_VIEW_ID,
            new DelegateViewProvider(() => new SourceControlExplorerViewModel(
                session,
                autoCheckOut,
                context.Editor,
                context.DialogHost,
                context.SystemIcons,
                showHistory))));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            HISTORY_VIEW_ID,
            new DelegateViewProvider(() => history)));

        context.Subscriptions.Add(new ActionDisposable(() =>
        {
            autoCheckOut.ActiveDocumentStateChanged -= statusHandler;
            session.StateChanged -= contextKeysHandler;
            changesetDetails.ChangesetUpdated -= changesetUpdatedHandler;
            offline.Dispose();
            autoCheckOut.Dispose();
            statusBarItem.Dispose();
            session.Dispose();
            serverClient.Dispose();
            outputChannel.Dispose();
        }));

        autoCheckOut.Start();
        _ = InitializeAsync(session, offline, logger, context.BackgroundActivities?.Begin(TfvcStrings.InitializingStatus));
        return Task.CompletedTask;
    }

    private static async Task InitializeAsync(TfvcSession session, TfvcOfflineCoordinator offline, IExtensionLogger logger, IDisposable? activity)
    {
        try
        {
            try
            {
                await session.InitializeAsync(CancellationToken.None);
            }
            finally
            {
                // The offline prompt that may follow waits for the user, not for background work.
                activity?.Dispose();
            }

            await offline.HandleInitializedAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error(TfvcStrings.LogInitializationFailed, ex);
        }
    }

    private static void RegisterOfflineCommands(ExtensionContext context, TfvcOfflineCoordinator offline)
    {
        context.Subscriptions.Add(context.Commands.Register(WORK_OFFLINE_COMMAND_ID, _ => offline.GoOfflineAsync(CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(GO_ONLINE_COMMAND_ID, _ => offline.GoOnlineAsync(CancellationToken.None)));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            WORK_OFFLINE_COMMAND_ID,
            new CommandMetadata(TfvcStrings.MenuWorkOffline, "TFVC", When: CONFIGURED_CONTEXT_KEY + " && !" + OFFLINE_CONTEXT_KEY, Priority: 10)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            GO_ONLINE_COMMAND_ID,
            new CommandMetadata(TfvcStrings.MenuGoOnline, "TFVC", When: OFFLINE_CONTEXT_KEY, Priority: 20)));

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(
            context.ExtensionId,
            [
                new ExtensionMenuContribution(WORK_OFFLINE_COMMAND_ID, TfvcStrings.MenuWorkOffline, ExtensionMenuLocations.Extensions, "tfvc", 10)
                {
                    Submenu = "TFVC",
                    CheckedWhen = OFFLINE_CONTEXT_KEY
                },
                new ExtensionMenuContribution(GO_ONLINE_COMMAND_ID, TfvcStrings.MenuGoOnline, ExtensionMenuLocations.Extensions, "tfvc", 20)
                {
                    Submenu = "TFVC"
                }
            ]));
        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            [
                new ExtensionCommandPaletteContribution(WORK_OFFLINE_COMMAND_ID, TfvcStrings.MenuWorkOffline, "TFVC"),
                new ExtensionCommandPaletteContribution(GO_ONLINE_COMMAND_ID, TfvcStrings.MenuGoOnline, "TFVC")
            ]));
    }

    private static void UpdateContextKeys(IContextKeys? contextKeys, TfvcSession session)
    {
        contextKeys?.Set(
        [
            new KeyValuePair<string, bool>(CONFIGURED_CONTEXT_KEY, session.Settings.IsConfigured),
            new KeyValuePair<string, bool>(OFFLINE_CONTEXT_KEY, session.IsOffline)
        ]);
    }

    private static void UpdateStatusBar(IStatusBarItem item, TfvcAutoCheckOutService autoCheckOut)
    {
        if (autoCheckOut.ActiveDocumentPath is null
            || (autoCheckOut.ActiveDocumentState.Kind is Models.TfvcFileStateKind.Unknown or Models.TfvcFileStateKind.NotInWorkspace
                && !autoCheckOut.IsCheckingActiveDocument))
        {
            item.Hide();
            return;
        }

        item.Text = TfvcDisplayText.FormatFileState(autoCheckOut.ActiveDocumentState, autoCheckOut.IsCheckingActiveDocument);
        item.Show();
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
