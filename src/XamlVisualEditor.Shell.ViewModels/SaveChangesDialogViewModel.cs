using System.Collections.Generic;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Asks whether modified files are saved, discarded or kept open.</summary>
public sealed class SaveChangesDialogViewModel : ReactiveObject
{
    public SaveChangesDialogViewModel(IReadOnlyList<string> fileNames)
    {
        Message = string.Format(UiStrings.SaveChangesPrompt, string.Join(System.Environment.NewLine, fileNames));
        SaveCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveChangesChoice.Save); });
        DiscardCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveChangesChoice.Discard); });
        CancelCommand = ReactiveCommand.CreateFromTask(async () => { await CloseInteraction.Handle(SaveChangesChoice.Cancel); });
    }

    public string Title => UiStrings.SaveChangesTitle;
    public string Message { get; }
    public string SaveText => UiStrings.Save;
    public string DiscardText => UiStrings.DontSave;
    public string CancelText => UiStrings.Cancel;

    public Interaction<SaveChangesChoice, RxVoid> CloseInteraction { get; } = new();
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> DiscardCommand { get; }
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }
}
