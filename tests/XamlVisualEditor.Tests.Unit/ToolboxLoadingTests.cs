using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.TestControls;
using XamlVisualEditor.ToolboxExtension;
using XamlVisualEditor.ToolboxExtension.Views;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class ToolboxLoadingTests
{
    [AvaloniaFact]
    public async Task CompletedTaskHidesAfterTenSecondsWhileFailedTaskStaysVisible()
    {
        MetadataControlCatalog success = new((_, _, _, _) => Array.Empty<XamlVisualEditor.Core.TypeMetadata>());
        MetadataControlCatalog failure = new((_, _, _, _) => throw new IOException("Testfehler"));
        using ToolboxPanelViewModel completed = CreateModel(success);
        using ToolboxPanelViewModel failed = CreateModel(failure);
        await completed.ReloadAsync();
        await failed.ReloadAsync();
        Assert.True(completed.IsLoadStatusVisible);
        Assert.True(failed.IsLoadStatusVisible);
        Assert.False(await failed.DismissLoadStatusCommand.CanExecute.FirstAsync());
        await completed.WhenAnyValue(model => model.IsLoadStatusVisible).Where(visible => !visible)
            .FirstAsync().Timeout(TimeSpan.FromSeconds(12)).ToTask();
        Assert.True(failed.IsLoadStatusVisible);
        Assert.True(failed.HasLoadError);
    }

    [Fact]
    public async Task UnusableAssembliesAreSkippedWhileOtherControlsAreLoaded()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-unusable-" + Guid.NewGuid().ToString("N") + ".dll");
        await File.WriteAllBytesAsync(path, new byte[] { 0x4d, 0x5a, 0, 0 });
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        catalog.SetWorkspaceAssemblies(new[] { path, typeof(CaptionControl).Assembly.Location });
        ControlCatalogSnapshot result = await catalog.GetControlsAsync(CancellationToken.None);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Controls, item => item.FullName == typeof(CaptionControl).FullName);
    }

    [AvaloniaFact]
    public async Task FailurePreservesControlsAndOffersWorkingRetryAndCancelButtons()
    {
        bool fail = false;
        string errorMessage = "Die Datei Controls.dll ist nicht lesbar." + Environment.NewLine + new string('x', 900);
        MetadataControlCatalog catalog = new((paths, reportFailure, progress, token) =>
        {
            if (fail) throw new IOException(errorMessage);
            return TypeMetadataService.ReadControlTypes(paths, reportFailure, progress, token);
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
            Avalonia.Controls.Button dismiss = view.FindControl<Avalonia.Controls.Button>("DismissControlLoad")!;
            Assert.True(dismiss.IsEffectivelyVisible && dismiss.IsEffectivelyEnabled);
            dismiss.Command!.Execute(dismiss.CommandParameter);
            Flush(window);
            Assert.False(view.FindControl<Border>("ControlLoadTask")!.IsEffectivelyVisible);
            fail = true;
            await model.ReloadAsync();
            Flush(window);
            Assert.True(model.HasLoadError);
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
        MetadataControlCatalog catalog = new((paths, reportFailure, progress, token) =>
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
    public async Task DisabledProjectSearchKeepsStandardControlsAndCanBeStartedLater()
    {
        MetadataControlCatalog catalog = new(TypeMetadataService.ReadControlTypes);
        string libraryPath = typeof(CaptionControl).Assembly.Location;
        catalog.SetWorkspaceAssemblies(new[] { libraryPath }, autoLoadProjectControls: false);
        using ToolboxPanelViewModel model = CreateModel(catalog);
        await model.ReloadAsync(includeProjectControls: catalog.AutoLoadProjectControls);
        Assert.NotEmpty(model.Items);
        Assert.All(model.Items, item => Assert.True(item.IsStandardControl));
        Assert.Equal(UiStrings.ProjectControlsDeferred, model.StatusText);
        await model.ReloadCommand.Execute().ToTask();
        Assert.Contains(model.Items, item => item.FullName == typeof(CaptionControl).FullName);

        using CancellationTokenSource cancellation = new();
        List<string> visited = new();
        Assert.Throws<OperationCanceledException>(() => TypeMetadataService.ReadControlTypes(
            new[] { libraryPath }, _ => { }, (_, _, assembly) =>
            {
                visited.Add(assembly);
                cancellation.Cancel();
            }, cancellation.Token));
        Assert.Single(visited);
    }

    [AvaloniaFact]
    public async Task ProjectDialogReturnsCheckboxChoiceAndCancelDoesNotOpenAnything()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-open-option-" + Guid.NewGuid().ToString("N") + ".slnx");
        await File.WriteAllTextAsync(path, "<Solution />");
        using WorkspaceOpenDialogViewModel model = new();
        WorkspaceOpenRequest? result = null;
        model.CloseRequested += request => result = request;
        WorkspaceOpenDialog dialog = new() { DataContext = model };
        try
        {
            dialog.Show();
            dialog.FindControl<TextBox>("WorkspacePath")!.Text = path;
            dialog.FindControl<CheckBox>("LoadProjectControls")!.IsChecked = false;
            Flush(dialog);
            await model.OpenCommand.Execute().ToTask();
            Assert.Equal(new WorkspaceOpenRequest(path, false), result);
            await model.CancelCommand.Execute().ToTask();
            Assert.Null(result);
        }
        finally { dialog.Close(); }
    }

    private static ToolboxPanelViewModel CreateModel(IControlCatalog catalog) => new(
        new CommandRegistry(), new InMemorySettingsStore(), catalog,
        new SynchronizationContextScheduler(SynchronizationContext.Current!));

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
