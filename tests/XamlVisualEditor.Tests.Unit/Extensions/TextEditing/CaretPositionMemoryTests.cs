using ReactiveUI.Primitives.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions.TextEditing;

public sealed class CaretPositionMemoryTests
{
    private const string POSITIONS_KEY = "textEditing.caretPositions";

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

    [Fact]
    public async Task DisposeAfterACanceledLoadKeepsTheSavedPositions()
    {
        CountingStorage storage = new();
        string savedPath = Path.GetFullPath("saved.cs");
        await storage.SetAsync(POSITIONS_KEY, new List<CaretPosition> { new(savedPath, 3, 2) }, CancellationToken.None);
        FakeEditor editor = new();
        FakeDocument document = new(Path.GetFullPath("current.cs"), "a\nb") { CaretOffset = 2 };
        editor.Activate(document);
        CaretPositionMemory memory = new(editor, storage, new RecordingLogger());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => memory.LoadAsync(new CancellationToken(canceled: true)));
        memory.Dispose();

        Assert.Equal(1, storage.Writes);
        List<CaretPosition>? saved = await storage.GetAsync<List<CaretPosition>>(POSITIONS_KEY, CancellationToken.None);
        Assert.Equal(new CaretPosition(savedPath, 3, 2), Assert.Single(saved!));
    }

    [Fact]
    public async Task FileOpenedAgainInTheSameSessionGetsItsCaretAgain()
    {
        FakeEditor editor = new();
        string path = Path.GetFullPath("reopened.cs");
        FakeDocument first = new(path, "a\nbcd");
        FakeDocument other = new(Path.GetFullPath("other.cs"), "x");
        using CaretPositionMemory memory = new(editor, new InMemoryExtensionStorage(), new RecordingLogger());
        await memory.LoadAsync(CancellationToken.None);
        editor.Activate(first);
        first.CaretOffset = 4;

        // Closing the file activates another one; opening it again creates a new document.
        editor.Activate(other);
        FakeDocument reopened = new(path, first.Text);
        editor.Activate(reopened);

        Assert.Equal(4, reopened.CaretOffset);
    }

    [Fact]
    public async Task LoadedPositionsKeepTheirOrderAndTheOldestIsEvictedBeyondTheLimit()
    {
        InMemoryExtensionStorage storage = new();
        List<CaretPosition> stored = Enumerable.Range(0, 200)
            .Select(index => new CaretPosition(Path.GetFullPath("file" + index + ".cs"), 1, 1))
            .ToList();
        await storage.SetAsync(POSITIONS_KEY, stored, CancellationToken.None);
        FakeEditor editor = new();
        FakeDocument used = new(stored[150].FilePath, "used");
        FakeDocument added = new(Path.GetFullPath("added.cs"), "new");
        FakeDocument last = new(Path.GetFullPath("last.cs"), "last");
        using CaretPositionMemory memory = new(editor, storage, new RecordingLogger(), TimeSpan.FromSeconds(2), new VirtualClock());
        await memory.LoadAsync(CancellationToken.None);
        Assert.Equal(stored.Select(position => position.FilePath), memory.Positions.Select(position => position.FilePath));

        editor.Activate(used);
        used.CaretOffset = 2;
        editor.Activate(added);
        IReadOnlyList<CaretPosition> afterUse = memory.Positions;

        Assert.Equal(200, afterUse.Count);
        Assert.Equal(new CaretPosition(used.FilePath, 1, 3), afterUse[0]);
        Assert.Equal(stored[0].FilePath, afterUse[1].FilePath);
        Assert.Equal(stored[199].FilePath, afterUse[^1].FilePath);

        editor.Activate(last);
        IReadOnlyList<CaretPosition> afterAdd = memory.Positions;

        Assert.Equal(200, afterAdd.Count);
        Assert.Equal(new[] { added.FilePath, used.FilePath, stored[0].FilePath }, afterAdd.Take(3).Select(position => position.FilePath));
        Assert.Equal(stored[198].FilePath, afterAdd[^1].FilePath);
        Assert.DoesNotContain(afterAdd, position => position.FilePath == stored[199].FilePath);
    }

    private sealed class CountingStorage : IExtensionStorage
    {
        private readonly InMemoryExtensionStorage _inner = new();

        public int Writes { get; private set; }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _inner.GetAsync<T>(key, cancellationToken);
        }

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
