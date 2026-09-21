using System.Globalization;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Dock.Model.ReactiveUI.Controls;
using XamlVisualEditor.App;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class OptionsDialogTests
{
    [AvaloniaTheory]
    [InlineData("en", "de", "Haltepunkte", "Lokale Variablen", "Keine Dokumente geöffnet")]
    [InlineData("de", "en", "Breakpoints", "Locals", "No documents open")]
    public void RestoredBuiltInDockTitlesUseTheSelectedLanguage(string savedLanguage, string openedLanguage,
        string breakpointsTitle, string localsTitle, string emptyDocumentsText)
    {
        CultureInfo? previousCulture = UiStrings.Culture;
        string file = Path.Combine(AppContext.BaseDirectory, "localized-dock-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            UiStrings.Culture = CultureInfo.GetCultureInfo(savedLanguage);
            using MainWindowViewModel vm = new();
            vm.DockFactory.SaveLayout(vm.DockLayout, file);
            Assert.True(File.Exists(file));
            UiStrings.Culture = CultureInfo.GetCultureInfo(openedLanguage);
            var restored = vm.DockFactory.LoadLayout(file);
            Assert.NotNull(restored);
            vm.DockFactory.ConfigureToolViewModels(restored);
            Assert.Equal(breakpointsTitle, XamlEditorDockFactory.FindDockable<BreakpointsTool>(restored, "Breakpoints")!.Title);
            Assert.Equal(localsTitle, XamlEditorDockFactory.FindDockable<LocalsTool>(restored, "Locals")!.Title);
            Assert.Equal(emptyDocumentsText, XamlEditorDockFactory.FindDockable<DocumentDock>(restored, "DocumentDock")!.EmptyContent);
            Assert.Equal(emptyDocumentsText, vm.DockFactory.CreateDocumentDock().EmptyContent);
            Assert.Equal(emptyDocumentsText, new CanvasDockFactory().CreateDocumentDock().EmptyContent);
        }
        finally { UiStrings.Culture = previousCulture; }
    }

    [AvaloniaTheory]
    [InlineData("en", "Options", "Inside XVE")]
    [InlineData("de", "Optionen", "Innerhalb von XVE")]
    public async Task OptionsValidateSaveReloadAndCancelInBothLanguages(string language, string title, string internalLabel)
    {
        CultureInfo? previousCulture = UiStrings.Culture;
        UiStrings.Culture = CultureInfo.GetCultureInfo(language);
        string file = Path.Combine(AppContext.BaseDirectory, "options-test-" + Guid.NewGuid().ToString("N") + ".json");
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Test process path unavailable.");
        EditorOptionsStore store = new(file);
        store.Save(new EditorOptions { Language = language, CommandPromptPath = executable });
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        using MainWindowViewModel vm = new(editorOptions: store);
        MainWindow window = new(vm);
        try
        {
            window.Show();
            Task open = vm.OpenOptionsCommand.Execute().ToTask();
            await Idle();
            OptionsDialog dialog = Assert.Single(window.OwnedWindows.OfType<OptionsDialog>());
            OptionsDialogViewModel options = Assert.IsType<OptionsDialogViewModel>(dialog.DataContext);
            Assert.Equal(title, dialog.Title);
            Assert.Equal(internalLabel, options.Locations[0].ToString());
            ComboBox mode = dialog.FindControl<ComboBox>("TerminalLocationChoice")!;
            ComboBox shell = dialog.FindControl<ComboBox>("TerminalShellChoice")!;
            mode.SelectedItem = options.Locations.Single(item => item.Value == TerminalLocation.External);
            shell.SelectedItem = options.Shells.Single(item => item.Value == TerminalShell.PowerShell7);
            TextBox path = dialog.FindControl<TextBox>("PowerShell7Path")!;
            path.Text = file + ".missing.exe";
            if (OperatingSystem.IsWindows())
            {
                await options.SaveCommand.Execute().FirstAsync();
                Assert.NotNull(options.Error);
                Assert.True(dialog.IsVisible);
                Assert.Equal(TerminalLocation.Internal, store.Current.TerminalLocation);
            }
            using IDisposable picker = options.BrowseExecutableInteraction.RegisterHandler(request => request.SetOutput(executable));
            await options.BrowseCommand.Execute(TerminalShell.PowerShell7).FirstAsync();
            Assert.Equal(executable, path.Text);
            await options.SaveCommand.Execute().FirstAsync();
            await open;
            EditorOptions saved = new EditorOptionsStore(file).Current;
            Assert.Equal(TerminalLocation.External, saved.TerminalLocation);
            Assert.Equal(TerminalShell.PowerShell7, saved.TerminalShell);
            Assert.Equal(executable, saved.PowerShell7Path);
            Assert.Equal(language, saved.Language);

            Task reopened = vm.OpenOptionsCommand.Execute().ToTask();
            await Idle();
            OptionsDialog second = Assert.Single(window.OwnedWindows.OfType<OptionsDialog>());
            OptionsDialogViewModel secondOptions = Assert.IsType<OptionsDialogViewModel>(second.DataContext);
            secondOptions.PowerShell7Path = "discarded.exe";
            await secondOptions.CancelCommand.Execute().FirstAsync();
            await reopened;
            Assert.Equal(saved, new EditorOptionsStore(file).Current);
        }
        finally
        {
            foreach (Window dialog in window.OwnedWindows.ToArray()) dialog.Close();
            window.Close();
            Application.Current.Styles.Remove(theme);
            UiStrings.Culture = previousCulture;
        }
    }

    private static async Task Idle() => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
