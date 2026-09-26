using System.Globalization;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed record OptionChoice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Bearbeitet Optionen mit sofortiger Theme-Vorschau; nur Speichern übernimmt die Änderungen dauerhaft.</summary>
public sealed class OptionsDialogViewModel : ReactiveObject, IDisposable
{
    private readonly EditorOptionsStore _store;
    private readonly IEditorThemeService? _editorThemes;
    private readonly EditorThemeSelection _originalTheme;
    private bool _updatingThemes;
    private bool _saved;
    private bool _disposed;
    private string? _themePreviewError;
    private IReadOnlyList<EditorThemeProviderCatalog> _themeProviders = Array.Empty<EditorThemeProviderCatalog>();
    private EditorThemeProviderCatalog? _themeProvider;
    private EditorTheme? _theme;
    private OptionChoice<string> _language;
    private bool _restoreLastWorkspace;
    private bool _fileExplorerFoldersBeforeFiles;
    private bool _showFullPathInTitle;
    private OptionChoice<TerminalLocation> _location;
    private OptionChoice<TerminalShell> _shell;
    private string _commandPromptPath;
    private string _windowsPowerShellPath;
    private string _powerShell7Path;
    private string? _error;

    public OptionsDialogViewModel(EditorOptionsStore store, IEditorThemeService? editorThemes = null)
    {
        _store = store;
        _editorThemes = editorThemes;
        _originalTheme = editorThemes?.Selection ?? store.Current.EditorTheme;
        _language = Languages.Single(item => item.Value == store.Current.Language);
        _restoreLastWorkspace = store.Current.RestoreLastWorkspace;
        _fileExplorerFoldersBeforeFiles = store.Current.FileExplorerFoldersBeforeFiles;
        _showFullPathInTitle = store.Current.ShowFullPathInTitle;
        _location = Locations.Single(item => item.Value == store.Current.TerminalLocation);
        _shell = Shells.Single(item => item.Value == store.Current.TerminalShell);
        _commandPromptPath = store.Current.CommandPromptPath;
        _windowsPowerShellPath = store.Current.WindowsPowerShellPath;
        _powerShell7Path = store.Current.PowerShell7Path;
        _error = store.LoadError is { } error ? string.Format(CultureInfo.CurrentCulture, UiStrings.OptionsLoadError, error) : null;
        RefreshEditorThemes();
        if (_editorThemes is not null) _editorThemes.Changed += OnEditorThemesChanged;
        SaveCommand = ReactiveCommand.Create(Save);
        CancelCommand = ReactiveCommand.Create(() =>
        {
            RestoreOriginalTheme();
            CloseRequested?.Invoke(false);
        });
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
    public IReadOnlyList<EditorThemeProviderCatalog> EditorThemeProviders => _themeProviders;
    public IReadOnlyList<EditorTheme> EditorThemes => _themeProvider?.Themes ?? Array.Empty<EditorTheme>();
    public string? EditorThemeError => _themePreviewError ?? _themeProvider?.Error;
    public EditorThemeProviderCatalog? SelectedEditorThemeProvider
    {
        get => _themeProvider;
        set
        {
            if (ReferenceEquals(_themeProvider, value)) return;
            this.RaiseAndSetIfChanged(ref _themeProvider, value);
            this.RaisePropertyChanged(nameof(EditorThemes));
            this.RaisePropertyChanged(nameof(EditorThemeError));
            EditorTheme? firstTheme = value?.Themes.FirstOrDefault();
            bool themeUnchanged = _theme == firstTheme;
            SelectedEditorTheme = firstTheme;
            if (themeUnchanged) PreviewEditorTheme();
        }
    }
    public EditorTheme? SelectedEditorTheme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            this.RaiseAndSetIfChanged(ref _theme, value);
            PreviewEditorTheme();
        }
    }
    public OptionChoice<string> SelectedLanguage { get => _language; set => this.RaiseAndSetIfChanged(ref _language, value); }
    public bool RestoreLastWorkspace { get => _restoreLastWorkspace; set => this.RaiseAndSetIfChanged(ref _restoreLastWorkspace, value); }
    public bool FileExplorerFoldersBeforeFiles
    {
        get => _fileExplorerFoldersBeforeFiles;
        set => this.RaiseAndSetIfChanged(ref _fileExplorerFoldersBeforeFiles, value);
    }
    public bool ShowFullPathInTitle { get => _showFullPathInTitle; set => this.RaiseAndSetIfChanged(ref _showFullPathInTitle, value); }
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
            EditorTheme = _themeProvider is { } provider && _theme is { } theme
                ? new EditorThemeSelection(provider.Id, theme.Id) : _store.Current.EditorTheme,
            Language = SelectedLanguage.Value, RestoreLastWorkspace = RestoreLastWorkspace, FileExplorerFoldersBeforeFiles = FileExplorerFoldersBeforeFiles, ShowFullPathInTitle = ShowFullPathInTitle, TerminalLocation = SelectedLocation.Value, TerminalShell = SelectedShell.Value,
            CommandPromptPath = CommandPromptPath.Trim(), WindowsPowerShellPath = WindowsPowerShellPath.Trim(), PowerShell7Path = PowerShell7Path.Trim()
        };
        if (IsWindows && !File.Exists(options.ShellPath))
        {
            Error = string.Format(CultureInfo.CurrentCulture, UiStrings.ExecutableNotFound, options.ShellPath);
            return;
        }
        EditorThemeSelection? previousTheme = _editorThemes?.Selection;
        try
        {
            _editorThemes?.Apply(options.EditorTheme);
            _store.Save(options);
        }
        catch (Exception error)
        {
            if (_editorThemes is not null && previousTheme is not null && _editorThemes.Selection != previousTheme)
            {
                try { _editorThemes.Apply(previousTheme); }
                catch (Exception restoreError) { Error = string.Format(CultureInfo.CurrentCulture, UiStrings.OptionsSaveError, error.Message + Environment.NewLine + restoreError.Message); return; }
            }
            Error = string.Format(CultureInfo.CurrentCulture, UiStrings.OptionsSaveError, error.Message);
            return;
        }
        Error = null;
        _saved = true;
        CloseRequested?.Invoke(true);
    }

    private void PreviewEditorTheme()
    {
        if (_updatingThemes || _disposed || _editorThemes is null || _themeProvider is null || _theme is null) return;
        _updatingThemes = true;
        try
        {
            _editorThemes.Apply(new(_themeProvider.Id, _theme.Id));
            _themePreviewError = null;
        }
        catch (Exception error) { _themePreviewError = error.GetBaseException().Message; }
        finally
        {
            _updatingThemes = false;
            this.RaisePropertyChanged(nameof(EditorThemeError));
        }
    }

    private void RestoreOriginalTheme()
    {
        if (_saved || _editorThemes is null || _editorThemes.Selection == _originalTheme) return;
        _updatingThemes = true;
        try { _editorThemes.Restore(_originalTheme); }
        finally { _updatingThemes = false; }
    }

    private void OnEditorThemesChanged(object? sender, EventArgs args)
    {
        if (!_updatingThemes) RefreshEditorThemes();
    }

    private void RefreshEditorThemes()
    {
        EditorThemeSelection selection = _themeProvider is { } provider && _theme is { } theme
            ? new(provider.Id, theme.Id) : _originalTheme;
        List<EditorThemeProviderCatalog> providers = _editorThemes?.Catalogs.ToList() ?? new();
        if (_editorThemes is not null && providers.All(item => item.Id != selection.ProviderId))
            providers.Add(new(selection.ProviderId, selection.ProviderId,
                new[] { new EditorTheme(selection.ThemeId, selection.ThemeId, false) }, UiStrings.EditorThemeUnavailable));
        _updatingThemes = true;
        try
        {
            _themeProviders = providers;
            this.RaisePropertyChanged(nameof(EditorThemeProviders));
            SelectedEditorThemeProvider = providers.FirstOrDefault(item => item.Id == selection.ProviderId) ?? providers.FirstOrDefault();
            SelectedEditorTheme = EditorThemes.FirstOrDefault(item => item.Id == selection.ThemeId) ?? EditorThemes.FirstOrDefault();
        }
        finally { _updatingThemes = false; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_editorThemes is not null) _editorThemes.Changed -= OnEditorThemesChanged;
        RestoreOriginalTheme();
        SaveCommand.Dispose();
        CancelCommand.Dispose();
        BrowseCommand.Dispose();
    }
}
