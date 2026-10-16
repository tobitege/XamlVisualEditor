using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Model.Controls;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.OutputExtension;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;
using OutputExtensionEntry = XamlVisualEditor.OutputExtension.OutputExtension;

namespace XamlVisualEditor.Tests.UI;

public sealed class OutputChannelShowTests
{
    private const string OUTPUT_VIEW_ID = "output.panel";
    private const string PROBLEMS_VIEW_ID = "problems.panel";

    [AvaloniaFact]
    public async Task Show_of_a_channel_brings_the_output_view_to_front_with_that_channel_selected()
    {
        using ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host = new(additionalExtension: new OutputExtensionEntry());
        await host.ActivateAsync();
        host.Window.CreateOutputChannel("Other");
        IOutputChannel git = host.Window.CreateOutputChannel("Git");
        await host.FlushAsync();
        OutputPanelViewModel output = GetOutputPanel(host);

        await RunOnUiThreadAsync(host, () =>
        {
            host.ViewModel.ActivateExtensionView(OUTPUT_VIEW_ID);
            host.ViewModel.ToggleExtensionView(OUTPUT_VIEW_ID);
            output.SelectedChannel = null;
        });
        Assert.False(host.ViewModel.IsExtensionViewVisible(OUTPUT_VIEW_ID));
        Assert.Null(output.SelectedChannel);

        git.Show();
        await host.FlushAsync();

        Assert.True(host.ViewModel.IsExtensionViewVisible(OUTPUT_VIEW_ID));
        Assert.Equal(OUTPUT_VIEW_ID, GetActiveBottomViewId(host));
        Assert.Equal("Git", output.SelectedChannel?.Name);

        await RunOnUiThreadAsync(host, () =>
        {
            host.ViewModel.ActivateExtensionView(PROBLEMS_VIEW_ID);
            output.ChannelSearchText = "Other";
            output.SelectedChannel = null;
        });
        Assert.Equal(PROBLEMS_VIEW_ID, GetActiveBottomViewId(host));

        git.Show();
        await host.FlushAsync();

        Assert.Equal(OUTPUT_VIEW_ID, GetActiveBottomViewId(host));
        Assert.Equal("Git", output.SelectedChannel?.Name);
        Assert.Null(output.ChannelSearchText);
    }

    private static OutputPanelViewModel GetOutputPanel(ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host)
    {
        Assert.True(host.ViewModel.TryGetExtensionView(OUTPUT_VIEW_ID, out ExtensionViewModel? view));
        return Assert.IsType<OutputPanelViewModel>(Assert.IsType<ExtensionCustomViewModel>(view).ViewModel);
    }

    private static string? GetActiveBottomViewId(ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host)
    {
        IToolDock bottomDock = XamlEditorDockFactory.FindDockable<IToolDock>(host.ViewModel.DockLayout!, "BottomToolDock")!;
        return (bottomDock.ActiveDockable as ExtensionTool)?.ViewId;
    }

    private static async Task RunOnUiThreadAsync(
        ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost host,
        System.Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
        await host.FlushAsync();
    }
}
