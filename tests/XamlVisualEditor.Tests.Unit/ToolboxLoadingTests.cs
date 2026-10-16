using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ReactiveUI;
using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.ToolboxExtension.Views;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxLoadingTests
{
    [AvaloniaFact]
    public async Task CompletedTaskHidesAfterItsDelayWhileFailedTaskStaysVisible()
    {
        MetadataControlCatalog success = new((_, _, _, _, _) => Array.Empty<XamlVisualEditor.Core.TypeMetadata>());
        MetadataControlCatalog failure = new((_, _, _, _, _) => throw new IOException("Testfehler"));
        using ToolboxPanelViewModel completed = CreateModel(success);
        using ToolboxPanelViewModel failed = CreateModel(failure);
        VirtualClock clock = new();
        completed.StatusDismissalScheduler = clock;
        failed.StatusDismissalScheduler = clock;
        Assert.Equal(TimeSpan.FromSeconds(10), completed.LoadStatusDismissDelay);
        await completed.ReloadAsync();
        await failed.ReloadAsync();
        Assert.True(completed.IsLoadStatusVisible);
        Assert.True(failed.IsLoadStatusVisible);
        Assert.False(await failed.DismissLoadStatusCommand.CanExecute.FirstAsync());
        clock.AdvanceBy(completed.LoadStatusDismissDelay - TimeSpan.FromSeconds(1));
        Assert.True(completed.IsLoadStatusVisible);
        clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.False(completed.IsLoadStatusVisible);
        Assert.True(failed.IsLoadStatusVisible);
        Assert.True(failed.HasLoadError);
    }

    [AvaloniaFact]
    public async Task AnOlderReloadThatFinishesLastDoesNotReplaceTheNewerCatalog()
    {
        ControllableControlCatalog catalog = new();
        using ToolboxPanelViewModel model = CreateModel(catalog);
        Task older = model.ReloadAsync();
        ControllableControlCatalog.Request olderRequest = await catalog.WaitForRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
        Task newer = model.ReloadAsync();
        ControllableControlCatalog.Request newerRequest = await catalog.WaitForRequestAsync(1).WaitAsync(TimeSpan.FromSeconds(10));

        newerRequest.Complete(ControllableControlCatalog.StandardControl("NewerControl"));
        await newer.WaitAsync(TimeSpan.FromSeconds(10));
        olderRequest.Complete(ControllableControlCatalog.StandardControl("OlderControl"));
        await older.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("NewerControl", Assert.Single(model.Items).DisplayName);
        Assert.False(model.IsLoading);
        Assert.False(model.HasLoadError);
        Assert.Equal(string.Format(UiStrings.ControlsAvailable, 1), model.StatusText);
    }

    [AvaloniaFact]
    public async Task TheBaseCatalogLoadReportsStandardControlsInsteadOfProjectControls()
    {
        ControllableControlCatalog catalog = new();
        using ToolboxPanelViewModel model = CreateModel(catalog);
        Task loading = model.ReloadAsync(includeProjectControls: false);
        ControllableControlCatalog.Request request = await catalog.WaitForRequestAsync(0).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsLoading);
        Assert.False(model.IsLoadingProjectControls);
        Assert.Equal(UiStrings.LoadingStandardControls, model.StatusText);
        request.Complete(ControllableControlCatalog.StandardControl("Button"));
        await loading.WaitAsync(TimeSpan.FromSeconds(10));

        loading = model.ReloadAsync();
        request = await catalog.WaitForRequestAsync(1).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(model.IsLoadingProjectControls);
        Assert.Equal(UiStrings.LoadingProjectControls, model.StatusText);
        request.Complete(ControllableControlCatalog.StandardControl("Button"));
        await loading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(model.IsLoadingProjectControls);
    }

    [AvaloniaFact]
    public async Task FailurePreservesControlsAndOffersWorkingRetryAndCancelButtons()
    {
        bool fail = false;
        string errorMessage = "The file Controls.dll cannot be read." + Environment.NewLine + new string('x', 900);
        MetadataControlCatalog catalog = new((paths, reportFailure, progress, token, includeProjectControls) =>
        {
            if (fail) throw new IOException(errorMessage);
            return TypeMetadataService.ReadControlTypes(paths, reportFailure, progress, token, includeProjectControls);
        });
        catalog.SetWorkspaceAssemblies(new[] { typeof(CaptionControl).Assembly.Location });
        using ToolboxPanelViewModel model = CreateModel(catalog);
        ToolboxPanelView view = new() { DataContext = model };
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window window = new() { Content = view, Width = 360, Height = 700 };
        try
        {
            window.Show();
            await model.ReloadAsync();
            ToolboxPanelItem[] original = model.Items.ToArray();
            Assert.Contains(original, item => item.FullName == typeof(CaptionControl).FullName);
            Flush(window);
            Border task = view.FindControl<Border>("ControlLoadTask")!;
            Assert.True(task.IsEffectivelyVisible);
            double taskHeight = task.Bounds.Height;
            Assert.True(taskHeight > 0);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Avalonia.Controls.Button>(),
                button => Equals(button.Content, UiStrings.LoadProjectControls));
            Avalonia.Controls.Button dismiss = view.FindControl<Avalonia.Controls.Button>("DismissControlLoad")!;
            Assert.True(dismiss.IsEffectivelyVisible && dismiss.IsEffectivelyEnabled);
            dismiss.Command!.Execute(dismiss.CommandParameter);
            Flush(window);
            Assert.False(view.FindControl<Border>("ControlLoadTask")!.IsEffectivelyVisible);
            fail = true;
            await model.ReloadAsync();
            Flush(window);
            Assert.True(model.HasLoadError);
            // The error state keeps the height of the task, so the controls below do not move.
            Assert.Equal(taskHeight, task.Bounds.Height);
            Assert.Equal(original, model.Items);
            TextBlock status = view.FindControl<TextBlock>("ControlLoadStatus")!;
            Avalonia.Controls.Button retry = view.FindControl<Avalonia.Controls.Button>("RetryControlLoad")!;
            Avalonia.Controls.Button cancel = view.FindControl<Avalonia.Controls.Button>("CancelControlLoad")!;
            Assert.Equal(UiStrings.ToolboxFileReadFailed, status.Text);
            Assert.Equal(2, status.MaxLines);
            Assert.True(status.IsEffectivelyVisible);
            Assert.True(retry.IsEffectivelyVisible && retry.IsEffectivelyEnabled);
            Assert.True(cancel.IsEffectivelyVisible && cancel.IsEffectivelyEnabled);
            Assert.Same(model.RetryLoadCommand, retry.Command);
            Assert.Same(model.CancelLoadCommand, cancel.Command);
            Assert.True(view.FindControl<Border>("ControlLoadTask")!.Bounds.Bottom <
                view.FindControl<ScrollViewer>("AssemblyGroupsViewport")!.TranslatePoint(default, view)!.Value.Y);

            Avalonia.Controls.Button details = view.FindControl<Avalonia.Controls.Button>("ControlLoadDetails")!;
            Assert.True(details.IsEffectivelyVisible);
            details.Flyout!.ShowAt(details);
            Flush(window);
            TextBox errorText = view.FindControl<TextBox>("ControlLoadErrorText")!;
            Assert.True(errorText.IsEffectivelyVisible);
            Assert.True(errorText.IsReadOnly);
            Assert.Contains(errorMessage, errorText.Text);
            Assert.True(errorText.Bounds.Width > view.Bounds.Width);
            Assert.InRange(errorText.Bounds.Height, 1, 320);
            details.Flyout.Hide();

            fail = false;
            await model.RetryLoadCommand.Execute().ToTask();
            Flush(window);
            Assert.False(model.HasLoadError);
            Assert.False(retry.IsEffectivelyVisible);
            Assert.Contains(model.Items, item => item.FullName == typeof(CaptionControl).FullName);

            fail = true;
            await model.ReloadAsync();
            cancel.Command!.Execute(cancel.CommandParameter);
            Flush(window);
            Assert.False(model.HasLoadTask);
            Assert.Equal(UiStrings.ControlDiscoveryCanceled, model.StatusText);
            Assert.False(retry.IsEffectivelyVisible);
            Assert.Contains(model.Items, item => item.FullName == typeof(CaptionControl).FullName);
        }
        finally
        {
            window.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    [AvaloniaFact]
    public async Task CancelShowsPendingStateUntilWorkerStopsAndIgnoresLateResults()
    {
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim finish = new();
        MetadataControlCatalog catalog = new((paths, reportFailure, progress, token, _) =>
        {
            progress(1, 3, "Controls.dll");
            started.SetResult();
            token.WaitHandle.WaitOne();
            cancellationObserved.SetResult();
            finish.Wait();
            token.ThrowIfCancellationRequested();
            return Array.Empty<XamlVisualEditor.Core.TypeMetadata>();
        });
        using ToolboxPanelViewModel model = CreateModel(catalog);
        ToolboxPanelView view = new() { DataContext = model };
        Window window = new() { Content = view, Width = 360, Height = 700 };
        Task loading = model.ReloadAsync();
        try
        {
            window.Show();
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Flush(window);
            Assert.True(model.IsLoading);
            Assert.Contains("Controls.dll", model.StatusText);
            Assert.InRange(model.LoadProgress, 33, 34);
            Assert.False(model.IsProgressIndeterminate);
            Assert.True(view.FindControl<ProgressBar>("ControlLoadProgress")!.IsEffectivelyVisible);
            Avalonia.Controls.Button cancel = view.FindControl<Avalonia.Controls.Button>("CancelControlLoad")!;
            Assert.True(cancel.IsEffectivelyEnabled);
            cancel.Command!.Execute(cancel.CommandParameter);
            await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Flush(window);
            Assert.True(model.IsLoading);
            Assert.True(model.IsCancelling);
            Assert.Equal(UiStrings.ControlDiscoveryCancelling, model.StatusText);
            Assert.False(cancel.IsEffectivelyEnabled);
            finish.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(model.IsLoading);
            Assert.False(model.IsCancelling);
            Assert.False(model.HasLoadError);
            Assert.Equal(UiStrings.ControlDiscoveryCanceled, model.StatusText);
            Assert.Empty(model.Items);
        }
        finally
        {
            if (model.IsLoading) await model.CancelLoadCommand.Execute().ToTask();
            finish.Set();
            await loading.WaitAsync(TimeSpan.FromSeconds(10));
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task WorkspaceAssembliesLoadAutomaticallyAfterTheBaseCatalog()
    {
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        string libraryPath = typeof(CaptionControl).Assembly.Location;
        using ToolboxPanelViewModel model = CreateModel(catalog);
        await model.ReloadAsync(includeProjectControls: false);
        Assert.NotEmpty(model.Items);
        Assert.All(model.Items, item => Assert.True(item.IsStandardControl));
        Task loaded = model.WhenAnyValue(item => item.IsLoading).Skip(1).Where(loading => !loading)
            .Take(1).Timeout(TimeSpan.FromSeconds(15)).ToTask();
        catalog.SetWorkspaceAssemblies(new[] { libraryPath });
        await loaded;
        Assert.Contains(model.Items, item => item.FullName == typeof(CaptionControl).FullName);
    }

    private static ToolboxPanelViewModel CreateModel(IControlCatalog catalog) => new(
        new CommandRegistry(), new InMemorySettingsStore(), catalog,
        new ReactiveUI.Primitives.Concurrency.SynchronizationContextSequencer(SynchronizationContext.Current!));

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
