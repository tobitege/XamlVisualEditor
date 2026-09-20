using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Workspace;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DesignerKeyboardWorkflowTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task Keyboard_selection_cancel_and_preview_input_stay_in_their_own_context()
    {
        const string xaml = """
            <UserControl xmlns="https://github.com/avaloniaui"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="400" Height="200">
              <Canvas>
                <Button x:Name="First" Canvas.Left="20" Canvas.Top="20" Width="50" Height="30" />
                <Button x:Name="Second" Canvas.Left="100" Canvas.Top="20" Width="50" Height="30" />
                <TextBox x:Name="Input" Text="A" Canvas.Left="20" Canvas.Top="70" Width="150" Height="30" />
              </Canvas>
            </UserControl>
            """;
        string path = Path.Combine(Path.GetTempPath(), "xve-keyboard-" + Guid.NewGuid().ToString("N") + ".axaml");
        Assert.False(File.Exists(path));
        await File.WriteAllTextAsync(path, xaml);
        TypeMetadataService metadata = new();
        metadata.LoadAssembly(typeof(Control).Assembly.Location);
        metadata.LoadAssembly(typeof(Brush).Assembly.Location);
        metadata.LoadAssembly(typeof(XamlVisualEditor.TestControls.CaptionControl).Assembly.Location);
        using DesignerDocumentViewModel document = new(path, metadata);
        await document.LoadAsync();
        XamlSerializationService serializer = new();
        string original = serializer.Serialize(document.SyncEngine.CurrentDocument!);
        DesignSurfaceView view = new() { DataContext = document.DesignSurface };
        TextBox outside = new() { Text = "Outside", Width = 150, Height = 30 };
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("*,180") };
        content.Children.Add(new Border { DataContext = document, Child = view });
        Grid.SetColumn(outside, 1);
        content.Children.Add(outside);
        Window host = new() { Content = content, Width = 1000, Height = 650 };
        host.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        try
        {
            host.Show();
            await FinishLayoutAsync(host);
            var first = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "First");
            var second = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Second");
            var canvas = document.DesignSurface.ControlMap.Single(pair => pair.Value.AstNode.TypeName == "Canvas");
            document.DesignSurface.SelectByAstNodeId(first.Value.AstNodeId);
            Assert.True(view.Focus());
            Press(host, Key.Tab, PhysicalKey.Tab);
            Assert.Equal(second.Value.AstNodeId, document.SelectedNodeId);
            Press(host, Key.Tab, PhysicalKey.Tab, RawInputModifiers.Shift);
            Assert.Equal(first.Value.AstNodeId, document.SelectedNodeId);
            Press(host, Key.Escape, PhysicalKey.Escape);
            Assert.Equal(canvas.Value.AstNodeId, document.SelectedNodeId);

            document.DesignSurface.SelectByAstNodeId(first.Value.AstNodeId);
            Point start = canvas.Key.TranslatePoint(new Point(390, 190), host)!.Value;
            Point end = canvas.Key.TranslatePoint(new Point(5, 5), host)!.Value;
            string pointerRoute = "not received by the designer";
            var selectionHistory = new System.Collections.Generic.List<string>();
            document.DesignSurface.Selection.SelectionChanged += items => selectionHistory.Add(string.Join(",", items.Select(item =>
                item.AstNodeId == first.Value.AstNodeId ? "First" : item.AstNodeId == second.Value.AstNodeId ? "Second"
                    : item.AstNodeId == canvas.Value.AstNodeId ? "Canvas" : item.AstNodeId.ToString())));
            view.AddHandler(InputElement.PointerPressedEvent, (_, args) =>
            {
                pointerRoute = $"source={args.Source?.GetType().Name}, capture={args.Pointer.Captured?.GetType().Name}, clicks={args.ClickCount}";
            }, RoutingStrategies.Bubble, handledEventsToo: true);
            host.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
            host.MouseMove(end, RawInputModifiers.LeftMouseButton);
            host.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.True(document.DesignSurface.Selection.SelectedItems.Count == 3,
                $"Marquee {start} to {end}: {pointerRoute}; selection history: {string.Join(" / ", selectionHistory)}");
            document.DesignSurface.SelectByAstNodeId(first.Value.AstNodeId);
            Point cancelStart = canvas.Key.TranslatePoint(new Point(370, 170), host)!.Value;
            host.MouseDown(cancelStart, MouseButton.Left, RawInputModifiers.None);
            host.MouseMove(end, RawInputModifiers.LeftMouseButton);
            Press(host, Key.Escape, PhysicalKey.Escape);
            host.MouseUp(end, MouseButton.Left, RawInputModifiers.None);
            Assert.Equal(first.Value.AstNodeId, Assert.Single(document.DesignSurface.Selection.SelectedItems).AstNodeId);

            document.DesignSurface.IsEditMode = false;
            await FinishLayoutAsync(host);
            TextBox input = Assert.IsType<TextBox>(document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Input").Key);
            Point inputPoint = input.TranslatePoint(new Point(10, 10), host)!.Value;
            host.MouseDown(inputPoint, MouseButton.Left, RawInputModifiers.None);
            host.MouseUp(inputPoint, MouseButton.Left, RawInputModifiers.None);
            Assert.True(input.IsKeyboardFocusWithin);
            input.CaretIndex = input.Text!.Length;
            bool? intercepted = null;
            host.AddHandler(InputElement.KeyDownEvent, (_, args) =>
            {
                if (args.Key == Key.Space) intercepted = args.Handled;
            }, RoutingStrategies.Tunnel, handledEventsToo: true);
            host.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.False(intercepted);
            host.KeyTextInput(" ");
            host.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            Assert.Equal("A ", input.Text);

            document.DesignSurface.IsEditMode = true;
            document.DesignSurface.SelectByAstNodeId(first.Value.AstNodeId);
            Assert.True(outside.Focus());
            Press(host, Key.Right, PhysicalKey.ArrowRight);
            Assert.Equal(original, serializer.Serialize(document.SyncEngine.CurrentDocument!));

            foreach (int scale in new[] { 1, 2 })
            {
                string nestedXaml = $$"""
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" Width="720" Height="480">
                      <Canvas x:Name="Nested" Margin="24" RenderTransformOrigin="0,0" Background="White">
                        <Canvas.RenderTransform><ScaleTransform ScaleX="{{scale}}" ScaleY="{{scale}}" /></Canvas.RenderTransform>
                        <Button x:Name="Moving" Canvas.Left="32" Canvas.Top="32" Width="120" Height="32" Content="Moving" />
                        <Button x:Name="Partner" Canvas.Left="32" Canvas.Top="80" Width="90" Height="24" Content="Partner" />
                        <Button x:Name="Anchor" Canvas.Left="205" Canvas.Top="128" Width="159" Height="32" Content="Anchor" />
                      </Canvas>
                    </UserControl>
                    """;
                await document.SyncEngine.LoadAsync(nestedXaml);
                document.DesignSurface.SnapToGrid = true;
                document.DesignSurface.SnapLinesEnabled = true;
                document.DesignSurface.SnapToGuides = false;
                document.DesignSurface.RequestRebuild();
                await FinishLayoutAsync(host);
                var moving = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Moving");
                var partner = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Partner");
                var anchor = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Anchor");
                Point dragStart = moving.Key.TranslatePoint(new Point(10, 10), host)!.Value;
                Point partnerPoint = partner.Key.TranslatePoint(new Point(10, 10), host)!.Value;
                host.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseUp(dragStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseDown(partnerPoint, MouseButton.Left, RawInputModifiers.Control);
                host.MouseUp(partnerPoint, MouseButton.Left, RawInputModifiers.Control);
                Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
                Point movingOrigin = moving.Key.TranslatePoint(default, host)!.Value;
                Point anchorOrigin = anchor.Key.TranslatePoint(default, host)!.Value;
                Point dragEnd = dragStart + new Vector(anchorOrigin.X - movingOrigin.X - 4, 0);
                host.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
                Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
                Assert.Contains(document.DesignSurface.Selection.SelectedItems, item => item.AstNodeId == moving.Value.AstNodeId);
                Assert.Contains(document.DesignSurface.Selection.SelectedItems, item => item.AstNodeId == partner.Value.AstNodeId);
                host.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                host.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
                await FinishLayoutAsync(host);
                MutableAstObjectNode moved = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(moving.Value.AstNodeId));
                MutableAstObjectNode movedPartner = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(partner.Value.AstNodeId));
                Assert.Equal("205", moved.GetPropertyValue("Canvas.Left"));
                Assert.Equal("205", movedPartner.GetPropertyValue("Canvas.Left"));
                Assert.Equal("32", moved.GetPropertyValue("Canvas.Top"));
                Assert.Equal("80", movedPartner.GetPropertyValue("Canvas.Top"));
                Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
                Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
                string movedText = document.SyncEngine.GetCurrentText();
                document.SyncEngine.Undo();
                Assert.Equal(nestedXaml, document.SyncEngine.GetCurrentText());
                document.SyncEngine.Redo();
                Assert.Equal(movedText, document.SyncEngine.GetCurrentText());
                document.SyncEngine.Undo();
                await FinishLayoutAsync(host);

                var resizeItem = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == "Moving");
                document.DesignSurface.SelectByAstNodeId(resizeItem.Value.AstNodeId);
                await FinishLayoutAsync(host);
                Point resizeStart = resizeItem.Key.TranslatePoint(default, host)!.Value;
                Point resizeEnd = resizeItem.Key.TranslatePoint(new Point(-16, -8), host)!.Value;
                host.MouseDown(resizeStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseMove(resizeEnd, RawInputModifiers.LeftMouseButton);
                host.MouseUp(resizeEnd, MouseButton.Left, RawInputModifiers.None);
                await FinishLayoutAsync(host);
                MutableAstObjectNode resized = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(resizeItem.Value.AstNodeId));
                Assert.Equal("136", resized.GetPropertyValue("Width"));
                Assert.Equal("40", resized.GetPropertyValue("Height"));
                Assert.Equal("16", resized.GetPropertyValue("Canvas.Left"));
                Assert.Equal("24", resized.GetPropertyValue("Canvas.Top"));
                Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
                document.SyncEngine.Undo();
                Assert.Equal(nestedXaml, document.SyncEngine.GetCurrentText());
                await FinishLayoutAsync(host);
            }

            foreach (string orientation in new[] { "Vertical", "Horizontal" })
            foreach (int scale in new[] { 1, 2 })
            {
                string stackXaml = $$"""
                    <UserControl xmlns="https://github.com/avaloniaui"
                                 xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                                 xmlns:lib="clr-namespace:XamlVisualEditor.TestControls;assembly=XamlVisualEditor.TestControls"
                                 Width="800" Height="440">
                      <StackPanel x:Name="Stack" Orientation="{{orientation}}" Spacing="12" Margin="24"
                                  HorizontalAlignment="Left" VerticalAlignment="Top" Background="White" RenderTransformOrigin="0,0">
                        <StackPanel.RenderTransform><ScaleTransform ScaleX="{{scale}}" ScaleY="{{scale}}" /></StackPanel.RenderTransform>
                        <Button x:Name="MovingStack" Width="80" Height="32" Content="First" />
                        <lib:CaptionControl x:Name="PartnerStack" Width="80" Height="32" Content="Second" />
                        <Button x:Name="AnchorStack" Width="80" Height="32" Content="Third" />
                        <Button x:Name="LastStack" Width="80" Height="32" Content="Last" />
                      </StackPanel>
                    </UserControl>
                    """;
                await document.SyncEngine.LoadAsync(stackXaml);
                document.DesignSurface.RequestRebuild();
                await FinishLayoutAsync(host);
                bool horizontal = orientation == "Horizontal";
                Control Item(string name) => document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == name).Key;
                Point Center(Control control) => control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), host)!.Value;
                Point Edge(Control control, bool after) => control.TranslatePoint(horizontal
                    ? new Point(after ? control.Bounds.Width - 3 : 3, control.Bounds.Height / 2)
                    : new Point(control.Bounds.Width / 2, after ? control.Bounds.Height - 3 : 3), host)!.Value;
                string[] Order() => document.SyncEngine.CurrentDocument!.Root!.Children.OfType<MutableAstObjectNode>().Single()
                    .Children.OfType<MutableAstObjectNode>().Select(node => node.GetPropertyValue("x:Name") ?? string.Empty).ToArray();
                void Click(Control control, RawInputModifiers modifiers)
                {
                    Point point = Center(control);
                    host.MouseDown(point, MouseButton.Left, modifiers);
                    host.MouseUp(point, MouseButton.Left, modifiers);
                }

                // Reverse click order must not reverse the selected siblings when dragging.
                Click(Item("PartnerStack"), RawInputModifiers.None);
                Click(Item("MovingStack"), RawInputModifiers.Control);
                Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
                await FinishLayoutAsync(host);
                Point dragStart = Center(Item("PartnerStack"));
                Point dragEnd = Edge(Item("LastStack"), after: true);
                host.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                Assert.Equal(stackXaml, document.SyncEngine.GetCurrentText());
                host.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
                await FinishLayoutAsync(host);
                Assert.True(new[] { "AnchorStack", "LastStack", "MovingStack", "PartnerStack" }.SequenceEqual(Order()),
                    $"{orientation}/{scale}: order={string.Join(",", Order())}, undo={document.SyncEngine.UndoRedo.UndoCount}; {pointerRoute}");
                Assert.Equal(2, document.DesignSurface.Selection.SelectedItems.Count);
                Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
                string reordered = document.SyncEngine.GetCurrentText();
                document.SyncEngine.Undo();
                Assert.Equal(stackXaml, document.SyncEngine.GetCurrentText());
                document.SyncEngine.Redo();
                Assert.Equal(reordered, document.SyncEngine.GetCurrentText());
                await FinishLayoutAsync(host);

                dragStart = Center(Item("MovingStack"));
                dragEnd = Edge(Item("AnchorStack"), after: false);
                host.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                Press(host, Key.Escape, PhysicalKey.Escape);
                host.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
                await FinishLayoutAsync(host);
                Assert.Equal(reordered, document.SyncEngine.GetCurrentText());
                Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);

                dragStart = Center(Item("PartnerStack"));
                dragEnd = Edge(Item("AnchorStack"), after: false);
                host.MouseDown(dragStart, MouseButton.Left, RawInputModifiers.None);
                host.MouseMove(dragEnd, RawInputModifiers.LeftMouseButton);
                host.MouseUp(dragEnd, MouseButton.Left, RawInputModifiers.None);
                await FinishLayoutAsync(host);
                Assert.Equal(new[] { "MovingStack", "PartnerStack", "AnchorStack", "LastStack" }, Order());
                Assert.Equal(2, document.SyncEngine.UndoRedo.UndoCount);
                document.SyncEngine.Undo();
                Assert.Equal(reordered, document.SyncEngine.GetCurrentText());
                document.SyncEngine.Undo();
                Assert.Equal(stackXaml, document.SyncEngine.GetCurrentText());
                await FinishLayoutAsync(host);

                foreach (string name in new[] { "MovingStack", "PartnerStack" })
                {
                    var resizeItem = document.DesignSurface.ControlMap.Single(pair => pair.Key.Name == name);
                    document.DesignSurface.SelectByAstNodeId(resizeItem.Value.AstNodeId);
                    await FinishLayoutAsync(host);
                    Size size = resizeItem.Key.Bounds.Size;
                    Point resizeStart = resizeItem.Key.TranslatePoint(new Point(size.Width, size.Height), host)!.Value;
                    Point resizeEnd = resizeItem.Key.TranslatePoint(new Point(size.Width + 24, size.Height + 16), host)!.Value;
                    host.MouseDown(resizeStart, MouseButton.Left, RawInputModifiers.None);
                    host.MouseMove(resizeEnd, RawInputModifiers.LeftMouseButton);
                    host.MouseUp(resizeEnd, MouseButton.Left, RawInputModifiers.None);
                    await FinishLayoutAsync(host);
                    MutableAstObjectNode resized = Assert.IsType<MutableAstObjectNode>(document.NodeMap.FindById(resizeItem.Value.AstNodeId));
                    Assert.Equal("104", resized.GetPropertyValue("Width"));
                    Assert.Equal("48", resized.GetPropertyValue("Height"));
                    Assert.Equal(1, document.SyncEngine.UndoRedo.UndoCount);
                    document.SyncEngine.Undo();
                    Assert.Equal(stackXaml, document.SyncEngine.GetCurrentText());
                    await FinishLayoutAsync(host);
                }
            }
        }
        finally { host.Close(); }
    }

    private static void Press(Window window, Key key, PhysicalKey physical, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, physical, string.Empty);
        window.KeyRelease(key, modifiers, physical, string.Empty);
    }

    private static async Task FinishLayoutAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
