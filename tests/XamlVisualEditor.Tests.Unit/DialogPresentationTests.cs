using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DialogPresentationTests
{
    [AvaloniaFact]
    public async Task MessageServiceUsesTranslatedWindowTitlesAndContentSizedDialogs()
    {
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
        FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window owner = new() { Width = 800, Height = 700 };
        AppWindow service = new(new MainWindowProvider { MainWindow = owner });
        try
        {
            owner.Show();
            Task warning = service.ShowWarningMessageAsync("Kurze Warnung.", CancellationToken.None);
            Flush(owner);
            MessageDialog shortDialog = Assert.IsType<MessageDialog>(Assert.Single(owner.OwnedWindows));
            Assert.Equal("Warnung", shortDialog.Title);
            Assert.Equal("Warnung", Assert.IsType<MessageDialogViewModel>(shortDialog.DataContext).Title);
            double shortHeight = shortDialog.ClientSize.Height;
            Assert.InRange(shortHeight, 70, 170);
            shortDialog.Close(true);
            await warning;

            Task error = service.ShowErrorMessageAsync(string.Join(" ", Enumerable.Repeat("Ausführliche Fehlermeldung.", 300)), CancellationToken.None);
            Flush(owner);
            MessageDialog longDialog = Assert.IsType<MessageDialog>(Assert.Single(owner.OwnedWindows));
            Assert.Equal("Fehler", longDialog.Title);
            Assert.InRange(longDialog.ClientSize.Height, shortHeight + 1, 600);
            ScrollViewer scroll = Assert.Single(longDialog.GetVisualDescendants().OfType<ScrollViewer>());
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
            Button confirm = Assert.Single(longDialog.GetVisualDescendants().OfType<Button>(), button => button.IsDefault);
            double buttonBottom = confirm.TranslatePoint(default, longDialog)!.Value.Y + confirm.Bounds.Height;
            Assert.True(buttonBottom <= longDialog.ClientSize.Height);
            longDialog.Close(true);
            await error;
        }
        finally
        {
            owner.Close();
            Application.Current.Styles.Remove(theme);
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [AvaloniaFact]
    public async Task ProgressDialogClosesWhenTheCallerEndsItsWork()
    {
        Window owner = new() { Width = 800, Height = 700 };
        AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
        try
        {
            owner.Show();
            using CancellationTokenSource workDone = new();
            Task<bool?> dialogTask = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DIALOG_ID, new ProgressDialogRequest("Converting", "This can take a while."), workDone.Token);
            Flush(owner);
            ProgressDialog dialog = Assert.IsType<ProgressDialog>(Assert.Single(owner.OwnedWindows));
            Assert.Single(dialog.GetVisualDescendants().OfType<ProgressBar>(), bar => bar.IsIndeterminate);
            Assert.False(dialogTask.IsCompleted);

            workDone.Cancel();
            Flush(owner);

            Assert.Null(await dialogTask);
            Assert.Empty(owner.OwnedWindows);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task ProgressDialogReportsTheUserCancel()
    {
        Window owner = new() { Width = 800, Height = 700 };
        AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
        try
        {
            owner.Show();
            using CancellationTokenSource workDone = new();
            Task<bool?> dialogTask = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DIALOG_ID, new ProgressDialogRequest("Converting", "This can take a while.", "Stop"), workDone.Token);
            Flush(owner);
            ProgressDialog dialog = Assert.IsType<ProgressDialog>(Assert.Single(owner.OwnedWindows));
            Button cancel = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.Name == "CancelButton");
            Assert.Equal("Stop", cancel.Content);

            cancel.Command!.Execute(null);
            Flush(owner);

            Assert.True(await dialogTask);
        }
        finally
        {
            owner.Close();
        }
    }

    private static void Flush(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (Window dialog in owner.OwnedWindows) dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
