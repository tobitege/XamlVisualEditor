using System.Diagnostics;
using System.Reactive.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitChangeMarkersTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private const string LINES = "one\ntwo\nthree\nfour\nfive\n";

    [Fact]
    public async Task Edits_are_marked_against_the_index_and_staging_restaging_committing_and_the_switch_update_the_marks()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "a.txt", LINES, "init");
        using Fixture fixture = await Fixture.CreateAsync(repository);
        FakeEditorDocument document = fixture.Open("a.txt", LINES);
        Assert.Empty(await fixture.ComputeAsync(document));

        document.Edit("one\nTWO\nthree\nfour\nfive\n");
        Assert.Equal([new TextEditorLineMark(2, 1, TextEditorLineMarkKind.Modified)], await fixture.ComputeAsync(document));
        document.Edit("one\ntwo\nthree\nnew\nfour\n");
        Assert.Equal(
            [new TextEditorLineMark(4, 1, TextEditorLineMarkKind.Added), new TextEditorLineMark(6, 0, TextEditorLineMarkKind.Deleted)],
            await fixture.ComputeAsync(document));

        // Staging the buffer clears the marks; staging another text again is seen through the index object id.
        await fixture.StageAsync(document);
        Assert.Empty(await fixture.ComputeAsync(document));
        document.Edit("one\nzwei\n");
        Assert.NotEmpty(await fixture.ComputeAsync(document));
        await fixture.StageAsync(document);
        Assert.Empty(await fixture.ComputeAsync(document));

        repository.RunGit("commit", "-q", "-m", "second");
        await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
        Assert.Empty(await fixture.ComputeAsync(document));

        document.Edit("one\nzwei\ndrei\n");
        Assert.Single(await fixture.ComputeAsync(document));
        await fixture.Markers.ToggleAsync();
        Assert.False(fixture.Markers.IsEnabled);
        Assert.Empty(fixture.Find(document));
        Assert.True(fixture.ContextKeys.TryGet(GitSettingsStore.CHANGE_MARKERS_KEY, out bool shown) && !shown);
        await fixture.Markers.ToggleAsync();
        Assert.Equal([new TextEditorLineMark(3, 1, TextEditorLineMarkKind.Added)], await fixture.ComputeAsync(document));

        // A new activation reads the saved switch.
        await fixture.Markers.ToggleAsync();
        using GitChangeMarkers reloaded = new(
            fixture.Session, fixture.Editor, fixture.Host, new GitSettingsStore(fixture.Storage), scheduler: fixture.Scheduler);
        await reloaded.LoadAsync();
        Assert.False(reloaded.IsEnabled);
    }

    [Fact]
    public async Task Fifty_edits_within_the_quiet_time_read_the_text_once_and_compare_once()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "a.txt", LINES, "init");
        using Fixture fixture = await Fixture.CreateAsync(repository);
        FakeEditorDocument document = fixture.Open("a.txt", LINES);
        await fixture.ComputeAsync(document);
        int reads = document.TextReads;
        int sets = 0;
        fixture.Registry.Changed += (_, _) => Interlocked.Increment(ref sets);

        for (int edit = 0; edit < 50; edit++)
        {
            document.Edit(LINES + new string('x', edit + 1) + "\n");
            fixture.Scheduler.AdvanceBy(TimeSpan.FromMilliseconds(5));
        }

        IReadOnlyList<TextEditorLineMark> marks = await fixture.ComputeAsync(document);

        Assert.Equal([new TextEditorLineMark(6, 1, TextEditorLineMarkKind.Added)], marks);
        Assert.Equal(reads + 1, document.TextReads);
        Assert.Equal(1, Volatile.Read(ref sets));
    }

    [Fact]
    public async Task Umlauts_a_byte_order_mark_and_line_endings_cause_no_false_marks()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "Größe ä.xaml", "<Grüße />\nzwei\n", "umlauts");
        File.WriteAllBytes(Path.Combine(repository.Path, "bom.txt"), [0xEF, 0xBB, 0xBF, .. "first\nsecond\n"u8.ToArray()]);
        repository.RunGit("-c", "core.autocrlf=true", "add", "--", "bom.txt");
        repository.WriteFile("crlf.txt", "a\r\nb\r\nc\r\n");
        repository.RunGit("-c", "core.autocrlf=true", "add", "--", "crlf.txt");
        repository.RunGit("commit", "-q", "-m", "encodings");
        using Fixture fixture = await Fixture.CreateAsync(repository);

        FakeEditorDocument umlauts = fixture.Open("Größe ä.xaml", "<Grüße />\nzwei\n");
        Assert.Empty(await fixture.ComputeAsync(umlauts));
        umlauts.Edit("<Grüße Text=\"ä\" />\nzwei\n");
        Assert.Equal([new TextEditorLineMark(1, 1, TextEditorLineMarkKind.Modified)], await fixture.ComputeAsync(umlauts));
        // The editor drops the byte order mark, as SharedFileReader.Decode does with the staged bytes.
        Assert.Empty(await fixture.ComputeAsync(fixture.Open("bom.txt", "first\nsecond\n")));
        // The index holds LF, the working file CRLF, the buffer mixed line endings.
        Assert.Empty(await fixture.ComputeAsync(fixture.Open("crlf.txt", "a\r\nb\nc\r")));
    }

    [Fact]
    public async Task Files_without_a_text_version_in_the_index_have_no_marks()
    {
        using GitTestRepository repository = GitTestRepository.Create("conflict.txt");
        File.WriteAllBytes(Path.Combine(repository.Path, "image.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01, 0x0A]);
        repository.WriteFile("pointer.txt", "version https://git-lfs.github.com/spec/v1\noid sha256:0000\nsize 1\n");
        repository.RunGit("add", "--", "image.png", "pointer.txt");
        repository.RunGit("commit", "-q", "-m", "binary");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "conflict.txt", "feature\n", "feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "conflict.txt", "main\n", "main");
        Assert.False(repository.TryRunGit(out _, "merge", "-q", "feature"));
        repository.WriteFile("untracked.txt", "new\n");
        string outside = Path.Combine(repository.RootPath, "outside.txt");
        File.WriteAllText(outside, "outside\n");
        using Fixture fixture = await Fixture.CreateAsync(repository);

        foreach (string path in new[] { "image.png", "pointer.txt", "untracked.txt", "conflict.txt" })
        {
            Assert.Empty(await fixture.ComputeAsync(fixture.Open(path, "changed\ntext\n")));
        }

        FakeEditorDocument outsideDocument = new(outside, "changed\n");
        fixture.Editor.Activate(outsideDocument);
        Assert.Empty(await fixture.ComputeAsync(outsideDocument));
    }

    [Fact]
    public async Task A_commit_with_thirty_open_documents_reads_each_staged_version_once()
    {
        using GitTestRepository repository = GitTestRepository.Create(
            Enumerable.Range(0, 30).Select(index => $"file{index}.txt").ToArray());
        using Fixture fixture = await Fixture.CreateAsync(repository);
        FakeEditorDocument[] documents = Enumerable.Range(0, 30)
            .Select(index => fixture.Open($"file{index}.txt", $"changed {index}\n"))
            .ToArray();
        Assert.All(await fixture.ComputeAllAsync(documents), marks => Assert.Single(marks));

        foreach (FakeEditorDocument document in documents)
        {
            File.WriteAllText(document.FilePath, document.Text);
        }

        repository.RunGit("add", "-A");
        repository.RunGit("commit", "-q", "-m", "all");
        await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
        int reads = 0;
        fixture.Service.CommandCompleted += (_, entry) =>
        {
            if (entry.Arguments[0] == "cat-file")
            {
                Interlocked.Increment(ref reads);
            }
        };
        Stopwatch stopwatch = Stopwatch.StartNew();

        IReadOnlyList<IReadOnlyList<TextEditorLineMark>> updated = await fixture.ComputeAllAsync(documents);

        TestContext.Current.TestOutputHelper?.WriteLine($"30 documents compared again after a commit in {stopwatch.ElapsedMilliseconds} ms");
        Assert.All(updated, Assert.Empty);
        Assert.Equal(30, Volatile.Read(ref reads));
    }

    /// <summary>
    /// A switch toggled off the UI thread, for example by a command from an IDE bridge, reads the open documents for a
    /// new comparison, which waits for the UI thread. The UI thread can sync the marks meanwhile and does not wait for
    /// the toggling thread.
    /// </summary>
    [Fact]
    public async Task A_sync_off_the_ui_thread_does_not_hold_the_lock_while_it_waits_for_the_ui_thread()
    {
        string root = Path.Combine(Path.GetTempPath(), "xve-markers-lock");
        using GitSession session = new(GitServiceSet.FromService(new FakeGitService(root)), new FakeWorkspaceInfo { WorkspacePath = root });
        UiBoundEditor editor = new();
        using GitChangeMarkers markers = new(session, editor, new LineMarkHost(new TextEditorLineMarkRegistry()), scheduler: new HistoricalScheduler());
        await markers.ToggleAsync();
        editor.Documents.Add(new FakeEditorDocument(Path.Combine(root, "a.txt"), "a\n"));

        Task toggle = Task.Run(() =>
        {
            editor.WaitingThreadId = Environment.CurrentManagedThreadId;
            return markers.ToggleAsync();
        });
        Assert.True(await Task.Run(() => editor.Waiting.Wait(WaitLimit)));
        Task ui = Task.Run(() =>
        {
            editor.RaiseActiveDocumentChanged();
            editor.UiDone.Set();
        });
        await Task.WhenAll(toggle, ui).WaitAsync(WaitLimit);

        Assert.False(editor.TimedOut);
        Assert.True(markers.IsEnabled);
    }

    [Fact]
    public void Blocks_become_marks_of_their_new_lines()
    {
        Assert.Equal(
            [
                new TextEditorLineMark(1, 2, TextEditorLineMarkKind.Added),
                new TextEditorLineMark(5, 1, TextEditorLineMarkKind.Modified),
                new TextEditorLineMark(8, 0, TextEditorLineMarkKind.Deleted)
            ],
            GitChangeMarkers.ToMarks([new LineDiffBlock(0, 0, 0, 2), new LineDiffBlock(2, 3, 4, 1), new LineDiffBlock(9, 2, 7, 0)]));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GitTestRepository _repository;

        private Fixture(GitTestRepository repository)
        {
            _repository = repository;
            Service = new GitService(options: repository.ProcessOptions);
            Session = new GitSession(
                GitServiceSet.FromService(Service),
                new FakeWorkspaceInfo { WorkspacePath = repository.Path },
                new GitSettingsStore(Storage));
            Host = new LineMarkHost(Registry);
            Markers = new GitChangeMarkers(Session, Editor, Host, new GitSettingsStore(Storage), ContextKeys, scheduler: Scheduler);
        }

        public GitService Service { get; }

        public GitSession Session { get; }

        public HistoricalScheduler Scheduler { get; } = new();

        public FakeEditorServices Editor { get; } = new();

        public TextEditorLineMarkRegistry Registry { get; } = new();

        public Tfvc.InMemoryExtensionStorage Storage { get; } = new();

        public ContextKeyRegistry ContextKeys { get; } = new();

        public ITextEditorViewHost Host { get; }

        public GitChangeMarkers Markers { get; }

        public static async Task<Fixture> CreateAsync(GitTestRepository repository)
        {
            Fixture fixture = new(repository);
            await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
            return fixture;
        }

        public FakeEditorDocument Open(string relativePath, string text)
        {
            FakeEditorDocument document = new(Path.Combine(_repository.Path, relativePath), text);
            Editor.Activate(document);
            return document;
        }

        public IReadOnlyList<TextEditorLineMark> Find(FakeEditorDocument document) => Registry.Find(document.FilePath, out _);

        public async Task StageAsync(FakeEditorDocument document)
        {
            File.WriteAllText(document.FilePath, document.Text);
            _repository.RunGit("add", "--", Path.GetFileName(document.FilePath));
            await Session.RefreshAsync().WaitAsync(WaitLimit);
        }

        // Ends the quiet time and returns the marks the comparison sets.
        public async Task<IReadOnlyList<TextEditorLineMark>> ComputeAsync(FakeEditorDocument document) =>
            (await ComputeAllAsync([document]))[0];

        public async Task<IReadOnlyList<IReadOnlyList<TextEditorLineMark>>> ComputeAllAsync(IReadOnlyList<FakeEditorDocument> documents)
        {
            Task[] sets = documents.Select(document => NextSetAsync(document.FilePath)).ToArray();
            Scheduler.AdvanceBy(GitFileDiffSource.Delay);
            await Task.WhenAll(sets).WaitAsync(WaitLimit);
            return documents.Select(Find).ToArray();
        }

        public void Dispose()
        {
            Markers.Dispose();
            Session.Dispose();
        }

        private Task NextSetAsync(string path)
        {
            TaskCompletionSource set = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<string>? handler = null;
            handler = (_, changed) =>
            {
                if (FileSystemPathComparison.Equals(changed, path))
                {
                    Registry.Changed -= handler;
                    set.TrySetResult();
                }
            };
            Registry.Changed += handler;
            return set.Task;
        }
    }

    // Editor services whose second read of the open documents on one thread waits for the UI thread, as the editor
    // services of the application do when called from another thread.
    private sealed class UiBoundEditor : IEditorServices
    {
        private int _reads;

        public List<IEditorDocument> Documents { get; } = new();

        public int? WaitingThreadId { get; set; }

        public ManualResetEventSlim Waiting { get; } = new();

        public ManualResetEventSlim UiDone { get; } = new();

        public bool TimedOut { get; private set; }

        public IEditorDocument? ActiveDocument => null;

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

        public void RaiseActiveDocumentChanged() =>
            ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = null });

        public IReadOnlyList<IEditorDocument> GetOpenDocuments()
        {
            if (Environment.CurrentManagedThreadId == WaitingThreadId && Interlocked.Increment(ref _reads) == 2)
            {
                Waiting.Set();
                TimedOut = !UiDone.Wait(TimeSpan.FromSeconds(10));
            }

            return Documents.ToArray();
        }

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) =>
            Task.FromResult<IEditorDocument?>(null);

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
    }

    private sealed class LineMarkHost(TextEditorLineMarkRegistry registry) : ITextEditorViewHost
    {
        public TextEditorViewOptions Options { get; } = new();

        public bool HasActiveEditor => false;

        public event EventHandler? OptionsChanged
        {
            add { }
            remove { }
        }

        public void SetOptions(TextEditorViewOptions options)
        {
        }

        public Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken) => Task.FromResult(false);

        public ITextEditorLineMarkLayer? CreateLineMarkLayer(string? clickCommandId = null) => registry.CreateLayer(clickCommandId);
    }
}
