using System.Reactive.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class RecentFilesMenuTests
{
    [AvaloniaFact]
    public async Task FullHistoryCanBeSearchedRemovedWithDeleteAndButtonAndClearedPersistently()
    {
        string prefix = Path.Combine(AppContext.BaseDirectory, "recent-test-" + Guid.NewGuid().ToString("N"));
        string store = prefix + ".json";
        string[] paths = Enumerable.Range(0, 12).Select(index => prefix + "-View" + index + ".axaml").ToArray();
        File.WriteAllText(paths[0], "<Canvas xmlns='https://github.com/avaloniaui' />");
        File.WriteAllText(store, JsonSerializer.Serialize(paths));
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Avalonia.Markup.Xaml.Styling.StyleInclude gridTheme = new(new Uri("avares://XamlVisualEditor.Tests.UI/"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml")
        };
        Application.Current!.Styles.Add(theme);
        Application.Current.Styles.Add(gridTheme);
        using MainWindowViewModel vm = new(recentFilesPath: store);
        MainWindow window = new(vm);
        try
        {
            window.Show();
            await Idle();
            Assert.Equal(12, vm.RecentFiles.Count);
            Menu menu = Assert.Single(window.GetVisualDescendants().OfType<Menu>());
            MenuItem file = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "_File"));
            MenuItem recent = Assert.Single(file.Items.OfType<MenuItem>(), item => Equals(item.Header, "Open _Recent"));
            MenuItem[] recentEntries = recent.Items.OfType<MenuItem>().Where(item => item.DataContext is RecentFileEntry).ToArray();
            Assert.Equal(10, recentEntries.Length);
            Assert.All(recentEntries, item => Assert.Same(vm.OpenPathCommand, item.Command));
            MenuItem more = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, "_More..."));
            MenuItem clear = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Clear"));
            Assert.Same(vm.ManageRecentFilesCommand, more.Command);
            Assert.Same(vm.ClearRecentFilesCommand, clear.Command);
            file.IsSubMenuOpen = true;
            recent.IsSubMenuOpen = true;
            await Idle();
            Assert.True(more.Focus());
            TopLevel.GetTopLevel(more)!.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await Idle();
            RecentFilesDialog dialog = Assert.Single(window.OwnedWindows.OfType<RecentFilesDialog>());
            var dialogVm = Assert.IsType<RecentFilesDialogViewModel>(dialog.DataContext);
            Assert.Equal(12, dialogVm.Entries.Count);
            DataGrid grid = dialog.FindControl<DataGrid>("RecentEntries")!;
            TextBox search = dialog.FindControl<TextBox>("RecentSearch")!;
            search.Text = "View11.axaml";
            await Idle();
            Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>(), row => row.IsVisible);
            search.Text = string.Empty;
            await Idle();
            ClickRow(dialog, grid, vm.RecentFiles[0]);
            Assert.Same(vm.RecentFiles[0], dialogVm.SelectedEntry);
            dialog.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Idle();
            Assert.Equal(11, vm.RecentFiles.Count);
            Assert.DoesNotContain(vm.RecentFiles, entry => entry.FilePath == paths[0]);
            Assert.True(File.Exists(paths[0]));
            ClickRow(dialog, grid, vm.RecentFiles[0]);
            Button remove = dialog.FindControl<Button>("RemoveRecentEntry")!;
            Click(dialog, remove);
            await Idle();
            Assert.Equal(10, vm.RecentFiles.Count);
            Assert.Equal(paths.Skip(2), JsonSerializer.Deserialize<string[]>(File.ReadAllText(store)));
            Assert.True(File.Exists(paths[0]));
            await dialogVm.CloseCommand.Execute().FirstAsync();
            await Idle();
            Assert.Empty(window.OwnedWindows);
            await vm.ClearRecentFilesCommand.Execute().FirstAsync();
            await Idle();
            Assert.Empty(vm.RecentFiles);
            Assert.DoesNotContain(recent.Items.OfType<MenuItem>(), item => item.DataContext is RecentFileEntry);
            file.IsSubMenuOpen = true;
            recent.IsSubMenuOpen = true;
            await Idle();
            Assert.False(clear.IsEffectivelyEnabled);
            using MainWindowViewModel reopened = new(recentFilesPath: store);
            Assert.Empty(reopened.RecentFiles);
            Assert.True(File.Exists(paths[0]));
        }
        finally
        {
            foreach (Window dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Application.Current.Styles.Remove(gridTheme);
            Application.Current.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async Task FileContributionsAreDirectAndToolsContributionsUseTheCentralExtensionsMenu()
    {
        ExtensionContributionRegistry contributions = new();
        using IDisposable registration = contributions.RegisterMenuItems("sample", new[]
        {
            new ExtensionMenuContribution("sample.folder", "Open Workspace Folder...", ExtensionMenuLocations.File, "workspace"),
            new ExtensionMenuContribution("sample.tool", "Sample Tool", ExtensionMenuLocations.Tools, "tools")
        });
        using MainWindowViewModel vm = new(extensionContributionRegistry: contributions);
        Assert.Empty(vm.RecentFiles);
        MainWindow window = new(vm);
        try
        {
            window.Show();
            await Idle();
            Menu menu = Assert.Single(window.GetVisualDescendants().OfType<Menu>());
            MenuItem file = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "_File"));
            MenuItem tools = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Tools"));
            MenuItem folder = Assert.Single(file.Items.OfType<MenuItem>(), item => Equals(item.Header, "Open Workspace Folder..."));
            Assert.Same(Assert.Single(vm.FileMenuItems).Command, folder.Command);
            Assert.DoesNotContain(file.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Extensions"));
            Assert.DoesNotContain(tools.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Extensions"));
            Assert.Single(vm.ExtensionMenuItems, item => item.CommandId == "sample.tool");
            registration.Dispose();
            await Idle();
            Assert.DoesNotContain(file.Items.OfType<MenuItem>(), item => Equals(item.Header, "Open Workspace Folder..."));
            Assert.DoesNotContain(vm.ExtensionMenuItems, item => item.CommandId == "sample.tool");
        }
        finally { window.Close(); }
    }

    private static async Task Idle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

    private static void ClickRow(Window window, DataGrid grid, RecentFileEntry entry)
    {
        DataGridRow row = Assert.Single(grid.GetVisualDescendants().OfType<DataGridRow>(), candidate => ReferenceEquals(candidate.DataContext, entry));
        Click(window, row);
    }

    private static void Click(Window window, Control control)
    {
        window.UpdateLayout();
        Point point = control.TranslatePoint(new Point(Math.Min(40, control.Bounds.Width / 2), control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }
}
