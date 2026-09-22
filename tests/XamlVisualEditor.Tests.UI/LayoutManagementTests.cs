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
    public async Task DesignerPresetHidesPanelsThatAreNotPartOfThePreset()
    {
        using LayoutTestHost host = new();
        await host.FlushAsync();

        host.ViewModel.ApplyLayoutPreset(LayoutPresetCatalog.DesignerId);
        await host.FlushAsync();

        IRootDock layout = host.ViewModel.DockLayout;
        Assert.True(host.IsPanelVisible("toolbox.panel"));
        Assert.True(host.IsPanelVisible("propertyEditor.panel"));
        Assert.False(host.IsPanelVisible("git.panel"));
        Assert.False(host.IsPanelVisible("acp.panel"));
        Assert.Contains(layout.HiddenDockables!, dockable => dockable is ExtensionTool { ViewId: "git.panel" });
        Assert.Contains(layout.HiddenDockables!, dockable => dockable is ExtensionTool { ViewId: "acp.panel" });
        Assert.Contains(layout.HiddenDockables!, dockable => dockable.Id == "Breakpoints");
        Assert.DoesNotContain(layout.RightPinnedDockables!, dockable => dockable is ExtensionTool { ViewId: "acp.panel" });
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
            interaction.SetOutput("Mein Layout");
        });

        host.ViewModel.ToggleExtensionView("toolbox.panel");
        await host.FlushAsync();
        Assert.False(host.IsPanelVisible("toolbox.panel"));

        await host.ViewModel.SaveLayoutAsCommand.Execute();
        await host.FlushAsync();

        Assert.Equal(string.Empty, promptedName);
        Assert.Equal("Mein Layout", host.ViewModel.CurrentLayoutName);
        Assert.True(File.Exists(host.Store.GetPath("Mein Layout")));
        Assert.True(host.ViewModel.HasSavedLayouts);
        Assert.Equal("Mein Layout", Assert.Single(host.ViewModel.SavedLayoutMenuItems).Title);
        Assert.Equal(string.Format(UiStrings.LayoutSaved, "Mein Layout"), host.ViewModel.StatusText);

        await host.ViewModel.ResetLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.True(host.IsPanelVisible("toolbox.panel"));
        Assert.Null(host.ViewModel.CurrentLayoutName);

        var loadCommand = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(host.ViewModel.SavedLayoutMenuItems[0].Command);
        loadCommand.Execute(null);
        await host.FlushAsync();

        Assert.False(host.IsPanelVisible("toolbox.panel"));
        Assert.True(host.IsPanelVisible("propertyEditor.panel"));
        Assert.Equal("Mein Layout", host.ViewModel.CurrentLayoutName);
        Assert.Equal(string.Format(UiStrings.LayoutLoaded, "Mein Layout"), host.ViewModel.StatusText);

        // "Speichern" ohne Nachfrage überschreibt das aktuelle benannte Layout.
        promptedName = "unset";
        host.ViewModel.ToggleExtensionView("propertyEditor.panel");
        await host.ViewModel.SaveLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.Equal("unset", promptedName);
        Assert.Equal("Mein Layout", host.ViewModel.CurrentLayoutName);
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
            interaction.SetOutput("Mein Layout"));
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
        Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, "Mein Layout"), host.ViewModel.LayoutStatusText);
        Assert.Equal(string.Format(UiStrings.MenuLayoutDeleteNamed, "Mein Layout"), host.ViewModel.DeleteLayoutMenuHeader);
        Assert.True(await host.ViewModel.DeleteLayoutCommand.CanExecute.FirstAsync());

        // Abgelehnte Bestätigung: nichts wird gelöscht.
        await host.ViewModel.DeleteLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.Equal("Mein Layout", confirmedName);
        Assert.True(host.Store.Exists("Mein Layout"));
        Assert.Equal("Mein Layout", host.ViewModel.CurrentLayoutName);
        Assert.Single(host.ViewModel.SavedLayoutMenuItems);

        confirmDelete = true;
        await host.ViewModel.DeleteLayoutCommand.Execute();
        await host.FlushAsync();
        Assert.False(host.Store.Exists("Mein Layout"));
        Assert.Empty(host.ViewModel.SavedLayoutMenuItems);
        Assert.Null(host.ViewModel.CurrentLayoutName);
        Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, UiStrings.LayoutUnnamed), host.ViewModel.LayoutStatusText);
        Assert.Equal(UiStrings.MenuLayoutDelete, host.ViewModel.DeleteLayoutMenuHeader);
        Assert.Equal(string.Format(UiStrings.LayoutDeleted, "Mein Layout"), host.ViewModel.StatusText);
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
    public async Task ViewMenuOffersLayoutSaveSaveAsLoadAndTemplateSubmenus()
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
            MenuItem layoutMenu = viewMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuLayout));
            menu.Open();
            viewMenu.Open();
            layoutMenu.Open();
            window.UpdateLayout();
            await host.FlushAsync();
            MenuItem[] entries = layoutMenu.Items.OfType<MenuItem>().ToArray();

            Assert.Equal(
                new object?[] { UiStrings.MenuLayoutSave, UiStrings.MenuLayoutSaveAs, UiStrings.MenuLayoutDelete, UiStrings.MenuLayoutLoad, UiStrings.MenuLayoutLoadFromTemplates, UiStrings.MenuResetLayout },
                entries.Select(item => item.Header));
            Assert.Same(host.ViewModel.SaveLayoutCommand, entries[0].Command);
            Assert.Same(host.ViewModel.SaveLayoutAsCommand, entries[1].Command);
            Assert.Same(host.ViewModel.DeleteLayoutCommand, entries[2].Command);
            Assert.False(entries[2].IsEffectivelyEnabled);
            Assert.Same(host.ViewModel.SavedLayoutMenuItems, entries[3].ItemsSource);
            Assert.False(entries[3].IsEnabled);
            Assert.Same(host.ViewModel.LayoutPresetMenuItems, entries[4].ItemsSource);
            Assert.Same(host.ViewModel.ResetLayoutCommand, entries[5].Command);
            Assert.Equal(
                new[] { UiStrings.LayoutPresetDesigner, UiStrings.LayoutPresetCode, UiStrings.LayoutPresetDebugging, UiStrings.LayoutPresetAssistants, UiStrings.LayoutPresetMinimal },
                host.ViewModel.LayoutPresetMenuItems.Select(item => item.Title));

            Border statusSection = window.FindControl<Border>("LayoutStatusSection")!;
            Assert.False(statusSection.IsVisible);

            using IDisposable prompt = host.ViewModel.LayoutNameInteraction.RegisterHandler(interaction =>
                interaction.SetOutput("Mein Layout"));
            await host.ViewModel.SaveLayoutAsCommand.Execute();
            await host.FlushAsync();

            Assert.True(statusSection.IsVisible);
            Assert.Equal(string.Format(UiStrings.StatusLayoutFormat, "Mein Layout"), window.FindControl<TextBlock>("LayoutStatusText")!.Text);
            Assert.Equal(string.Format(UiStrings.MenuLayoutDeleteNamed, "Mein Layout"), entries[2].Header);
            Assert.True(entries[2].IsEffectivelyEnabled);
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

        public LayoutTestHost()
        {
            Store = new UserLayoutStore(_directory);
            CommandRegistry commands = new();
            ExtensionContributionRegistry contributions = new();
            ExtensionViewRegistry views = new();
            ViewModel = new MainWindowViewModel(
                extensionCommands: commands,
                extensionContributionRegistry: contributions,
                extensionViewRegistry: views,
                userLayoutStore: Store);

            string[] viewIds = { "solutionExplorer.panel", "fileExplorer.panel", "toolbox.panel", "propertyEditor.panel", "acp.panel", "git.panel" };
            foreach (string viewId in viewIds)
            {
                _registrations.Add(views.RegisterCustomViewProvider(viewId, new PanelProvider()));
            }

            _registrations.Add(contributions.RegisterViews("test.layouts", new[]
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
