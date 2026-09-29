using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Holds the selection for opening a project or a solution.</summary>
public sealed record WorkspaceOpenRequest(string FilePath);

/// <summary>Captures the project file and the optional toolbox search.</summary>
public sealed partial class WorkspaceOpenDialogViewModel : ReactiveObject, IDisposable
{
    [Reactive] public partial string FilePath { get; set; } = string.Empty;
    [Reactive] public partial string? Error { get; private set; }

    public Interaction<Unit, string?> BrowseInteraction { get; } = new();
    public event Action<WorkspaceOpenRequest?>? CloseRequested;
    public ReactiveCommand<Unit, Unit> BrowseCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    public WorkspaceOpenDialogViewModel()
    {
        BrowseCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            try
            {
                string? path = await BrowseInteraction.Handle(Unit.Default);
                if (path is not null) FilePath = path;
                Error = null;
            }
            catch (Exception error) { Error = string.Format(UiStrings.WorkspacePickerFailed, error.Message); }
        });
        OpenCommand = ReactiveCommand.Create(() =>
        {
            string path = FilePath.Trim();
            string extension = System.IO.Path.GetExtension(path);
            if (!System.IO.File.Exists(path) || !(extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)))
            {
                Error = UiStrings.SelectProjectOrSolution;
                return;
            }
            CloseRequested?.Invoke(new WorkspaceOpenRequest(path));
        }, this.WhenAnyValue(x => x.FilePath).Select(path => !string.IsNullOrWhiteSpace(path)));
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(null));
    }

    public void Dispose()
    {
        BrowseCommand.Dispose();
        OpenCommand.Dispose();
        CancelCommand.Dispose();
    }
}
