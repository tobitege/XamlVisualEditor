using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed record OptionChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Bearbeitet eine Kopie der Optionen und übernimmt sie erst beim Speichern.</summary>
public sealed class OptionsDialogViewModel : ReactiveObject, IDisposable
{
    private readonly EditorOptionsStore _store;
    private OptionChoice<string> _language;
    private OptionChoice<TerminalLocation> _location;
    private OptionChoice<TerminalShell> _shell;
    private string _commandPromptPath;
    private string _windowsPowerShellPath;
    private string _powerShell7Path;
    private string? _error;

    public OptionsDialogViewModel(EditorOptionsStore store)
    {
        _store = store;
        _language = Languages.Single(item => item.Value == store.Current.Language);
        _location = Locations.Single(item => item.Value == store.Current.TerminalLocation);
        _shell = Shells.Single(item => item.Value == store.Current.TerminalShell);
        _commandPromptPath = store.Current.CommandPromptPath;
        _windowsPowerShellPath = store.Current.WindowsPowerShellPath;
        _powerShell7Path = store.Current.PowerShell7Path;
        _error = store.LoadError is { } error ? string.Format(CultureInfo.CurrentCulture, UiStrings.OptionsLoadError, error) : null;
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(() => CloseRequested?.Invoke(false));
        BrowseCommand = ReactiveCommand.CreateFromTask<TerminalShell>(async (TerminalShell shell) =>
        {
            string? path = await BrowseExecutableInteraction.Handle(shell);
            if (path is null) return;
            switch (shell)
            {
                case TerminalShell.CommandPrompt: CommandPromptPath = path; break;
                case TerminalShell.WindowsPowerShell: WindowsPowerShellPath = path; break;
                case TerminalShell.PowerShell7: PowerShell7Path = path; break;
            }
        });
    }

    public IReadOnlyList<OptionChoice<string>> Languages { get; } = new[] { new OptionChoice<string>("en", "English"), new("de", "Deutsch") };
    public IReadOnlyList<OptionChoice<TerminalLocation>> Locations { get; } = new[]
    {
        new OptionChoice<TerminalLocation>(TerminalLocation.Internal, UiStrings.TerminalInternal), new(TerminalLocation.External, UiStrings.TerminalExternal)
    };
    public IReadOnlyList<OptionChoice<TerminalShell>> Shells { get; } = new[]
    {
        new OptionChoice<TerminalShell>(TerminalShell.CommandPrompt, UiStrings.CommandPrompt),
        new(TerminalShell.WindowsPowerShell, UiStrings.WindowsPowerShell), new(TerminalShell.PowerShell7, UiStrings.PowerShell7)
    };
    public bool IsWindows => OperatingSystem.IsWindows();
    public OptionChoice<string> SelectedLanguage { get => _language; set => this.RaiseAndSetIfChanged(ref _language, value); }
    public OptionChoice<TerminalLocation> SelectedLocation { get => _location; set => this.RaiseAndSetIfChanged(ref _location, value); }
    public OptionChoice<TerminalShell> SelectedShell { get => _shell; set => this.RaiseAndSetIfChanged(ref _shell, value); }
    public string CommandPromptPath { get => _commandPromptPath; set => this.RaiseAndSetIfChanged(ref _commandPromptPath, value); }
    public string WindowsPowerShellPath { get => _windowsPowerShellPath; set => this.RaiseAndSetIfChanged(ref _windowsPowerShellPath, value); }
    public string PowerShell7Path { get => _powerShell7Path; set => this.RaiseAndSetIfChanged(ref _powerShell7Path, value); }
    public string? Error { get => _error; private set => this.RaiseAndSetIfChanged(ref _error, value); }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<TerminalShell, Unit> BrowseCommand { get; }
    public Interaction<TerminalShell, string?> BrowseExecutableInteraction { get; } = new();
    public event Action<bool>? CloseRequested;

    private void Save()
    {
        EditorOptions options = new()
        {
            Language = SelectedLanguage.Value, TerminalLocation = SelectedLocation.Value, TerminalShell = SelectedShell.Value,
            CommandPromptPath = CommandPromptPath.Trim(), WindowsPowerShellPath = WindowsPowerShellPath.Trim(), PowerShell7Path = PowerShell7Path.Trim()
        };
        if (IsWindows && !File.Exists(options.ShellPath))
        {
            Error = string.Format(CultureInfo.CurrentCulture, UiStrings.ExecutableNotFound, options.ShellPath);
            return;
        }
        try { _store.Save(options); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Error = string.Format(CultureInfo.CurrentCulture, UiStrings.OptionsSaveError, error.Message);
            return;
        }
        Error = null;
        CloseRequested?.Invoke(true);
    }

    public void Dispose()
    {
        SaveCommand.Dispose();
        CancelCommand.Dispose();
        BrowseCommand.Dispose();
    }
}
