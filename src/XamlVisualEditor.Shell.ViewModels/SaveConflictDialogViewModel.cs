using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Asks whether a file that changed on disk is overwritten with the editor text or reloaded from disk.</summary>
public sealed class SaveConflictDialogViewModel : ReactiveObject
{
    public SaveConflictDialogViewModel(string fileName)
    {
        Message = string.Format(UiStrings.SaveConflictPrompt, fileName);
        OverwriteCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveConflictChoice.Overwrite); });
        ReloadCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveConflictChoice.Reload); });
        CancelCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveConflictChoice.Cancel); });
    }

    public string Title => UiStrings.SaveConflictTitle;
    public string Message { get; }
    public string OverwriteText => UiStrings.Overwrite;
    public string ReloadText => UiStrings.Reload;
    public string CancelText => UiStrings.Cancel;

    public Interaction<SaveConflictChoice, RxVoid> CloseInteraction { get; } = new();
    public ReactiveCommand<RxVoid, RxVoid> OverwriteCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> ReloadCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}
