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
            viewModel.ViewMenuItems.Select(item => item.Title));
    }
}
