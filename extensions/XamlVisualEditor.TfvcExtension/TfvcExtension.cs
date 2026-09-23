using System;
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

    /// <inheritdoc />
    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        IOutputChannel outputChannel = context.Window.CreateOutputChannel("TFVC");
        TfvcOutputLogger logger = new(context.Logger, outputChannel);
        TfvcServerClient serverClient = new();
        TfvcSession session = new(
            new TfvcClient(new TfCommandRunner(new TfExeLocator())),
            serverClient,
            new TfvcSettingsStore(context.Storage),
            context.WorkspaceInfo,
            logger);
        TfvcAutoCheckOutService autoCheckOut = new(session, context.Editor, context.Window, logger);
        IStatusBarItem statusBarItem = context.Window.CreateStatusBarItem(StatusBarAlignment.Left, 40);
        statusBarItem.Tooltip = TfvcStrings.PendingChangesTitle;

        EventHandler statusHandler = (_, _) => Dispatcher.UIThread.Post(() => UpdateStatusBar(statusBarItem, autoCheckOut));
        autoCheckOut.ActiveDocumentStateChanged += statusHandler;

        ExtensionViewContribution[] views =
        [
            new(PENDING_CHANGES_VIEW_ID, TfvcStrings.PendingChangesTitle, ExtensionViewType.Custom, ExtensionViewLocation.Right, 20),
            new(SOURCE_CONTROL_EXPLORER_VIEW_ID, TfvcStrings.SourceControlExplorerTitle, ExtensionViewType.Custom, ExtensionViewLocation.Left, 40)
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
                context.SystemIcons))));
        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            SOURCE_CONTROL_EXPLORER_VIEW_ID,
            new DelegateViewProvider(() => new SourceControlExplorerViewModel(
                session,
                autoCheckOut,
                context.Editor,
                context.DialogHost,
                context.SystemIcons))));

        context.Subscriptions.Add(new ActionDisposable(() =>
        {
            autoCheckOut.ActiveDocumentStateChanged -= statusHandler;
            autoCheckOut.Dispose();
            statusBarItem.Dispose();
            session.Dispose();
            serverClient.Dispose();
            outputChannel.Dispose();
        }));

        autoCheckOut.Start();
        _ = InitializeAsync(session, logger);
        return Task.CompletedTask;
    }

    private static async Task InitializeAsync(TfvcSession session, IExtensionLogger logger)
    {
        try
        {
            await session.InitializeAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.Error("TFVC initialization failed.", ex);
        }
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
