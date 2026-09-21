using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using ReactiveUI;
using Xunit;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.App;
using XamlVisualEditor.Designer.Rendering;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.TreeView;
using XamlVisualEditor.PropertyEditor;
using XamlVisualEditor.Collaboration.UI;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Workspace;

namespace XamlVisualEditor.Tests.UI;

// ==============================================
// 10.3.1 — Designer Surface Interactions
// ==============================================

/// <summary>
/// Exercises designer state and viewport input through headless controls.
/// </summary>
public sealed class DesignerSurfaceTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task DesignSurfaceViewModel_Can_Set_Zoom()
    {
        DesignSurfaceViewModel vm = new() { CanvasWidth = 460, CanvasHeight = 360 };
        vm.Zoom = 1.5;
        Assert.Equal(1.5, vm.Zoom);
        vm.Zoom = 8;
        Assert.Equal(3, vm.Zoom);
        vm.Zoom = 0.1;
        Assert.Equal(0.25, vm.Zoom);

        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            XamlVisualEditor.App.Views.DesignSurfaceView view = new() { DataContext = vm };
            window = new Window { Content = view, Width = 900, Height = 650 };
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            XamlVisualEditor.App.Views.DesignerViewportToolbar toolbar = view.GetVisualDescendants()
                .OfType<XamlVisualEditor.App.Views.DesignerViewportToolbar>().Single();
            Control surface = view.FindControl<Control>("ZoomSurface")!;
            Control container = view.FindControl<Control>("ZoomContainer")!;
            Panel canvas = view.FindControl<Panel>("DesignCanvas")!;
            ScrollViewer scroll = view.FindControl<ScrollViewer>("SurfaceScrollViewer")!;
            XamlVisualEditor.Designer.Adorners.RulerControl horizontalRuler = view.FindControl<XamlVisualEditor.Designer.Adorners.RulerControl>("HorizontalRuler")!;
            XamlVisualEditor.Designer.Adorners.RulerControl verticalRuler = view.FindControl<XamlVisualEditor.Designer.Adorners.RulerControl>("VerticalRuler")!;
            foreach (double zoom in new[] { 0.25, 0.75, 1, 2, 3 })
            {
                vm.Zoom = zoom;
                scroll.Offset = default;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                Point origin = surface.TranslatePoint(default, container)!.Value;
                Point extent = surface.TranslatePoint(new Point(vm.CanvasWidth, vm.CanvasHeight), container)!.Value;
                Assert.Equal(0, origin.X, 8);
                Assert.Equal(0, origin.Y, 8);
                Assert.Equal(vm.CanvasWidth * zoom, extent.X, 8);
                Assert.Equal(vm.CanvasHeight * zoom, extent.Y, 8);
                Point canvasOrigin = canvas.TranslatePoint(default, window)!.Value;
                Assert.Equal(canvasOrigin.X, horizontalRuler.TranslatePoint(default, window)!.Value.X - horizontalRuler.Offset, 8);
                Assert.Equal(canvasOrigin.Y, verticalRuler.TranslatePoint(default, window)!.Value.Y - verticalRuler.Offset, 8);
            }

            vm.Zoom = 2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            scroll.Offset = new Vector(40, 80);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.True(scroll.Offset.X > 0 && scroll.Offset.Y > 0);
            Point viewportCenter = new(scroll.Viewport.Width / 2, scroll.Viewport.Height / 2);
            Point canvasFocus = scroll.TranslatePoint(viewportCenter, canvas)!.Value;
            Assert.True(view.Focus());
            view.RaiseEvent(new KeyEventArgs
            {
                RoutedEvent = InputElement.KeyDownEvent,
                Key = Key.OemPlus,
                KeyModifiers = KeyModifiers.Control
            });
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Assert.Equal(2.25, vm.Zoom);
            Point zoomedFocus = canvas.TranslatePoint(canvasFocus, scroll)!.Value;
            Assert.InRange(Math.Abs(zoomedFocus.X - viewportCenter.X), 0, 1);
            Assert.InRange(Math.Abs(zoomedFocus.Y - viewportCenter.Y), 0, 1);
            Point scrolledOrigin = canvas.TranslatePoint(default, window)!.Value;
            Assert.Equal(scrolledOrigin.X, horizontalRuler.TranslatePoint(default, window)!.Value.X - horizontalRuler.Offset, 8);
            Assert.Equal(scrolledOrigin.Y, verticalRuler.TranslatePoint(default, window)!.Value.Y - verticalRuler.Offset, 8);

            await Click("Fit");
            window.UpdateLayout();
            Point fittedOrigin = canvas.TranslatePoint(default, scroll)!.Value;
            Point fittedExtent = canvas.TranslatePoint(new Point(vm.CanvasWidth, vm.CanvasHeight), scroll)!.Value;
            Assert.InRange(fittedOrigin.X, 0, scroll.Viewport.Width);
            Assert.InRange(fittedOrigin.Y, 0, scroll.Viewport.Height);
            Assert.InRange(fittedExtent.X, 0, scroll.Viewport.Width + 1);
            Assert.InRange(fittedExtent.Y, 0, scroll.Viewport.Height + 1);

            vm.Zoom = 0.25;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            Slider slider = toolbar.GetVisualDescendants().OfType<Slider>().Single();
            Track track = slider.GetVisualDescendants().OfType<Track>().Single();
            double thumbWidth = track.Thumb!.Bounds.Width;
            double travel = track.Bounds.Width - thumbWidth;
            Point point = track.TranslatePoint(new Point(thumbWidth / 2, track.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left);
            for (int index = 0; index <= 20; index++)
            {
                point = track.TranslatePoint(new Point(thumbWidth / 2 + travel * index / 20, track.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point, RawInputModifiers.LeftMouseButton);
                Assert.InRange(vm.Zoom, 0.25, 3);
                Assert.Equal(Math.Round(vm.Zoom / 0.25), vm.Zoom / 0.25, 8);
            }
            window.MouseUp(point, MouseButton.Left);
            Assert.Equal(3, vm.Zoom);

            async System.Threading.Tasks.Task Click(string text)
            {
                Button button = toolbar.GetVisualDescendants().OfType<Button>().Single(control => Equals(control.Content, text));
                Point center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
            vm.Zoom = 0.87;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(0.87, vm.Zoom);
            await Click("+");
            Assert.Equal(1, vm.Zoom);
            await Click("-");
            Assert.Equal(0.75, vm.Zoom);
            vm.Zoom = 3;
            await Click("+");
            Assert.Equal(3, vm.Zoom);
            vm.Zoom = 0.25;
            await Click("-");
            Assert.Equal(0.25, vm.Zoom);
            await Click("100%");
            Assert.Equal(1, vm.Zoom);
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task CancelingDocumentLoadDoesNotReopenTheClosedDocument()
    {
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xve-cancel-load-" + Guid.NewGuid().ToString("N") + ".axaml");
        const string original = "<UserControl xmlns=\"https://github.com/avaloniaui\"><Button Content=\"Unchanged\" /></UserControl>";
        await System.IO.File.WriteAllTextAsync(path, original);
        using MainWindowViewModel main = new(metadataService: new TypeMetadataService());
        System.Threading.Tasks.Task opening = main.OpenFileAsync(path);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(Assert.Single(main.Documents));
        Assert.True(document.IsLoading);
        Assert.False(document.IsLoaded);
        Assert.False(((System.Windows.Input.ICommand)document.SaveCommand).CanExecute(null));
        document.UseExternalPreviewer = true;

        await document.CloseCommand.Execute();
        await opening.WaitAsync(TimeSpan.FromSeconds(5));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(document.IsDisposed);
        Assert.False(document.IsLoading);
        Assert.False(document.IsLoaded);
        Assert.Null(document.PreviewerSession);
        Assert.Empty(main.Documents);
        Assert.Null(main.ActiveDesignerDocument);
        Assert.StartsWith("Closed ", main.StatusText);
        Assert.Equal(original, await System.IO.File.ReadAllTextAsync(path));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async System.Threading.Tasks.Task PreviewerStartup_WaitsForLoadedDocumentAndKeepsItsOwner(bool projectWorkspace)
    {
        string firstPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xve-preview-first-" + Guid.NewGuid().ToString("N") + ".axaml");
        string secondPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xve-preview-second-" + Guid.NewGuid().ToString("N") + ".axaml");
        const string firstText = "<UserControl xmlns=\"https://github.com/avaloniaui\"><TextBlock Text=\"First\" /></UserControl>";
        const string secondText = "<UserControl xmlns=\"https://github.com/avaloniaui\"><TextBlock Text=\"Second\" /></UserControl>";
        await System.IO.File.WriteAllTextAsync(firstPath, firstText);
        await System.IO.File.WriteAllTextAsync(secondPath, secondText);
        List<(DesignerDocumentViewModel Owner, string? Text, bool Loaded)> starts = new();
        System.Threading.Tasks.Task Start(DesignerDocumentViewModel owner)
        {
            starts.Add((owner, owner.SyncEngine.CurrentText, owner.IsLoaded));
            return System.Threading.Tasks.Task.CompletedTask;
        }

        WorkspaceModel? workspace = projectWorkspace ? new WorkspaceModel
        {
            Projects = new[] { new ProjectModel
            {
                Name = "PreviewLibrary", ProjectPath = System.IO.Path.ChangeExtension(firstPath, ".csproj"),
                XamlFiles = Array.Empty<XamlFileModel>(), Files = Array.Empty<ProjectFileModel>(),
                References = Array.Empty<AssemblyReference>()
            } }
        } : null;
        using DesignerDocumentViewModel first = new(firstPath, workspaceProvider: () => workspace, startPreviewerAsync: Start);
        using DesignerDocumentViewModel second = new(secondPath, workspaceProvider: () => workspace, startPreviewerAsync: Start);
        Assert.Equal(projectWorkspace, first.UseExternalPreviewer);
        Assert.Equal(projectWorkspace, second.UseExternalPreviewer);
        if (!projectWorkspace)
        {
            first.UseExternalPreviewer = true;
            second.UseExternalPreviewer = true;
        }
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Empty(starts);

        await second.LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Same(second, Assert.Single(starts).Owner);
        Assert.Equal(secondText, starts[0].Text);
        await first.LoadAsync();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, starts.Count);
        Assert.Same(first, starts[1].Owner);
        Assert.Equal(firstText, starts[1].Text);
        Assert.All(starts, start => Assert.True(start.Loaded));

        first.SetPreviewerLaunchError("Missing host application.");
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "XVE_PREVIEW_START" && diagnostic.FilePath == firstPath);
        Assert.Empty(second.Diagnostics);
        using PreviewerTcpSession session = new(firstPath, log: null);
        first.PreviewerSession = session;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Contains(first.Diagnostics, diagnostic => diagnostic.Code == "XVE_PREVIEW_START");
        first.SetPreviewerLaunchError(null);
        Assert.DoesNotContain(first.Diagnostics, diagnostic => diagnostic.Code == "XVE_PREVIEW_START");

        first.Dispose();
        first.UseExternalPreviewer = false;
        first.UseExternalPreviewer = true;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Assert.Equal(2, starts.Count);
    }

    [Fact]
    public void DesignSurfaceViewModel_Can_Toggle_Grid()
    {
        DesignSurfaceViewModel vm = new();

        bool initial = vm.ShowGrid;
        vm.ShowGrid = !initial;

        Assert.NotEqual(initial, vm.ShowGrid);
    }

    [Fact]
    public void SelectionManager_Select_And_Clear()
    {
        SelectionManager manager = new();
        MutableAstObjectNode node = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        DesignItem item = new(node);

        manager.Select(item);
        Assert.Single(manager.SelectedItems);

        manager.ClearSelection();
        Assert.Empty(manager.SelectedItems);
    }

    [Fact]
    public void SelectionManager_MultiSelect()
    {
        SelectionManager manager = new();
        MutableAstObjectNode node1 = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        MutableAstObjectNode node2 = new() { TypeName = "TextBlock", XmlNamespace = "https://github.com/avaloniaui" };
        DesignItem item1 = new(node1);
        DesignItem item2 = new(node2);

        manager.Select(item1);
        manager.ToggleSelection(item2);

        Assert.Equal(2, manager.SelectedItems.Count);
    }

    [AvaloniaFact]
    public void DesignerDocument_SelectionManager_Is_DesignSurface_Selection()
    {
        using DesignerDocumentViewModel doc = new("SelectionSyncTest.axaml");
        MutableAstObjectNode node = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        DesignItem item = new(node);

        Assert.Same(doc.DesignSurface.Selection, doc.SelectionManager);

        doc.DesignSurface.Selection.Select(item);

        Assert.Single(doc.SelectionManager.SelectedItems);
        Assert.Equal(node.Id, doc.SelectedNodeId);
    }
}

// ==============================================
// 10.3.2 — Code Editor Typing and Completion
// ==============================================

/// <summary>
/// Tests for code editor ViewModel behavior.
/// </summary>
public sealed class CodeEditorTests
{
    [AvaloniaFact]
    public void CodeEditorViewModel_SetTextSilently()
    {
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        AstNodeMap map = new();
        SyncEngine engine = new(parser, serializer, map);
        XamlVisualEditor.Xaml.Intellisense.CompletionProviderRegistry registry = new();
        XamlVisualEditor.CodeEditor.CodeEditorViewModel vm = new("Test.xaml", engine, registry);

        string xaml = "<Grid xmlns=\"https://github.com/avaloniaui\" />";
        vm.SetTextSilently(xaml);

        Assert.Equal(xaml, vm.Document.Text);
    }

    [AvaloniaFact]
    public void CodeEditorViewModel_Options_Defaults()
    {
        XamlParsingService parser = new();
        XamlSerializationService serializer = new();
        AstNodeMap map = new();
        SyncEngine engine = new(parser, serializer, map);
        XamlVisualEditor.Xaml.Intellisense.CompletionProviderRegistry registry = new();
        XamlVisualEditor.CodeEditor.CodeEditorViewModel vm = new("Test.xaml", engine, registry);

        Assert.True(vm.ShowLineNumbers);
        Assert.True(vm.FontSize > 0);
    }
}

// ==============================================
// 10.3.3 — Tree View Selection Sync
// ==============================================

/// <summary>
/// Tests for tree view selection synchronization.
/// </summary>
public sealed class TreeViewSelectionSyncTests
{
    [Fact]
    public void VisualTreeNode_Selection_Fires_Event()
    {
        MutableAstObjectNode astNode = new() { TypeName = "Grid", XmlNamespace = "https://github.com/avaloniaui" };
        VisualTreeNodeViewModel vm = new(astNode.Id);
        vm.TypeName = astNode.TypeName;

        Guid? receivedId = null;
        vm.NodeSelected += id => receivedId = id;

        // Setting IsSelected triggers the NodeSelected event via the subscription in the constructor
        vm.IsSelected = true;

        // The event should fire with the node's ID
        Assert.NotNull(receivedId);
        Assert.Equal(astNode.Id, receivedId);
    }

    [Fact]
    public void VisualTree_FromAstDocument_Creates_Tree()
    {
        MutableAstObjectNode root = new() { TypeName = "Grid", XmlNamespace = "https://github.com/avaloniaui" };
        MutableAstObjectNode child = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        root.Children.Add(child);

        MutableAstDocument doc = new() { Root = root };

        VisualTreeNodeViewModel? treeRoot = VisualTreeNodeViewModel.FromAstDocument(doc);

        Assert.NotNull(treeRoot);
        Assert.Equal("Grid", treeRoot!.TypeName);
        Assert.Single(treeRoot.Children);
        Assert.Equal("Button", treeRoot.Children[0].TypeName);
    }

    [Fact]
    public void LogicalTree_FromAstDocument_Creates_Tree()
    {
        MutableAstObjectNode root = new() { TypeName = "StackPanel", XmlNamespace = "https://github.com/avaloniaui" };
        MutableAstObjectNode child = new() { TypeName = "TextBlock", XmlNamespace = "https://github.com/avaloniaui" };
        child.SetPropertyValue("Text", "Hello");
        root.Children.Add(child);

        MutableAstDocument doc = new() { Root = root };

        LogicalTreeNodeViewModel? treeRoot = LogicalTreeNodeViewModel.FromAstDocument(doc);

        Assert.NotNull(treeRoot);
        Assert.Equal("StackPanel", treeRoot!.TypeName);
        Assert.Single(treeRoot.Children);
    }
}

// ==============================================
// 10.3.4 — Property Editor Value Editing
// ==============================================

/// <summary>
/// Tests for property editor value editing.
/// </summary>
public sealed class PropertyEditorEditingTests
{
    [Fact]
    public void PropertyEditor_LoadFromDesignItem_Populates_Properties()
    {
        AstNodeMap map = new();
        PropertyEditorViewModel vm = new(map);

        MutableAstObjectNode node = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        node.SetPropertyValue("Content", "Click Me");
        node.SetPropertyValue("Width", "100");
        map.Register(node);
        DesignItem item = new(node);

        vm.LoadFromDesignItem(item);

        Assert.NotEmpty(vm.Categories);
    }

    [Fact]
    public void PropertyEditor_ApplyValue_Updates_AST()
    {
        AstNodeMap map = new();
        PropertyEditorViewModel vm = new(map);

        MutableAstObjectNode node = new() { TypeName = "Button", XmlNamespace = "https://github.com/avaloniaui" };
        node.SetPropertyValue("Width", "100");
        map.Register(node);
        DesignItem item = new(node);

        vm.LoadFromDesignItem(item);

        // Find the Width property and change it
        PropertyItemViewModel? widthProp = vm.Categories.SelectMany(c => c.Properties).FirstOrDefault(p => p.Name == "Width");
        Assert.NotNull(widthProp);
        widthProp.Value = "200";
        vm.ApplyPropertyChange(widthProp);

        string? newValue = node.GetPropertyValue("Width");
        Assert.Equal("200", newValue);
    }
}

// ==============================================
// 10.3.5 — Docking Layout Manipulation
// ==============================================

/// <summary>
/// Tests for docking layout commands.
/// </summary>
public sealed class DockingLayoutTests
{
    [AvaloniaFact]
    public void MainWindowViewModel_ResetLayout_Sets_All_Panels_Visible()
    {
        MainWindowViewModel vm = new();
        vm.IsToolboxVisible = false;
        vm.IsPropertiesVisible = false;
        vm.IsOutputVisible = false;
        vm.IsBreakpointsVisible = false;
        vm.IsCallStackVisible = false;
        vm.IsLocalsVisible = false;
        vm.IsWatchesVisible = false;

        vm.ResetLayoutCommand.Execute().Subscribe();

        Assert.True(vm.IsToolboxVisible);
        Assert.True(vm.IsPropertiesVisible);
        Assert.True(vm.IsVisualTreeVisible);
        Assert.True(vm.IsLogicalTreeVisible);
        Assert.True(vm.IsOutputVisible);
        Assert.True(vm.IsBreakpointsVisible);
        Assert.True(vm.IsCallStackVisible);
        Assert.True(vm.IsLocalsVisible);
        Assert.True(vm.IsWatchesVisible);
    }

    [AvaloniaFact]
    public void MainWindowViewModel_ToggleCommands_Work()
    {
        MainWindowViewModel vm = new();
        IWorkspaceCommands workspaceCommands = vm;

        bool initial = vm.IsBreakpointsVisible;
        workspaceCommands.ToggleBreakpointsAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult();

        Assert.NotEqual(initial, vm.IsBreakpointsVisible);
    }
}

// ==============================================
// 10.3.6 — Drag-and-Drop Workflows
// ==============================================

/// <summary>
/// Tests for drag-and-drop ViewModel operations.
/// </summary>
public sealed class DragDropWorkflowTests
{
    [Fact]
    public void ToolboxItem_Can_Create_DragData()
    {
        ToolboxItemViewModel item = new("Button", "Button", "https://github.com/avaloniaui", "Controls");

        Assert.Equal("Button", item.DisplayName);
        Assert.Equal("Button", item.TypeName);
        Assert.Equal("Controls", item.Category);
    }

    [Fact]
    public void DropPosition_Enum_Has_Expected_Values()
    {
        Assert.True(Enum.IsDefined(typeof(DropPosition), DropPosition.Before));
        Assert.True(Enum.IsDefined(typeof(DropPosition), DropPosition.After));
        Assert.True(Enum.IsDefined(typeof(DropPosition), DropPosition.Inside));
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task NewDocument_Creates_Document_ViewModel()
    {
        MainWindowViewModel vm = new();

        await vm.NewDocumentCommand.Execute().FirstAsync();

        Assert.Single(vm.Documents);
        Assert.NotNull(vm.ActiveDocument);
    }
}

/// <summary>
/// Placeholder for full Avalonia headless UI tests.
/// These would use [AvaloniaFact] for proper UI testing.
/// </summary>
public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void MainWindowViewModel_Creates_With_Default_State()
    {
        MainWindowViewModel vm = new();

        Assert.True(vm.IsToolboxVisible);
        Assert.True(vm.IsPropertiesVisible);
        Assert.True(vm.IsOutputVisible);
        Assert.True(vm.IsBreakpointsVisible);
        Assert.True(vm.IsCallStackVisible);
        Assert.True(vm.IsLocalsVisible);
        Assert.True(vm.IsWatchesVisible);
        Assert.Empty(vm.Documents);
    }
}

public sealed class HeadlessUiTests
{
    [AvaloniaFact]
    public async System.Threading.Tasks.Task SaveShortcutPersistsTheActiveDocumentWithClosedMenus()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "xve-save-shortcut-" + Guid.NewGuid().ToString("N") + ".axaml");
        const string original = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Width=\"100\" /></Canvas>";
        string edited = original.Replace("100", "140", StringComparison.Ordinal);
        await System.IO.File.WriteAllTextAsync(path, original);
        using MainWindowViewModel viewModel = new();
        MainWindow window = new(viewModel) { Focusable = true };
        try
        {
            window.Show();
            await viewModel.OpenFileAsync(path);
            DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(viewModel.ActiveDocument);
            document.CodeEditor.Document.Text = edited;
            await document.WhenAnyValue(item => item.IsModified).Where(value => value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.True(window.Focus());

            window.KeyPress(Key.S, RawInputModifiers.Control, PhysicalKey.S, null);

            await document.WhenAnyValue(item => item.IsModified).Where(value => !value)
                .FirstAsync().Timeout(TimeSpan.FromSeconds(5)).ToTask();
            Assert.Equal(edited, await System.IO.File.ReadAllTextAsync(path));
            Assert.DoesNotContain(window.GetVisualDescendants().OfType<MenuItem>(), item => item.IsSubMenuOpen);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async System.Threading.Tasks.Task ExtensionsDialog_OpensAndClosesWithoutChangingDockLayout()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        using MainWindowViewModel viewModel = new();
        MainWindow window = new(viewModel);
        var layout = viewModel.DockLayout;
        var mainLayout = layout.ActiveDockable;
        var left = XamlVisualEditor.Shell.XamlEditorDockFactory.FindDockable<Dock.Model.ReactiveUI.Controls.ToolDock>(layout, "LeftToolDock")!;
        left.Proportion = 0.18;
        try
        {
            window.Show();
            for (int iteration = 0; iteration < 2; iteration++)
            {
                var execution = viewModel.OpenExtensionManagerCommand.Execute().ToTask();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                XamlVisualEditor.App.Views.ExtensionManagerDialog dialog = Assert.Single(
                    window.OwnedWindows.OfType<XamlVisualEditor.App.Views.ExtensionManagerDialog>());
                Assert.True(dialog.IsVisible);
                Assert.Same(viewModel.ExtensionManager, dialog.DataContext);
                Assert.Same(layout, viewModel.DockLayout);
                Assert.Same(mainLayout, layout.ActiveDockable);
                Assert.Equal(0.18, left.Proportion);
                Assert.Null(XamlVisualEditor.Shell.XamlEditorDockFactory.FindDockable<Dock.Model.Core.IDockable>(layout, "ExtensionsManager"));

                dialog.UpdateLayout();
                if (iteration == 0)
                {
                    Button close = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Close"));
                    Point center = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), dialog)!.Value;
                    dialog.MouseDown(center, MouseButton.Left);
                    dialog.MouseUp(center, MouseButton.Left);
                }
                else
                {
                    dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                }
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.False(dialog.IsVisible);
                await execution.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Same(mainLayout, layout.ActiveDockable);
                Assert.Equal(0.18, left.Proportion);
            }
        }
        finally
        {
            foreach (Window owned in window.OwnedWindows.ToArray()) owned.Close();
            window.Close();
            Application.Current!.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public void MainWindow_Creates_With_DataContext()
    {
        MainWindowViewModel vm = new();
        MainWindow window = new(vm);

        Assert.Same(vm, window.DataContext);
    }

    [AvaloniaFact]
    public void ControlFactory_HitTest_Returns_Child_When_Inside()
    {
        Canvas root = new() { Width = 200, Height = 200 };
        Border child = new()
        {
            Width = 50,
            Height = 40
        };
        Canvas.SetLeft(child, 20);
        Canvas.SetTop(child, 30);
        root.Children.Add(child);

        Window window = new()
        {
            Width = 200,
            Height = 200,
            Content = root
        };

        window.Measure(new Size(200, 200));
        window.Arrange(new Rect(0, 0, 200, 200));
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));

        Control? hit = ControlFactory.HitTest(root, new Point(25, 35));

        Assert.Same(child, hit);

        child.RenderTransformOrigin = RelativePoint.TopLeft;
        child.RenderTransform = new Avalonia.Media.ScaleTransform { ScaleX = 2, ScaleY = 2 };
        child.IsEnabled = false;
        child.IsHitTestVisible = false;
        Assert.Same(child, ControlFactory.HitTest(root, new Point(100, 100)));

        Border overlay = new() { Width = 50, Height = 40 };
        Canvas.SetLeft(overlay, 20);
        Canvas.SetTop(overlay, 30);
        root.Children.Add(overlay);
        root.Measure(new Size(200, 200));
        root.Arrange(new Rect(0, 0, 200, 200));
        Assert.Same(overlay, ControlFactory.HitTest(root, new Point(25, 35)));
        child.ZIndex = 1;
        Assert.Same(child, ControlFactory.HitTest(root, new Point(25, 35)));

        Canvas.SetLeft(child, 150);
        root.Arrange(new Rect(0, 0, 200, 200));
        Assert.Same(child, ControlFactory.HitTest(root, new Point(230, 50)));
        root.ClipToBounds = true;
        Assert.Null(ControlFactory.HitTest(root, new Point(230, 50)));
        root.ClipToBounds = false;
        child.IsVisible = false;
        Assert.Null(ControlFactory.HitTest(root, new Point(230, 50)));
    }

    [AvaloniaFact]
    public void RuntimeXamlLoader_Parses_UserControl()
    {
        string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui">
                <Border Width="120" Height="80" />
            </UserControl>
            """;

        UserControl control = AvaloniaRuntimeXamlLoader.Parse<UserControl>(xaml, typeof(MainWindow).Assembly);

        Assert.NotNull(control);
        Assert.NotNull(control.Content);
    }
}
