using System.Diagnostics;
using System.Globalization;
using System.Reactive.Linq;
using System.Reflection;
using ReactiveUI;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using XamlVisualEditor.App;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
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
    public async Task DialogWithoutMainWindowCompletesAtOnceAndClosesWithItsToken()
    {
        AppDialogHost host = new(new MainWindowProvider());
        Window shown = new();
        using IDisposable registration = host.RegisterDialog("test.unowned", _ => shown);
        using CancellationTokenSource workDone = new();

        bool? result = await host.ShowDialogAsync<bool?>("test.unowned", null, workDone.Token);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(result);
        Assert.True(shown.IsVisible);

        workDone.Cancel();
        Dispatcher.UIThread.RunJobs();

        Assert.False(shown.IsVisible);
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
    public async Task Progress_with_a_cancel_delay_offers_cancel_and_the_window_button_only_after_the_delay()
    {
        Window owner = new() { Width = 800, Height = 700 };
        try
        {
            owner.Show();
            AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
            using CancellationTokenSource workDone = new();
            Task<bool?> result = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DialogId,
                new ProgressDialogRequest("Continue", "Continuing", "Stop git…") { CancelDelay = TimeSpan.FromMilliseconds(300) },
                workDone.Token);
            Flush(owner);
            ProgressDialog dialog = Assert.IsType<ProgressDialog>(Assert.Single(owner.OwnedWindows));
            Button stop = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), button => button.Name == "CancelButton");
            ProgressDialogViewModel viewModel = Assert.IsType<ProgressDialogViewModel>(dialog.DataContext);
            Assert.False(stop.IsVisible);
            Assert.False(viewModel.CancelCommand.CanExecute.FirstAsync().Wait());

            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Flush(owner);
            CloseWithWindowButton(dialog);
            Flush(owner);
            Assert.True(dialog.IsVisible);
            Assert.False(result.IsCompleted);

            // Once the delay elapsed, the button shows and the window button cancels.
            await viewModel.WhenAnyValue(x => x.CanCancel).FirstAsync(canCancel => canCancel).Timeout(TimeSpan.FromSeconds(5));
            Flush(owner);
            Assert.True(stop.IsVisible);
            Assert.Equal("Stop git…", stop.Content);
            CloseWithWindowButton(dialog);
            Flush(owner);
            Assert.True(await result);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task Progress_offers_cancel_and_the_window_button_only_while_the_work_can_be_cancelled()
    {
        Window owner = new() { Width = 800, Height = 700 };
        try
        {
            owner.Show();
            AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
            using CancellationTokenSource workDone = new();
            using System.Reactive.Subjects.BehaviorSubject<bool> writing = new(false);
            Task<bool?> result = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DialogId,
                new ProgressDialogRequest("Checkout", "Running", "Stop git…") { CancelAvailable = writing },
                workDone.Token);
            Flush(owner);
            ProgressDialog dialog = Assert.IsType<ProgressDialog>(Assert.Single(owner.OwnedWindows));
            Button stop = Assert.Single(dialog.GetLogicalDescendants().OfType<Button>(), button => button.Name == "CancelButton");
            Assert.False(stop.IsVisible);
            CloseWithWindowButton(dialog);
            Flush(owner);
            Assert.False(result.IsCompleted);

            // A change on another thread reaches the dialog on the UI thread.
            await Task.Run(() => writing.OnNext(true));
            Flush(owner);
            Assert.True(stop.IsVisible);

            await Task.Run(() => writing.OnNext(false));
            Flush(owner);
            Assert.False(stop.IsVisible);
            CloseWithWindowButton(dialog);
            Flush(owner);
            Assert.False(result.IsCompleted);

            writing.OnNext(true);
            Flush(owner);
            CloseWithWindowButton(dialog);
            Flush(owner);
            Assert.True(await result);
        }
        finally
        {
            owner.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_window_button_cancels_a_cancellable_progress_and_answers_no_to_a_destructive_question()
    {
        Window owner = new() { Width = 800, Height = 700 };
        try
        {
            owner.Show();
            AppDialogHost host = new(new MainWindowProvider { MainWindow = owner });
            using CancellationTokenSource workDone = new();
            Task<bool?> progress = host.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DialogId,
                new ProgressDialogRequest("Fetch", "Fetching"),
                workDone.Token);
            Flush(owner);
            CloseWithWindowButton(Assert.Single(owner.OwnedWindows));
            Flush(owner);
            Assert.True(await progress);

            Task<bool?> answer = host.ShowDialogAsync<bool?>(
                ConfirmationDialogRequest.DialogId,
                new ConfirmationDialogRequest("Delete", "Delete it?", "Delete", IsDestructive: true),
                CancellationToken.None);
            Flush(owner);
            CloseWithWindowButton(Assert.Single(owner.OwnedWindows));
            Flush(owner);
            Assert.NotEqual(true, await answer);
        }
        finally
        {
            owner.Close();
        }
    }

    /// <summary>
    /// Closes <paramref name="window"/> as its window button or Alt+F4 does: the platform asks the window through its
    /// closing callback with the reason <see cref="WindowCloseReason.WindowClosing"/> and closes it unless the callback
    /// cancels. Avalonia has no public way to raise that callback, so reflection reaches it; a programmatic
    /// <see cref="Window.Close()"/> would take another path that the dialogs let through.
    /// </summary>
    private static void CloseWithWindowButton(Window window)
    {
        object platform = typeof(TopLevel)
            .GetProperty("PlatformImpl", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .GetValue(window)!;
        PropertyInfo closing = platform.GetType()
            .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .First(property => property.Name.EndsWith("Closing", StringComparison.Ordinal)
                && property.PropertyType.Name.StartsWith("Func", StringComparison.Ordinal));
        Delegate callback = (Delegate)closing.GetValue(platform)!;
        object reason = Enum.Parse(callback.GetType().GetGenericArguments()[0], nameof(WindowCloseReason.WindowClosing));
        // The platform closes the window when the callback does not cancel.
        if (!(bool)callback.DynamicInvoke(reason)!)
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task The_main_window_waits_for_a_writing_git_command_before_it_closes()
    {
        using GitTestRepository repository = CreateRepositoryWithWaitingHook();
        GitService git = new(options: repository.ProcessOptions);
        MainWindow window = new() { WhenGitWritesEnded = git.WhenWritesEnded };
        window.Show();
        Task switching = git.SwitchAsync(repository.Path, "other", CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => File.Exists(Path.Combine(repository.Path, ".git", "hook-started")));

            window.Close();
            Flush(window);

            Assert.True(window.IsVisible);
            Assert.IsType<ProgressDialog>(Assert.Single(window.OwnedWindows));
        }
        finally
        {
            File.WriteAllText(Path.Combine(repository.Path, ".git", "release"), "");
        }

        await switching;
        await WaitUntilAsync(() => !window.IsVisible);
    }

    [AvaloniaFact]
    public async Task The_main_window_closes_despite_a_hanging_git_command_only_after_a_confirmation()
    {
        using GitTestRepository repository = CreateRepositoryWithWaitingHook();
        GitService git = new(options: repository.ProcessOptions);
        using MainWindowViewModel viewModel = new(recentFilesPath: Path.Combine(repository.RootPath, "recent.json"));
        MainWindow window = new(viewModel) { WhenGitWritesEnded = git.WhenWritesEnded };
        TimeSpan delay = MainWindow.GitWriteCloseDelay;
        MainWindow.GitWriteCloseDelay = TimeSpan.FromMilliseconds(200);
        window.Show();
        Task switching = git.SwitchAsync(repository.Path, "other", CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => File.Exists(Path.Combine(repository.Path, ".git", "hook-started")));
            // As after File › Exit, the documents were confirmed before the window waits for git.
            Assert.True(await viewModel.ConfirmShutdownAsync());

            // The window button of the progress asks, and the window button of the question keeps the editor open and
            // withdraws the confirmation of the documents, so documents changed from then on are asked about again.
            window.Close();
            ProgressDialog progress = await WaitForOwnedAsync<ProgressDialog>(window);
            await WaitUntilAsync(() => ((ProgressDialogViewModel)progress.DataContext!).CanCancel);
            CloseWithWindowButton(progress);
            ConfirmDialog question = await WaitForOwnedAsync<ConfirmDialog>(window);
            Assert.Equal(UiStrings.GitWritesCloseAnywayPrompt, ((ConfirmDialogViewModel)question.DataContext!).Message);
            CloseWithWindowButton(question);
            await WaitUntilAsync(() => window.OwnedWindows.Count == 0 && !viewModel.IsShutdownConfirmed);
            Assert.True(window.IsVisible);

            // The button of the progress asks again, and only the confirmation closes the editor while git runs. The
            // window ignores a close until the declined wait has ended, which happens after the question closed.
            await WaitUntilAsync(() =>
            {
                window.Close();
                Flush(window);
                return window.OwnedWindows.OfType<ProgressDialog>().Any();
            });
            progress = await WaitForOwnedAsync<ProgressDialog>(window);
            await WaitUntilAsync(() => ((ProgressDialogViewModel)progress.DataContext!).CanCancel);
            ((ProgressDialogViewModel)progress.DataContext!).CancelCommand.Execute().Subscribe();
            question = await WaitForOwnedAsync<ConfirmDialog>(window);
            ((ConfirmDialogViewModel)question.DataContext!).ConfirmCommand.Execute().Subscribe();
            await WaitUntilAsync(() => !window.IsVisible);
            Assert.False(switching.IsCompleted);
        }
        finally
        {
            MainWindow.GitWriteCloseDelay = delay;
            File.WriteAllText(Path.Combine(repository.Path, ".git", "release"), "");
        }

        await switching;
    }

    [AvaloniaFact]
    public async Task A_close_question_that_is_open_when_git_ends_closes_and_the_editor_closes()
    {
        using GitTestRepository repository = CreateRepositoryWithWaitingHook();
        GitService git = new(options: repository.ProcessOptions);
        MainWindow window = new() { WhenGitWritesEnded = git.WhenWritesEnded };
        TimeSpan delay = MainWindow.GitWriteCloseDelay;
        MainWindow.GitWriteCloseDelay = TimeSpan.FromMilliseconds(200);
        window.Show();
        Task switching = git.SwitchAsync(repository.Path, "other", CancellationToken.None);
        try
        {
            await WaitUntilAsync(() => File.Exists(Path.Combine(repository.Path, ".git", "hook-started")));
            window.Close();
            ProgressDialog progress = await WaitForOwnedAsync<ProgressDialog>(window);
            await WaitUntilAsync(() => ((ProgressDialogViewModel)progress.DataContext!).CanCancel);
            CloseWithWindowButton(progress);
            await WaitForOwnedAsync<ConfirmDialog>(window);
        }
        finally
        {
            MainWindow.GitWriteCloseDelay = delay;
            File.WriteAllText(Path.Combine(repository.Path, ".git", "release"), "");
        }

        await switching;
        await WaitUntilAsync(() => !window.IsVisible);
    }

    /// <summary>
    /// Creates a repository whose checkout of the branch other waits in a hook for the file .git/release, at most
    /// 30 seconds, so a failed test leaves no git behind.
    /// </summary>
    private static GitTestRepository CreateRepositoryWithWaitingHook()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("branch", "other");
        string hook = Path.Combine(repository.Path, ".git", "hooks", "post-checkout");
        File.WriteAllText(
            hook,
            "#!/bin/sh\ntouch .git/hook-started\ni=0\nwhile [ ! -f .git/release ] && [ $i -lt 300 ]; do sleep 0.1; i=$((i+1)); done\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return repository;
    }

    private static async Task<T> WaitForOwnedAsync<T>(Window owner) where T : Window
    {
        await WaitUntilAsync(() => owner.OwnedWindows.OfType<T>().Any());
        Flush(owner);
        return owner.OwnedWindows.OfType<T>().Single();
    }

    // Runs the jobs of the UI thread while it waits, so posted continuations run.
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        Stopwatch waited = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(20), "The condition did not become true.");
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }

    private static void Flush(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        foreach (Window dialog in owner.OwnedWindows) dialog.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
