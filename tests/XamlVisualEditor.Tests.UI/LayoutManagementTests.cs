using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using System.Reactive.Linq;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class LayoutManagementTests
{
    [AvaloniaFact]
    public async Task DesignerPresetContainsOnlyThePanelsItLists()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();

        host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.DesignerId);
        await host.FlushAsync();

        IRootDock layout = host.ViewModel.DockLayout;
        Assert.True(host.IsPanelVisible("toolbox.panel"));
        Assert.True(host.IsPanelVisible("propertyEditor.panel"));
        Assert.False(host.IsPanelInLayout("git.panel"));
        Assert.False(host.IsPanelInLayout("acp.panel"));
        Assert.Null(XamlEditorDockFactory.FindDockable<IDockable>(layout, "Breakpoints"));
        Assert.Empty(layout.HiddenDockables!);
        Assert.False(host.ViewModel.IsBreakpointsVisible);
        Assert.False(host.ViewModel.IsWatchesVisible);
        Assert.Equal(0, XamlEditorDockFactory.FindDockable<IToolDock>(layout, "BottomToolDock")!.Proportion);
        Assert.Null(host.ViewModel.CurrentLayoutName);
        Assert.Equal(string.Format(UiStrings.LayoutLoaded, UiStrings.LayoutPresetDesigner), host.ViewModel.StatusText);
    }

    [AvaloniaFact]
    public async Task AssistantsPresetShowsServerPanelsAsRegularTabsAndDebuggingPresetKeepsDebugTools()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();

        host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.AssistantsId);
        await host.FlushAsync();

        IToolDock rightDock = XamlEditorDockFactory.FindDockable<IToolDock>(host.ViewModel.DockLayout, "RightToolDock")!;
        Assert.Contains(rightDock.VisibleDockables!, dockable => dockable is ExtensionTool { ViewId: "acp.panel" });
        Assert.DoesNotContain(host.ViewModel.DockLayout.RightPinnedDockables!, dockable => dockable is ExtensionTool { ViewId: "acp.panel" });
        Assert.False(host.IsPanelVisible("toolbox.panel"));

        host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.DebuggingId);
        await host.FlushAsync();

        Assert.True(host.ViewModel.IsBreakpointsVisible);
        Assert.True(host.ViewModel.IsCallStackVisible);
        Assert.True(host.ViewModel.IsLocalsVisible);
        Assert.True(host.ViewModel.IsWatchesVisible);
        Assert.False(host.IsPanelVisible("propertyEditor.panel"));
        Assert.NotEqual(0, XamlEditorDockFactory.FindDockable<IToolDock>(host.ViewModel.DockLayout, "BottomToolDock")!.Proportion);
    }

    [AvaloniaFact]
    public async Task SaveAsAndLoadRestoreAUserLayoutAndPopulateTheMenu()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();
        string? promptedName = null;
        using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction =>
        {
            promptedName = interaction.Input;
            interaction.SetOutput("My Layout");
        });

        // Toggling hides only the frontmost tab; a covered toolbox would be brought to the front instead.
        host.ViewModel.ActivateExtensionView("propertyEditor.panel");
        host.ViewModel.ActivateExtensionView("toolbox.panel");
        host.ViewModel.ToggleExtensionView("toolbox.panel");
        await host.FlushAsync();
        Assert.False(host.IsPanelVisible("toolbox.panel"));

        await host.ViewModel.SaveLayoutAsCommand.Execute();
        await host.FlushAsync();

        Assert.Equal(string.Empty, promptedName);
        Assert.Equal("My Layout", host.ViewModel.CurrentLayoutName);
        Assert.True(File.Exists(host.Store.GetPath("My Layout")));
        Assert.True(host.ViewModel.HasSavedLayouts);
        Assert.Equal("My Layout", Assert.Single(host.ViewModel.SavedLayoutMenuItems).Title);
        Assert.Equal(string.Format(UiStrings.LayoutSaved, "My Layout"), host.ViewModel.StatusText);

        await host.ViewModel.ResetLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.False(host.IsPanelInLayout("toolbox.panel"));
        Assert.False(host.IsPanelInLayout("propertyEditor.panel"));
        Assert.Null(host.ViewModel.CurrentLayoutName);

        var loadCommand = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(host.ViewModel.SavedLayoutMenuItems[0].Command);
        loadCommand.Execute(null);
        await host.FlushAsync();

        Assert.False(host.IsPanelVisible("toolbox.panel"));
        Assert.True(host.IsPanelVisible("propertyEditor.panel"));
        Assert.Equal("My Layout", host.ViewModel.CurrentLayoutName);
        Assert.Equal(string.Format(UiStrings.LayoutLoaded, "My Layout"), host.ViewModel.StatusText);

        // "Save" overwrites the current named layout without asking.
        promptedName = "unset";
        host.ViewModel.ToggleExtensionView("propertyEditor.panel");
        await host.ViewModel.SaveLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.Equal("unset", promptedName);
        Assert.Equal("My Layout", host.ViewModel.CurrentLayoutName);
    }

    [AvaloniaFact]
    public async Task PanelsJoinALayoutOnlyThroughItsPresetOrTheViewMenu()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();
        using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction => interaction.SetOutput("Mit Toolbox"));

        // The first start builds the Debugging preset: registered panels outside it stay out, also later ones.
        Assert.True(host.IsPanelInLayout("solutionExplorer.panel"));
        Assert.True(host.IsPanelInLayout("fileExplorer.panel"));
        Assert.False(host.IsPanelInLayout("toolbox.panel"));
        Assert.False(host.IsPanelInLayout("git.panel"));
        host.RegisterView(new ExtensionViewContribution("early.panel", "Early", ExtensionViewType.Custom, ExtensionViewLocation.Right, 50));
        await host.FlushAsync();
        Assert.False(host.IsPanelInLayout("early.panel"));

        // A panel of the preset that the user closed stays closed when the views change.
        ExtensionTool files = XamlEditorDockFactory.FindDockable<ExtensionTool>(host.ViewModel.DockLayout, ExtensionTool.BuildId("fileExplorer.panel"))!;
        host.ViewModel.DockFactory.CloseDockable(files);
        host.RegisterView(new ExtensionViewContribution("middle.panel", "Middle", ExtensionViewType.Custom, ExtensionViewLocation.Right, 55));
        await host.FlushAsync();
        Assert.False(host.IsPanelInLayout("fileExplorer.panel"));

        // A loaded layout keeps exactly its panels.
        host.ViewModel.ActivateExtensionView("toolbox.panel");
        await host.ViewModel.SaveLayoutAsCommand.Execute();
        await host.ViewModel.ResetLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.False(host.IsPanelInLayout("toolbox.panel"));
        Assert.True(host.IsPanelInLayout("fileExplorer.panel"));

        Assert.IsAssignableFrom<System.Windows.Input.ICommand>(host.ViewModel.SavedLayoutMenuItems[0].Command).Execute(null);
        await host.FlushAsync();
        Assert.True(host.IsPanelInLayout("toolbox.panel"));
        Assert.False(host.IsPanelInLayout("fileExplorer.panel"));
        Assert.False(host.IsPanelInLayout("early.panel"));

        host.RegisterView(new ExtensionViewContribution("late.panel", "Late", ExtensionViewType.Custom, ExtensionViewLocation.Right, 60));
        await host.FlushAsync();
        Assert.False(host.IsPanelInLayout("late.panel"));

        // The view menu opens every missing panel.
        host.ViewModel.ToggleExtensionView("fileExplorer.panel");
        host.ViewModel.ToggleExtensionView("late.panel");
        await host.FlushAsync();
        Assert.True(host.IsPanelVisible("fileExplorer.panel"));
        Assert.True(host.IsPanelVisible("late.panel"));
    }

    [AvaloniaFact]
    public async Task ManageLayoutsRenamesAndDeletesSavedLayouts()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();
        string nextName = "Alpha";
        using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction => interaction.SetOutput(nextName));
        await host.ViewModel.SaveLayoutAsCommand.Execute();
        nextName = "Beta";
        await host.ViewModel.SaveLayoutAsCommand.Execute();
        Assert.Equal("Beta", host.ViewModel.CurrentLayoutName);
        bool? confirmAnswer = null;
        using IDisposable manage = host.ViewModel.ManageLayoutsInteraction.RegisterHandler(async interaction =>
        {
            ManageLayoutsDialogViewModel dialog = interaction.Input;
            using IDisposable confirm = dialog.ConfirmDeleteInteraction.RegisterHandler(confirmation => confirmation.SetOutput(confirmAnswer!.Value));
            Assert.Equal(new[] { "Alpha", "Beta" }, dialog.Names);

            dialog.SelectedName = "Beta";
            dialog.EditName = "Alpha";
            await dialog.RenameCommand.Execute();
            Assert.Equal(string.Format(UiStrings.LayoutNameExists, "Alpha"), dialog.ErrorText);

            dialog.EditName = "Gamma";
            await dialog.RenameCommand.Execute();
            Assert.Equal(new[] { "Alpha", "Gamma" }, dialog.Names);
            Assert.Equal("Gamma", dialog.SelectedName);
            Assert.Null(dialog.ErrorText);

            dialog.SelectedName = "Alpha";
            confirmAnswer = false;
            await dialog.DeleteCommand.Execute();
            Assert.Equal(new[] { "Alpha", "Gamma" }, dialog.Names);
            confirmAnswer = true;
            await dialog.DeleteCommand.Execute();
            Assert.Equal(new[] { "Gamma" }, dialog.Names);
            Assert.Equal("Gamma", dialog.SelectedName);
            interaction.SetOutput(ReactiveUI.Primitives.RxVoid.Default);
        });

        await host.ViewModel.ManageLayoutsCommand.Execute();

        Assert.Equal("Gamma", host.ViewModel.CurrentLayoutName);
        Assert.Equal(new[] { "Gamma" }, host.ViewModel.SavedLayoutMenuItems.Select(item => item.Title));
        Assert.False(host.Store.Exists("Alpha"));
        Assert.False(host.Store.Exists("Beta"));
        Assert.True(host.Store.Exists("Gamma"));
    }

    [AvaloniaFact]
    public async Task EveryPresetKeepsSolutionExplorerAndFileExplorerVisible()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();

        foreach (LayoutPreset preset in LayoutPresetCatalog.Create())
        {
            host.ViewModel.ApplyLayoutPreset(preset.Id);
            await host.FlushAsync();

            Assert.True(host.IsPanelVisible("solutionExplorer.panel"), preset.Id);
            Assert.True(host.IsPanelVisible("fileExplorer.panel"), preset.Id);
        }
    }

    [AvaloniaFact]
    public async Task LayoutStatusTextFollowsTheActiveLayoutAndDeleteRemovesTheSavedLayout()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();
        using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction =>
            interaction.SetOutput("My Layout"));
        bool confirmDelete = false;
        string? confirmedName = null;
        using IDisposable confirm = host.ViewModel.ConfirmLayoutDeleteInteraction.RegisterHandler(interaction =>
        {
            confirmedName = interaction.Input;
            interaction.SetOutput(confirmDelete);
        });

        Assert.Null(host.ViewModel.LayoutStatusText);
        Assert.Equal(UiStrings.MenuLayoutDelete, host.ViewModel.DeleteLayoutMenuHeader);
        Assert.False(await host.ViewModel.DeleteLayoutCommand.CanExecute.FirstAsync());

        host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.CodeId);
        await host.FlushAsync();
        Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, UiStrings.LayoutUnnamed), host.ViewModel.LayoutStatusText);
        Assert.False(await host.ViewModel.DeleteLayoutCommand.CanExecute.FirstAsync());

        await host.ViewModel.SaveLayoutAsCommand.Execute();
        await host.FlushAsync();
        Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, "My Layout"), host.ViewModel.LayoutStatusText);
        Assert.Equal(string.Format(UiStrings.MenuLayoutDeleteNamed, "My Layout"), host.ViewModel.DeleteLayoutMenuHeader);
        Assert.True(await host.ViewModel.DeleteLayoutCommand.CanExecute.FirstAsync());

        // Declined confirmation: nothing is deleted.
        await host.ViewModel.DeleteLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.Equal("My Layout", confirmedName);
        Assert.True(host.Store.Exists("My Layout"));
        Assert.Equal("My Layout", host.ViewModel.CurrentLayoutName);
        Assert.Single(host.ViewModel.SavedLayoutMenuItems);

        confirmDelete = true;
        await host.ViewModel.DeleteLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.False(host.Store.Exists("My Layout"));
        Assert.Empty(host.ViewModel.SavedLayoutMenuItems);
        Assert.Null(host.ViewModel.CurrentLayoutName);
        Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, UiStrings.LayoutUnnamed), host.ViewModel.LayoutStatusText);
        Assert.Equal(UiStrings.MenuLayoutDelete, host.ViewModel.DeleteLayoutMenuHeader);
        Assert.Equal(string.Format(UiStrings.LayoutDeleted, "My Layout"), host.ViewModel.StatusText);
        Assert.False(await host.ViewModel.DeleteLayoutCommand.CanExecute.FirstAsync());

        await host.ViewModel.ResetLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.Null(host.ViewModel.LayoutStatusText);
    }

    [AvaloniaFact]
    public async Task SaveAsWithInvalidNameReportsAnErrorAndKeepsTheStoreUntouched()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();
        using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction =>
            interaction.SetOutput("bad/name"));

        await host.ViewModel.SaveLayoutAsCommand.Execute();
        await host.FlushAsync();

        Assert.Equal(UiStrings.LayoutInvalidName, host.ViewModel.StatusText);
        Assert.Null(host.ViewModel.CurrentLayoutName);
        Assert.Empty(host.Store.ListNames());
    }

    [AvaloniaFact]
    public async Task ApplyingALayoutKeepsOpenDocumentsInTheDocumentDock()
    {
        string filePath = Path.Combine(Path.GetTempPath(), $"xve-layout-doc-{Guid.NewGuid():N}.cs");
        await File.WriteAllTextAsync(filePath, "class A { }");
        try
        {
            using LayoutTestHost host = new();
            await host.FlushAsync();
            await Dispatcher.UIThread.InvokeAsync(() => host.ViewModel.OpenFileAsync(filePath), DispatcherPriority.Background);
            await host.FlushAsync();
            Assert.Single(host.ViewModel.Documents);

            host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.MinimalId);
            await host.FlushAsync();

            IDocumentDock documentDock = XamlEditorDockFactory.FindDockable<IDocumentDock>(host.ViewModel.DockLayout, "DocumentDock")!;
            TextDocument document = Assert.IsType<TextDocument>(Assert.Single(documentDock.VisibleDockables!));
            Assert.Equal(filePath, document.Id);
            Assert.Same(document, documentDock.ActiveDockable);

            await host.ViewModel.ResetLayoutCommand.Execute();
            await host.FlushAsync();

            documentDock = XamlEditorDockFactory.FindDockable<IDocumentDock>(host.ViewModel.DockLayout, "DocumentDock")!;
            Assert.Equal(filePath, Assert.IsType<TextDocument>(Assert.Single(documentDock.VisibleDockables!)).Id);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [AvaloniaFact]
    public async Task WindowMenuOffersTheLayoutCommandsAndTheViewMenuNoLongerDoes()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();

        XamlVisualEditor.App.MainWindow window = new(host.ViewModel);
        WindowLayoutBehavior.SetIsEnabled(window, false);
        try
        {
            window.Show();
            window.UpdateLayout();
            await host.FlushAsync();
            Menu menu = window.GetLogicalDescendants().OfType<Menu>().First();
            MenuItem viewMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuView));
            Assert.DoesNotContain(viewMenu.Items.OfType<MenuItem>(), item => ReferenceEquals(item.Command, host.ViewModel.ResetLayoutCommand)
                || item.Items.OfType<MenuItem>().Any(child => ReferenceEquals(child.Command, host.ViewModel.ResetLayoutCommand)));
            MenuItem windowMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuWindow));
            MenuItem helpMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuHelp));
            Assert.Equal(menu.Items.IndexOf(windowMenu) + 1, menu.Items.IndexOf(helpMenu));
            menu.Open();
            windowMenu.Open();
            window.UpdateLayout();
            await host.FlushAsync();
            MenuItem[] entries = windowMenu.Items.OfType<MenuItem>().ToArray();

            Assert.Equal(
                new object?[] { UiStrings.MenuLayoutSave, UiStrings.MenuLayoutSaveAs, UiStrings.MenuLayoutLoad, UiStrings.MenuLayoutLoadFromTemplates, UiStrings.MenuManageLayouts, UiStrings.MenuLayoutDelete, UiStrings.MenuResetLayout },
                entries.Select(item => item.Header));
            Assert.Same(host.ViewModel.SaveLayoutCommand, entries[0].Command);
            Assert.Same(host.ViewModel.SaveLayoutAsCommand, entries[1].Command);
            Assert.Same(host.ViewModel.SavedLayoutMenuItems, entries[2].ItemsSource);
            Assert.False(entries[2].IsEnabled);
            Assert.Same(host.ViewModel.LayoutPresetMenuItems, entries[3].ItemsSource);
            Assert.Same(host.ViewModel.ManageLayoutsCommand, entries[4].Command);
            Assert.Same(host.ViewModel.DeleteLayoutCommand, entries[5].Command);
            Assert.False(entries[5].IsEffectivelyEnabled);
            Assert.Same(host.ViewModel.ResetLayoutCommand, entries[6].Command);
            Assert.Equal(
                new[] { UiStrings.LayoutPresetDesigner, UiStrings.LayoutPresetCode, UiStrings.LayoutPresetDebugging, UiStrings.LayoutPresetAssistants, UiStrings.LayoutPresetMinimal },
                host.ViewModel.LayoutPresetMenuItems.Select(item => item.Title));

            Border statusSection = window.FindControl<Border>("LayoutStatusSection")!;
            Assert.False(statusSection.IsVisible);

            using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction =>
                interaction.SetOutput("My Layout"));
            await host.ViewModel.SaveLayoutAsCommand.Execute();
            await host.FlushAsync();

            Assert.True(statusSection.IsVisible);
            Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, "My Layout"), window.FindControl<TextBlock>("LayoutStatusText")!.Text);
            Assert.Equal(string.Format(UiStrings.MenuLayoutDeleteNamed, "My Layout"), entries[5].Header);
            Assert.True(entries[5].IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class LayoutTestHost : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-layouts-" + Guid.NewGuid().ToString("N"));
        private readonly List<IDisposable> _registrations = new();
        private readonly ExtensionContributionRegistry _contributions = new();
        private readonly ExtensionViewRegistry _views = new();

        public LayoutTestHost()
        {
            Store = new UserLayoutStore(_directory);
            CommandRegistry commands = new();
            ViewModel = new MainWindowViewModel(
                extensionCommands: commands,
                extensionContributionRegistry: _contributions,
                extensionViewRegistry: _views,
                userLayoutStore: Store);

            string[] viewIds = { "solutionExplorer.panel", "fileExplorer.panel", "toolbox.panel", "propertyEditor.panel", "acp.panel", "git.panel" };
            foreach (string viewId in viewIds)
            {
                _registrations.Add(_views.RegisterCustomViewProvider(viewId, new PanelProvider()));
            }

            _registrations.Add(_contributions.RegisterViews("test.layouts", new[]
            {
                new ExtensionViewContribution("solutionExplorer.panel", "Solution", ExtensionViewType.Custom, ExtensionViewLocation.Left, 1),
                new ExtensionViewContribution("fileExplorer.panel", "Files", ExtensionViewType.Custom, ExtensionViewLocation.Left, 2),
                new ExtensionViewContribution("toolbox.panel", "Toolbox", ExtensionViewType.Custom, ExtensionViewLocation.Left, 10),
                new ExtensionViewContribution("propertyEditor.panel", "Properties", ExtensionViewType.Custom, ExtensionViewLocation.Right, 20),
                new ExtensionViewContribution("acp.panel", "ACP", ExtensionViewType.Custom, ExtensionViewLocation.Right, 30) { AutoHideByDefault = true },
                new ExtensionViewContribution("git.panel", "Git", ExtensionViewType.Custom, ExtensionViewLocation.Bottom, 40)
            }));
        }

        public MainWindowViewModel ViewModel { get; }

        public UserLayoutStore Store { get; }

        public bool IsPanelVisible(string viewId) => ViewModel.IsExtensionViewVisible(viewId);

        public bool IsPanelInLayout(string viewId) =>
            XamlEditorDockFactory.FindDockable<ExtensionTool>(ViewModel.DockLayout, ExtensionTool.BuildId(viewId)) is not null;

        public void RegisterView(ExtensionViewContribution contribution)
        {
            _registrations.Add(_views.RegisterCustomViewProvider(contribution.ViewId, new PanelProvider()));
            _registrations.Add(_contributions.RegisterViews("test.layouts." + contribution.ViewId, new[] { contribution }));
        }

        public Task FlushAsync() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

        public void Dispose()
        {
            foreach (IDisposable registration in _registrations)
            {
                registration.Dispose();
            }

            ViewModel.Dispose();
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private sealed class PanelProvider : ICustomViewProvider
    {
        public object CreateViewModel() => new object();
    }
}
