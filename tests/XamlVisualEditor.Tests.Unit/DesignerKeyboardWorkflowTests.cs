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
                pointerRoute = $"source={args.Source?.GetType().Name}, capture={args.Pointer.Captured?.GetType().Name}";
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
