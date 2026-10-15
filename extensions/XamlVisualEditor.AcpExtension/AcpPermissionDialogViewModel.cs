using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Threading.Tasks;
using ReactiveUI;
using XamlVisualEditor.Acp;
using XamlVisualEditor.Core.Localization;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.AcpExtension;

/// <summary>
/// ViewModel for ACP permission prompt dialog.
/// </summary>
public sealed class AcpPermissionDialogViewModel : ReactiveObject
{
    public string Title { get; } = UiStrings.PermissionRequired;
    public string Message { get; }
    public string SessionId { get; }
    public string? ToolTitle { get; }
    public string? ToolKind { get; }
    public string? ToolCallId { get; }

    public ObservableCollection<AcpPermissionOptionViewModel> Options { get; } = new();

    public Interaction<AcpPermissionOutcome, RxVoid> CloseInteraction { get; } = new();

    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public AcpPermissionDialogViewModel(AcpPermissionRequest request)
    {
        SessionId = request.SessionId;
        ToolTitle = request.ToolTitle;
        ToolKind = request.ToolKind;
        ToolCallId = request.ToolCallId;

        string toolLabel = !string.IsNullOrWhiteSpace(ToolTitle) ? ToolTitle : UiStrings.OperationFallback;
        string kindLabel = !string.IsNullOrWhiteSpace(ToolKind) ? ToolKind : UiStrings.ToolFallback;
        Message = string.Format(UiStrings.AgentPermissionRequest, kindLabel, toolLabel);

        foreach (AcpPermissionOption option in request.Options)
        {
            ReactiveCommand<RxVoid, RxVoid> selectCommand = ReactiveCommand.CreateFromTask(async () =>
                await CloseInteraction.Handle(AcpPermissionOutcome.Selected(option.OptionId)).ConfigureAwait(false));
            Options.Add(new AcpPermissionOptionViewModel(option, selectCommand));
        }

        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(AcpPermissionOutcome.Cancelled()).ConfigureAwait(false));
    }
}

public sealed class AcpPermissionOptionViewModel : ReactiveObject
{
    public string OptionId { get; }
    public string Name { get; }
    public string Kind { get; }
    public ReactiveCommand<RxVoid, RxVoid> SelectCommand { get; }

    public AcpPermissionOptionViewModel(AcpPermissionOption option, ReactiveCommand<RxVoid, RxVoid> selectCommand)
    {
        OptionId = option.OptionId;
        Name = option.Name;
        Kind = option.Kind;
        SelectCommand = selectCommand;
    }
}
