using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Dock.Model.Controls;
using Dock.Model.Core;
using System.Reactive.Linq;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ServerPanelsTests
{
    [AvaloniaFact]
    public async Task ServerMenuOpensCollapsedPanelsAndResetRestoresTheirDefault()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        ExtensionViewHostAdapter host = new(viewModel);
        using IDisposable acpCommand = commands.Register("acp.toggleView", _ => host.ToggleAsync("acp.panel", CancellationToken.None));
        using IDisposable mcpCommand = commands.Register("mcp.toggleView", _ => host.ToggleAsync("mcp.panel", CancellationToken.None));
        using IDisposable propertiesCommand = commands.Register("properties.toggleView", _ => host.ToggleAsync("properties.panel", CancellationToken.None));
        using IDisposable acpProvider = views.RegisterCustomViewProvider("acp.panel", new PanelProvider());
        using IDisposable mcpProvider = views.RegisterCustomViewProvider("mcp.panel", new PanelProvider());
        using IDisposable propertiesProvider = views.RegisterCustomViewProvider("properties.panel", new PanelProvider());
        using IDisposable registeredViews = contributions.RegisterViews("test.server-panels", new[]
        {
            new ExtensionViewContribution("acp.panel", "ACP", ExtensionViewType.Custom, ExtensionViewLocation.Right, 30) { AutoHideByDefault = true },
            new ExtensionViewContribution("mcp.panel", "MCP", ExtensionViewType.Custom, ExtensionViewLocation.Right, 36) { AutoHideByDefault = true },
            new ExtensionViewContribution("properties.panel", "Properties", ExtensionViewType.Custom, ExtensionViewLocation.Right, 70)
        });
        using IDisposable registeredMenus = contributions.RegisterMenuItems("test.server-panels", new[]
        {
            new ExtensionMenuContribution("acp.toggleView", "ACP", ExtensionMenuLocations.ViewServers, "servers", 30),
            new ExtensionMenuContribution("mcp.toggleView", "MCP", ExtensionMenuLocations.ViewServers, "servers", 36),
            new ExtensionMenuContribution("properties.toggleView", "Properties", ExtensionMenuLocations.View, "views.right", 70)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.Equal(new[] { "ACP", "MCP" }, viewModel.ServerMenuItems.Select(item => item.Title));
        Assert.Single(viewModel.ViewMenuItems);
        Assert.Equal("Properties", viewModel.ViewMenuItems[0].Title);
        AssertCollapsedServers(viewModel.DockLayout);

        XamlVisualEditor.App.MainWindow window = new(viewModel);
        WindowLayoutBehavior.SetIsEnabled(window, false);
        try
        {
            Menu menu = window.GetLogicalDescendants().OfType<Menu>().First();
            MenuItem viewMenu = menu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuView));
            MenuItem serverMenu = viewMenu.Items.OfType<MenuItem>().Single(item => Equals(item.Header, UiStrings.MenuServers));
            Assert.Same(viewModel.ServerMenuItems, serverMenu.ItemsSource);

            IDockable mainLayout = viewModel.DockLayout.ActiveDockable!;
            foreach (ExtensionMenuItemViewModel item in viewModel.ServerMenuItems)
            {
                var command = Assert.IsAssignableFrom<System.Windows.Input.ICommand>(item.Command);
                command.Execute(null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                ExtensionTool opened = Assert.IsType<ExtensionTool>(viewModel.DockLayout.PinnedDock!.ActiveDockable);
                Assert.Equal(item.Title, opened.Title);
                Assert.Same(mainLayout, viewModel.DockLayout.ActiveDockable);
                Assert.Contains(opened, viewModel.DockLayout.RightPinnedDockables!);
            }

            await viewModel.ResetLayoutCommand.Execute();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            AssertCollapsedServers(viewModel.DockLayout);
        }
        finally
        {
            window.Close();
        }
    }

    private static void AssertCollapsedServers(IRootDock layout)
    {
        Assert.Equal(new[] { "acp.panel", "mcp.panel" },
            layout.RightPinnedDockables!.OfType<ExtensionTool>().Select(tool => tool.ViewId).OrderBy(id => id));
        Assert.True(layout.PinnedDock?.ActiveDockable is null);
        IToolDock rightDock = XamlEditorDockFactory.FindDockable<IToolDock>(layout, "RightToolDock")!;
        Assert.NotNull(rightDock);
        ExtensionTool visible = Assert.IsType<ExtensionTool>(Assert.Single(rightDock.VisibleDockables!));
        Assert.Equal("properties.panel", visible.ViewId);
        Assert.Same(visible, rightDock.ActiveDockable);
    }

    private sealed class PanelProvider : ICustomViewProvider
    {
        public object CreateViewModel() => new object();
    }
}
