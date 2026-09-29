using System;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using XamlVisualEditor.App;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.Tests.UI;

public sealed class ExtensionManagerMenuTests
{
    [AvaloniaFact]
    public async Task ExtensionsMenuOpensManagerWithoutExtensionContributions()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        using MainWindowViewModel viewModel = new();
        MainWindow window = new(viewModel);
        var layout = viewModel.DockLayout;
        try
        {
            window.Show();
            Menu menu = Assert.Single(window.GetVisualDescendants().OfType<Menu>());
            MenuItem extensions = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Extensions"));
            for (int iteration = 0; iteration < 2; iteration++)
            {
                window.UpdateLayout();
                Point center = extensions.TranslatePoint(new Point(extensions.Bounds.Width / 2,
                    extensions.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.True(extensions.IsSubMenuOpen);
                MenuItem manager = Assert.IsType<MenuItem>(extensions.ContainerFromIndex(0));
                Assert.Same(viewModel.OpenExtensionManagerCommand, viewModel.ExtensionMenuItems[0].Command);
                Assert.Same(viewModel.ExtensionMenuItems[0], manager.DataContext);
                Assert.Equal("_Extensions Manager...", manager.Header);
                Assert.Same(viewModel.OpenExtensionManagerCommand, manager.Command);
                Assert.True(manager.Focus());
                TopLevel.GetTopLevel(manager)!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                ExtensionManagerDialog dialog = Assert.Single(window.OwnedWindows.OfType<ExtensionManagerDialog>());
                Assert.True(dialog.IsVisible);
                Assert.Same(viewModel.ExtensionManager, dialog.DataContext);
                Assert.Same(layout, viewModel.DockLayout);
                dialog.Close();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

                // The command runs until the modal dialog closes and disables its menu item meanwhile;
                // its completion can take more than one dispatcher round, and a disabled item cannot take focus.
                await viewModel.OpenExtensionManagerCommand.IsExecuting
                    .FirstAsync(executing => !executing)
                    .Timeout(TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            foreach (Window dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async Task ExtensionsManager_Command_OpensDialogWithoutChangingDockLayout()
    {
        using MainWindowViewModel vm = new();
        var layout = vm.DockLayout;
        var active = layout.ActiveDockable;
        int opened = 0;
        using IDisposable handler = vm.ExtensionManagerInteraction.RegisterHandler(interaction =>
        {
            opened++;
            Assert.Same(layout, vm.DockLayout);
            Assert.Same(active, layout.ActiveDockable);
            interaction.SetOutput(Unit.Default);
        });

        await vm.OpenExtensionManagerCommand.Execute().FirstAsync();
        await vm.OpenExtensionManagerCommand.Execute().FirstAsync();
        Assert.Equal(2, opened);
        Assert.Same(active, layout.ActiveDockable);
    }
}
