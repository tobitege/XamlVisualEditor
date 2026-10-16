using System;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.CollaborationExtension.Views;

namespace XamlVisualEditor.CollaborationExtension;

public sealed class CollaborationExtension : IXveExtension
{
    private const string CollaborationViewId = "collaboration.panel";
    private const string ToggleCollaborationCommandId = "collaboration.toggleView";
    private const string StartSessionCommandId = "collaboration.startSession";
    private const string JoinSessionCommandId = "collaboration.joinSession";
    private const string LeaveSessionCommandId = "collaboration.leaveSession";
    private const string ShareSessionCommandId = "collaboration.shareSession";
    private const string InviteParticipantCommandId = "collaboration.inviteParticipant";

    public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        CollaborationPanelToolViewModel panelViewModel = new(context.Collaboration, context.Window);
        context.Subscriptions.Add(panelViewModel);

        context.Subscriptions.Add(context.Commands.Register(
            ToggleCollaborationCommandId,
            _ => context.ViewHost.ToggleAsync(CollaborationViewId, CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(
            StartSessionCommandId,
            _ => context.Collaboration.StartSessionAsync(CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(
            JoinSessionCommandId,
            _ => JoinSessionAsync(context, CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(
            LeaveSessionCommandId,
            _ => context.Collaboration.LeaveSessionAsync(CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(
            ShareSessionCommandId,
            _ => ShareSessionAsync(context, CancellationToken.None)));
        context.Subscriptions.Add(context.Commands.Register(
            InviteParticipantCommandId,
            _ => InviteParticipantAsync(context, CancellationToken.None)));

        context.Subscriptions.Add(context.Contributions.RegisterViews(
            context.ExtensionId,
            new[]
            {
                new ExtensionViewContribution(
                    CollaborationViewId,
                    UiStrings.Collaboration,
                    ExtensionViewType.Custom,
                    ExtensionViewLocation.Bottom,
                    50,
                    ActivateByDefault: false)
            }));

        context.Subscriptions.Add(context.Contributions.RegisterMenuItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionMenuContribution(
                    ToggleCollaborationCommandId,
                    UiStrings.Collaboration,
                    ExtensionMenuLocations.View,
                    "views.bottom",
                    65)
            }));

        context.Subscriptions.Add(context.CommandMetadata.Register(
            ToggleCollaborationCommandId,
            new CommandMetadata(
                Title: UiStrings.ToggleCollaborationCommand,
                Category: UiStrings.View,
                Priority: 100)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            StartSessionCommandId,
            new CommandMetadata(
                Title: UiStrings.CollaborationStartCommand,
                Category: UiStrings.Collaboration,
                Priority: 10)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            JoinSessionCommandId,
            new CommandMetadata(
                Title: UiStrings.CollaborationJoinCommand,
                Category: UiStrings.Collaboration,
                Priority: 20)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            LeaveSessionCommandId,
            new CommandMetadata(
                Title: UiStrings.CollaborationLeaveCommand,
                Category: UiStrings.Collaboration,
                Priority: 30)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            ShareSessionCommandId,
            new CommandMetadata(
                Title: UiStrings.CollaborationShareCommand,
                Category: UiStrings.Collaboration,
                Priority: 40)));
        context.Subscriptions.Add(context.CommandMetadata.Register(
            InviteParticipantCommandId,
            new CommandMetadata(
                Title: UiStrings.CollaborationInviteCommand,
                Category: UiStrings.Collaboration,
                Priority: 50)));

        context.Subscriptions.Add(context.Contributions.RegisterCommandPaletteItems(
            context.ExtensionId,
            new[]
            {
                new ExtensionCommandPaletteContribution(StartSessionCommandId, UiStrings.StartCollaborationSession, UiStrings.Collaboration),
                new ExtensionCommandPaletteContribution(JoinSessionCommandId, UiStrings.JoinCollaborationSession, UiStrings.Collaboration),
                new ExtensionCommandPaletteContribution(LeaveSessionCommandId, UiStrings.LeaveCollaborationSession, UiStrings.Collaboration),
                new ExtensionCommandPaletteContribution(ShareSessionCommandId, UiStrings.CopyCollaborationLink, UiStrings.Collaboration),
                new ExtensionCommandPaletteContribution(InviteParticipantCommandId, UiStrings.InviteCollaborationParticipant, UiStrings.Collaboration),
                new ExtensionCommandPaletteContribution(ToggleCollaborationCommandId, UiStrings.ToggleCollaboration, UiStrings.View)
            }));

        context.Subscriptions.Add(context.Views.RegisterCustomViewProvider(
            CollaborationViewId,
            new CollaborationPanelViewProvider(panelViewModel)));

        IStatusBarItem statusBarItem = context.Window.CreateStatusBarItem(StatusBarAlignment.Left, 95);
        statusBarItem.CommandId = ToggleCollaborationCommandId;
        UpdateStatusBar(statusBarItem, context.Collaboration);
        context.Subscriptions.Add(statusBarItem);

        EventHandler<CollaborationSessionChangedEventArgs> sessionChanged = (_, _) =>
            UpdateStatusBar(statusBarItem, context.Collaboration);
        EventHandler<CollaborationParticipantsChangedEventArgs> participantsChanged = (_, _) =>
            UpdateStatusBar(statusBarItem, context.Collaboration);
        context.Collaboration.SessionChanged += sessionChanged;
        context.Collaboration.ParticipantsChanged += participantsChanged;
        context.Subscriptions.Add(System.Reactive.Disposables.Disposable.Create(() =>
        {
            context.Collaboration.SessionChanged -= sessionChanged;
            context.Collaboration.ParticipantsChanged -= participantsChanged;
        }));

        return Task.CompletedTask;
    }

    private static async Task JoinSessionAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        string? sessionId = await context.Window.ShowInputBoxAsync(
            new InputBoxOptions(
                UiStrings.JoinCollaborationSession,
                UiStrings.SessionIdPrompt,
                context.Collaboration.SessionId),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        bool joined = await context.Collaboration.JoinSessionAsync(sessionId, cancellationToken);
        if (!joined)
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.JoinCollaborationFailed,
                cancellationToken);
        }
    }

    private static async Task ShareSessionAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        string? link = await context.Collaboration.CreateShareLinkAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(link))
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.ShareSessionRequired,
                cancellationToken);
            return;
        }

