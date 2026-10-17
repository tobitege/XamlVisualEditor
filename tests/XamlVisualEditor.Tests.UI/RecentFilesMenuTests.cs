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
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class RecentFilesMenuTests
{
    [AvaloniaFact]
    public async Task RecentMenusSeparateOpeningKindsAndPreserveThemWhenReopened()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-recent-groups-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string project = Path.Combine(root, "Sample.csproj");
        string code = Path.Combine(root, "Code.cs");
        string store = Path.Combine(root, "recent.json");
        await File.WriteAllTextAsync(project, "<Project />");
        await File.WriteAllTextAsync(code, "class Sample { }");
        await File.WriteAllTextAsync(store, JsonSerializer.Serialize(new[] { project, code, root }));
        using MainWindowViewModel vm = new(recentFilesPath: store);
        MainWindow window = new(vm);
        try
        {
            window.Show();
            await Idle();
            Assert.Equal(project, Assert.Single(vm.RecentWorkspaces).FilePath);
            Assert.Equal(code, Assert.Single(vm.RecentDocuments).FilePath);
            Assert.Equal(root, Assert.Single(vm.RecentFolders).FilePath);
            Menu menu = Assert.Single(window.GetVisualDescendants().OfType<Menu>());
            MenuItem file = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuFile));
            MenuItem recent = Assert.Single(file.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuOpenRecent));
            MenuItem projects = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuRecentProjects));
            MenuItem files = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuRecentFiles));
            MenuItem folders = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuRecentFolders));
            Assert.True(projects.IsVisible && files.IsVisible && folders.IsVisible);
            MenuItem folderEntry = Assert.Single(folders.Items.OfType<MenuItem>());
            Assert.Same(vm.OpenRecentCommand, folderEntry.Command);
            await vm.OpenRecentCommand.Execute(Assert.IsType<RecentFileEntry>(folderEntry.CommandParameter)).FirstAsync();
            Assert.Equal(RecentEntryKind.Folder, vm.RecentFiles[0].Kind);
            Assert.Empty(vm.Documents);

            using IDisposable filePicker = vm.OpenFileInteraction.RegisterHandler(context => context.SetOutput(project));
            await vm.OpenDocumentCommand.Execute().FirstAsync();
            await Idle();
            Assert.False(vm.HasWorkspace);
            Assert.Equal(project, Assert.Single(vm.Documents).FilePath);
            Assert.Single(vm.RecentWorkspaces);
            Assert.Equal(2, vm.RecentDocuments.Count());
            MenuItem projectAsFile = Assert.Single(files.Items.OfType<MenuItem>(),
                item => item.DataContext is RecentFileEntry entry && entry.FilePath == project);
            Assert.Same(vm.OpenRecentCommand, projectAsFile.Command);
            Assert.Equal(RecentEntryKind.File, Assert.IsType<RecentFileEntry>(projectAsFile.CommandParameter).Kind);
            using MainWindowViewModel restored = new(recentFilesPath: store);
            Assert.Single(restored.RecentWorkspaces);
            Assert.Equal(2, restored.RecentDocuments.Count());
            Assert.Single(restored.RecentFolders);
            await restored.OpenRecentCommand.Execute(restored.RecentDocuments.First()).FirstAsync();
            Assert.False(restored.HasWorkspace);
            Assert.Equal(project, Assert.Single(restored.Documents).FilePath);

            await vm.ClearRecentFilesCommand.Execute().FirstAsync();
            await Idle();
            Assert.False(projects.IsVisible || files.IsVisible || folders.IsVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task GermanFileMenuExposesSeparateCommandsAndDebugAccessKey()
    {
        System.Globalization.CultureInfo? previousCulture = UiStrings.Culture;
        UiStrings.Culture = System.Globalization.CultureInfo.GetCultureInfo("de");
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        using MainWindowViewModel vm = new();
        MainWindow window = new(vm);
        try
        {
            window.Show();
            await Idle();
            Menu menu = Assert.Single(window.GetVisualDescendants().OfType<Menu>());
            MenuItem file = Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "_Datei"));
            MenuItem[] items = file.Items.OfType<MenuItem>().ToArray();
            Assert.Same(vm.OpenDocumentCommand, Assert.Single(items, item => Equals(item.Header, "Datei _öffnen...")).Command);
            Assert.Same(vm.OpenWorkspaceCommand, Assert.Single(items, item => Equals(item.Header, "_Projekt/Solution öffnen...")).Command);
            MenuItem closeFile = Assert.Single(items, item => Equals(item.Header, "Datei s_chließen"));
            MenuItem closeWorkspace = Assert.Single(items, item => Equals(item.Header, "Projekt/Solution schließen"));
            Assert.Same(vm.CloseActiveDocumentCommand, closeFile.Command);
            Assert.Same(vm.CloseWorkspaceCommand, closeWorkspace.Command);
            file.IsSubMenuOpen = true;
            await Idle();
            Assert.False(closeFile.IsEffectivelyEnabled);
            Assert.False(closeWorkspace.IsEffectivelyEnabled);
            Assert.Single(menu.Items.OfType<MenuItem>(), item => Equals(item.Header, "Debu_ggen"));

            string codePath = Path.Combine(Path.GetTempPath(), $"xve-close-menu-{Guid.NewGuid():N}.cs");
            await File.WriteAllTextAsync(codePath, "class Example { }");
            await vm.OpenFileAsync(codePath);
            await Idle();
            Assert.True(closeFile.IsEffectivelyEnabled);
            closeFile.Command!.Execute(closeFile.CommandParameter);
            await Idle();
            Assert.Empty(vm.Documents);
            Assert.False(closeFile.IsEffectivelyEnabled);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
            UiStrings.Culture = previousCulture;
        }
    }

    [AvaloniaFact]
    public async Task FullHistoryCanBeSearchedRemovedWithDeleteAndButtonAndClearedPersistently()
    {
        string prefix = Path.Combine(Path.GetTempPath(), "recent-test-" + Guid.NewGuid().ToString("N"));
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
            MenuItem recentFiles = Assert.Single(recent.Items.OfType<MenuItem>(), item => Equals(item.Header, UiStrings.MenuRecentFiles));
            MenuItem[] recentEntries = recentFiles.Items.OfType<MenuItem>().Where(item => item.DataContext is RecentFileEntry).ToArray();
            Assert.Equal(10, recentEntries.Length);
            Assert.All(recentEntries, item => Assert.Same(vm.OpenRecentCommand, item.Command));
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
            // After the removal the next entry is selected.
            Assert.Same(vm.RecentFiles[0], dialogVm.SelectedEntry);
            Assert.Same(vm.RecentFiles[0], grid.SelectedItem);
            Button remove = dialog.FindControl<Button>("RemoveRecentEntry")!;
            Assert.True(remove.IsEffectivelyEnabled);
            Click(dialog, remove);
            await Idle();
            Assert.Equal(10, vm.RecentFiles.Count);
            Assert.Same(vm.RecentFiles[0], dialogVm.SelectedEntry);
            // For the last entry the previous one moves up.
            ClickRow(dialog, grid, vm.RecentFiles[9]);
            Assert.Same(vm.RecentFiles[9], dialogVm.SelectedEntry);
            RecentFileEntry previous = vm.RecentFiles[8];
            dialog.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            await Idle();
            Assert.Equal(9, vm.RecentFiles.Count);
            Assert.Same(previous, dialogVm.SelectedEntry);
            ClickRow(dialog, grid, vm.RecentFiles[0]);
            Click(dialog, remove);
            await Idle();
            Assert.Equal(8, vm.RecentFiles.Count);
            Assert.Equal(paths.Skip(3).Take(8), JsonSerializer.Deserialize<RecentFileEntry[]>(File.ReadAllText(store))!.Select(entry => entry.FilePath));
            Assert.True(File.Exists(paths[0]));
            await dialogVm.CloseCommand.Execute().FirstAsync();
            await Idle();
            Assert.Empty(window.OwnedWindows);
            await vm.ClearRecentFilesCommand.Execute().FirstAsync();
            await Idle();
            Assert.Empty(vm.RecentFiles);
            Assert.False(recentFiles.IsVisible);
            file.IsSubMenuOpen = true;
            recent.IsSubMenuOpen = true;
            await Idle();
            // A closed submenu keeps its entries until it opens again.
            Assert.Empty(recentFiles.Items);
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
            foreach (string path in paths.Append(store)) File.Delete(path);
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
