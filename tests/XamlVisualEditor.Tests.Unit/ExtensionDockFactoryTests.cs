using Avalonia.Headless.XUnit;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.ReactiveUI.Controls;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ExtensionDockFactoryTests
{
    private sealed class LayoutLogger : ILogger<XamlEditorDockFactory>
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData("BottomPinnedDockables")]
    [InlineData("HiddenDockables")]
    [InlineData("VisibleDockables")]
    public void LoadLayout_RemovesFormerExtensionDockWithoutResettingLayout(string collection)
    {
        using MainWindowViewModel viewModel = new();
        LayoutLogger logger = new();
        XamlEditorDockFactory factory = new(viewModel, logger);
        IRootDock layout = factory.CreateDefaultLayout();
        factory.InitLayout(layout);
        ToolDock left = XamlEditorDockFactory.FindDockable<ToolDock>(layout, "LeftToolDock")!;
        ToolDock right = XamlEditorDockFactory.FindDockable<ToolDock>(layout, "RightToolDock")!;
        left.Proportion = 0.18;
        right.Proportion = 0.31;
        factory.CollapseBottomToolDock(layout);
        IDockable? leftOwner = left.Owner;
        IFactory? leftFactory = left.Factory;
        IDockable[] pinnedTools = layout.BottomPinnedDockables!.ToArray();
        OutputTool output = XamlEditorDockFactory.FindDockable<OutputTool>(layout, "Output")!;
        var outputViewModel = output.OutputViewModel;
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xve-dialog-layout-" + Guid.NewGuid().ToString("N") + ".json");
        factory.SaveLayout(layout, path);
        Assert.True(System.IO.File.Exists(path), string.Join(Environment.NewLine, logger.Messages));
        Assert.Same(leftOwner, left.Owner);
        Assert.Same(leftFactory, left.Factory);
        Assert.Same(outputViewModel, output.OutputViewModel);
        Assert.Equal(pinnedTools, layout.BottomPinnedDockables);
        System.Text.Json.Nodes.JsonObject json = System.Text.Json.Nodes.JsonNode.Parse(System.IO.File.ReadAllText(path))!.AsObject();
        System.Text.Json.Nodes.JsonObject obsolete = new()
        {
            ["$type"] = "XamlVisualEditor.Shell.ExtensionManagerTool",
            ["Id"] = "ExtensionsManager",
            ["Title"] = "Extensions"
        };
        json[collection] ??= new System.Text.Json.Nodes.JsonArray();
        json[collection]!.AsArray().Add(obsolete);
        json[nameof(IRootDock.ActiveDockable)] = obsolete.DeepClone();
        json[nameof(IRootDock.DefaultDockable)] = obsolete.DeepClone();
        System.IO.File.WriteAllText(path, json.ToJsonString());

        IRootDock? restoredLayout = factory.LoadLayout(path);
        Assert.True(restoredLayout is not null, string.Join(Environment.NewLine, logger.Messages));
        IRootDock restored = restoredLayout!;

        Assert.Equal("MainLayout", restored.ActiveDockable!.Id);
        Assert.Equal(0.18, XamlEditorDockFactory.FindDockable<ToolDock>(restored, "LeftToolDock")!.Proportion);
        Assert.Equal(0.31, XamlEditorDockFactory.FindDockable<ToolDock>(restored, "RightToolDock")!.Proportion);
        Assert.Equal(0, XamlEditorDockFactory.FindDockable<ToolDock>(restored, "BottomToolDock")!.Proportion);
        Assert.Contains(restored.BottomPinnedDockables!, tool => tool.Id == "Breakpoints");
        Assert.Null(XamlEditorDockFactory.FindDockable<IDockable>(restored, "ExtensionsManager"));
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task TogglePinnedTool_PreservesMainLayoutAndAutoHideStrip()
    {
        using MainWindowViewModel viewModel = new();
        IRootDock layout = viewModel.DockLayout;
        viewModel.DockFactory.CollapseBottomToolDock(layout);
        IDockable mainLayout = layout.ActiveDockable!;
        IDockable tool = XamlEditorDockFactory.FindDockable<IDockable>(layout, "Breakpoints")!;
        viewModel.IsBreakpointsVisible = false;
        IWorkspaceCommands commands = viewModel;

        for (int iteration = 0; iteration < 2; iteration++)
        {
            await commands.ToggleBreakpointsAsync(System.Threading.CancellationToken.None);
            Assert.Same(mainLayout, layout.ActiveDockable);
            Assert.Contains(tool, layout.BottomPinnedDockables!);
            Assert.Same(tool, layout.PinnedDock!.ActiveDockable);

            await commands.ToggleBreakpointsAsync(System.Threading.CancellationToken.None);
            Assert.Same(mainLayout, layout.ActiveDockable);
            Assert.Contains(tool, layout.BottomPinnedDockables!);
            Assert.DoesNotContain(tool, layout.PinnedDock?.VisibleDockables ?? Array.Empty<IDockable>());
        }
    }

    [Fact]
    public void CollapseBottomToolDock_PreservesToolsAndTheirSizeAcrossReload()
    {
        using MainWindowViewModel viewModel = new();
        IRootDock layout = viewModel.DockFactory.CreateDefaultLayout();
        viewModel.DockFactory.InitLayout(layout);
        ToolDock? bottomDock = XamlEditorDockFactory.FindDockable<ToolDock>(
            layout,
            "BottomToolDock");
        Assert.NotNull(bottomDock);
        Assert.NotEmpty(bottomDock!.VisibleDockables!);
        bottomDock.ActiveDockable = bottomDock.VisibleDockables![0];
        bottomDock.IsExpanded = true;

        viewModel.DockFactory.CollapseBottomToolDock(layout);

        Assert.Null(bottomDock.ActiveDockable);
        Assert.False(bottomDock.IsExpanded);
        Assert.Equal(0, bottomDock.Proportion);
        Assert.Equal(0.25, bottomDock.CollapsedProportion);
        Assert.Empty(bottomDock.VisibleDockables!);
        Assert.NotEmpty(layout.BottomPinnedDockables!);
        IDockable pinnedTool = layout.BottomPinnedDockables![0];
        pinnedTool.GetPinnedBounds(out _, out _, out double width, out double height);
        Assert.Equal(400, width);
        Assert.Equal(300, height);
        pinnedTool.SetPinnedBounds(0, 0, 760, 420);

        string path = Path.Combine(Path.GetTempPath(), "xve-pinned-layout-" + Guid.NewGuid().ToString("N") + ".json");
        viewModel.DockFactory.SaveLayout(layout, path);
        IRootDock restored = Assert.IsAssignableFrom<IRootDock>(viewModel.DockFactory.LoadLayout(path));
        viewModel.DockFactory.InitLayout(restored);
        IDockable restoredTool = Assert.Single(restored.BottomPinnedDockables!, tool => tool.Id == pinnedTool.Id);
        restoredTool.GetPinnedBounds(out _, out _, out width, out height);
        Assert.Equal(760, width);
        Assert.Equal(420, height);
        Assert.Null(restored.PinnedDock?.ActiveDockable);
        Assert.Equal(0, XamlEditorDockFactory.FindDockable<ToolDock>(restored, "BottomToolDock")!.Proportion);
    }

    [Fact]
    public void AddExtensionTool_WiresOwnerAndFactory_WhenInsertedIntoLeftDock()
    {
        using MainWindowViewModel viewModel = new();
        ExtensionViewContribution contribution = new(
            "test.left.panel",
            "Test Left Panel",
            ExtensionViewType.Webview,
            ExtensionViewLocation.Left,
            10);
        ExtensionWebviewViewModel extensionView = new(contribution, "Placeholder");

        ExtensionTool? tool = viewModel.DockFactory.AddExtensionTool(viewModel.DockLayout, extensionView);

        ExtensionTool actual = Assert.IsType<ExtensionTool>(tool);
        IDock owner = Assert.IsAssignableFrom<IDock>(actual.Owner);
        Assert.Same(viewModel.DockFactory, owner.Factory);
        Assert.NotNull(actual.DockCapabilityOverrides);
        Assert.NotNull(owner.DockCapabilityPolicy);
    }
}
