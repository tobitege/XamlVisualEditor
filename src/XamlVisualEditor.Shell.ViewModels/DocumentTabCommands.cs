using System.IO;
using System.Reactive;
using ReactiveUI;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Commands of the context menu of a file document tab; each takes the full path of the file.</summary>
public sealed class DocumentTabCommands
{
    /// <summary>Creates the commands.</summary>
    /// <param name="revealInFileExplorer">Shows a file in the File Explorer panel.</param>
    /// <param name="revealInSolutionExplorer">Shows a file in the Solution Explorer.</param>
    /// <param name="copyText">Copies text to the clipboard.</param>
    public DocumentTabCommands(Func<string, Task> revealInFileExplorer, Action<string> revealInSolutionExplorer,
        Func<string, Task> copyText)
    {
        RevealInFileExplorerCommand = ReactiveCommand.CreateFromTask<string?>(path =>
            string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : revealInFileExplorer(path));
        RevealInSolutionExplorerCommand = ReactiveCommand.Create<string?>(path =>
        {
            if (!string.IsNullOrWhiteSpace(path)) revealInSolutionExplorer(path);
        });
        CopyPathCommand = ReactiveCommand.CreateFromTask<string?>(path =>
            string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : copyText(path));
        CopyFileNameCommand = ReactiveCommand.CreateFromTask<string?>(path =>
            string.IsNullOrWhiteSpace(path) ? Task.CompletedTask : copyText(Path.GetFileName(path)));
    }

    /// <summary>Gets the command that shows the File Explorer panel with the file selected.</summary>
    public ReactiveCommand<string?, RxVoid> RevealInFileExplorerCommand { get; }

    /// <summary>Gets the command that shows the Solution Explorer with the file selected.</summary>
    public ReactiveCommand<string?, RxVoid> RevealInSolutionExplorerCommand { get; }

    /// <summary>Gets the command that copies the full path of the file.</summary>
    public ReactiveCommand<string?, RxVoid> CopyPathCommand { get; }

    /// <summary>Gets the command that copies the file name with its extension.</summary>
    public ReactiveCommand<string?, RxVoid> CopyFileNameCommand { get; }
}
