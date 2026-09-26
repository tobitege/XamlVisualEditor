using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerCoordinateStripTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "xve-strip-" + Guid.NewGuid().ToString("N"));

    public DesignerCoordinateStripTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task Strip_ReportsPointerHoverSelectionAndMeasurementInRootPixels()
    {
        string path = Path.Combine(_directory, "Form.axaml");
        await File.WriteAllTextAsync(path, """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="300">
              <Canvas>
                <Button x:Name="Action" Canvas.Left="40" Canvas.Top="30" Width="100" Height="40" Content="Run" />
              </Canvas>
            </UserControl>
            """);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        DesignSurfaceViewModel surface = document.DesignSurface;
        DesignSurfaceView view = new() { DataContext = surface };
        Window host = new() { Content = new Border { DataContext = document, Child = view }, Width = 800, Height = 600 };
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            surface.Zoom = 2;
            await FinishLayoutAsync(host);
            Panel canvas = Assert.IsAssignableFrom<Panel>(view.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "DesignCanvas"));
            DesignerCoordinateStrip strip = view.GetVisualDescendants().OfType<DesignerCoordinateStrip>().Single();
            Assert.True(strip.IsEffectivelyVisible);

            host.MouseMove(ToWindow(canvas, host, new Point(50.5, 35.5)));
            await FinishLayoutAsync(host);
            Assert.Equal(50, surface.Readout.PointerX);
            Assert.Equal(35, surface.Readout.PointerY);
            Assert.Equal("Button#Action", surface.Readout.HoverText);

            // The canvas gap left of the root lies inside the viewport.
            surface.Zoom = 1;
            await FinishLayoutAsync(host);
            host.MouseMove(ToWindow(canvas, host, new Point(-3, 10)));
            await FinishLayoutAsync(host);
            Assert.Equal(-3, surface.Readout.PointerX);
            Assert.Null(surface.Readout.HoverText);

            MutableAstObjectNode button = Assert.Single(surface.ItemMap.Values, item => item.AstNode.TypeName == "Button").AstNode;
            surface.SelectByAstNodeId(button.Id);
            await FinishLayoutAsync(host);
            Assert.Equal("40,30,100,40", surface.Readout.SelectionValue);

            // Hovering the resize handles and borders of the selection changes the cursor and the strip.
            foreach (Point grip in new[] { new Point(140, 70), new Point(90, 70), new Point(140, 50), new Point(40, 30), new Point(141, 71) })
            {
                host.MouseMove(ToWindow(canvas, host, grip));
                await FinishLayoutAsync(host);
            }
            Assert.Equal(141, surface.Readout.PointerX);

            surface.IsMeasuring = true;
            host.MouseDown(ToWindow(canvas, host, new Point(100.5, 100.5)), MouseButton.Left);
            host.MouseMove(ToWindow(canvas, host, new Point(130.5, 140.5)), RawInputModifiers.LeftMouseButton);
            host.MouseUp(ToWindow(canvas, host, new Point(130.5, 140.5)), MouseButton.Left);
            await FinishLayoutAsync(host);
            Assert.Equal("30,40", surface.Readout.MeasureValue);
            Assert.Equal("40,30,100,40", surface.Readout.SelectionValue);

            surface.ShowCoordinateStrip = false;
            await FinishLayoutAsync(host);
            Assert.False(strip.IsVisible);
        }
        finally { host.Close(); }
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task ViewportSettings_ApplyToOpenDocumentsAndPersist()
    {
        string first = Path.Combine(_directory, "First.axaml");
        string second = Path.Combine(_directory, "Second.axaml");
        const string xaml = """<UserControl xmlns="https://github.com/avaloniaui" />""";
        await File.WriteAllTextAsync(first, xaml);
        await File.WriteAllTextAsync(second, xaml);
        string optionsPath = Path.Combine(_directory, "options.json");
        EditorOptionsStore store = new(optionsPath);
        store.Save(new EditorOptions { DesignerViewport = new DesignerViewportOptions { ShowGrid = true, GridSize = 12 } });
        using MainWindowViewModel viewModel = new(editorOptions: store);

        await viewModel.OpenFileAsync(first);
        await viewModel.OpenFileAsync(second);
        DesignerDocumentViewModel firstDocument = viewModel.Documents.OfType<DesignerDocumentViewModel>().Single(document => document.FilePath == first);
        DesignerDocumentViewModel secondDocument = viewModel.Documents.OfType<DesignerDocumentViewModel>().Single(document => document.FilePath == second);
        Assert.True(firstDocument.DesignSurface.ShowGrid);
        Assert.Equal(12, secondDocument.DesignSurface.GridSnapSize);

        firstDocument.DesignSurface.GridSnapSize = 5;
        firstDocument.DesignSurface.ShowCoordinateStrip = false;

        Assert.Equal(5, secondDocument.DesignSurface.GridSnapSize);
        Assert.False(secondDocument.DesignSurface.ShowCoordinateStrip);
        DesignerViewportOptions reloaded = new EditorOptionsStore(optionsPath).Current.DesignerViewport;
        Assert.Equal(5, reloaded.GridSize);
        Assert.False(reloaded.ShowCoordinateStrip);
        Assert.True(reloaded.ShowGrid);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static Point ToWindow(Visual canvas, Window host, Point canvasPoint) =>
        canvas.TranslatePoint(canvasPoint, host) ?? throw new InvalidOperationException("The canvas is not part of the window.");

    private static async Task FinishLayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