        await context.Window.ShowInformationMessageAsync(
            UiStrings.ShareLinkPrefix + link,
            cancellationToken);
    }

    private static async Task InviteParticipantAsync(ExtensionContext context, CancellationToken cancellationToken)
    {
        string? invitee = await context.Window.ShowInputBoxAsync(
            new InputBoxOptions(
                UiStrings.InviteParticipantTitle,
                UiStrings.ParticipantPrompt,
                null),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(invitee))
        {
            return;
        }

        bool invited = await context.Collaboration.InviteAsync(invitee, cancellationToken);
        if (!invited)
        {
            await context.Window.ShowWarningMessageAsync(
                UiStrings.InviteSessionRequired,
                cancellationToken);
            return;
        }

        await context.Window.ShowInformationMessageAsync(
            UiStrings.InvitePreparedPrefix + invitee.Trim(),
            cancellationToken);
    }

    private static void UpdateStatusBar(IStatusBarItem statusBarItem, ICollaborationHost collaboration)
    {
        if (!collaboration.IsSessionActive)
        {
            statusBarItem.Hide();
            return;
        }

        int participantCount = collaboration.GetParticipants().Count;
        string session = string.IsNullOrWhiteSpace(collaboration.SessionId)
            ? UiStrings.Unknown
            : collaboration.SessionId!;

        statusBarItem.Text = string.Format(UiStrings.CollaborationCount, participantCount);
        statusBarItem.Tooltip = participantCount == 1
            ? string.Format(UiStrings.SessionOneParticipant, session)
            : string.Format(UiStrings.SessionParticipants, session, participantCount);
        statusBarItem.Show();
    }

    private sealed class CollaborationPanelViewProvider : ICustomViewProvider
    {
        private readonly CollaborationPanelToolViewModel _viewModel;

        public CollaborationPanelViewProvider(CollaborationPanelToolViewModel viewModel)
        {
            _viewModel = viewModel;
        }

        public object? CreateViewModel()
        {
            return _viewModel;
        }
    }
}
