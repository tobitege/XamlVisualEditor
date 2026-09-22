using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class WindowResizeGripTests
{
    [AvaloniaTheory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(15)]
    public void DragKeepsTheClickedPointAnchoredAndStopsOnRelease(int offset)
    {
        WindowResizeGrip grip = new();
        Window window = new() { Width = 400, Height = 300, MinWidth = 350, MinHeight = 250, MaxWidth = 450, MaxHeight = 350, Content = grip };
        try
        {
            window.Show();
            Flush(window);
            Assert.Equal(new Size(16, 16), grip.Bounds.Size);
            Point start = grip.TranslatePoint(new Point(offset, offset), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start);
            Assert.Equal(400, window.Width);
            Assert.Equal(300, window.Height);
            window.MouseMove(start + new Vector(20, 15));
            Assert.Equal(420, window.Width);
            Assert.Equal(315, window.Height);
            window.MouseMove(start + new Vector(200, 200));
            Assert.Equal(450, window.Width);
            Assert.Equal(350, window.Height);
            window.MouseMove(start - new Vector(200, 200));
            Assert.Equal(350, window.Width);
            Assert.Equal(250, window.Height);
            window.MouseUp(start, MouseButton.Left);
            window.MouseMove(start + new Vector(40, 40));
            Assert.Equal(350, window.Width);
            Assert.Equal(250, window.Height);
            window.CanResize = false;
            Assert.False(grip.IsVisible);
            window.CanResize = true;
            window.WindowState = WindowState.Maximized;
            Assert.False(grip.IsVisible);
        }
        finally { window.Close(); }
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
