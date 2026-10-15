using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Dock.Avalonia.Controls;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class DockViewReleaseTests
{
    [AvaloniaFact]
    public async Task Saving_the_layout_after_releasing_the_dock_views_logs_no_binding_errors()
    {
        IStyle[] styles = [new Avalonia.Themes.Fluent.FluentTheme(), new Dock.Avalonia.Themes.Fluent.DockFluentTheme()];
        Application.Current!.Styles.AddRange(styles);
        ILogSink? previousSink = Logger.Sink;
        BindingErrorSink bindingErrors = new();
        Logger.Sink = bindingErrors;
        string layoutPath = Path.Combine(Path.GetTempPath(), "xve-dock-release-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            // Without the release, the owners the layout save detaches break the bindings of every visible tab.
            Assert.NotEmpty(await SaveLayoutWhileShownAsync(bindingErrors, layoutPath, release: false));
            Assert.Empty(await SaveLayoutWhileShownAsync(bindingErrors, layoutPath, release: true));
            Assert.True(File.Exists(layoutPath));
        }
        finally
        {
            Logger.Sink = previousSink;
            foreach (IStyle style in styles)
            {
                Application.Current.Styles.Remove(style);
            }

            File.Delete(layoutPath);
        }
    }

    private static async Task<List<string>> SaveLayoutWhileShownAsync(BindingErrorSink bindingErrors, string layoutPath, bool release)
    {
        using MainWindowViewModel viewModel = new();
        DockControl dockControl = new() { Layout = viewModel.DockLayout, Factory = viewModel.DockFactory };
        Window window = new() { Width = 1000, Height = 700, Content = dockControl };
        try
        {
            window.Show();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            window.UpdateLayout();
            if (release)
            {
                DockViewRelease.Release(window);
            }

            bindingErrors.Errors.Clear();
            viewModel.DockFactory.SaveLayout(viewModel.DockLayout!, layoutPath);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            return [.. bindingErrors.Errors];
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class BindingErrorSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (area == LogArea.Binding)
            {
                Errors.Add(string.Join(", ", propertyValues) + " (" + source + ")");
            }
        }
    }
}
