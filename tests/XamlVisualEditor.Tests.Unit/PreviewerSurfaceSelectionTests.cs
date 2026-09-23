using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Viewport;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Designer.Core;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Workspace;
using Xunit;
using RemoteInput = Avalonia.Remote.Protocol.Input;

namespace XamlVisualEditor.Tests.Unit;

public sealed class PreviewerSurfaceSelectionTests
{
    [AvaloniaFact]
    public void SurfaceWithoutFrameReservesVisibleStatusArea()
    {
        PreviewerSurfaceControl surface = new()
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
        };
        Window window = new() { Content = new ScrollViewer { Content = surface }, Width = 600, Height = 200 };
        try
        {
            window.Show();
            window.UpdateLayout();
            Assert.True(surface.Bounds.Width >= 480);
            Assert.True(surface.Bounds.Height >= 40);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ExternalSurfaceKeyboardDuplicatesSelectsAndDeletesWithoutLocalControls()
    {
        const string original = "<Canvas xmlns=\"https://github.com/avaloniaui\" Width=\"200\" Height=\"100\"><Button Name=\"Original\" Width=\"40\" Height=\"20\" /></Canvas>";
        string path = Path.Combine(Path.GetTempPath(), "xve-external-keys-" + Guid.NewGuid().ToString("N") + ".axaml");
        await File.WriteAllTextAsync(path, original);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        using MainWindowViewModel main = new(metadataService: metadata);
        await main.OpenFileAsync(path);
        DesignerDocumentViewModel document = Assert.IsType<DesignerDocumentViewModel>(main.ActiveDesignerDocument);
        document.UseExternalPreviewer = true;
        using PreviewerTcpSession session = new(path, log: null);
        PreviewerSurfaceControl surface = new() { Document = document, Session = session };
        Window window = new() { Content = surface, Width = 300, Height = 200 };
        async Task PublishTree()
        {
            MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
            Guid revision = Guid.NewGuid();
            await session.SendUpdateXamlAsync(document.SyncEngine.CurrentText!, "Library.dll", "/Keys.axaml", 200, 100, revision);
            List<DesignerHostNode> nodes = new()
            {
                new(root.Id, 1, null, "Avalonia.Controls.Canvas", null, 0, 0, 200, 100, true,
                    Layout: new() { Kind = "Canvas", Width = 200, Height = 100 })
            };
            int index = 0;
            foreach (MutableAstObjectNode child in root.Children.OfType<MutableAstObjectNode>())
                nodes.Add(new(child.Id, ++index + 1, 1, "Avalonia.Controls.Button", child.GetPropertyValue("Name"), index * 50, 10, 40, 20, true));
            DesignerHostSnapshot tree = new(revision, 200, 100, nodes);
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree)));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        try
        {
            window.Show();
            await PublishTree();
            MutableAstObjectNode button = Assert.Single(document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>());
            document.SetSelectedNode(button.Id, SyncSource.TreeView);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.True(surface.Focus());
            window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, null);
            MutableAstObjectNode[] children = document.SyncEngine.CurrentDocument.Root.Children.OfType<MutableAstObjectNode>().ToArray();
            Assert.Equal(2, children.Length);
            MutableAstObjectNode duplicate = Assert.Single(children, child => child.Id != button.Id);
            Assert.NotEqual("Original", duplicate.GetPropertyValue("Name"));
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);

            await PublishTree();
            document.SetSelectedNode(duplicate.Id, SyncSource.TreeView);
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, null);
            Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
            document.SetSelectedNode(duplicate.Id, SyncSource.TreeView);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Single(document.SyncEngine.CurrentDocument.Root.Children.OfType<MutableAstObjectNode>());
            Assert.Empty(document.DesignSurface.ControlMap);
            Assert.Equal(2, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            document.SyncEngine.Undo();
            Assert.Equal(original, document.SyncEngine.CurrentText);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SynchronizedSelectionPublishesCompleteStateWithoutEchoingDocumentChanges()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button /><TextBox /></Canvas>";
        using DesignerDocumentViewModel document = new("SelectionState.axaml") { UseExternalPreviewer = true };
        await document.SyncEngine.LoadAsync(xaml);
        DesignItem[] items = document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>()
            .Select(node => new DesignItem(node)).ToArray();
        Guid? selectedNode = document.SelectedNodeId;
        List<int> publishedCounts = new();
        List<bool> synchronized = new();
        document.DesignSurface.Selection.SelectionChanged += selection =>
        {
            publishedCounts.Add(selection.Count);
            synchronized.Add(document.DesignSurface.IsSelectionSyncing);
        };

        document.DesignSurface.SelectDesignItemsFromSync(new[] { items[0], items[1], items[0] });
        Assert.Equal(items, document.DesignSurface.Selection.SelectedItems);
        Assert.Same(items[0], document.DesignSurface.Selection.PrimarySelection);
        Assert.All(items, item => Assert.True(item.IsSelected));

        document.DesignSurface.SelectDesignItemsFromSync(document.DesignSurface.Selection.SelectedItems);
        Assert.Equal(items, document.DesignSurface.Selection.SelectedItems);
        document.DesignSurface.SelectDesignItemsFromSync(Array.Empty<DesignItem>());

        Assert.Equal(new[] { 2, 2, 0 }, publishedCounts);
        Assert.All(synchronized, value => Assert.True(value));
        Assert.All(items, item => Assert.False(item.IsSelected));
        Assert.Null(document.DesignSurface.Selection.PrimarySelection);
        Assert.Equal(selectedNode, document.SelectedNodeId);
        Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
        Assert.Equal(0, document.SyncEngine.UndoRedo.UndoCount);
    }

    [Fact]
    public void SharedSpacingGuidesUseNearestOverlappingNeighbors()
    {
        var guides = XamlVisualEditor.Designer.Adorners.SpacingGuideAdorner.Calculate(new Rect(100, 100, 80, 40), new[]
        {
            new Rect(0, 110, 20, 20), new Rect(60, 110, 20, 20),
            new Rect(220, 100, 40, 40), new Rect(110, 50, 50, 20),
            new Rect(100, 180, 80, 20), new Rect(0, 0, 20, 20), new Rect(300, 100, 0, 40)
        });
        Assert.Equal(4, guides.Count);
        Assert.Contains(guides, guide => guide.Start == new Point(80, 120) && guide.End == new Point(100, 120) && guide.Label == "20");
        Assert.Contains(guides, guide => guide.Start == new Point(180, 120) && guide.End == new Point(220, 120) && guide.Label == "40");
        Assert.Contains(guides, guide => guide.Start == new Point(135, 70) && guide.End == new Point(135, 100) && guide.Label == "30");
        Assert.Contains(guides, guide => guide.Start == new Point(140, 140) && guide.End == new Point(140, 180) && guide.Label == "40");
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task ExternalViewportRulersAndGuideSnappingUseDocumentCoordinates()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\" Width=\"600\" Height=\"400\"><Button Canvas.Left=\"100\" Canvas.Top=\"100\" Width=\"80\" Height=\"40\" /></Canvas>";
        using DesignerDocumentViewModel document = new("ExternalViewport.axaml")
        {
            UseExternalPreviewer = true, ViewMode = DocumentViewMode.Design
        };
        document.DesignSurface.Zoom = 0.75;
        document.DesignSurface.CanvasGap = 8;
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        document.DesignSurface.ShowAlignmentGuides = false;
        document.DesignSurface.SnapToGuides = true;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode button = root.Children.OfType<MutableAstObjectNode>().Single();
        Guid revision = Guid.NewGuid();
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        await session.SendUpdateXamlAsync(xaml, "Library.dll", "/ExternalViewport.axaml", 600, 400, revision);
        document.PreviewerSession = session;
        DesignerHostSnapshot tree = new(revision, 600, 400, new[]
        {
            new DesignerHostNode(root.Id, 1, null, "Avalonia.Controls.Canvas", null, 0, 0, 600, 400, true,
                Layout: new() { Kind = "Canvas", Width = 600, Height = 400 }),
            new DesignerHostNode(button.Id, 2, 1, "Avalonia.Controls.Button", null, 100, 100, 80, 40, true,
                Layout: new() { X = 100, Y = 100, Width = 80, Height = 40, CanvasLeft = 100, CanvasTop = 100, M31 = 100, M32 = 100 })
        });
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window? window = null;
        try
        {
            DesignerDocumentView view = new() { DataContext = document };
            window = new Window { Content = view, Width = 950, Height = 650 };
            window.Show();
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree)));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            PreviewerSurfaceControl surface = view.FindControl<PreviewerSurfaceControl>("ExternalPreviewSurface")!;
            ScrollViewer scroll = view.FindControl<ScrollViewer>("PreviewerViewport")!;
            var horizontal = view.FindControl<XamlVisualEditor.Designer.Adorners.RulerControl>("ExternalHorizontalRuler")!;
            var vertical = view.FindControl<XamlVisualEditor.Designer.Adorners.RulerControl>("ExternalVerticalRuler")!;
            document.DesignSurface.SelectByAstNodeIdFromSync(button.Id);
            DesignItem selected = Assert.IsType<DesignItem>(document.DesignSurface.Selection.PrimarySelection);
            Assert.Null(selected.VisualElement);
            Assert.Empty(document.DesignSurface.ControlMap);

            void CheckRulers()
            {
                Point origin = surface.TranslatePoint(default, window)!.Value;
                Assert.Equal(origin.X, horizontal.TranslatePoint(default, window)!.Value.X - horizontal.Offset, 6);
                Assert.Equal(origin.Y, vertical.TranslatePoint(default, window)!.Value.Y - vertical.Offset, 6);
                Assert.Equal(document.DesignSurface.Zoom, horizontal.Zoom);
                Assert.Equal(100, horizontal.SelectionStart);
                Assert.Equal(180, horizontal.SelectionEnd);
                Assert.Equal(100, vertical.SelectionStart);
                Assert.Equal(140, vertical.SelectionEnd);
            }
            CheckRulers();
            Point xGuide = horizontal.TranslatePoint(new Point(250 * horizontal.Zoom - horizontal.Offset, 12), window)!.Value;
            window.MouseDown(xGuide, MouseButton.Left);
            window.MouseUp(xGuide, MouseButton.Left);
            Point yGuide = vertical.TranslatePoint(new Point(12, 200 * vertical.Zoom - vertical.Offset), window)!.Value;
            window.MouseDown(yGuide, MouseButton.Left);
            window.MouseUp(yGuide, MouseButton.Left);
            Assert.Equal(250, Assert.Single(document.DesignSurface.VerticalGuides), 6);
            Assert.Equal(200, Assert.Single(document.DesignSurface.HorizontalGuides), 6);

            PreviewerGeometryEditor editor = new(document, tree);
            foreach (double zoom in new[] { 0.25, 0.75, 1, 2, 3 })
            {
                document.DesignSurface.Zoom = zoom;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                CheckRulers();
                Vector delta = new(70 - 2 / zoom, 60 - 2 / zoom);
                Point pointer = new(140 + delta.X, 120 + delta.Y);
                Vector snapped = editor.PreviewMove(new[] { selected }, 2, delta, pointer).Delta;
                Assert.Equal(70, snapped.X, 8);
                Assert.Equal(60, snapped.Y, 8);
                Rect resized = editor.PreviewResize(selected, delta, PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom);
                Assert.Equal(100, resized.X, 8);
                Assert.Equal(100, resized.Y, 8);
                Assert.Equal(150, resized.Width, 8);
                Assert.Equal(100, resized.Height, 8);
                document.DesignSurface.SnapToGuides = false;
                Vector unsnapped = editor.PreviewMove(new[] { selected }, 2, delta, pointer).Delta;
                Assert.Equal(delta.X, unsnapped.X, 8);
                Assert.Equal(delta.Y, unsnapped.Y, 8);
                Rect freeResize = editor.PreviewResize(selected, delta, PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom);
                Assert.Equal(80 + delta.X, freeResize.Width, 8);
                Assert.Equal(40 + delta.Y, freeResize.Height, 8);
                document.DesignSurface.SnapToGuides = true;
            }
            document.DesignSurface.Zoom = 1;
            editor.MovePointer(new[] { selected }, 2, new Vector(68, 58), new Point(208, 178));
            Assert.Equal("170", button.GetPropertyValue("Canvas.Left"));
            Assert.Equal("160", button.GetPropertyValue("Canvas.Top"));
            Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());

            editor.ResizePointer(selected, new Vector(68, 58), PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom);
            MutableAstObjectNode resizedButton = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
            Assert.Equal("150", resizedButton.GetPropertyValue("Width"));
            Assert.Equal("100", resizedButton.GetPropertyValue("Height"));
            string resizedXaml = document.SyncEngine.GetCurrentText();
            document.SyncEngine.Undo();
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());
            document.SyncEngine.Redo();
            Assert.Equal(resizedXaml, document.SyncEngine.GetCurrentText());
            document.SyncEngine.Undo();

            document.DesignSurface.Zoom = 2;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            scroll.Offset = new Vector(40, 70);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            CheckRulers();
            document.DesignSurface.ShowRulers = false;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.False(horizontal.IsVisible);
            Assert.False(vertical.IsVisible);
            document.DesignSurface.ShowRulers = true;
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            CheckRulers();
        }
        finally
        {
            window?.Close();
            Application.Current!.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async Task DocumentChangesRejectPreviousGeometryBeforeTheNextPreviewRequest()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Name=\"Moved\" Canvas.Left=\"16\" Width=\"120\" /></Canvas>";
        using DesignerDocumentViewModel document = new("PendingGeometry.axaml") { UseExternalPreviewer = true };
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        document.PreviewerSession = session;
        Guid initial = Guid.NewGuid();
        await session.SendUpdateXamlAsync(xaml, "Library.dll", "/PendingGeometry.axaml", 200, 100, initial);
        DesignerHostSnapshot snapshot = new(initial, 200, 100,
            new[] { new DesignerHostNode(root.Id, 1, null, "Avalonia.Controls.Canvas", null, 0, 0, 200, 100, true) });
        string Message(DesignerHostSnapshot value) => PreviewerTcpSession.DesignTreeMessagePrefix
            + System.Text.Json.JsonSerializer.Serialize(value);
        session.TryReceiveDesignTree(Message(snapshot));
        Assert.NotNull(session.LastDesignTree);

        MutableAstObjectNode button = Assert.IsType<MutableAstObjectNode>(root.Children[0]);
        button.SetPropertyValue("Canvas.Left", "80");
        document.SyncEngine.NotifyAstChanged(document.SyncEngine.CurrentDocument, SyncSource.DesignSurface);

        Assert.Null(session.LastDesignTree);
        session.TryReceiveDesignTree(Message(snapshot));
        Assert.Null(session.LastDesignTree);
        Guid current = Guid.NewGuid();
        await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll",
            "/PendingGeometry.axaml", 200, 100, current);
        session.TryReceiveDesignTree(Message(snapshot with { RenderId = current }));
        Assert.Equal(current, session.LastDesignTree?.RenderId);
    }

    [AvaloniaFact(Timeout = 30000)]
    public async Task PreviewSelectionUsesInstanceGeometryClippingAndTheCurrentDocumentRevision()
    {
        const string xaml = "<Canvas xmlns=\"https://github.com/avaloniaui\"><Button Name=\"Repeated\" Content=\"Initial\" /><TextBox Name=\"Other\" /></Canvas>";
        using DesignerDocumentViewModel document = new("PreviewSelection.axaml") { UseExternalPreviewer = true };
        int publishedSelectionCount = -1;
        document.DesignSurface.Selection.SelectionChanged += items => publishedSelectionCount = items.Count;
        document.DesignSurface.SnapToGrid = false;
        document.DesignSurface.SnapLinesEnabled = false;
        document.DesignSurface.Zoom = 2;
        document.DesignSurface.CanvasGap = 0;
        await document.SyncEngine.LoadAsync(xaml);
        MutableAstObjectNode root = document.SyncEngine.CurrentDocument!.Root!;
        MutableAstObjectNode button = root.Children.OfType<MutableAstObjectNode>().Single(node => node.TypeName == "Button");
        MutableAstObjectNode other = root.Children.OfType<MutableAstObjectNode>().Single(node => node.TypeName == "TextBox");
        Guid revision = Guid.NewGuid();
        using PreviewerTcpSession session = new(document.FilePath, log: null);
        await session.SendUpdateXamlAsync(xaml, "Library.dll", "/PreviewSelection.axaml", 200, 100, revision);
        PreviewerSurfaceControl surface = new() { Document = document, Session = session, Width = 400, Height = 200 };
        Window window = new() { Content = surface, Width = 400, Height = 200 };
        window.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            window.Show();
            window.UpdateLayout();
            using IAvaloniaRemoteTransportConnection connection = await new BsonTcpTransport().Connect(IPAddress.Loopback, session.Port);
            var input = Channel.CreateUnbounded<RemoteInput.InputEventMessageBase>();
            connection.OnMessage += (_, message) =>
            {
                if (message is RemoteInput.InputEventMessageBase inputMessage) input.Writer.TryWrite(inputMessage);
            };
            connection.Start();
            long sequence = 0;
            async Task SendHostViewport(int width, int height, double dpi)
            {
                TaskCompletionSource<bool> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnFrame(FrameMessage frame) { if (frame.SequenceId == sequence) received.TrySetResult(true); }
                byte[] pixels = new byte[width * height * 4];
                for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
                session.FrameReceived += OnFrame;
                try
                {
                    await connection.Send(new RequestViewportResizeMessage { Width = width, Height = height });
                    await connection.Send(new FrameMessage
                    {
                        SequenceId = ++sequence, Width = width, Height = height, Stride = width * 4,
                        Data = pixels, Format = PixelFormat.Bgra8888, DpiX = dpi, DpiY = dpi
                    });
                    await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                finally { session.FrameReceived -= OnFrame; }
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }
            await SendHostViewport(200, 100, 96);
            Assert.Equal(400, surface.Width);
            Assert.Equal(200, surface.Height);
            DesignerHostSnapshot tree = new(revision, 200, 100, new[]
            {
                new DesignerHostNode(root.Id, 1, null, "Avalonia.Controls.Canvas", null, 0, 0, 200, 100, true,
                    Layout: new() { Kind = "Canvas", Width = 200, Height = 100 }),
                new DesignerHostNode(button.Id, 2, 1, "Avalonia.Controls.Button", "Repeated", 10, 10, 60, 40, true,
                    Layout: new() { Width = 60, Height = 40, CanvasLeft = 10, CanvasTop = 10, M31 = 10, M32 = 10 }),
                new DesignerHostNode(button.Id, 3, 1, "Avalonia.Controls.Button", "Repeated", 110, 10, 60, 40, true,
                    new DesignerHostRect(140, 10, 30, 40)),
                new DesignerHostNode(other.Id, 4, 1, "Avalonia.Controls.TextBox", "Other", 10, 65, 60, 20, true)
            });
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree)));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(30);
            Dispatcher.UIThread.RunJobs();

            void Click(double x, double y)
            {
                Point point = surface.TranslatePoint(new Point(x, y), window)!.Value;
                window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
            }
            Click(50, 50);
            Assert.Equal(button.Id, document.SelectedNodeId);
            DesignItem selected = Assert.IsType<DesignItem>(Assert.Single(document.DesignSurface.Selection.SelectedItems));
            Assert.Equal(2, selected.HostInstanceId);
            Assert.Null(selected.VisualElement);
            Assert.Empty(document.DesignSurface.ControlMap);
            Click(250, 50);
            Assert.Equal(root.Id, document.SelectedNodeId);
            Click(300, 50);
            Assert.Equal(button.Id, document.SelectedNodeId);
            Assert.Equal(3, Assert.IsType<DesignItem>(document.DesignSurface.Selection.PrimarySelection).HostInstanceId);
            Assert.Equal(xaml, document.SyncEngine.GetCurrentText());

            Guid nextRevision = Guid.NewGuid();
            await session.SendUpdateXamlAsync(xaml, "Library.dll", "/PreviewSelection.axaml", 200, 100, nextRevision);
            Dispatcher.UIThread.RunJobs();
            Click(50, 50);
            Assert.Equal(3, Assert.IsType<DesignItem>(document.DesignSurface.Selection.PrimarySelection).HostInstanceId);
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree)));
            Assert.Null(session.LastDesignTree);

            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree with { RenderId = nextRevision })));
            Dispatcher.UIThread.RunJobs();
            Click(50, 50);
            void Right()
            {
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            }
            Right();
            Assert.Equal("11", button.GetPropertyValue("Canvas.Left"));
            await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll", "/PreviewSelection.axaml", 200, 100, Guid.NewGuid());
            Dispatcher.UIThread.RunJobs();
            Right();
            Assert.Equal("12", button.GetPropertyValue("Canvas.Left"));

            Guid dragRevision = Guid.NewGuid();
            await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll", "/PreviewSelection.axaml", 200, 100, dragRevision);
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(tree with { RenderId = dragRevision })));
            Dispatcher.UIThread.RunJobs();
            string beforeDrag = document.SyncEngine.GetCurrentText();
            Point start = surface.TranslatePoint(new Point(50, 50), window)!.Value;
            Point end = surface.TranslatePoint(new Point(90, 70), window)!.Value;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Assert.Equal(beforeDrag, document.SyncEngine.GetCurrentText());
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(beforeDrag, document.SyncEngine.GetCurrentText());
            int undoCount = document.SyncEngine.UndoRedo.UndoCount;
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal("32", button.GetPropertyValue("Canvas.Left"));
            Assert.Equal("20", button.GetPropertyValue("Canvas.Top"));
            Assert.Equal(undoCount + 1, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            Assert.Equal(beforeDrag, document.SyncEngine.GetCurrentText());
            button = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
            await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll", "/PreviewSelection.axaml", 200, 100, Guid.NewGuid());
            Dispatcher.UIThread.RunJobs();
            using (var edit = document.SyncEngine.BeginEditTransaction("Property edit"))
            {
                button.SetPropertyValue("Tag", "changed elsewhere");
                edit.Commit();
            }
            Right();
            Assert.Equal("12", button.GetPropertyValue("Canvas.Left"));

            Guid resizeRevision = Guid.NewGuid();
            await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll", "/PreviewSelection.axaml", 200, 100, resizeRevision);
            DesignerHostSnapshot resizeTree = tree with
            {
                RenderId = resizeRevision,
                Nodes = tree.Nodes.Select(node => node.InstanceId == 2
                    ? node with { X = 12, Layout = node.Layout! with { CanvasLeft = 12, M31 = 12 } } : node).ToArray()
            };
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(resizeTree)));
            Dispatcher.UIThread.RunJobs();
            Click(50, 50);
            string beforeResize = document.SyncEngine.GetCurrentText();
            Point handle = surface.TranslatePoint(new Point(144, 100), window)!.Value;
            Point resized = surface.TranslatePoint(new Point(184, 120), window)!.Value;
            window.MouseDown(handle, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(resized, RawInputModifiers.LeftMouseButton);
            Assert.Equal(beforeResize, document.SyncEngine.GetCurrentText());
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(resized, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(beforeResize, document.SyncEngine.GetCurrentText());
            undoCount = document.SyncEngine.UndoRedo.UndoCount;
            window.MouseDown(handle, MouseButton.Left, RawInputModifiers.None);
            window.MouseMove(resized, RawInputModifiers.LeftMouseButton);
            window.MouseUp(resized, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal("80", button.GetPropertyValue("Width"));
            Assert.Equal("50", button.GetPropertyValue("Height"));
            Assert.Equal("12", button.GetPropertyValue("Canvas.Left"));
            Assert.Equal(undoCount + 1, document.SyncEngine.UndoRedo.UndoCount);
            document.SyncEngine.Undo();
            Assert.Equal(beforeResize, document.SyncEngine.GetCurrentText());
            document.SyncEngine.Redo();
            MutableAstObjectNode resizedNode = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(button.Id));
            Assert.Equal("80", resizedNode.GetPropertyValue("Width"));
            Assert.Equal("50", resizedNode.GetPropertyValue("Height"));

            Guid marqueeRevision = Guid.NewGuid();
            await session.SendUpdateXamlAsync(document.SyncEngine.GetCurrentText(), "Library.dll", "/PreviewSelection.axaml", 200, 100, marqueeRevision);
            Assert.True(session.TryReceiveDesignTree(PreviewerTcpSession.DesignTreeMessagePrefix + JsonSerializer.Serialize(resizeTree with { RenderId = marqueeRevision })));
            Dispatcher.UIThread.RunJobs();
            Click(50, 150);
            Assert.Equal(other.Id, document.SelectedNodeId);
            string beforeMarquee = document.SyncEngine.GetCurrentText();
            Point marqueeStart = surface.TranslatePoint(new Point(380, 120), window)!.Value;
            Point marqueeEnd = surface.TranslatePoint(new Point(10, 10), window)!.Value;
            window.MouseDown(marqueeStart, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(marqueeEnd, RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(marqueeEnd, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(other.Id, Assert.Single(document.DesignSurface.Selection.SelectedItems).AstNodeId);
            window.MouseDown(marqueeStart, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(marqueeEnd, RawInputModifiers.LeftMouseButton | RawInputModifiers.Shift);
            window.MouseUp(marqueeEnd, MouseButton.Left, RawInputModifiers.Shift);
            Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
            Assert.Contains(document.DesignSurface.Selection.SelectedItems, item => item.AstNodeId == other.Id);
            Assert.Contains(document.DesignSurface.Selection.SelectedItems, item => item.AstNodeId == button.Id);
            Assert.Equal(2, publishedSelectionCount);
            Assert.Equal(beforeMarquee, document.SyncEngine.GetCurrentText());
            document.DesignSurface.CanvasGap = 8;
            Assert.Equal(new Thickness(8), surface.Margin);
            Assert.NotNull(surface.FitToViewport(new Size(216, 116), false));
            window.UpdateLayout();
            Assert.Equal(1, document.DesignSurface.Zoom);
            Assert.Equal(200, surface.Width);
            Assert.Equal(100, surface.Height);
            document.DesignSurface.SelectByAstNodeIdFromSync(button.Id);
            Assert.NotNull(surface.FitToViewport(new Size(136, 96), true));
            Assert.True(document.DesignSurface.Zoom > 1);
            Assert.Equal(beforeMarquee, document.SyncEngine.GetCurrentText());

            document.DesignSurface.Zoom = 0.5;
            await SendHostViewport(100, 50, 48);
            Assert.Equal(100, surface.Width);
            Assert.Equal(50, surface.Height);
            document.DesignSurface.Zoom = 1.25;
            await SendHostViewport(251, 126, 120);
            Assert.Equal(250, surface.Width);
            Assert.Equal(125, surface.Height);
            await SendHostViewport(400, 200, 192);
            Assert.Equal(250, surface.Width);
            Assert.Equal(125, surface.Height);

            await session.SendUpdateXamlAsync(beforeMarquee, "Library.dll", "/PreviewSelection.axaml", 200, 100, Guid.NewGuid());
            Dispatcher.UIThread.RunJobs();
            Assert.Null(session.LastDesignTree);
            await SendHostViewport(320, 160, 192);
            Assert.Equal(200, surface.Width);
            Assert.Equal(100, surface.Height);
            Assert.Equal(beforeMarquee, document.SyncEngine.GetCurrentText());

            Assert.False(input.Reader.TryRead(out _));
            async Task<T> NextInput<T>() where T : RemoteInput.InputEventMessageBase
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
                while (true)
                {
                    RemoteInput.InputEventMessageBase message = await input.Reader.ReadAsync(timeout.Token);
                    if (message is T typed) return typed;
                }
            }
            undoCount = document.SyncEngine.UndoRedo.UndoCount;
            Guid? selectedBeforePreview = document.SelectedNodeId;
            document.DesignSurface.IsEditMode = false;
            Point livePoint = surface.TranslatePoint(new Point(50, 25), window)!.Value;
            window.MouseDown(livePoint, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseUp(livePoint, MouseButton.Left, RawInputModifiers.Shift);
            RemoteInput.PointerPressedEventMessage press = await NextInput<RemoteInput.PointerPressedEventMessage>();
            Assert.Equal(40, press.X);
            Assert.Equal(20, press.Y);
            Assert.Equal(RemoteInput.MouseButton.Left, press.Button);
            Assert.Contains(RemoteInput.InputModifiers.Shift, press.Modifiers!);
            Assert.Equal(RemoteInput.MouseButton.Left, (await NextInput<RemoteInput.PointerReleasedEventMessage>()).Button);
            window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            window.KeyTextInput("Live text");
            window.KeyRelease(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
            RemoteInput.KeyEventMessage down = await NextInput<RemoteInput.KeyEventMessage>();
            Assert.True(down.IsDown);
            Assert.Equal(RemoteInput.PhysicalKey.A, down.PhysicalKey);
            Assert.Contains(RemoteInput.InputModifiers.Control, down.Modifiers!);
            Assert.Equal("Live text", (await NextInput<RemoteInput.TextInputEventMessage>()).Text);
            Assert.False((await NextInput<RemoteInput.KeyEventMessage>()).IsDown);
            window.MouseWheel(livePoint, new Vector(0, -2), RawInputModifiers.None);
            RemoteInput.ScrollEventMessage scroll = await NextInput<RemoteInput.ScrollEventMessage>();
            Assert.Equal(-2, scroll.DeltaY);
            Assert.Equal(40, scroll.X);

            window.MouseDown(livePoint, MouseButton.Left, RawInputModifiers.None);
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await NextInput<RemoteInput.PointerPressedEventMessage>();
            Assert.True((await NextInput<RemoteInput.KeyEventMessage>()).IsDown);
            document.DesignSurface.IsEditMode = true;
            RemoteInput.PointerReleasedEventMessage cancelled = await NextInput<RemoteInput.PointerReleasedEventMessage>();
            Assert.Equal(-1, cancelled.X);
            Assert.Equal(-1, cancelled.Y);
            Assert.False((await NextInput<RemoteInput.KeyEventMessage>()).IsDown);
            window.MouseUp(livePoint, MouseButton.Left, RawInputModifiers.None);
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal(selectedBeforePreview, document.SelectedNodeId);
            Assert.Equal(undoCount, document.SyncEngine.UndoRedo.UndoCount);
            Assert.Equal(beforeMarquee, document.SyncEngine.GetCurrentText());
        }
        finally { window.Close(); }
    }
}
