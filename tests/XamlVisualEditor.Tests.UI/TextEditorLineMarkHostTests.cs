using System.Collections.ObjectModel;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.CodeEditor;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Sync;
using XamlVisualEditor.Xaml.Intellisense;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TextEditorLineMarkHostTests
{
    private static readonly string FilePath = Path.Combine(Path.GetTempPath(), "line-marks.txt");

    [AvaloniaFact]
    public async Task MarginFollowsTheLineNumbersAlsoWhenTheyAreSwitchedOffAndOn()
    {
        using Fixture fixture = await Fixture.CreateAsync(new TextFileView { DataContext = CreateTextDocument() });
        TextArea area = fixture.Editor.TextArea;

        AssertBehindLineNumbers(area);
        Assert.Equal(FilePath, fixture.Margin.FilePath);

        fixture.Editor.ShowLineNumbers = false;
        Assert.DoesNotContain(area.LeftMargins, margin => margin is LineNumberMargin);
        fixture.Editor.ShowLineNumbers = true;
        AssertBehindLineNumbers(area);
    }

    [AvaloniaFact]
    public async Task CodeEditorShowsTheMarksOfItsFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "line-marks.axaml");
        using CodeEditorViewModel document = new(
            path,
            new SyncEngine(new XamlParsingService(), new XamlSerializationService()),
            CompletionProviderRegistry.CreateDefault());
        document.Document.Text = "<Grid>\n  <Button />\n</Grid>\n";
        XamlCodeEditorView view = new() { DataContext = document };
        using Fixture fixture = await Fixture.CreateAsync(view);
        using ITextEditorLineMarkLayer layer = fixture.Host.CreateLineMarkLayer()!;

        layer.Set(path, [new TextEditorLineMark(2, 1, TextEditorLineMarkKind.Modified)]);
        await Flush();

        AssertBehindLineNumbers(fixture.Editor.TextArea);
        (Rect bounds, TextEditorLineMarkKind kind) = Assert.Single(fixture.Margin.GetMarkBounds());
        Assert.Equal(TextEditorLineMarkKind.Modified, kind);
        Assert.Equal(Line(fixture.Editor, 2).VisualTop, bounds.Top);
        view.Dispose();
    }

    [AvaloniaFact]
    public async Task MarksOfTheVisibleLinesAreDrawnOnTheirLines()
    {
        using Fixture fixture = await Fixture.CreateAsync(new TextFileView { DataContext = CreateTextDocument() });
        using ITextEditorLineMarkLayer layer = fixture.Host.CreateLineMarkLayer()!;
        using ITextEditorLineMarkLayer later = fixture.Host.CreateLineMarkLayer()!;
        later.Set(FilePath, [new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Added)]);

        // The layer normalizes the path.
        layer.Set(Path.Combine(Path.GetTempPath(), ".", "line-marks.txt"),
        [
            new TextEditorLineMark(7, 1, TextEditorLineMarkKind.Modified),
            new TextEditorLineMark(2, 2, TextEditorLineMarkKind.Added),
            new TextEditorLineMark(5, 0, TextEditorLineMarkKind.Deleted),
            new TextEditorLineMark(500, 3, TextEditorLineMarkKind.Added)
        ]);
        await Flush();

        TextEditor editor = fixture.Editor;
        List<(Rect Bounds, TextEditorLineMarkKind Kind)> drawn = fixture.Margin.GetMarkBounds().ToList();
        Assert.Equal(
        [
            (Line(editor, 2).VisualTop, TextEditorLineMarkKind.Added),
            (Line(editor, 3).VisualTop, TextEditorLineMarkKind.Added),
            (Line(editor, 5).VisualTop - 2, TextEditorLineMarkKind.Deleted),
            (Line(editor, 7).VisualTop, TextEditorLineMarkKind.Modified)
        ],
            drawn.Select(mark => (mark.Bounds.Top, mark.Kind)));
        Assert.Equal(Line(editor, 2).Height, drawn[0].Bounds.Height);

        // The first layer wins; an empty list leaves the marks of the later layer.
        layer.Set(FilePath, []);
        await Flush();
        Assert.Equal(TextEditorLineMarkKind.Added, Assert.Single(fixture.Margin.GetMarkBounds()).Kind);
        later.Dispose();
        await Flush();
        Assert.Empty(fixture.Margin.GetMarkBounds());
    }

    [AvaloniaFact]
    public async Task RemovedLinesBelowTheLastLineAreDrawnAtItsBottom()
    {
        TextDocumentViewModel document = CreateTextDocument();
        document.Document.Text = "a\nb";
        using Fixture fixture = await Fixture.CreateAsync(new TextFileView { DataContext = document });
        using ITextEditorLineMarkLayer layer = fixture.Host.CreateLineMarkLayer()!;

        layer.Set(FilePath, [new TextEditorLineMark(3, 0, TextEditorLineMarkKind.Deleted)]);
        await Flush();

        VisualLine last = Line(fixture.Editor, 2);
        Assert.Equal(last.VisualTop + last.Height - 2, Assert.Single(fixture.Margin.GetMarkBounds()).Bounds.Top);
    }

    [AvaloniaFact]
    public async Task ClickOnAMarkRunsTheCommandOfItsLayerWithPathAndLine()
    {
        List<(string Command, IReadOnlyList<object?> Arguments)> executed = new();
        // As in the application, the document has breakpoints, so the breakpoint margin watches the gutter too.
        TextDocumentViewModel document = CreateTextDocument();
        BreakpointsViewModel breakpoints = new();
        document.Breakpoints = breakpoints;
        using Fixture fixture = await Fixture.CreateAsync(
            new TextFileView { DataContext = document },
            (command, arguments) => executed.Add((command, arguments)));
        using ITextEditorLineMarkLayer layer = fixture.Host.CreateLineMarkLayer("test.showChange")!;
        layer.Set(FilePath, [new TextEditorLineMark(4, 2, TextEditorLineMarkKind.Modified)]);
        await Flush();

        // The left edge of the margin lies beside the drawn bar. The breakpoint margin takes the clicks of the whole
        // gutter and leaves only those on the change marks.
        fixture.Click(Line(fixture.Editor, 3), 0.5);
        fixture.Click(Line(fixture.Editor, 5), 0.5);

        (string command, IReadOnlyList<object?> arguments) = Assert.Single(executed);
        Assert.Equal("test.showChange", command);
        Assert.Equal([FilePath, 5], arguments);
        Assert.Empty(breakpoints.Items);
    }

    [AvaloniaFact]
    public async Task ClickOnALineNumberTogglesOneBreakpoint()
    {
        TextDocumentViewModel document = CreateTextDocument();
        BreakpointsViewModel breakpoints = new();
        document.Breakpoints = breakpoints;
        using Fixture fixture = await Fixture.CreateAsync(new TextFileView { DataContext = document });
        LineNumberMargin lineNumbers = fixture.Editor.TextArea.LeftMargins.OfType<LineNumberMargin>().Single();

        fixture.Click(lineNumbers, Line(fixture.Editor, 3), lineNumbers.Bounds.Width - 4);
        Assert.Equal(3, Assert.Single(breakpoints.Items).Line);

        fixture.Click(lineNumbers, Line(fixture.Editor, 3), lineNumbers.Bounds.Width - 4);
        Assert.Empty(breakpoints.Items);
    }

    [AvaloniaFact]
    public async Task UnregisterRemovesTheMargin()
    {
        using Fixture fixture = await Fixture.CreateAsync(new TextFileView { DataContext = CreateTextDocument() });

        TextEditorFeaturesBehavior.SetIsEnabled(fixture.Editor, false);

        Assert.DoesNotContain(fixture.Editor.TextArea.LeftMargins, margin => margin is LineMarkMargin);
    }

    private static TextDocumentViewModel CreateTextDocument()
    {
        TextDocumentViewModel document = new(FilePath);
        document.Document.Text = string.Join("\n", Enumerable.Range(1, 12).Select(line => $"line {line}")) + "\n";
        return document;
    }

    private static void AssertBehindLineNumbers(TextArea area)
    {
        int lineNumbers = area.LeftMargins.IndexOf(area.LeftMargins.OfType<LineNumberMargin>().Single());
        Assert.True(DottedLineMargin.IsDottedLineMargin(area.LeftMargins[lineNumbers + 1]));
        Assert.IsType<LineMarkMargin>(area.LeftMargins[lineNumbers + 2]);
    }

    private static VisualLine Line(TextEditor editor, int lineNumber) =>
        editor.TextArea.TextView.VisualLines.Single(line => line.FirstDocumentLine.LineNumber == lineNumber);

    private static Task Flush() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private sealed class Fixture : IDisposable
    {
        private readonly IStyle[] _styles;
        private readonly Window _window;

        private Fixture(Control view, Action<string, IReadOnlyList<object?>>? executeCommand)
        {
            Uri baseUri = new("avares://XamlVisualEditor.App/");
            _styles =
            [
                new Avalonia.Themes.Fluent.FluentTheme(),
                new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") },
                new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") }
            ];
            Host = new TextEditorViewHost(
                new ObservableCollection<ExtensionMenuItemViewModel>(),
                new BehaviorSubject<object?>(null),
                executeCommand: executeCommand);
            Application.Current!.Styles.AddRange(_styles);
            Application.Current.Resources[TextEditorViewHost.RESOURCE_KEY] = Host;
            _window = new Window { Content = view, Width = 600, Height = 400 };
        }

        public TextEditorViewHost Host { get; }

        public TextEditor Editor { get; private set; } = null!;

        public LineMarkMargin Margin => Editor.TextArea.LeftMargins.OfType<LineMarkMargin>().Single();

        public static async Task<Fixture> CreateAsync(Control view, Action<string, IReadOnlyList<object?>>? executeCommand = null)
        {
            Fixture fixture = new(view, executeCommand);
            fixture._window.Show();
            await Flush();
            fixture.Editor = view.GetLogicalDescendants().OfType<TextEditor>().Single();
            return fixture;
        }

        public void Click(VisualLine line, double x) => Click(Margin, line, x);

        public void Click(Control target, VisualLine line, double x)
        {
            double y = line.VisualTop - Editor.TextArea.TextView.ScrollOffset.Y + (line.Height / 2);
            Point point = target.TranslatePoint(new Point(x, y), _window) ?? throw new InvalidOperationException();            _window.MouseDown(point, MouseButton.Left);
            _window.MouseUp(point, MouseButton.Left);
        }

        public void Dispose()
        {
            _window.Close();
            Host.Dispose();
            Application.Current!.Resources.Remove(TextEditorViewHost.RESOURCE_KEY);
            foreach (IStyle style in _styles)
            {
                Application.Current.Styles.Remove(style);
            }
        }
    }
}
