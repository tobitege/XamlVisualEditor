using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Avalonia.Themes.Fluent;
using Dock.Model.Controls;
using Dock.Model.ReactiveUI.Controls;
using XamlVisualEditor.Shell;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class DockBindingErrorTests
{
    [AvaloniaFact(Timeout = 30000)]
    public async Task ClosingPinningAndPreviewingToolsLogsNoBindingErrors()
    {
        System.Threading.CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new DockFluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorDensity.axaml") },
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") }
        ];
        Application.Current!.Styles.AddRange(styles);
        ILogSink? previousSink = Logger.Sink;
        BindingLogSink log = new();
        Logger.Sink = log;
        using MainWindowViewModel viewModel = new();
        IRootDock layout = viewModel.DockLayout!;
        XamlEditorDockFactory factory = viewModel.DockFactory;
        ToolDock left = XamlEditorDockFactory.FindDockable<ToolDock>(layout, "LeftToolDock")!;
        ToolDock right = XamlEditorDockFactory.FindDockable<ToolDock>(layout, "RightToolDock")!;
        ToolDock bottom = XamlEditorDockFactory.FindDockable<ToolDock>(layout, "BottomToolDock")!;
        DockControl dockControl = new() { Layout = layout, Factory = factory, Width = 1200, Height = 800 };
        Window window = new() { Content = dockControl, Width = 1200, Height = 800 };
        try
        {
            window.Show();
            await FlushAsync(window).WaitAsync(cancellationToken);

            log.Phase = "close the last tool of a dock";
            OutputTool closed = AddTool(viewModel, right, "BindingTestClosed");
            await FlushAsync(window).WaitAsync(cancellationToken);
            factory.CloseDockable(closed);
            await FlushAsync(window).WaitAsync(cancellationToken);

            log.Phase = "close one of two tools";
            OutputTool first = AddTool(viewModel, left, "BindingTestFirst");
            OutputTool second = AddTool(viewModel, left, "BindingTestSecond");
            await FlushAsync(window).WaitAsync(cancellationToken);
            factory.CloseDockable(second);
            await FlushAsync(window).WaitAsync(cancellationToken);
            Assert.Same(first, left.ActiveDockable);
            Assert.Equal("BindingTestFirst", ChromeTitle(window, left));

            log.Phase = "hide the last tool of a dock";
            factory.HideDockable(first);
            await FlushAsync(window).WaitAsync(cancellationToken);

            OutputTool output = XamlEditorDockFactory.FindDockable<OutputTool>(layout, "Output")!;
            if (!factory.IsDockablePinned(output, layout))
            {
                factory.PinDockable(output);
                await FlushAsync(window).WaitAsync(cancellationToken);
            }

            log.Phase = "preview a pinned tool and hide the preview";
            factory.PreviewPinnedDockable(output);
            await FlushAsync(window).WaitAsync(cancellationToken);
            factory.HidePreviewingDockables(layout);
            await FlushAsync(window).WaitAsync(cancellationToken);

            log.Phase = "preview a pinned tool and close it";
            factory.PreviewPinnedDockable(output);
            await FlushAsync(window).WaitAsync(cancellationToken);
            factory.CloseDockable(output);
            await FlushAsync(window).WaitAsync(cancellationToken);

            BreakpointsTool breakpoints = XamlEditorDockFactory.FindDockable<BreakpointsTool>(layout, "Breakpoints")!;
            if (factory.IsDockablePinned(breakpoints, layout))
            {
                factory.UnpinDockable(breakpoints);
                await FlushAsync(window).WaitAsync(cancellationToken);
            }

            factory.SetActiveDockable(breakpoints);
            await FlushAsync(window).WaitAsync(cancellationToken);
            Assert.Equal(breakpoints.Title, ChromeTitle(window, bottom));
            log.Phase = "collapse the bottom dock";
            factory.CollapseBottomToolDock(layout);
            await FlushAsync(window).WaitAsync(cancellationToken);

            log.Phase = "show a tool of the collapsed bottom dock again";
            factory.UnpinDockable(breakpoints);
            await FlushAsync(window).WaitAsync(cancellationToken);
            Assert.Same(breakpoints, bottom.ActiveDockable);
            Assert.Equal(breakpoints.Title, ChromeTitle(window, bottom));

            Assert.True(log.Errors.Count == 0, string.Join(Environment.NewLine, log.Errors));
        }
        finally
        {
            window.Close();
            Logger.Sink = previousSink;
            foreach (IStyle style in styles)
            {
                Application.Current.Styles.Remove(style);
            }
        }
    }

    private static OutputTool AddTool(MainWindowViewModel viewModel, ToolDock dock, string id)
    {
        OutputTool tool = new(viewModel.Output) { Id = id, Title = id };
        viewModel.DockFactory.AddDockable(dock, tool);
        viewModel.DockFactory.SetActiveDockable(tool);
        return tool;
    }

    private static string? ChromeTitle(Window window, ToolDock dock)
    {
        ToolChromeControl chrome = Assert.Single(window.GetVisualDescendants().OfType<ToolChromeControl>(), item => item.DataContext == dock);
        return chrome.GetVisualDescendants().OfType<TextBlock>().Single(item => item.Name == "PART_Title").Text;
    }

    private static async Task FlushAsync(Window window)
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        window.UpdateLayout();
    }

    private sealed class BindingLogSink : ILogSink
    {
        public List<string> Errors { get; } = [];

        public string Phase { get; set; } = "show";

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Binding && level >= LogEventLevel.Warning;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (IsEnabled(level, area))
            {
                Errors.Add($"{Phase}: {source?.GetType().Name} {string.Join(", ", propertyValues)}");
            }
        }
    }
}
