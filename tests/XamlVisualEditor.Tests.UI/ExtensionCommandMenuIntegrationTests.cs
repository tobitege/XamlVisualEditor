using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using Xunit;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Debugging;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Language;
using XamlVisualEditor.Shell.ViewModels;
using WorkspaceExtensionEntry = XamlVisualEditor.WorkspaceExtension.WorkspaceExtension;
using TextEditingExtensionEntry = XamlVisualEditor.TextEditingExtension.TextEditingExtension;

namespace XamlVisualEditor.Tests.UI;

public sealed class ExtensionCommandMenuIntegrationTests
{
    [AvaloniaFact]
    public async Task Only_Enabled_Installed_Packages_With_A_Main_Assembly_Are_Activated_From_Their_Installation_Directory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-installed-packages-{Guid.NewGuid():N}");
        try
        {
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            FileExtensionStateStore state = new(Path.Combine(root, "extension-state.json"));
            ExtensionPackageInfo enabled = await store.InstallAsync(
                InstalledPackageProbe.CreatePackage(root, "enabled"), CancellationToken.None);
            ExtensionPackageInfo disabled = await store.InstallAsync(
                InstalledPackageProbe.CreatePackage(root, "disabled"), CancellationToken.None);
            await state.SetEnabledAsync(enabled.Manifest.ExtensionId, true, CancellationToken.None);

            using ExtensionMenuTestHost host = new(packageStore: store, stateStore: state);
            await host.ActivateAsync();

            Assert.Equal(enabled.InstallationDirectory, InstalledPackageProbe.GetActivationPath(enabled.Manifest.ExtensionId));
            Assert.Null(InstalledPackageProbe.GetActivationPath(disabled.Manifest.ExtensionId));
        }
        finally
        {
            InstalledPackageProbe.TryDeleteDirectory(root);
        }
    }

    [AvaloniaFact]
    public async Task Text_Editing_Extension_Edits_The_Active_Document_And_Checks_Toggles_In_Place()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-text-editing-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "int a;\nint b;\n");

        try
        {
            using ExtensionMenuTestHost host = new(withTextEditing: true);
            await host.ActivateAsync();
            await host.OpenFileAsync(filePath);
            TextDocumentViewModel document = host.ViewModel.ActiveTextDocument!;
            document.SetSelection(0, "int a;\nint b;".Length, "int a;\nint b;".Length);

            await host.ExecuteCommandAsync("textEditing.toggleComment");

            Assert.Equal("// int a;\n// int b;\n", await host.GetActiveTextAsync());
            Assert.Equal((0, "// int a;\n// int b;".Length), (document.SelectionStart, document.SelectionLength));
            Assert.Equal(document.SelectionStart + document.SelectionLength, document.CaretOffset);

            await host.ExecuteCommandAsync("editing.undo");
            Assert.Equal("int a;\nint b;\n", await host.GetActiveTextAsync());

            ExtensionMenuItemViewModel format = host.ViewModel.EditorContextMenuItems.Single(item => item.Title == "Format");
            Assert.Contains(format.Children, item => item.CommandId == "textEditing.toggleComment");
            Assert.Contains(format.Children, item => item.IsSeparator);
            ExtensionMenuItemViewModel wordWrap = host.ViewModel.EditorContextMenuItems
                .SelectMany(item => item.Children)
                .Single(item => item.CommandId == "textEditing.toggle.wordWrap");
            Assert.True(wordWrap.IsToggle);
            Assert.False(wordWrap.IsChecked);

            int contributionChanges = 0;
            host.Contributions.Changed += (_, _) => contributionChanges++;
            await host.ExecuteCommandAsync("textEditing.toggle.wordWrap");

            Assert.True(host.TextEditorView.Options.WordWrap);
            Assert.True(wordWrap.IsChecked);
            Assert.Same(wordWrap, host.ViewModel.EditorContextMenuItems.SelectMany(item => item.Children)
                .Single(item => item.CommandId == "textEditing.toggle.wordWrap"));
            Assert.Equal(0, contributionChanges);

            Assert.Equal(CommandKeybindingScope.TextEditor,
                host.ViewModel.ExtensionKeyBindings.Single(binding => binding.CommandId == "textEditing.find").Scope);
            Assert.Equal(CommandKeybindingScope.Window,
                host.ViewModel.ExtensionKeyBindings.Single(binding => binding.CommandId == "editing.undo").Scope);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [AvaloniaFact]
    public async Task Text_Editor_Keybindings_Apply_Only_While_An_Editor_Has_The_Focus()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-keys-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "ALPHA");
        Application application = Application.Current!;
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        StyleInclude editorTheme = new(new Uri("avares://AvaloniaEdit/"))
        {
            Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
        };
        application.Styles.Add(theme);
        application.Styles.Add(editorTheme);
        XamlVisualEditor.App.MainWindow? window = null;

        try
        {
            using ExtensionMenuTestHost host = new(withTextEditing: true);
            await host.ActivateAsync();
            await host.OpenFileAsync(filePath);
            host.ViewModel.ActiveTextDocument!.SetSelection(0, 5, 5);
            TextBox input = new() { Text = "SEARCH", Width = 200 };
            AvaloniaEdit.TextEditor editor = new() { Text = "editor", Width = 300, Height = 80 };
            window = new XamlVisualEditor.App.MainWindow(host.ViewModel)
            {
                Content = new StackPanel { Children = { input, editor } },
                Width = 520, Height = 240
            };
            WindowLayoutBehavior.SetIsEnabled(window, false);
            window.Show();
            window.UpdateLayout();
            KeyBinding lowerCase = window.KeyBindings.Single(binding =>
                binding.Gesture.Key == Key.U && binding.Gesture.KeyModifiers == KeyModifiers.Control);

            Assert.True(input.Focus());
            Assert.False(lowerCase.Command.CanExecute(null));
            window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, null);
            await host.FlushAsync();
            Assert.Equal("ALPHA", await host.GetActiveTextAsync());

            Assert.True(editor.TextArea.Focus());
            Assert.True(lowerCase.Command.CanExecute(null));
            window.KeyPress(Key.U, RawInputModifiers.Control, PhysicalKey.U, null);
            await host.FlushAsync();
            Assert.Equal("alpha", await host.GetActiveTextAsync());
        }
        finally
        {
            window?.Close();
            application.Styles.Remove(editorTheme);
            application.Styles.Remove(theme);
            File.Delete(filePath);
        }
    }

    [AvaloniaFact]
    public async Task Select_All_And_Paste_Show_Their_Selection_And_Caret_In_The_Editor()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-selection-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "alpha beta");
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        Avalonia.Styling.IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") }
        ];
        Application.Current!.Styles.AddRange(styles);
        Window? window = null;

        try
        {
            using ExtensionMenuTestHost host = new();
            await host.ActivateAsync();
            await host.OpenFileAsync(filePath);
            TextDocumentViewModel document = host.ViewModel.ActiveTextDocument!;
            TextFileView view = new() { DataContext = document };
            window = new Window { Content = view, Width = 500, Height = 300 };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            AvaloniaEdit.TextEditor editor = Avalonia.LogicalTree.LogicalExtensions.GetLogicalDescendants(view)
                .OfType<AvaloniaEdit.TextEditor>().Single();
            document.SetSelection(0, 5, 5);
            await host.ExecuteCommandAsync("editing.copy");

            await host.ExecuteCommandAsync("editing.selectAll");
            Assert.Equal((0, 10, 10), (editor.SelectionStart, editor.SelectionLength, editor.TextArea.Caret.Offset));

            document.SetSelection(10, 0, 10);
            await host.ExecuteCommandAsync("editing.paste");

            Assert.Equal("alpha betaalpha", editor.Text);
            Assert.Equal((0, 15), (editor.SelectionLength, editor.TextArea.Caret.Offset));
            Assert.Equal((0, 15), (document.SelectionLength, document.CaretOffset));
        }
        finally
        {
            window?.Close();
            foreach (Avalonia.Styling.IStyle style in styles)
            {
                Application.Current.Styles.Remove(style);
            }

            File.Delete(filePath);
        }
    }

    [AvaloniaFact]
    public async Task Edit_Menu_Commands_Track_Text_Document_State_And_Execute()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-menu-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "alpha beta");

        try
        {
            using ExtensionMenuTestHost host = new();
            await host.ActivateAsync();
            await host.OpenFileAsync(filePath);

            ExtensionMenuItemViewModel copyItem = await host.GetMenuItemAsync(host.ViewModel.EditMenuItems, "editing.copy");
            ExtensionMenuItemViewModel pasteItem = await host.GetMenuItemAsync(host.ViewModel.EditMenuItems, "editing.paste");
            ExtensionToolbarItemViewModel copyToolbarItem = await host.GetToolbarItemAsync(host.ViewModel.MainToolbarItems, "editing.copy");
            ExtensionAsyncCommand copyCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.copy");
            ExtensionAsyncCommand pasteCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.paste");
            ExtensionAsyncCommand deleteCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.delete");
            ExtensionAsyncCommand selectAllCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.selectAll");
            ExtensionAsyncCommand undoCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.undo");
            ExtensionAsyncCommand redoCommand = await host.GetMenuCommandAsync(host.ViewModel.EditMenuItems, "editing.redo");

            Assert.False(copyItem.IsEnabled);
            Assert.False(pasteItem.IsEnabled);
            Assert.False(copyToolbarItem.IsEnabled);
            Assert.NotNull(copyItem.InputGesture);
            Assert.NotNull(pasteItem.InputGesture);
            Assert.False(await host.CanExecuteAsync(copyCommand));
            Assert.False(await host.CanExecuteAsync(pasteCommand));
            Assert.False(await host.CanExecuteAsync(redoCommand));
            Assert.True(await host.CanExecuteAsync(selectAllCommand));

            TextBox input = new() { Text = "search text", Width = 200 };
            AvaloniaEdit.TextEditor editor = new() { Text = "editor text", Width = 300, Height = 80 };
            Button documentTarget = new() { Content = "Document command target" };
            Application application = Application.Current!;
            var theme = new Avalonia.Themes.Fluent.FluentTheme();
            application.Styles.Add(theme);
            var editorTheme = new StyleInclude(new Uri("avares://AvaloniaEdit/"))
            {
                Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml")
            };
            application.Styles.Add(editorTheme);
            XamlVisualEditor.App.MainWindow? window = null;
            try
            {
                window = new XamlVisualEditor.App.MainWindow(host.ViewModel)
                {
                    Content = new StackPanel { Children = { input, editor, documentTarget } },
                    Width = 520, Height = 240
                };
                WindowLayoutBehavior.SetIsEnabled(window, false);
                window.Show();
                window.UpdateLayout();
                Assert.Contains(window.KeyBindings, binding => binding.Gesture.Key == Key.A);
                void Press(Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
                {
                    window.KeyPress(key, modifiers, physical, null);
                    window.KeyRelease(key, modifiers, physical, null);
                }

                Assert.True(input.Focus());
                Assert.Same(input, window.FocusManager!.GetFocusedElement());
                Assert.False(window.KeyBindings.Single(binding => binding.Gesture.Key == Key.A).Command.CanExecute(null));
                Press(Key.A, PhysicalKey.A, RawInputModifiers.Control);
                Assert.Equal("search text", input.SelectedText);
                Press(Key.Delete, PhysicalKey.Delete);
                Assert.Equal(string.Empty, input.Text);
                Assert.Equal("alpha beta", await host.GetActiveTextAsync());
                Press(Key.Z, PhysicalKey.Z, RawInputModifiers.Control);
                Assert.Equal("search text", input.Text);

                Assert.True(editor.TextArea.Focus());
                // AvaloniaEdit binds its editing gestures to Cmd on macOS and to Ctrl elsewhere.
                Press(Key.A, PhysicalKey.A, OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control);
                Assert.Equal("editor text", editor.SelectedText);
                Press(Key.Delete, PhysicalKey.Delete);
                Assert.Equal(string.Empty, editor.Text);
                Assert.Equal("alpha beta", await host.GetActiveTextAsync());

                Assert.True(documentTarget.Focus());
                Press(Key.A, PhysicalKey.A, RawInputModifiers.Control);
                await host.FlushAsync();
                Assert.Equal(await host.GetActiveTextLengthAsync(), await host.GetSelectionLengthAsync());
            }
            finally
            {
                window?.Close();
                application.Styles.Remove(editorTheme);
                application.Styles.Remove(theme);
            }
            await host.SetTextSelectionAsync(start: 0, length: 0);

            bool copyChanged = false;
            await host.SubscribeCanExecuteChangedAsync(copyCommand, () => copyChanged = true);

            await host.SetTextSelectionAsync(start: 0, length: 5);

            Assert.True(copyChanged);
            Assert.True(copyItem.IsEnabled);
            Assert.True(copyToolbarItem.IsEnabled);
            Assert.True(await host.CanExecuteAsync(copyCommand));
            Assert.True(await host.CanExecuteAsync(deleteCommand));

            await host.ExecuteCommandAsync("editing.copy");
            Assert.True(pasteItem.IsEnabled);
            Assert.True(await host.CanExecuteAsync(pasteCommand));

            await host.ExecuteCommandAsync("editing.cut");
            Assert.Equal(" beta", await host.GetActiveTextAsync());
            Assert.False(copyItem.IsEnabled);
            Assert.False(copyToolbarItem.IsEnabled);
            Assert.True(await host.CanExecuteAsync(undoCommand));
            Assert.False(await host.CanExecuteAsync(copyCommand));

            await host.ExecuteCommandAsync("editing.undo");
            Assert.Equal("alpha beta", await host.GetActiveTextAsync());
            Assert.True(await host.CanExecuteAsync(redoCommand));

            await host.ExecuteCommandAsync("editing.redo");
            Assert.Equal(" beta", await host.GetActiveTextAsync());

            await host.ExecuteCommandAsync("editing.paste");
            Assert.Equal("alpha beta", await host.GetActiveTextAsync());

            await host.ExecuteCommandAsync("editing.selectAll");
            Assert.Equal(
                await host.GetActiveTextLengthAsync(),
                await host.GetSelectionLengthAsync());

            await host.ExecuteCommandAsync("editing.delete");
            Assert.Equal(string.Empty, await host.GetActiveTextAsync());
        }
        finally
        {
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
        }
    }

    [AvaloniaFact]
    public async Task Main_Toolbar_Orders_Edit_Commands_Before_Terminal_And_Hides_Empty_Extension_Group()
    {
        using ExtensionMenuTestHost host = new();
        await host.ActivateAsync();

        string[] commandIds = await Dispatcher.UIThread.InvokeAsync(
            () => host.ViewModel.MainToolbarItems.Select(item => item.CommandId).ToArray(),
            DispatcherPriority.Background);

        Assert.Equal(
            new[]
            {
                "editing.undo",
                "editing.redo",
                "editing.cut",
                "editing.copy",
                "editing.paste",
                "editing.delete",
                "terminal.new"
            },
            commandIds);

        Assert.Empty(host.ViewModel.ExtensionToolbarItems);
        Assert.False(host.ViewModel.HasExtensionToolbarItems);
    }

    [AvaloniaFact]
    public async Task Debug_Start_Menu_Command_Follows_Debugger_Service_Availability()
    {
        using ExtensionMenuTestHost host = new();
        await host.ActivateAsync();

        ExtensionMenuItemViewModel startDebugItem = await host.GetMenuItemAsync(host.ViewModel.DebugMenuItems, "debug.start");
        ExtensionAsyncCommand startDebugCommand = await host.GetMenuCommandAsync(host.ViewModel.DebugMenuItems, "debug.start");
        await host.SetWorkspaceLoadedAsync("/tmp/Test.sln");

        Assert.False(startDebugItem.IsEnabled);
        Assert.NotNull(startDebugItem.InputGesture);
        Assert.False(await host.CanExecuteAsync(startDebugCommand));

        bool changed = false;
        await host.SubscribeCanExecuteChangedAsync(startDebugCommand, () => changed = true);
        await host.RegisterDebuggerServiceAsync();

        Assert.True(changed);
        Assert.True(startDebugItem.IsEnabled);
        Assert.True(await host.CanExecuteAsync(startDebugCommand));
    }

    [AvaloniaFact]
    public async Task Workspace_Menu_Commands_Track_Loaded_And_Busy_State()
    {
        using ExtensionMenuTestHost host = new();
        await host.ActivateAsync();

        string[] commandIds =
        {
            "workspace.load",
            "workspace.restore",
            "workspace.build",
            "workspace.rebuild",
            "workspace.clean"
        };

        foreach (string commandId in commandIds)
        {
            ExtensionMenuItemViewModel item = await host.GetMenuItemAsync(host.ViewModel.WorkspaceMenuItems, commandId);
            Assert.False(item.IsEnabled);
        }

        await host.SetWorkspaceLoadedAsync("/tmp/Test.sln");

        foreach (string commandId in commandIds)
        {
            ExtensionMenuItemViewModel item = await host.GetMenuItemAsync(host.ViewModel.WorkspaceMenuItems, commandId);
            Assert.True(item.IsEnabled);
        }

        await host.SetWorkspaceCommandRunningAsync(true);

        foreach (string commandId in commandIds)
        {
            ExtensionMenuItemViewModel item = await host.GetMenuItemAsync(host.ViewModel.WorkspaceMenuItems, commandId);
            Assert.False(item.IsEnabled);
        }
    }

    [AvaloniaFact]
    public async Task Status_Bar_Shows_Startup_Activities_And_Ready_Only_After_The_Last_One_Ends()
    {
        BackgroundActivityTracker activities = new();
        IDisposable startup = activities.Begin(UiStrings.StartingStatus);
        ActivityProbeExtension probe = new(activities);
        using ExtensionMenuTestHost host = new(backgroundActivities: activities, additionalExtension: probe);

        Assert.True(host.ViewModel.IsStarting);
        Assert.Equal(UiStrings.StartingStatus, host.ViewModel.StatusText);

        await host.ActivateAsync();

        Assert.Same(activities, probe.ContextActivities);
        Assert.Equal(string.Format(UiStrings.ActivatingExtensionStatus, probe.ExtensionId), probe.DescriptionDuringActivation);
        Assert.Equal(ActivityProbeExtension.WORK_DESCRIPTION, host.ViewModel.StatusText);

        startup.Dispose();
        await host.FlushAsync();

        Assert.True(host.ViewModel.IsStarting);
        Assert.Equal(ActivityProbeExtension.WORK_DESCRIPTION, host.ViewModel.StatusText);

        probe.Work!.Dispose();
        await host.FlushAsync();

        Assert.False(host.ViewModel.IsStarting);
        Assert.Equal(UiStrings.Ready, host.ViewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task Only_Extensions_Declaring_The_Capability_Receive_Document_Open_Interceptors()
    {
        DocumentOpenInterceptorRegistry interceptors = new();
        UndeclaredInterceptorProbe undeclared = new();
        DeclaredInterceptorProbe declared = new();
        using ExtensionMenuTestHost host = new(additionalExtension: undeclared, secondExtension: declared,
            documentOpenInterceptors: interceptors);

        await host.ActivateAsync();

        Assert.True(undeclared.Activated);
        Assert.Null(undeclared.Interceptors);
        Assert.Same(interceptors, declared.Interceptors);
        Assert.Equal("redirected.axaml", await interceptors.ResolveAsync("source.xml", CancellationToken.None));
    }

    [AvaloniaFact]
    public async Task Installed_Packages_Receive_Document_Open_Interceptors_Only_When_Their_Manifest_Declares_Them()
    {
        string root = Path.Combine(Path.GetTempPath(), $"xve-interceptor-packages-{Guid.NewGuid():N}");
        try
        {
            ExtensionPackageStore store = new(Path.Combine(root, "installed"), new ExtensionPackageLoader());
            FileExtensionStateStore state = new(Path.Combine(root, "extension-state.json"));
            ExtensionPackageInfo declared = await store.InstallAsync(InstalledPackageProbe.CreatePackage(root, "declared",
                ExtensionHostCapabilities.DocumentOpenInterceptors), CancellationToken.None);
            ExtensionPackageInfo undeclared = await store.InstallAsync(
                InstalledPackageProbe.CreatePackage(root, "undeclared"), CancellationToken.None);
            await state.SetEnabledAsync(declared.Manifest.ExtensionId, true, CancellationToken.None);
            await state.SetEnabledAsync(undeclared.Manifest.ExtensionId, true, CancellationToken.None);

            using ExtensionMenuTestHost host = new(packageStore: store, stateStore: state,
                documentOpenInterceptors: new DocumentOpenInterceptorRegistry());
            await host.ActivateAsync();

            Assert.True(InstalledPackageProbe.ReceivedDocumentOpenInterceptors(declared.Manifest.ExtensionId));
            Assert.False(InstalledPackageProbe.ReceivedDocumentOpenInterceptors(undeclared.Manifest.ExtensionId));
        }
        finally
        {
            InstalledPackageProbe.TryDeleteDirectory(root);
        }
    }

    private sealed class UndeclaredInterceptorProbe : IXveExtension
    {
        public bool Activated { get; private set; }

        public IDocumentOpenInterceptors? Interceptors { get; private set; }

        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
        {
            Activated = true;
            Interceptors = context.DocumentOpenInterceptors;
            if (context.DocumentOpenInterceptors is { } interceptors)
                context.Subscriptions.Add(interceptors.Register(new RedirectingInterceptor("hijacked.axaml")));
            return Task.CompletedTask;
        }
    }

    [ExtensionCapabilities(ExtensionHostCapabilities.DocumentOpenInterceptors)]
    private sealed class DeclaredInterceptorProbe : IXveExtension
    {
        public IDocumentOpenInterceptors? Interceptors { get; private set; }

        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
        {
            Interceptors = context.DocumentOpenInterceptors;
            if (context.DocumentOpenInterceptors is { } interceptors)
                context.Subscriptions.Add(interceptors.Register(new RedirectingInterceptor("redirected.axaml")));
            return Task.CompletedTask;
        }
    }

    private sealed class RedirectingInterceptor(string target) : IDocumentOpenInterceptor
    {
        public Task<string?> RedirectAsync(string filePath, CancellationToken cancellationToken) =>
            Task.FromResult<string?>(target);
    }

    /// <summary>Records the activity state during activation and starts work that outlives it.</summary>
    private sealed class ActivityProbeExtension(BackgroundActivityTracker activities) : IXveExtension
    {
        public const string WORK_DESCRIPTION = "Probe work...";

        public IBackgroundActivities? ContextActivities { get; private set; }

        public string? ExtensionId { get; private set; }

        public string? DescriptionDuringActivation { get; private set; }

        public IDisposable? Work { get; private set; }

        public Task ActivateAsync(ExtensionContext context, CancellationToken cancellationToken)
        {
            ContextActivities = context.BackgroundActivities;
            ExtensionId = context.ExtensionId;
            DescriptionDuringActivation = activities.CurrentDescription;
            Work = context.BackgroundActivities?.Begin(WORK_DESCRIPTION);
            return Task.CompletedTask;
        }
    }

    /// <summary>The shell view model with the built-in extension host; <see cref="CommandPaletteCoverageTests" /> uses it too.</summary>
    internal sealed class ExtensionMenuTestHost : IDisposable
    {
        private readonly IWorkspaceService _workspaceService = new StubWorkspaceService();
        private readonly WorkspaceInfoService _workspaceInfo = new();
        private readonly CommandRegistry _commands = new();
        private readonly CommandMetadataRegistry _commandMetadata = new();
        private readonly ExtensionContributionRegistry _contributions = new();
        private readonly ExtensionViewRegistry _views = new();
        private readonly DebuggerServiceRegistry _debuggerRegistry = new();
        private readonly InMemoryWorkspace _workspace = new();
        private readonly EditorServicesAdapter _editor;
        private readonly LanguageServiceRegistry _languageRegistry;
        private readonly NavigationHistoryServiceAdapter _navigationHistory;
        private readonly CollaborationPanelHostAdapter _collaborationHost;
        private readonly DebugSettingsHostAdapter _debugSettingsHost;
        private readonly TerminalBridgeAdapter _terminalBridge;
        private readonly DiagnosticsServiceAdapter _diagnostics;
        private readonly ExtensionViewHostAdapter _viewHost;
        private readonly WorkspaceModelAdapter _workspaceModel;
        private readonly BuiltInExtensionHost _extensionHost;

        public ExtensionMenuTestHost(bool withTextEditing = false, IExtensionPackageStore? packageStore = null,
            IExtensionStateStore? stateStore = null, BackgroundActivityTracker? backgroundActivities = null,
            IXveExtension? additionalExtension = null, IDocumentOpenInterceptors? documentOpenInterceptors = null,
            IXveExtension? secondExtension = null,
            Func<ExtensionMenuTestHost, IXveExtension[]>? builtInExtensions = null)
        {
            _languageRegistry = new LanguageServiceRegistry(Array.Empty<ILanguageIntellisenseService>());
            ViewModel = new MainWindowViewModel(
                workspaceService: _workspaceService,
                workspaceInfoUpdater: _workspaceInfo,
                languageRegistry: _languageRegistry,
                debuggerRegistry: _debuggerRegistry,
                extensionCommands: _commands,
                commandMetadata: _commandMetadata,
                extensionContributionRegistry: _contributions,
                extensionViewRegistry: _views,
                contextKeys: ContextKeys,
                backgroundActivities: backgroundActivities);
            TextEditorView = new TextEditorViewHost(
                ViewModel.EditorContextMenuItems,
                ViewModel.WhenAnyValue(x => x.ActiveTextDocument).Select(document => (object?)document),
                ContextKeys);

            _editor = new EditorServicesAdapter(ViewModel);
            _navigationHistory = new NavigationHistoryServiceAdapter(ViewModel);
            _collaborationHost = new CollaborationPanelHostAdapter(ViewModel);
            _debugSettingsHost = new DebugSettingsHostAdapter(ViewModel);
            _terminalBridge = new TerminalBridgeAdapter(ViewModel);
            _diagnostics = new DiagnosticsServiceAdapter(ViewModel);
            _viewHost = new ExtensionViewHostAdapter(ViewModel);
            _workspaceModel = new WorkspaceModelAdapter(_workspaceInfo, ViewModel, _workspaceService);

            IXveExtension[] extensions = builtInExtensions?.Invoke(this) ?? (withTextEditing
                ? [new WorkspaceExtensionEntry(ViewModel, _workspaceInfo), new TextEditingExtensionEntry()]
                : [new WorkspaceExtensionEntry(ViewModel, _workspaceInfo)]);
            if (additionalExtension is not null)
                extensions = [.. extensions, additionalExtension];
            if (secondExtension is not null)
                extensions = [.. extensions, secondExtension];
            _extensionHost = new BuiltInExtensionHost(
                extensions,
                _commands,
                _commandMetadata,
                _contributions,
                _debuggerRegistry,
                new DesignerHostAdapter(_editor),
                _workspace,
                _workspaceModel,
                _workspaceInfo,
                new StubSystemIconService(),
                new InMemoryWindow(),
                new InMemoryDialogHost(),
                new InMemoryWorkspaceHost(),
                _views,
                new ExtensionLanguageServiceRegistry(),
                new LanguageNavigationServiceAdapter(_languageRegistry, _editor),
                _navigationHistory,
                new AnimationEditorHostAdapter(ViewModel),
                _collaborationHost,
                _collaborationHost,
                _debugSettingsHost,
                new LspSettingsHostAdapter(null),
                _editor,
                _diagnostics,
                new PropertyEditorRegistry(),
                _terminalBridge,
                _viewHost,
                Settings,
                textEditorView: TextEditorView,
                contextKeys: ContextKeys,
                packageStore: packageStore,
                stateStore: stateStore,
                backgroundActivities: backgroundActivities,
                documentOpenInterceptors: documentOpenInterceptors);
        }

        public MainWindowViewModel ViewModel { get; }

        public ContextKeyRegistry ContextKeys { get; } = new();

        public TextEditorViewHost TextEditorView { get; }

        public ExtensionContributionRegistry Contributions => _contributions;

        public CommandRegistry Commands => _commands;

        public CommandMetadataRegistry CommandMetadata => _commandMetadata;

        public InMemorySettingsStore Settings { get; } = new();

        public WorkspaceInfoService WorkspaceInfo => _workspaceInfo;

        public IEditorServices Editor => _editor;

        public async Task ActivateAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => _extensionHost.ActivateAsync(CancellationToken.None),
                DispatcherPriority.Background);
            await FlushAsync();
        }

        public async Task OpenFileAsync(string filePath)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => ViewModel.OpenFileAsync(filePath),
                DispatcherPriority.Background);
            await FlushAsync();
        }

        public async Task ExecuteCommandAsync(string commandId)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => _commands.ExecuteAsync(commandId, null, CancellationToken.None),
                DispatcherPriority.Background);
            await FlushAsync();
        }

        public Task<string> GetActiveTextAsync()
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => ViewModel.ActiveTextDocument?.Document.Text ?? string.Empty,
                DispatcherPriority.Background).GetTask();
        }

        public Task<int> GetActiveTextLengthAsync()
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => ViewModel.ActiveTextDocument?.Document.TextLength ?? 0,
                DispatcherPriority.Background).GetTask();
        }

        public Task<int> GetSelectionLengthAsync()
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => ViewModel.ActiveTextDocument?.SelectionLength ?? 0,
                DispatcherPriority.Background).GetTask();
        }

        public Task<ExtensionAsyncCommand> GetMenuCommandAsync(
            System.Collections.ObjectModel.ObservableCollection<ExtensionMenuItemViewModel> items,
            string commandId)
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => Assert.IsType<ExtensionAsyncCommand>(items.Single(item => item.CommandId == commandId).Command),
                DispatcherPriority.Background).GetTask();
        }

        public Task<ExtensionMenuItemViewModel> GetMenuItemAsync(
            System.Collections.ObjectModel.ObservableCollection<ExtensionMenuItemViewModel> items,
            string commandId)
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => items.Single(item => item.CommandId == commandId),
                DispatcherPriority.Background).GetTask();
        }

        public Task<ExtensionToolbarItemViewModel> GetToolbarItemAsync(
            System.Collections.ObjectModel.ObservableCollection<ExtensionToolbarItemViewModel> items,
            string commandId)
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => items.Single(item => item.CommandId == commandId),
                DispatcherPriority.Background).GetTask();
        }

        public Task<bool> CanExecuteAsync(ICommand command)
        {
            return Dispatcher.UIThread.InvokeAsync(
                () => command.CanExecute(null),
                DispatcherPriority.Background).GetTask();
        }

        public async Task SubscribeCanExecuteChangedAsync(ICommand command, Action onChanged)
        {
            await Dispatcher.UIThread.InvokeAsync(
                () => command.CanExecuteChanged += (_, _) => onChanged(),
                DispatcherPriority.Background);
        }

        public async Task SetTextSelectionAsync(int start, int length)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                TextDocumentViewModel document = Assert.IsType<TextDocumentViewModel>(ViewModel.ActiveTextDocument);
                document.SelectionStart = start;
                document.SelectionLength = length;
                document.SetCaretOffset(start + length);
            }, DispatcherPriority.Background);

            await FlushAsync();
        }

        public async Task SetWorkspaceLoadedAsync(string workspacePath)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _workspaceInfo.UpdateWorkspacePath(workspacePath);
                typeof(MainWindowViewModel)
                    .GetProperty(nameof(MainWindowViewModel.HasWorkspace))!
                    .GetSetMethod(nonPublic: true)!
                    .Invoke(ViewModel, new object[] { true });
            }, DispatcherPriority.Background);

            await FlushAsync();
        }

        public async Task SetWorkspaceCommandRunningAsync(bool value)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                typeof(MainWindowViewModel)
                    .GetProperty(nameof(MainWindowViewModel.IsWorkspaceCommandRunning))!
                    .GetSetMethod(nonPublic: true)!
                    .Invoke(ViewModel, new object[] { value });
            }, DispatcherPriority.Background);

            await FlushAsync();
        }

        public async Task RegisterDebuggerServiceAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _debuggerRegistry.Register(
                    new DebuggerServiceRegistration("fake", "Fake Debugger", new NoOpDebuggerService()),
                    makeDefault: true);
            }, DispatcherPriority.Background);

            await FlushAsync();
        }

        public async Task FlushAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }

        public void Dispose()
        {
            _extensionHost.Dispose();
            TextEditorView.Dispose();
            _workspaceModel.Dispose();
            _viewHost.Dispose();
            _diagnostics.Dispose();
            _terminalBridge.Dispose();
            _debugSettingsHost.Dispose();
            _collaborationHost.Dispose();
            _navigationHistory.Dispose();
            _editor.Dispose();
            ViewModel.Dispose();
        }
    }

    private sealed class StubSystemIconService : ISystemIconService
    {
        public object? GetIcon(string? path, bool isDirectory, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;

        public object? GetFileIcon(string? path, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;

        public object? GetFolderIcon(string? path, object? fallbackIcon = null, int iconSize = 16)
            => fallbackIcon;
    }

    private sealed class StubWorkspaceService : IWorkspaceService
    {
        public Task<WorkspaceModel> LoadSolutionAsync(string solutionPath, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceModel { Projects = Array.Empty<ProjectModel>() });

        public Task<WorkspaceModel> LoadProjectAsync(string projectPath, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceModel { Projects = Array.Empty<ProjectModel>() });

        public WorkspaceModel CreateStandaloneWorkspace(string xamlFilePath) =>
            new() { Projects = Array.Empty<ProjectModel>() };
    }

    private sealed class NoOpDebuggerService : IDebuggerService
    {
        public Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken ct = default) =>
            Task.FromResult<IDebugSession>(new NoOpDebugSession());

        public Task<IDebugSession> AttachAsync(DebugAttachOptions options, CancellationToken ct = default) =>
            Task.FromResult<IDebugSession>(new NoOpDebugSession());
    }

    private sealed class NoOpDebugSession : IDebugSession
    {
        public DebugSessionState State => DebugSessionState.Created;

#pragma warning disable CS0067
        public event Action<DebugSessionState>? StateChanged;
        public event Action<DebugEvent>? EventReceived;
#pragma warning restore CS0067

        public Task<IReadOnlyList<BreakpointInfo>> SetBreakpointsAsync(
            string filePath,
            IReadOnlyList<SourceBreakpoint> breakpoints,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BreakpointInfo>>(Array.Empty<BreakpointInfo>());

        public Task<IReadOnlyList<ThreadInfo>> GetThreadsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ThreadInfo>>(Array.Empty<ThreadInfo>());

        public Task<StackTraceInfo> GetStackTraceAsync(int threadId, int startFrame, int levels, CancellationToken ct = default) =>
            Task.FromResult(new StackTraceInfo { Frames = Array.Empty<StackFrameInfo>() });

        public Task<IReadOnlyList<ScopeInfo>> GetScopesAsync(int frameId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScopeInfo>>(Array.Empty<ScopeInfo>());

        public Task<IReadOnlyList<VariableInfo>> GetVariablesAsync(int variablesReference, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<VariableInfo>>(Array.Empty<VariableInfo>());

        public Task<EvaluateResult> EvaluateAsync(EvaluateRequest request, CancellationToken ct = default) =>
            Task.FromResult(new EvaluateResult { Result = string.Empty });

        public Task ContinueAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepInAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOutAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task StepOverAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task PauseAsync(int? threadId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DisconnectAsync(bool terminateDebuggee, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
