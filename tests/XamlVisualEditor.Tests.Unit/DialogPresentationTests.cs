using System.Globalization;
using System.Reactive.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
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

            Task error = service.ShowErrorMessageAsync(string.Join(" ", Enumerable.Repeat("Detailed error message.", 300)), CancellationToken.None);
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
                ProgressDialogRequest.DialogId, new ProgressDialogRequest("Converting", "This can take a while."), workDone.Token);
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
                ProgressDialogRequest.DialogId, new ProgressDialogRequest("Converting", "This can take a while.", "Stop"), workDone.Token);
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

    [AvaloniaFact]
    public async Task ProgressWithoutMainWindowCompletesAtOnceAndClosesWithItsToken()
    {
        AppDialogHost host = new(new MainWindowProvider());
        Window shown = new();
        using IDisposable registration = host.RegisterDialog(ProgressDialogRequest.DialogId, _ => shown);
        using CancellationTokenSource workDone = new();

        bool? result = await host.ShowDialogAsync<bool?>(ProgressDialogRequest.DialogId, null, workDone.Token);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(result);
        Assert.True(shown.IsVisible);

        workDone.Cancel();
        Dispatcher.UIThread.RunJobs();

        Assert.False(shown.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_question_without_main_window_is_not_shown_and_answers_no()
    {
        AppDialogHost host = new(new MainWindowProvider());
        Window question = new();
        using IDisposable registration = host.RegisterDialog("test.question", _ => question);

        bool? answer = await host.ShowDialogAsync<bool?>("test.question", null, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        Assert.Null(answer);
        Assert.False(question.IsVisible);
    }

    [AvaloniaFact]
    public async Task A_destructive_question_starts_on_cancel_so_enter_space_or_the_window_button_answer_no()
    {
        foreach ((Key key, PhysicalKey physicalKey, string? symbol) in new (Key, PhysicalKey, string?)[]
                 {
                     (Key.Enter, PhysicalKey.Enter, "\r"), (Key.Space, PhysicalKey.Space, " "), (Key.Escape, PhysicalKey.Escape, null)
                 })
        {
            Window owner = new() { Width = 800, Height = 700 };
            try
            {
                owner.Show();
                AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
                Task<bool?> answer = host.ShowDialogAsync<bool?>(
                    ConfirmationDialogRequest.DialogId,
                    new ConfirmationDialogRequest("Delete", "Delete it?", "Delete", IsDestructive: true),
                    CancellationToken.None);
                Flush(owner);
                ConfirmDialog dialog = Assert.IsType<ConfirmDialog>(Assert.Single(owner.OwnedWindows));
                Button cancel = Assert.Single(dialog.GetVisualDescendants().OfType<Button>(), button => button.Name == "CancelButton");
                Assert.True(cancel.IsFocused, key.ToString());

                // Enter and Escape act on the press and close the dialog; a button clicks on the release of Space.
                dialog.KeyPress(key, RawInputModifiers.None, physicalKey, symbol);
                if (key == Key.Space)
                {
                    dialog.KeyRelease(key, RawInputModifiers.None, physicalKey, symbol);
                }

                Flush(owner);

                Assert.False(await answer, key.ToString());
            }
            finally
            {
                owner.Close();
            }
        }
    }

    [AvaloniaFact]
    public async Task Progress_that_cannot_be_cancelled_has_no_cancel_button_and_ignores_escape()
    {
        Window owner = new() { Width = 800, Height = 700 };
        try
        {
            owner.Show();
            AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
            using CancellationTokenSource workDone = new();
            Task<bool?> result = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DialogId,
                new ProgressDialogRequest("Continue", "Continuing", CanCancel: false),
                workDone.Token);
            Flush(owner);
            ProgressDialog dialog = Assert.IsType<ProgressDialog>(Assert.Single(owner.OwnedWindows));
            Assert.False(Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), button => button.Name == "CancelButton").IsVisible);
            Assert.False(Assert.IsType<ProgressDialogViewModel>(dialog.DataContext).CancelCommand.CanExecute.FirstAsync().Wait());

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Flush(owner);
            Assert.True(dialog.IsVisible);
            Assert.False(result.IsCompleted);

            workDone.Cancel();
            Flush(owner);
            Assert.Null(await result);
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
