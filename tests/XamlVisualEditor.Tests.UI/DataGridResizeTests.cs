using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
            // also installs process-wide class handlers that would affect the windows of later tests.
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

    [AvaloniaFact]
    public void DraggingAStarColumnEdgeWidensTheGridInsteadOfSqueezingTheNextColumns()
    {
        XamlVisualEditor.App.Services.DataGridColumnSizing.Initialize();
        IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://XamlVisualEditor.App/"))
                { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") },
        ];
        Application.Current!.Styles.AddRange(styles);
        Window? window = null;
        try
        {
            DataGrid grid = new()
            {
                AutoGenerateColumns = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                CanUserResizeColumns = true,
                ItemsSource = new[] { "A message" }
            };
            foreach ((string header, DataGridLength width, double minWidth) in new[]
            {
                ("Message", new DataGridLength(1, DataGridLengthUnitType.Star), 100d),
                ("Severity", new DataGridLength(120), 20d),
                ("Source", new DataGridLength(90), 20d)
            })
            {
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = header,
                    Width = width,
                    MinWidth = minWidth,
                    CellTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock { Text = value })
                });
            }
            window = new Window { Content = grid, Width = 600, Height = 300 };
            window.Show();
            window.UpdateLayout();

            DataGridColumnHeader messageHeader = grid.GetVisualDescendants().OfType<DataGridColumnHeader>()
                .Single(header => Equals(header.Content, "Message"));
            Point edge = messageHeader.TranslatePoint(new Point(messageHeader.Bounds.Width - 1, messageHeader.Bounds.Height / 2), window)!.Value;
            Point target = new(window.Bounds.Width - 5, edge.Y);
            window.MouseDown(edge, MouseButton.Left);
            for (int step = 1; step <= 10; step++)
                window.MouseMove(new Point(edge.X + (target.X - edge.X) * step / 10, edge.Y), RawInputModifiers.LeftMouseButton);
            window.MouseUp(target, MouseButton.Left);
            window.UpdateLayout();

            Assert.Equal(120, grid.Columns[1].ActualWidth, 3);
            Assert.Equal(90, grid.Columns[2].ActualWidth, 3);
            Assert.True(grid.Columns.Sum(column => column.ActualWidth) > window.Bounds.Width,
                $"Columns end at {grid.Columns.Sum(column => column.ActualWidth)} within a {window.Bounds.Width} wide window.");
        }
        finally
        {
            window?.Close();
            foreach (IStyle style in styles)
                Application.Current!.Styles.Remove(style);
        }
    }

    [AvaloniaFact]
    public void StarColumnsOfAGridInTheDesignSurfaceAreNotFrozen()
    {
        IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://XamlVisualEditor.App/"))
                { Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.v2.xaml") },
        ];
        Application.Current!.Styles.AddRange(styles);
        Window? window = null;
        try
        {
            static DataGrid CreateGrid()
            {
                DataGrid grid = new() { AutoGenerateColumns = false, ItemsSource = new[] { "A" }, Height = 100 };
                grid.Columns.Add(new DataGridTemplateColumn
                {
                    Header = "Name",
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    CellTemplate = new FuncDataTemplate<string>((value, _) => new TextBlock { Text = value })
                });
                return grid;
            }

            DataGrid editorGrid = CreateGrid();
            DataGrid designedGrid = CreateGrid();
            XamlVisualEditor.App.Views.DesignSurfaceView surface = new() { Height = 300 };
            Panel canvas = surface.FindControl<Panel>("DesignCanvas")!;
            canvas.Children.Add(designedGrid);
            window = new Window { Content = new StackPanel { Children = { editorGrid, surface } }, Width = 600, Height = 500 };
            window.Show();
            window.UpdateLayout();
            Assert.NotNull(designedGrid.FindAncestorOfType<XamlVisualEditor.App.Views.DesignSurfaceView>());

            XamlVisualEditor.App.Services.DataGridColumnSizing.FreezeStarColumns(editorGrid);
            XamlVisualEditor.App.Services.DataGridColumnSizing.FreezeStarColumns(designedGrid);

            Assert.False(editorGrid.Columns[0].Width.IsStar);
            Assert.True(designedGrid.Columns[0].Width.IsStar);
        }
        finally
        {
            window?.Close();
            foreach (IStyle style in styles)
                Application.Current!.Styles.Remove(style);
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
