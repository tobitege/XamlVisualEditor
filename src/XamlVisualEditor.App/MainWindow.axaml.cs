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
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.App.Views;

using XamlVisualEditor.Core.Localization;

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
                interaction.SetOutput(Unit.Default);
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
            interaction.SetOutput(Unit.Default);
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
