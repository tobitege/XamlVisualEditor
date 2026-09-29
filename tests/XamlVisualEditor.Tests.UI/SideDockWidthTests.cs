using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Xaml.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Fluent;
using Dock.Model.ReactiveUI.Controls;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class SideDockWidthTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task ResizingWindowKeepsSideDockWidthsAndChangesTheEditorWidth()
    {
        Avalonia.Themes.Fluent.FluentTheme fluent = new();
        DockFluentTheme dockTheme = new();
        Application.Current!.Styles.Add(fluent);
        Application.Current.Styles.Add(dockTheme);
        using MainWindowViewModel viewModel = new();
        ToolDock left = XamlEditorDockFactory.FindDockable<ToolDock>(viewModel.DockLayout, "LeftToolDock")!;
        ToolDock right = XamlEditorDockFactory.FindDockable<ToolDock>(viewModel.DockLayout, "RightToolDock")!;
        ProportionalDock editor = XamlEditorDockFactory.FindDockable<ProportionalDock>(viewModel.DockLayout, "CenterAndBottom")!;
        SolutionExplorerTool leftTool = new(viewModel.SolutionExplorer);
        OutputTool rightTool = new(viewModel.Output) { Id = "WidthTestRight" };
        viewModel.DockFactory.AddDockable(left, leftTool);
        viewModel.DockFactory.AddDockable(right, rightTool);
        left.IsEmpty = right.IsEmpty = false;
        viewModel.DockFactory.SetActiveDockable(leftTool);
        viewModel.DockFactory.SetActiveDockable(rightTool);
        DockControl dockControl = new()
        {
            Layout = viewModel.DockLayout,
            Factory = viewModel.DockFactory,
            Width = 1200,
            Height = 800
        };
        Interaction.GetBehaviors(dockControl).Add(new FixedSideDockWidthsBehavior());
        Window window = new() { Content = dockControl, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await FlushAsync(window);
            Assert.Equal(1200, dockControl.Bounds.Width);
            double leftWidth = left.Proportion * dockControl.Bounds.Width;
            double rightWidth = right.Proportion * dockControl.Bounds.Width;
            double editorWidth = editor.Proportion * dockControl.Bounds.Width;
            Assert.True(leftWidth > 100 && rightWidth > 100,
                $"Dock width {dockControl.Bounds.Width}, side proportions {left.Proportion} and {right.Proportion}");

            window.Width = 1600;
            dockControl.Width = 1600;
            await FlushAsync(window);
            Assert.InRange(left.Proportion * dockControl.Bounds.Width, leftWidth - 2, leftWidth + 2);
            Assert.InRange(right.Proportion * dockControl.Bounds.Width, rightWidth - 2, rightWidth + 2);
            Assert.InRange(editor.Proportion * dockControl.Bounds.Width, editorWidth + 398, editorWidth + 402);

            window.Width = 1000;
            dockControl.Width = 1000;
            await FlushAsync(window);
            Assert.InRange(left.Proportion * dockControl.Bounds.Width, leftWidth - 2, leftWidth + 2);
            Assert.InRange(right.Proportion * dockControl.Bounds.Width, rightWidth - 2, rightWidth + 2);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(dockTheme);
            Application.Current.Styles.Remove(fluent);
        }
    }

    private static async Task FlushAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }
}
