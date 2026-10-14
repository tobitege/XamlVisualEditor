using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.FileExplorerExtension.ViewModels;

public sealed partial class OpenFolderDialogViewModel : ReactiveObject
{
    public OpenFolderDialogViewModel(string? initialPath = null)
    {
        FolderPath = initialPath;

        BrowseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            string? path = await SelectFolderInteraction.Handle(RxVoid.Default);
            if (!string.IsNullOrWhiteSpace(path))
            {
                FolderPath = path;
            }
        });

        OpenCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (string.IsNullOrWhiteSpace(FolderPath))
            {
                return;
            }

            await CloseInteraction.Handle(FolderPath);
        });

        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
            await CloseInteraction.Handle(null));
    }

    [Reactive]
    public partial string? FolderPath { get; set; }

    public ReactiveCommand<RxVoid, RxVoid> BrowseCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> OpenCommand { get; }

    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    public Interaction<RxVoid, string?> SelectFolderInteraction { get; } = new();

    public Interaction<string?, RxVoid> CloseInteraction { get; } = new();
}
