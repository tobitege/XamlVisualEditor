using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ViewMenuGroupsTests
{
    [AvaloniaFact]
    public async Task PanelsMenuListsDebuggerToolsFirstAndSeparatesGroups()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: new ExtensionViewRegistry());
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.view-groups", new[]
        {
            new ExtensionMenuContribution("properties.toggleView", "Properties", ExtensionMenuLocations.View, "views.right", 70),
            new ExtensionMenuContribution("custom.toggleView", "Custom", ExtensionMenuLocations.View, "views.custom", 10),
            new ExtensionMenuContribution("output.toggleView", "Output", ExtensionMenuLocations.View, "views.bottom", 10),
            new ExtensionMenuContribution("explorer.toggleView", "Explorer", ExtensionMenuLocations.View, "views.left", 10),
            new ExtensionMenuContribution("locals.toggleView", "Locals", ExtensionMenuLocations.View, "views.debug", 140),
            new ExtensionMenuContribution("breakpoints.toggleView", "Breakpoints", ExtensionMenuLocations.View, "views.debug", 120)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(
            new[] { "Breakpoints", "Locals", "-", "Output", "-", "Explorer", "-", "Properties", "-", "Custom" },
            viewModel.ViewMenuItems.Select(item => item.Header));
    }

    [AvaloniaFact]
    public async Task EditorContextMenuNestsSubmenusAndSeparatesTheirGroups()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ContextKeyRegistry contextKeys = new();
        contextKeys.Set("test.wrap", true);
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: new ExtensionViewRegistry(),
            contextKeys: contextKeys);
        string location = ExtensionMenuLocations.EditorContext;
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.editor-context", new[]
        {
            new ExtensionMenuContribution("options", "Options", location, "9.options", 0),
            new ExtensionMenuContribution("upper", "Upper", location, "4.format", 20) { Submenu = "Format", SubmenuGroup = "2.case" },
            new ExtensionMenuContribution("comment", "Comment", location, "4.format", 10) { Submenu = "Format", SubmenuGroup = "1.comment" },
            new ExtensionMenuContribution("lower", "Lower", location, "4.format", 30) { Submenu = "Format", SubmenuGroup = "2.case" },
            new ExtensionMenuContribution("wrap", "Wrap", location, "6.display", 0) { Submenu = "Display", CheckedWhen = "test.wrap" },
            new ExtensionMenuContribution("copy", "Copy", location, "2.clipboard", 0)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(
            new[] { "Copy", "-", "Format", "-", "Display", "-", "Options" },
            viewModel.EditorContextMenuItems.Select(item => item.Header));
        Assert.Equal(new[] { false, true, false, true, false, true, false }, viewModel.EditorContextMenuItems.Select(item => item.IsSeparator));
        ExtensionMenuItemViewModel format = viewModel.EditorContextMenuItems[2];
        Assert.Null(format.Command);
        Assert.Equal(new[] { "Comment", "-", "Upper", "Lower" }, format.Children.Select(item => item.Header));
        ExtensionMenuItemViewModel wrap = Assert.Single(viewModel.EditorContextMenuItems[4].Children);
        Assert.True(wrap.IsToggle);
        Assert.True(wrap.IsChecked);
        Assert.False(format.Children[0].IsToggle);

        contextKeys.Set("test.wrap", false);

        Assert.False(wrap.IsChecked);
        Assert.Same(wrap, Assert.Single(viewModel.EditorContextMenuItems[4].Children));
    }

    [AvaloniaFact]
    public async Task ExtensionsMenuKeepsTheManagerFirstAndNestsSubmenus()
    {
        ExtensionContributionRegistry contributions = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: new CommandRegistry(),
            extensionContributionRegistry: contributions,
            extensionViewRegistry: new ExtensionViewRegistry());
        string location = ExtensionMenuLocations.Extensions;
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.extensions", new[]
        {
            new ExtensionMenuContribution("terminal", "Terminal", location, "terminal", 0),
            new ExtensionMenuContribution("online", "Go Online", location, "scc", 20) { Submenu = "Source Control", SubmenuGroup = "1" },
            new ExtensionMenuContribution("offline", "Work Offline", location, "scc", 10) { Submenu = "Source Control", SubmenuGroup = "1" },
            new ExtensionMenuContribution("reconcile", "Reconcile", location, "scc", 30) { Submenu = "Source Control", SubmenuGroup = "2" }
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal("shell.extensions.manage", viewModel.ExtensionMenuItems[0].CommandId);
        Assert.Equal(
            new[] { "-", "Source Control", "-", "Terminal" },
            viewModel.ExtensionMenuItems.Skip(1).Select(item => item.Header));
        Assert.Equal(
            new[] { "Work Offline", "Go Online", "-", "Reconcile" },
            viewModel.ExtensionMenuItems[2].Children.Select(item => item.Header));
    }

    [AvaloniaFact]
    public async Task SubmenuItemsFollowTheirWhenConditionsAfterContextKeyChanges()
    {
        ExtensionContributionRegistry contributions = new();
        CommandMetadataRegistry metadata = new();
        ContextKeyRegistry contextKeys = new();
        contextKeys.Set([new("test.offline", false), new("test.connected", false)]);
        using MainWindowViewModel viewModel = new(
            extensionCommands: new CommandRegistry(),
            extensionContributionRegistry: contributions,
            extensionViewRegistry: new ExtensionViewRegistry(),
            commandMetadata: metadata,
            contextKeys: contextKeys);
        using IDisposable offlineMetadata = metadata.Register("offline", new CommandMetadata("Work Offline", When: "!test.offline"));
        using IDisposable reconcileMetadata = metadata.Register("reconcile", new CommandMetadata("Reconcile", When: "test.connected"));
        string location = ExtensionMenuLocations.Extensions;
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.submenu-enablement", new[]
        {
            new ExtensionMenuContribution("offline", "Work Offline", location, "scc", 10) { Submenu = "Source Control", CheckedWhen = "test.offline" },
            new ExtensionMenuContribution("reconcile", "Reconcile", location, "scc", 20) { Submenu = "Source Control" }
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        ExtensionMenuItemViewModel submenu = viewModel.ExtensionMenuItems.Single(item => item.Header == "Source Control");
        ExtensionMenuItemViewModel offline = submenu.Children.Single(item => item.Header == "Work Offline");
        ExtensionMenuItemViewModel reconcile = submenu.Children.Single(item => item.Header == "Reconcile");
        Assert.True(offline.IsEnabled);
        Assert.False(reconcile.IsEnabled);

        contextKeys.Set("test.connected", true);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.True(reconcile.IsEnabled);

        contextKeys.Set([new("test.offline", true), new("test.connected", false)]);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(offline.IsEnabled);
        Assert.True(offline.IsChecked);
        Assert.False(reconcile.IsEnabled);
    }

    [AvaloniaFact]
    public async Task MenuItemTitledWithTheReservedDashIsDrawnAsASeparatorButKeepsItsCommand()
    {
        ExtensionContributionRegistry contributions = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: new CommandRegistry(),
            extensionContributionRegistry: contributions,
            extensionViewRegistry: new ExtensionViewRegistry());
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.dash", new[]
        {
            new ExtensionMenuContribution("dash", "-", ExtensionMenuLocations.EditorContext, "1", 0)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        // "-" is reserved: the menu draws the header "-" as a separator, whatever the item is.
        ExtensionMenuItemViewModel dash = Assert.Single(viewModel.EditorContextMenuItems);
        Assert.Equal("-", dash.Header);
        Assert.False(dash.IsSeparator);
        Assert.NotNull(dash.Command);
    }
}
