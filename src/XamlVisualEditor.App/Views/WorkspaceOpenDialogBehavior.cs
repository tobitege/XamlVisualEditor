using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Xaml.Interactivity;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>Connects the project selection, including its toolbox option, with the main window.</summary>
public sealed class WorkspaceOpenDialogBehavior : Behavior<Window>
{
    private IDisposable? _registration;

    protected override void OnAttached()
    {
        base.OnAttached();
        AssociatedObject!.DataContextChanged += OnDataContextChanged;
        Register();
    }

    protected override void OnDetaching()
    {
        AssociatedObject!.DataContextChanged -= OnDataContextChanged;
        _registration?.Dispose();
        _registration = null;
        base.OnDetaching();
    }

    private void OnDataContextChanged(object? sender, EventArgs args) => Register();

    private void Register()
    {
        _registration?.Dispose();
        _registration = null;
        if (AssociatedObject is not { DataContext: MainWindowViewModel viewModel } owner) return;
        _registration = viewModel.OpenWorkspaceInteraction.RegisterHandler(async interaction =>
        {
            using WorkspaceOpenDialogViewModel options = new();
            WorkspaceOpenDialog dialog = new() { DataContext = options };
            void Close(WorkspaceOpenRequest? result) => dialog.Close(result);
            options.CloseRequested += Close;
            using IDisposable browse = options.BrowseInteraction.RegisterHandler(async request =>
            {
                FilePickerOpenOptions picker = new()
                {
                    Title = UiStrings.OpenWorkspace,
                    AllowMultiple = false,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType(UiStrings.WorkspaceFiles) { Patterns = new[] { "*.csproj", "*.slnx", "*.sln" } },
                        new FilePickerFileType(UiStrings.Projects) { Patterns = new[] { "*.csproj" } },
                        new FilePickerFileType(UiStrings.Solutions) { Patterns = new[] { "*.slnx", "*.sln" } }
                    }
                };
                if (File.Exists(options.FilePath) && Path.GetDirectoryName(options.FilePath) is { } directory)
                    picker.SuggestedStartLocation = await dialog.StorageProvider.TryGetFolderFromPathAsync(directory);
                IReadOnlyList<IStorageFile> files = await dialog.StorageProvider.OpenFilePickerAsync(picker);
                request.SetOutput(files.FirstOrDefault()?.TryGetLocalPath());
            });
            try { interaction.SetOutput(await dialog.ShowDialog<WorkspaceOpenRequest?>(owner)); }
            finally { options.CloseRequested -= Close; }
        });
    }
}
