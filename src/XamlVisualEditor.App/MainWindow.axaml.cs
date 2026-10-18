using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reactive;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.App.Views;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App;

/// <summary>
/// Main application window.
/// </summary>
public sealed partial class MainWindow : Window
{
    private IDisposable? _openFileHandler;
    private IDisposable? _saveFileHandler;
    private IDisposable? _renameSymbolHandler;
    private IDisposable? _definitionPickerHandler;
    private IDisposable? _codeActionPickerHandler;
    private IDisposable? _workspaceSymbolQueryHandler;
    private IDisposable? _layoutNameHandler;
    private IDisposable? _confirmLayoutDeleteHandler;
    private IDisposable? _saveChangesHandler;
    private IDisposable? _saveConflictHandler;
    private bool _isConfirmingClose;
    private bool _isWaitingForGit;
    private bool _closeDespiteGit;
    private IDisposable? _commandPaletteHandler;
    private IDisposable? _extensionPackageOpenHandler;
    private IDisposable? _extensionManagerHandler;
    private ExtensionManagerDialog? _extensionManagerDialog;
    private IDisposable? _previewerTrustHandler;
    private IDisposable? _debugToolConsentHandler;
    private MainWindowViewModel? _keyBindingsSource;
    private readonly List<KeyBinding> _extensionKeyBindings = new();

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(MainWindowViewModel? viewModel)
    {
        if (viewModel is not null)
        {
            DataContext = viewModel;
        }

        InitializeComponent();

        // Register interaction handlers when DataContext is set
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm)
            {
                RegisterInteractions(vm);
            }
        };

        // Dispose handlers when window is closing
        Closing += (_, e) =>
        {
            // Modified documents keep the window open until the user decides; a confirmed close runs again.
            if (!e.Cancel && DataContext is MainWindowViewModel closingVm && !closingVm.IsShutdownConfirmed
                && closingVm.Documents.Any(document => document.IsModified))
            {
                e.Cancel = true;
                if (_isConfirmingClose) return;
                _isConfirmingClose = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    try
                    {
                        if (!await closingVm.ConfirmShutdownAsync()) return;
                    }
                    finally
                    {
                        _isConfirmingClose = false;
                    }
                    Close();
                });
                return;
            }

            // A writing git command is not stopped half way and outlives the editor, but it can fail when it writes
            // to the output the editor closed; the window waits for it, and after a while the user can close anyway.
            if (!e.Cancel && WhenGitWritesEnded is { } whenWritesEnded && !_closeDespiteGit && !whenWritesEnded().IsCompleted)
            {
                e.Cancel = true;
                if (_isWaitingForGit) return;
                _isWaitingForGit = true;
                Dispatcher.UIThread.Post(async () =>
                {
                    bool close;
                    try
                    {
                        close = await WaitForGitWritesAsync(whenWritesEnded);
                    }
                    finally
                    {
                        _isWaitingForGit = false;
                    }

                    // Both approvals hold for this close only: the editor that stays open asks again next time.
                    if (!close)
                    {
                        (DataContext as MainWindowViewModel)?.ResetShutdownConfirmation();
                        return;
                    }

                    _closeDespiteGit = true;
                    Close();
                    _closeDespiteGit = false;
                });
                return;
            }

            // Closing the window takes the tabs out of the visual tree; they have to let go of their dockables
            // before, while they can still be found, or the layout save on shutdown logs a binding error per tab.
            if (!e.Cancel)
            {
                DockViewRelease.Release(this);
            }


            _openFileHandler?.Dispose();
            _saveFileHandler?.Dispose();
            _renameSymbolHandler?.Dispose();
            _definitionPickerHandler?.Dispose();
            _codeActionPickerHandler?.Dispose();
            _workspaceSymbolQueryHandler?.Dispose();
            _layoutNameHandler?.Dispose();
            _confirmLayoutDeleteHandler?.Dispose();
            _saveChangesHandler?.Dispose();
            _saveConflictHandler?.Dispose();
            _commandPaletteHandler?.Dispose();
            _extensionPackageOpenHandler?.Dispose();
            _extensionManagerHandler?.Dispose();
            _previewerTrustHandler?.Dispose();
            _debugToolConsentHandler?.Dispose();
            UnbindExtensionKeyBindings();
        };

        if (DataContext is MainWindowViewModel vm)
        {
            RegisterInteractions(vm);
        }
    }

    /// <summary>
    /// Gets or sets what returns a task that completes when no writing git command runs; the window waits for it before
    /// it closes.
    /// </summary>
    public Func<Task>? WhenGitWritesEnded { get; set; }

    /// <summary>
    /// Shows a progress until the writing git commands ended and returns true then. After
    /// <see cref="GitWriteCloseDelay"/> its button, and as with any progress its window button and Escape, ask whether
    /// to close anyway, for a command that hangs, for example in a hook; only a confirmation returns true, otherwise
    /// the editor stays open.
    /// </summary>
    private async Task<bool> WaitForGitWritesAsync(Func<Task> whenWritesEnded)
    {
        ProgressDialog progress = new()
        {
            DataContext = new ProgressDialogViewModel(
                UiStrings.GitWritesClosingTitle,
                UiStrings.GitWritesClosingMessage,
                UiStrings.GitWritesCloseAnyway,
                GitWriteCloseDelay)
        };
        Task<bool?> shown = progress.ShowDialog<bool?>(this);
        Task ended = whenWritesEnded();
        if (await Task.WhenAny(ended, shown) == ended)
        {
            progress.Close();
            await shown;
            return true;
        }

        if (await shown != true)
        {
            return false;
        }

        ConfirmDialog question = new()
        {
            DataContext = new ConfirmDialogViewModel(
                UiStrings.GitWritesClosingTitle,
                UiStrings.GitWritesCloseAnywayPrompt,
                UiStrings.GitWritesCloseAnyway,
                isDestructive: true)
        };
        // The question is about git that still runs: when it ends, the question closes and the editor closes safely.
        Task<bool> answer = question.ShowDialog<bool>(this);
        if (await Task.WhenAny(ended, answer) == ended)
        {
            question.Close();
            await answer;
            return true;
        }

        return await answer;
    }

    /// <summary>Gets how long the window waits for the writing git commands before it offers to close anyway.</summary>
    internal static TimeSpan GitWriteCloseDelay { get; set; } = TimeSpan.FromSeconds(15);

    private void RegisterInteractions(MainWindowViewModel vm)
    {
        // Dispose previous handlers
        _openFileHandler?.Dispose();
        _saveFileHandler?.Dispose();
        _renameSymbolHandler?.Dispose();
        _definitionPickerHandler?.Dispose();
        _codeActionPickerHandler?.Dispose();
        _workspaceSymbolQueryHandler?.Dispose();
        _layoutNameHandler?.Dispose();
        _confirmLayoutDeleteHandler?.Dispose();
        _saveChangesHandler?.Dispose();
        _saveConflictHandler?.Dispose();
        _commandPaletteHandler?.Dispose();
        _extensionPackageOpenHandler?.Dispose();
        _extensionManagerHandler?.Dispose();
        _previewerTrustHandler?.Dispose();
        _debugToolConsentHandler?.Dispose();

        // Open file dialog interaction
        _openFileHandler = vm.OpenFileInteraction.RegisterHandler(async interaction =>
        {
            FilePickerOpenOptions options = new()
            {
                Title = UiStrings.OpenFileTitle,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(UiStrings.AllSupportedFiles)
                    {
                        Patterns = new[] { "*.axaml", "*.xaml", "*.cs" }
                    },
                    new FilePickerFileType(UiStrings.XamlFiles) { Patterns = new[] { "*.axaml", "*.xaml" } },
                    new FilePickerFileType(UiStrings.CSharpFiles) { Patterns = new[] { "*.cs" } },
                    new FilePickerFileType(UiStrings.AllFiles) { Patterns = new[] { "*" } }
                }
            };

            var files = await StorageProvider.OpenFilePickerAsync(options);
            if (files.Count > 0)
            {
                interaction.SetOutput(files[0].Path.LocalPath);
            }
            else
            {
                interaction.SetOutput(null);
            }
        });

        // Save file dialog interaction
        _saveFileHandler = vm.SaveFileInteraction.RegisterHandler(async interaction =>
        {
            FilePickerSaveOptions options = new()
            {
                Title = UiStrings.SaveXamlFile,
                DefaultExtension = ".axaml",
                SuggestedFileName = interaction.Input,
                FileTypeChoices = new[]
                {
                    new FilePickerFileType(UiStrings.XamlFiles) { Patterns = new[] { "*.axaml", "*.xaml" } },
                    new FilePickerFileType(UiStrings.AllFiles) { Patterns = new[] { "*" } }
                }
            };

            var file = await StorageProvider.SaveFilePickerAsync(options);
            interaction.SetOutput(file?.Path.LocalPath);
        });

        _renameSymbolHandler = vm.RenameSymbolInteraction.RegisterHandler(async interaction =>
        {
            LanguageRenameInfo info = interaction.Input;
            RenameSymbolDialogViewModel dialogVm = new(
                "Rename Symbol",
                "New name:",
                info.Name);
            RenameSymbolDialog dialog = new()
            {
                DataContext = dialogVm
            };

            string? result = await dialog.ShowDialog<string?>(this);
            interaction.SetOutput(result);
        });

        _definitionPickerHandler = vm.SelectDefinitionInteraction.RegisterHandler(async interaction =>
        {
            DefinitionPickerDialogViewModel dialogVm = new(interaction.Input);
            DefinitionPickerDialog dialog = new()
            {
                DataContext = dialogVm
            };

            ReferenceLocationViewModel? result = await dialog.ShowDialog<ReferenceLocationViewModel?>(this);
            interaction.SetOutput(result);
        });

        _codeActionPickerHandler = vm.SelectCodeActionInteraction.RegisterHandler(async interaction =>
        {
            CodeActionPickerDialogViewModel dialogVm = new(interaction.Input);
            CodeActionPickerDialog dialog = new()
            {
                DataContext = dialogVm
            };

            LanguageCodeAction? result = await dialog.ShowDialog<LanguageCodeAction?>(this);
            interaction.SetOutput(result);
        });

        _workspaceSymbolQueryHandler = vm.WorkspaceSymbolQueryInteraction.RegisterHandler(async interaction =>
        {
            RenameSymbolDialogViewModel dialogVm = new(
                "Workspace Symbols",
                "Search:",
                interaction.Input ?? string.Empty);
            RenameSymbolDialog dialog = new()
            {
                DataContext = dialogVm
            };

            string? result = await dialog.ShowDialog<string?>(this);
            interaction.SetOutput(result);
        });

        _layoutNameHandler = vm.LayoutNameInteraction.RegisterHandler(async interaction =>
        {
            InputBoxDialogViewModel dialogVm = new(new XamlVisualEditor.Extensions.InputBoxOptions(
                UiStrings.LayoutSaveTitle,
                UiStrings.LayoutSavePrompt,
                interaction.Input));
            InputBoxDialog dialog = new()
            {
                DataContext = dialogVm
            };

            string? result = await dialog.ShowDialog<string?>(this);
            interaction.SetOutput(result);
        });

        _confirmLayoutDeleteHandler = vm.ConfirmLayoutDeleteInteraction.RegisterHandler(async interaction =>
        {
            ConfirmDialogViewModel dialogVm = new(
                UiStrings.ConfirmDelete,
                string.Format(UiStrings.LayoutDeletePrompt, interaction.Input),
                UiStrings.Delete);
            ConfirmDialog dialog = new()
            {
                DataContext = dialogVm
            };

            bool result = await dialog.ShowDialog<bool>(this);
            interaction.SetOutput(result);
        });

        _saveChangesHandler = vm.SaveChangesInteraction.RegisterHandler(async interaction =>
        {
            SaveChangesDialog dialog = new()
            {
                DataContext = new SaveChangesDialogViewModel(interaction.Input)
            };
            interaction.SetOutput(await dialog.ShowDialog<SaveChangesChoice>(this));
        });

        _saveConflictHandler = vm.SaveConflictInteraction.RegisterHandler(async interaction =>
        {
            SaveConflictDialog dialog = new()
            {
                DataContext = new SaveConflictDialogViewModel(interaction.Input)
            };
            interaction.SetOutput(await dialog.ShowDialog<SaveConflictChoice>(this));
        });

        _commandPaletteHandler = vm.CommandPaletteInteraction.RegisterHandler(async interaction =>
        {
            using CommandPaletteDialogViewModel dialogVm = new(interaction.Input);
            CommandPaletteDialog dialog = new()
            {
                DataContext = dialogVm
            };

            ExtensionCommandPaletteItemViewModel? result =
                await dialog.ShowDialog<ExtensionCommandPaletteItemViewModel?>(this);
            interaction.SetOutput(result);
        });

        _extensionManagerHandler = vm.ExtensionManagerInteraction.RegisterHandler(async interaction =>
        {
            if (_extensionManagerDialog is not null)
            {
                _extensionManagerDialog.Activate();
                interaction.SetOutput(RxVoid.Default);
                return;
            }

            ExtensionManagerDialog dialog = new() { DataContext = vm.ExtensionManager };
            _extensionManagerDialog = dialog;
            try
            {
                await dialog.ShowDialog(this);
            }
            finally
            {
                _extensionManagerDialog = null;
            }
            interaction.SetOutput(RxVoid.Default);
        });

        _extensionPackageOpenHandler = vm.ExtensionPackageOpenInteraction.RegisterHandler(async interaction =>
        {
            FilePickerOpenOptions options = new()
            {
                Title = UiStrings.InstallExtensionPackage,
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType(UiStrings.NuGetPackage) { Patterns = new[] { "*.nupkg" } },
                    new FilePickerFileType(UiStrings.AllFiles) { Patterns = new[] { "*" } }
                }
            };

            var files = await (_extensionManagerDialog ?? (Window)this).StorageProvider.OpenFilePickerAsync(options);
            if (files.Count > 0)
            {
                interaction.SetOutput(files[0].Path.LocalPath);
            }
            else
            {
                interaction.SetOutput(null);
            }
        });

        _previewerTrustHandler = vm.PreviewerTrustInteraction.RegisterHandler(async interaction =>
        {
            PreviewerTrustDialogViewModel dialogVm = new(interaction.Input);
            PreviewerTrustDialog dialog = new()
            {
                DataContext = dialogVm
            };

            PreviewerTrustDecision result = await dialog.ShowDialog<PreviewerTrustDecision>(this);
            interaction.SetOutput(result);
        });

        _debugToolConsentHandler = vm.DebugToolConsentInteraction.RegisterHandler(async interaction =>
        {
            DebugToolConsentDialogViewModel dialogVm = new(interaction.Input);
            DebugToolConsentDialog dialog = new()
            {
                DataContext = dialogVm
            };

            bool result = await dialog.ShowDialog<bool>(this);
            interaction.SetOutput(result);
        });

        BindExtensionKeyBindings(vm);
    }

    private void BindExtensionKeyBindings(MainWindowViewModel vm)
    {
        if (_keyBindingsSource is not null)
        {
            _keyBindingsSource.ExtensionKeyBindings.CollectionChanged -= OnExtensionKeyBindingsChanged;
        }

        ClearExtensionKeyBindings();
        _keyBindingsSource = vm;
        _keyBindingsSource.ExtensionKeyBindings.CollectionChanged += OnExtensionKeyBindingsChanged;
        ApplyExtensionKeyBindings(vm);
    }

    private void UnbindExtensionKeyBindings()
    {
        if (_keyBindingsSource is not null)
        {
            _keyBindingsSource.ExtensionKeyBindings.CollectionChanged -= OnExtensionKeyBindingsChanged;
            _keyBindingsSource = null;
        }

        ClearExtensionKeyBindings();
    }

    private void OnExtensionKeyBindingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_keyBindingsSource is null)
        {
            return;
        }

        ApplyExtensionKeyBindings(_keyBindingsSource);
    }

    private void ApplyExtensionKeyBindings(MainWindowViewModel vm)
    {
        ClearExtensionKeyBindings();
        HashSet<string> seenGestures = new(StringComparer.OrdinalIgnoreCase);

        foreach (ExtensionKeyBindingViewModel entry in vm.ExtensionKeyBindings)
        {
            if (string.IsNullOrWhiteSpace(entry.Gesture) || !seenGestures.Add(entry.Gesture))
            {
                continue;
            }

            if (!TryParseKeyGesture(entry.Gesture, out KeyGesture? gesture) || gesture is null)
            {
                continue;
            }

            KeyBinding binding = new()
            {
                Gesture = gesture,
                Command = entry.Scope == XamlVisualEditor.Extensions.CommandKeybindingScope.TextEditor
                    ? new TextEditorFocusCommand(this, entry.Command)
                    : entry.CommandId is "editing.undo" or "editing.redo" or "editing.cut" or "editing.copy"
                        or "editing.paste" or "editing.delete" or "editing.selectAll" or "editing.duplicate"
                        ? new FocusAwareEditingCommand(this, entry.Command) : entry.Command
            };

            KeyBindings.Add(binding);
            _extensionKeyBindings.Add(binding);
        }
    }

    private void ClearExtensionKeyBindings()
    {
        foreach (KeyBinding binding in _extensionKeyBindings)
        {
            KeyBindings.Remove(binding);
        }

        _extensionKeyBindings.Clear();
    }

    private sealed class FocusAwareEditingCommand(Window window, ICommand command) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => command.CanExecuteChanged += value;
            remove => command.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter)
        {
            for (Visual? visual = window.FocusManager?.GetFocusedElement() as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is TextBox or AvaloniaEdit.TextEditor
                    or PreviewerSurfaceControl { Document.DesignSurface.IsEditMode: false }
                    or DesignSurfaceView { DataContext: DesignSurfaceViewModel { IsEditMode: false } }) return false;
            }
            return command.CanExecute(parameter);
        }

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) command.Execute(parameter);
        }
    }

    // Runs a keybinding only while a text or code editor has the keyboard focus; elsewhere the keys stay free.
    private sealed class TextEditorFocusCommand(Window window, ICommand command) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add => command.CanExecuteChanged += value;
            remove => command.CanExecuteChanged -= value;
        }

        public bool CanExecute(object? parameter)
        {
            for (Visual? visual = window.FocusManager?.GetFocusedElement() as Visual; visual is not null; visual = visual.GetVisualParent())
            {
                if (visual is AvaloniaEdit.TextEditor)
                {
                    return command.CanExecute(parameter);
                }
            }

            return false;
        }

        public void Execute(object? parameter)
        {
            if (CanExecute(parameter)) command.Execute(parameter);
        }
    }

    private static bool TryParseKeyGesture(string value, out KeyGesture? gesture)
    {
        gesture = null;

        try
        {
            gesture = KeyGesture.Parse(value);
            return gesture is not null;
        }
        catch
        {
            return false;
        }
    }
}
