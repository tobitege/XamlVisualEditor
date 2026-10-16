using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions.TextEditing;

public sealed class CaretPositionMemoryTests
{
    [Fact]
    public async Task CaretReturnsToItsLineAndColumnWhenTheFileOpensAgain()
    {
        InMemoryExtensionStorage storage = new();
        FakeEditor editor = new();
        FakeDocument first = new(Path.GetFullPath("first.cs"), "line one\r\nline two\r\nline three");
        FakeDocument second = new(Path.GetFullPath("second.cs"), "other");

        using (CaretPositionMemory memory = new(editor, storage, new RecordingLogger()))
        {
            await memory.LoadAsync(CancellationToken.None);
            editor.Activate(first);
            first.CaretOffset = first.Text.IndexOf("two", StringComparison.Ordinal);
            editor.Activate(second);

            Assert.Equal(new CaretPosition(first.FilePath, 2, 6), memory.Positions[0]);
        }

        FakeEditor restartedEditor = new();
        FakeDocument reopened = new(first.FilePath, first.Text);
        using CaretPositionMemory restarted = new(restartedEditor, storage, new RecordingLogger());
        await restarted.LoadAsync(CancellationToken.None);
        restartedEditor.Activate(reopened);

        Assert.Equal(first.Text.IndexOf("two", StringComparison.Ordinal), reopened.CaretOffset);
    }

    [Fact]
    public async Task DisposeKeepsTheCaretOfTheActiveDocument()
    {
        InMemoryExtensionStorage storage = new();
        FakeEditor editor = new();
        FakeDocument document = new(Path.GetFullPath("active.cs"), "a\nbc");
        using (CaretPositionMemory memory = new(editor, storage, new RecordingLogger()))
        {
            await memory.LoadAsync(CancellationToken.None);
            editor.Activate(document);
            document.CaretOffset = 3;
        }

        List<CaretPosition>? saved = await storage.GetAsync<List<CaretPosition>>("textEditing.caretPositions", CancellationToken.None);

        Assert.Equal(new CaretPosition(document.FilePath, 2, 2), Assert.Single(saved!));
    }

    [Fact]
    public async Task SwitchingDocumentsSavesTheCollectedPositionsOnceAfterTheDelay()
    {
        VirtualClock clock = new();
        CountingStorage storage = new();
        FakeEditor editor = new();
        FakeDocument first = new(Path.GetFullPath("one.cs"), "a\nb");
        FakeDocument second = new(Path.GetFullPath("two.cs"), "c");
        using CaretPositionMemory memory = new(editor, storage, new RecordingLogger(), TimeSpan.FromSeconds(2), clock);
        await memory.LoadAsync(CancellationToken.None);

        editor.Activate(first);
        first.CaretOffset = 2;
        editor.Activate(second);
        editor.Activate(first);
        clock.AdvanceBy(TimeSpan.FromSeconds(1));
        Assert.False(memory.FlushAsync().IsCompleted);
        Assert.Equal(0, storage.Writes);

        clock.AdvanceBy(TimeSpan.FromSeconds(1));
        await memory.FlushAsync();

        Assert.Equal(1, storage.Writes);
        List<CaretPosition>? saved = await storage.GetAsync<List<CaretPosition>>("textEditing.caretPositions", CancellationToken.None);
        Assert.Equal(new[] { second.FilePath, first.FilePath }, saved!.Select(position => position.FilePath));
    }

    [Fact]
    public async Task LoadingDocumentGetsItsCaretWhenTheTextArrives()
    {
        InMemoryExtensionStorage storage = new();
        string path = Path.GetFullPath("loading.axaml");
        await storage.SetAsync("textEditing.caretPositions", new List<CaretPosition> { new(path, 3, 2) }, CancellationToken.None);
        FakeEditor editor = new();
        FakeDocument document = new(path, string.Empty);
        using CaretPositionMemory memory = new(editor, storage, new RecordingLogger());
        await memory.LoadAsync(CancellationToken.None);

        editor.Activate(document);
        Assert.Equal(0, document.CaretOffset);
        document.SetText("a\nb\nshort");

        Assert.Equal(5, document.CaretOffset);
    }

    [Fact]
    public async Task FailingDocumentIsLoggedAndTheNextDocumentStillGetsItsCaret()
    {
        InMemoryExtensionStorage storage = new();
        string nextPath = Path.GetFullPath("next.cs");
        await storage.SetAsync("textEditing.caretPositions", new List<CaretPosition> { new(nextPath, 2, 3) }, CancellationToken.None);
        RecordingLogger logger = new();
        FakeEditor editor = new();
        FakeDocument broken = new(Path.GetFullPath("broken.cs"), "text") { Failure = new InvalidOperationException("gone") };
        FakeDocument next = new(nextPath, "one\ntwo");
        using CaretPositionMemory memory = new(editor, storage, logger);
        await memory.LoadAsync(CancellationToken.None);

        editor.Activate(broken);
        editor.Activate(next);

        Assert.Contains(logger.Errors, error => error is InvalidOperationException { Message: "gone" });
        Assert.Equal(6, next.CaretOffset);
    }

    private sealed class CountingStorage : IExtensionStorage
    {
        private readonly InMemoryExtensionStorage _inner = new();

        public int Writes { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => _inner.GetAsync<T>(key, cancellationToken);

        public Task SetAsync<T>(string key, T value, CancellationToken cancellationToken)
        {
            Writes++;
            return _inner.SetAsync(key, value, cancellationToken);
        }

        public Task RemoveAsync(string key, CancellationToken cancellationToken) => _inner.RemoveAsync(key, cancellationToken);
    }

    private sealed class RecordingLogger : IExtensionLogger
    {
        public List<Exception?> Errors { get; } = new();

        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null) => Errors.Add(exception);
    }

    private sealed class FakeEditor : IEditorServices
    {
        public IEditorDocument? ActiveDocument { get; private set; }

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

        public void Activate(IEditorDocument document)
        {
            ActiveDocument = document;
            ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = document });
        }

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => ActiveDocument is null ? [] : [ActiveDocument];

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) =>
            Task.FromResult<IEditorDocument?>(null);

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class FakeDocument : IEditorDocument
    {
        public FakeDocument(string filePath, string text)
        {
            FilePath = filePath;
            Text = text;
        }

        public string FilePath { get; }

        public string? LanguageId => null;

        public string Text { get; private set; }

        public Exception? Failure { get; init; }

        public int CaretOffset { get; set; }

        public int SelectionStart { get; set; }

        public int SelectionLength { get; set; }

        public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

        public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;

        public void SetText(string text)
        {
            Text = text;
            Changed?.Invoke(this, new EditorDocumentChangedEventArgs { FilePath = FilePath });
        }

        public Task<string> GetTextAsync(CancellationToken ct) => Failure is null ? Task.FromResult(Text) : Task.FromException<string>(Failure);

        public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct)
        {
            SelectionChanged?.Invoke(this, new EditorSelectionChangedEventArgs { FilePath = FilePath, SelectionStart = 0, SelectionLength = 0 });
            return Task.CompletedTask;
        }
    }
}
