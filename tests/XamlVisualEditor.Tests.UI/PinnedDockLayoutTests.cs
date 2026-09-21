using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Model.Core;
using XamlVisualEditor.App;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class PinnedDockLayoutTests
{
    [AvaloniaFact]
    public async Task OpeningCollapsedPanelUsesItsSavedHeightOnFirstLayout()
    {
        Avalonia.Themes.Fluent.FluentTheme fluent = new();
        Dock.Avalonia.Themes.Fluent.DockFluentTheme dockTheme = new();
        Application.Current!.Styles.Add(fluent);
        Application.Current.Styles.Add(dockTheme);
        using MainWindowViewModel viewModel = new();
        MainWindow window = new(viewModel) { Width = 1000, Height = 800 };
        viewModel.DockFactory.CollapseBottomToolDock(viewModel.DockLayout);
        viewModel.IsBreakpointsVisible = false;
        IDockable tool = XamlEditorDockFactory.FindDockable<IDockable>(viewModel.DockLayout, "Breakpoints")!;
        IWorkspaceCommands commands = viewModel;
        try
        {
            window.Show();
            foreach (double height in new[] { 320d, 440d })
            {
                tool.SetPinnedBounds(0, 0, 400, height);
                await commands.ToggleBreakpointsAsync(CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                Control panel = Assert.Single(window.GetVisualDescendants().OfType<Control>(),
                    control => control.Name == "PART_PinnedDock");
                Assert.True(panel.IsVisible);
                Assert.InRange(panel.Bounds.Height, height - 1, height + 1);
                await commands.ToggleBreakpointsAsync(CancellationToken.None);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            }
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(dockTheme);
            Application.Current.Styles.Remove(fluent);
        }
    }
}
