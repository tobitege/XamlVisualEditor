using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Threading;
using AvaloniaEdit;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class TypingLatencyWithChangeMarkersTests
{
    private const int KEYSTROKES = 500;
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Typing feeds only the quiet time of the change marks; the comparison runs after it, off the UI thread. 500
    /// keystrokes in a 10,000-line file, each with its layout and render pass, take about as long with marks as without.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_in_a_ten_thousand_line_file_costs_about_the_same_with_change_marks()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        string committed = string.Concat(Enumerable.Range(0, 10_000).Select(index => $"    <Element Index=\"{index}\" />\n"));
        repository.CommitFileIn(repository.Path, "big.xml", committed, "big");
        string buffer = committed.Replace("Index=\"3\"", "Index=\"three\"", StringComparison.Ordinal);

        TimeSpan withoutMarks = await MeasureAsync(repository, buffer, withMarks: false);
        TimeSpan withMarks = await MeasureAsync(repository, buffer, withMarks: true);

        double perKeyWithout = withoutMarks.TotalMilliseconds / KEYSTROKES;
        double perKeyWith = withMarks.TotalMilliseconds / KEYSTROKES;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{KEYSTROKES} keystrokes in 10,000 lines: {perKeyWithout:F2} ms per keystroke without marks, {perKeyWith:F2} ms with marks");
        // A headless keystroke with its layout and render pass of a 10,000-line editor takes tens of milliseconds also
        // without marks; the limit leaves room for slower machines, the comparison carries the measurement.
        Assert.True(perKeyWith < 150, $"{perKeyWith:F2} ms per keystroke with marks");
        Assert.True(perKeyWith < (perKeyWithout * 2) + 2, $"{perKeyWith:F2} ms with marks, {perKeyWithout:F2} ms without");
    }

    private static async Task<TimeSpan> MeasureAsync(GitTestRepository repository, string buffer, bool withMarks)
    {
        Uri baseUri = new("avares://XamlVisualEditor.App/");
        IStyle[] styles =
        [
            new Avalonia.Themes.Fluent.FluentTheme(),
            new StyleInclude(baseUri) { Source = new Uri("avares://AvaloniaEdit/Themes/Fluent/AvaloniaEdit.xaml") },
            new StyleInclude(baseUri) { Source = new Uri("avares://XamlVisualEditor.App/Styles/EditorTheme.axaml") }
        ];
        Application.Current!.Styles.AddRange(styles);
        using TextEditorViewHost host = new(new ObservableCollection<ExtensionMenuItemViewModel>(), new BehaviorSubject<object?>(null));
        Application.Current.Resources[TextEditorViewHost.RESOURCE_KEY] = host;
        using TextDocumentViewModel document = new(Path.Combine(repository.Path, "big.xml"));
        document.Document.Text = buffer;
        Window window = new() { Content = new TextFileView { DataContext = document }, Width = 800, Height = 600 };
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new TestWorkspaceInfo(repository.Path));
        GitChangeMarkers? markers = null;
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            TextEditor editor = window.GetLogicalDescendants().OfType<TextEditor>().Single();
            if (withMarks)
            {
                await session.RefreshAsync().WaitAsync(WaitLimit);
                SignalingHost signaling = new(host);
                TestEditorServices editorServices = new();
                editorServices.Open(new EditorDocument(document.FilePath, editor));
                markers = new GitChangeMarkers(session, editorServices, signaling);
                await signaling.FirstSet.WaitAsync(WaitLimit);
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Assert.NotEmpty(editor.TextArea.LeftMargins.OfType<LineMarkMargin>().Single().GetMarkBounds());
            }

            editor.TextArea.Focus();
            editor.TextArea.Caret.Offset = editor.Document.GetLineByNumber(4).EndOffset;
            Stopwatch stopwatch = Stopwatch.StartNew();
            for (int key = 0; key < KEYSTROKES; key++)
            {
                window.KeyTextInput("x");
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }

            TimeSpan elapsed = stopwatch.Elapsed;
            Assert.EndsWith(new string('x', KEYSTROKES), editor.Document.GetText(editor.Document.GetLineByNumber(4)));
            return elapsed;
        }
        finally
        {
            markers?.Dispose();
            window.Close();
            Application.Current.Resources.Remove(TextEditorViewHost.RESOURCE_KEY);
            foreach (IStyle style in styles)
            {
                Application.Current.Styles.Remove(style);
            }
        }
    }

    // The open document of the editor, as the editor services of the application provide it.
    private sealed class EditorDocument : IEditorDocument
    {
        private readonly TextEditor _editor;

        public EditorDocument(string filePath, TextEditor editor)
        {
            FilePath = filePath;
            _editor = editor;
            _editor.Document.TextChanged += (_, _) => Changed?.Invoke(this, new EditorDocumentChangedEventArgs { FilePath = FilePath });
        }

        public string FilePath { get; }

        public string? LanguageId => null;

        public int CaretOffset { get; set; }

        public int SelectionStart { get; set; }

        public int SelectionLength { get; set; }

        public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

#pragma warning disable CS0067
        public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;
#pragma warning restore CS0067

        public Task<string> GetTextAsync(CancellationToken ct) => Dispatcher.UIThread.InvokeAsync(() => _editor.Document.Text).GetTask();

        public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct) => Task.CompletedTask;
    }

    // Reports the first marks the layer receives.
    private sealed class SignalingHost(ITextEditorViewHost inner) : ITextEditorViewHost
    {
        private readonly TaskCompletionSource _firstSet = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task FirstSet => _firstSet.Task;

        public TextEditorViewOptions Options => inner.Options;

        public bool HasActiveEditor => inner.HasActiveEditor;

        public event EventHandler? OptionsChanged
        {
            add => inner.OptionsChanged += value;
            remove => inner.OptionsChanged -= value;
        }

        public void SetOptions(TextEditorViewOptions options) => inner.SetOptions(options);

        public Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken) =>
            inner.ExecuteAsync(action, cancellationToken);

        public ITextEditorLineMarkLayer? CreateLineMarkLayer(string? clickCommandId = null) =>
            new SignalingLayer(inner.CreateLineMarkLayer(clickCommandId)!, _firstSet);

        private sealed class SignalingLayer(ITextEditorLineMarkLayer layer, TaskCompletionSource firstSet) : ITextEditorLineMarkLayer
        {
            public void Set(string filePath, IReadOnlyList<TextEditorLineMark> marks)
            {
                layer.Set(filePath, marks);
                if (marks.Count > 0)
                {
                    firstSet.TrySetResult();
                }
            }

            public void Dispose() => layer.Dispose();
        }
    }
}
