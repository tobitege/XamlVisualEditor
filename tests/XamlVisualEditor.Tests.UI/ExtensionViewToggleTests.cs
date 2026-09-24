using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Model.Controls;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class ExtensionViewToggleTests
{
    [AvaloniaFact]
    public async Task Toggle_brings_a_covered_view_to_front_and_hides_only_the_active_view()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        using IDisposable firstProvider = views.RegisterCustomViewProvider("first.panel", new PanelProvider());
        using IDisposable secondProvider = views.RegisterCustomViewProvider("second.panel", new PanelProvider());
        using IDisposable registeredViews = contributions.RegisterViews("test.toggle", new[]
        {
            new ExtensionViewContribution("first.panel", "First", ExtensionViewType.Custom, ExtensionViewLocation.Right, 70),
            new ExtensionViewContribution("second.panel", "Second", ExtensionViewType.Custom, ExtensionViewLocation.Right, 71)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        viewModel.ActivateExtensionView("first.panel");
        viewModel.ActivateExtensionView("second.panel");
        IToolDock rightDock = XamlEditorDockFactory.FindDockable<IToolDock>(viewModel.DockLayout!, "RightToolDock")!;
        Assert.Equal("second.panel", Assert.IsType<ExtensionTool>(rightDock.ActiveDockable).ViewId);
        Assert.True(viewModel.IsExtensionViewVisible("first.panel"));

        viewModel.ToggleExtensionView("first.panel");

        Assert.True(viewModel.IsExtensionViewVisible("first.panel"));
        Assert.Equal("first.panel", Assert.IsType<ExtensionTool>(rightDock.ActiveDockable).ViewId);

        viewModel.ToggleExtensionView("first.panel");

        Assert.False(viewModel.IsExtensionViewVisible("first.panel"));
        Assert.DoesNotContain(rightDock.VisibleDockables!.OfType<ExtensionTool>(), tool => tool.ViewId == "first.panel");
    }

    [AvaloniaFact]
    public async Task Toggle_reopens_a_view_that_was_closed_in_the_dock()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        using IDisposable firstProvider = views.RegisterCustomViewProvider("first.panel", new PanelProvider());
        using IDisposable secondProvider = views.RegisterCustomViewProvider("second.panel", new PanelProvider());
        using IDisposable registeredViews = contributions.RegisterViews("test.toggle", new[]
        {
            new ExtensionViewContribution("first.panel", "First", ExtensionViewType.Custom, ExtensionViewLocation.Right, 70),
            new ExtensionViewContribution("second.panel", "Second", ExtensionViewType.Custom, ExtensionViewLocation.Right, 71)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        viewModel.ActivateExtensionView("first.panel");
        ExtensionTool first = XamlEditorDockFactory.FindDockable<ExtensionTool>(viewModel.DockLayout!, ExtensionTool.BuildId("first.panel"))!;

        viewModel.DockFactory.CloseDockable(first);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(viewModel.IsExtensionViewVisible("first.panel"));

        viewModel.ToggleExtensionView("first.panel");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(viewModel.IsExtensionViewVisible("first.panel"));
    }

    [AvaloniaFact]
    public async Task Toggle_reopens_a_debugger_tool_that_was_closed_in_the_dock()
    {
        using MainWindowViewModel viewModel = new();
        IWorkspaceCommands workspaceCommands = viewModel;
        BreakpointsTool closed = XamlEditorDockFactory.FindDockable<BreakpointsTool>(viewModel.DockLayout!, "Breakpoints")!;
        viewModel.DockFactory.CloseDockable(closed);
        Assert.Null(XamlEditorDockFactory.FindDockable<BreakpointsTool>(viewModel.DockLayout!, "Breakpoints"));

        await workspaceCommands.ToggleBreakpointsAsync(CancellationToken.None);

        BreakpointsTool reopened = XamlEditorDockFactory.FindDockable<BreakpointsTool>(viewModel.DockLayout!, "Breakpoints")!;
        Assert.Same(viewModel.Breakpoints, reopened.BreakpointsViewModel);
        Assert.True(reopened.Owner is IToolDock { VisibleDockables: { } shown } && shown.Contains(reopened));
        Assert.DoesNotContain(reopened, viewModel.DockLayout!.HiddenDockables!);
    }

    [AvaloniaFact]
    public async Task Toggle_shows_a_view_that_a_restored_layout_keeps_hidden_below_the_root()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        using IDisposable firstProvider = views.RegisterCustomViewProvider("first.panel", new PanelProvider());
        using IDisposable registeredViews = contributions.RegisterViews("test.toggle", new[]
        {
            new ExtensionViewContribution("first.panel", "First", ExtensionViewType.Custom, ExtensionViewLocation.Right, 70)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        viewModel.ActivateExtensionView("first.panel");
        IRootDock root = viewModel.DockLayout!;
        ExtensionTool first = XamlEditorDockFactory.FindDockable<ExtensionTool>(root, ExtensionTool.BuildId("first.panel"))!;
        ((Dock.Model.Core.IDock)first.Owner!).VisibleDockables!.Remove(first);
        first.Owner = root;
        root.HiddenDockables!.Add(first);
        Assert.False(viewModel.IsExtensionViewVisible("first.panel"));

        viewModel.ToggleExtensionView("first.panel");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(viewModel.IsExtensionViewVisible("first.panel"));
        Assert.DoesNotContain(first, root.HiddenDockables!);
        IToolDock rightDock = XamlEditorDockFactory.FindDockable<IToolDock>(root, "RightToolDock")!;
        Assert.Contains(rightDock.VisibleDockables!.OfType<ExtensionTool>(), tool => tool.ViewId == "first.panel");
    }

    [AvaloniaFact]
    public async Task Toggle_breakpoints_shows_them_after_a_restored_layout_kept_them_hidden_below_the_root()
    {
        using MainWindowViewModel viewModel = new();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        IRootDock root = viewModel.DockLayout!;
        Dock.Model.Core.IDockable breakpoints = XamlEditorDockFactory.FindDockable<Dock.Model.Core.IDockable>(root, "Breakpoints")!;
        if (breakpoints.Owner is Dock.Model.Core.IDock owner && owner is not IRootDock)
        {
            owner.VisibleDockables!.Remove(breakpoints);
        }

        root.LeftPinnedDockables?.Remove(breakpoints);
        root.RightPinnedDockables?.Remove(breakpoints);
        root.BottomPinnedDockables?.Remove(breakpoints);
        breakpoints.Owner = root;
        if (!root.HiddenDockables!.Contains(breakpoints))
        {
            root.HiddenDockables.Add(breakpoints);
        }

        await ((IWorkspaceCommands)viewModel).ToggleBreakpointsAsync(CancellationToken.None);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.DoesNotContain(breakpoints, root.HiddenDockables);
        Assert.True(viewModel.IsBreakpointsVisible);
        bool shownInDock = breakpoints.Owner is Dock.Model.Core.IDock dock && dock is not IRootDock && dock.VisibleDockables!.Contains(breakpoints);
        bool shownPinned = root.PinnedDock?.VisibleDockables?.Contains(breakpoints) == true;
        Assert.True(shownInDock || shownPinned);
    }

    [AvaloniaFact]
    public async Task Toggle_opens_a_view_in_its_own_window_when_its_dock_is_missing()
    {
        CommandRegistry commands = new();
        ExtensionContributionRegistry contributions = new();
        ExtensionViewRegistry views = new();
        using MainWindowViewModel viewModel = new(
            extensionCommands: commands,
            extensionContributionRegistry: contributions,
            extensionViewRegistry: views);
        IRootDock root = viewModel.DockLayout!;
        IToolDock rightDock = XamlEditorDockFactory.FindDockable<IToolDock>(root, "RightToolDock")!;
        ((Dock.Model.Core.IDock)rightDock.Owner!).VisibleDockables!.Remove(rightDock);
        using IDisposable firstProvider = views.RegisterCustomViewProvider("first.panel", new PanelProvider());
        using IDisposable registeredViews = contributions.RegisterViews("test.toggle", new[]
        {
            new ExtensionViewContribution("first.panel", "First", ExtensionViewType.Custom, ExtensionViewLocation.Right, 70)
        });
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.False(viewModel.IsExtensionViewVisible("first.panel"));

        viewModel.ToggleExtensionView("first.panel");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(viewModel.IsExtensionViewVisible("first.panel"));
        Assert.Contains(root.Windows!, window =>
            XamlEditorDockFactory.FindDockable<ExtensionTool>(window.Layout!, ExtensionTool.BuildId("first.panel")) is not null);
    }

    private sealed class PanelProvider : ICustomViewProvider
    {
        public object CreateViewModel() => new object();
    }
}
