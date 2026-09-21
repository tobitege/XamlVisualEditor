using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Xaml.Interactivity;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>Öffnet die Optionen und die zugehörige Auswahl einer Shell-Datei.</summary>
public sealed class OptionsDialogBehavior : Behavior<Window>
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
        _registration = viewModel.OptionsInteraction.RegisterHandler(async interaction =>
        {
            OptionsDialogViewModel options = interaction.Input;
            OptionsDialog dialog = new() { DataContext = options };
            void Close(bool saved) => dialog.Close(saved);
            options.CloseRequested += Close;
            using IDisposable browse = options.BrowseExecutableInteraction.RegisterHandler(async request =>
            {
                FilePickerOpenOptions picker = new()
                {
                    Title = UiStrings.ExecutablePickerTitle,
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(UiStrings.ExecutableFiles) { Patterns = new[] { "*.exe" } } }
                };
                string current = request.Input switch
                {
                    TerminalShell.CommandPrompt => options.CommandPromptPath,
                    TerminalShell.WindowsPowerShell => options.WindowsPowerShellPath,
                    _ => options.PowerShell7Path
                };
                if (File.Exists(current) && Path.GetDirectoryName(current) is { } directory)
                    picker.SuggestedStartLocation = await dialog.StorageProvider.TryGetFolderFromPathAsync(directory);
                IReadOnlyList<IStorageFile> files = await dialog.StorageProvider.OpenFilePickerAsync(picker);
                request.SetOutput(files.FirstOrDefault()?.TryGetLocalPath());
            });
            try { interaction.SetOutput(await dialog.ShowDialog<bool>(owner)); }
            finally { options.CloseRequested -= Close; }
        });
    }
}
