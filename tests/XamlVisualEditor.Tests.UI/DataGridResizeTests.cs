using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;
using System.Globalization;

namespace XamlVisualEditor.Tests.UI;

public sealed class DataGridResizeTests
{
    [AvaloniaTheory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 80)]
    [InlineData(true, 80)]
    public async Task EditorGridWithStarColumnsSettlesAfterWindowResize(bool autoHideScrollBars, int rowCount)
    {
        CultureInfo previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo? previousDefaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        CultureInfo? previousUiCulture = XamlVisualEditor.Core.Localization.UiStrings.Culture;
        IStyle[] styles = [];
        ILogSink? previousSink = Logger.Sink;
        LayoutLogSink layoutLog = new();
        Logger.Sink = layoutLog;
        Window? window = null;
        try
        {
            // Mirrors Application.Styles from App.axaml. App.Initialize() is not used here because it
            // also installs the process-wide DialogAppearance handler, which would re-wrap every owned
            // dialog opened by later tests.
            Uri baseUri = new("avares://XamlVisualEditor.App/");
            styles =
            [
                new Avalonia.Themes.Fluent.FluentTheme(),
                new Dock.Avalonia.Themes.Fluent.DockFluentTheme(),
                new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") },
                new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri) { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") },
                new Avalonia.Markup.Xaml.Styling.StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorDensity.axaml") },
            ];
            Application.Current!.Styles.AddRange(styles);
            string[] items = Enumerable.Range(0, rowCount)
                .Select(index => $"Property {index}: a value that wraps in a narrow column").ToArray();
            DataGrid grid = new()
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                ItemsSource = items
            };
            grid.Classes.Add("editor-chrome");
            ScrollViewer.SetAllowAutoHide(grid, autoHideScrollBars);
            foreach (int weight in new[] { 1, 2 })
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = weight == 1 ? "Name" : "Value",
                    Width = new DataGridLength(weight, DataGridLengthUnitType.Star),
                    MinWidth = 90,
                    CellTemplate = new FuncDataTemplate<string>((value, _) =>
                        new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap })
                });
            }

            Grid editorLayout = new() { RowDefinitions = new RowDefinitions("48,*") };
            Grid.SetRow(grid, 1);
            editorLayout.Children.Add(grid);
            window = new Window { Content = editorLayout, Width = 600, Height = 500 };
            layoutLog.Phase = "Show";
            window.Show();
            foreach (Size size in new[] { new Size(600, 500), new Size(317, 271), new Size(823, 643), new Size(190, 359), new Size(600, 500) })
            {
                layoutLog.Phase = size.ToString();
                window.Width = size.Width;
                window.Height = size.Height;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                if (rowCount > 0)
                    Assert.NotEmpty(grid.GetVisualDescendants().OfType<DataGridRow>());
                Assert.True(grid.IsMeasureValid && grid.IsArrangeValid);
            }

            if (rowCount > 0)
            {
                layoutLog.Phase = "ScrollIntoView";
                grid.ScrollIntoView(items[^1], grid.Columns[1]);
                window.UpdateLayout();
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Assert.Contains(grid.GetVisualDescendants().OfType<DataGridRow>(), row => Equals(row.DataContext, items[^1]));
            }

            Assert.True(layoutLog.Cycles.Count == 0,
                string.Join(Environment.NewLine, layoutLog.Cycles));
        }
        finally
        {
            window?.Close();
            Logger.Sink = previousSink;
            foreach (IStyle style in styles)
                Application.Current!.Styles.Remove(style);
            CultureInfo.CurrentUICulture = previousCulture;
            CultureInfo.DefaultThreadCurrentUICulture = previousDefaultCulture;
            XamlVisualEditor.Core.Localization.UiStrings.Culture = previousUiCulture;
        }
    }

    private sealed class LayoutLogSink : ILogSink
    {
        public List<string> Cycles { get; } = [];
        public string Phase { get; set; } = string.Empty;

        public bool IsEnabled(LogEventLevel level, string area) => area == LogArea.Layout;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
            => Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (area == LogArea.Layout && messageTemplate.Contains("Layout cycle", StringComparison.Ordinal))
            {
                Cycles.Add(Phase + ": " + string.Join(", ", propertyValues));
            }
        }
    }
}
