using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Marks the conflict blocks of every open document that git lists as conflicted, from the text of the editor
/// buffer. A click on a mark runs <see cref="GitCommands.RESOLVE_CONFLICT"/>. The change marks have no marks for a
/// conflicted file, which has no staged version, so the two layers never mark the same file.
/// </summary>
internal sealed class GitConflictMarkers : IDisposable
{
    private readonly object _gate = new();
    private readonly IEditorServices? _editor;
    private readonly ITextEditorLineMarkLayer? _layer;
    private readonly IScheduler _scheduler;
    private readonly IExtensionLogger? _logger;
    private readonly IDisposable _snapshots;
    private readonly Dictionary<string, Tracked> _files = new(FileSystemPathComparison.Comparer);
    private HashSet<string> _conflicted = new(FileSystemPathComparison.Comparer);
    private long _syncsStarted;
    private long _syncApplied;
    private bool _disposed;

    /// <summary>Creates the marks.</summary>
    /// <param name="session">Provides the conflicted files.</param>
    /// <param name="editor">Provides the open documents; null marks nothing.</param>
    /// <param name="textEditorView">Draws the marks; null, or a host without line marks, marks nothing.</param>
    /// <param name="logger">Receives failures to read a document.</param>
    /// <param name="scheduler">Times <see cref="GitFileDiffSource.Delay"/>; <see cref="Scheduler.Default"/> when null.</param>
    public GitConflictMarkers(
        GitSession session,
        IEditorServices? editor,
        ITextEditorViewHost? textEditorView,
        IExtensionLogger? logger = null,
        IScheduler? scheduler = null)
    {
        _editor = editor;
        _logger = logger;
        _scheduler = scheduler ?? Scheduler.Default;
        _layer = textEditorView?.CreateLineMarkLayer(GitCommands.RESOLVE_CONFLICT);
        if (_editor is not null)
        {
            _editor.ActiveDocumentChanged += OnActiveDocumentChanged;
        }

        _snapshots = session.Snapshots.Subscribe(snapshot =>
        {
            HashSet<string> conflicted = new(FileSystemPathComparison.Comparer);
            if (snapshot.RepositoryRoot is { } root)
            {
                foreach (GitFileChange change in snapshot.Changes.Where(change => change.IsConflicted))
                {
                    conflicted.Add(Path.GetFullPath(Path.Combine(root, change.Path)));
                }
            }

            lock (_gate)
            {
                _conflicted = conflicted;
            }

            Sync();
        });
    }

    /// <summary>Converts conflict blocks into marks.</summary>
    public static IReadOnlyList<TextEditorLineMark> ToMarks(IReadOnlyList<GitConflictBlock> blocks) =>
        blocks.Select(block => new TextEditorLineMark(block.StartLine, block.LineCount, TextEditorLineMarkKind.Conflict)).ToArray();

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (Tracked file in _files.Values)
            {
                file.Subscription.Dispose();
            }

            _files.Clear();
        }

        _snapshots.Dispose();
        if (_editor is not null)
        {
            _editor.ActiveDocumentChanged -= OnActiveDocumentChanged;
        }

        _layer?.Dispose();
    }

    private void OnActiveDocumentChanged(object? sender, EditorActiveDocumentChangedEventArgs e) => Sync();

    // Follows every open document of a conflicted file; a document that closed, was opened again or whose file is no
    // longer conflicted loses its subscription and its marks.
    private void Sync()
    {
        if (_editor is null || _layer is null)
        {
            return;
        }

        // Snapshots and document switches sync from different threads; a sync that read the open documents before a
        // later one must not undo what the later one applied.
        long sync = Interlocked.Increment(ref _syncsStarted);
        IReadOnlyList<IEditorDocument> documents = _editor.GetOpenDocuments();
        List<IDisposable> closed = new();
        List<(string Path, IEditorDocument Document, SerialDisposable Subscription)> opened = new();
        lock (_gate)
        {
            if (_disposed || sync < _syncApplied)
            {
                return;
            }

            _syncApplied = sync;

            Dictionary<string, IEditorDocument> wanted = new(FileSystemPathComparison.Comparer);
            foreach (IEditorDocument document in documents)
            {
                string path = Path.GetFullPath(document.FilePath);
                if (_conflicted.Contains(path))
                {
                    wanted[path] = document;
                }
            }

            foreach ((string path, Tracked file) in _files.ToArray())
            {
                if (!wanted.TryGetValue(path, out IEditorDocument? document) || !ReferenceEquals(document, file.Document))
                {
                    _files.Remove(path);
                    closed.Add(file.Subscription);
                    _layer.Set(path, []);
                }
            }

            foreach ((string path, IEditorDocument document) in wanted)
            {
                if (!_files.ContainsKey(path))
                {
                    SerialDisposable subscription = new();
                    _files.Add(path, new Tracked(document, subscription));
                    opened.Add((path, document, subscription));
                }
            }
        }

        // Reading the text waits for the UI thread, so the subscriptions start outside the lock.
        closed.ForEach(subscription => subscription.Dispose());
        foreach ((string path, IEditorDocument document, SerialDisposable subscription) in opened)
        {
            subscription.Disposable = Observable.Switch(Observable.FromEventPattern<EditorDocumentChangedEventArgs>(
                    handler => document.Changed += handler,
                    handler => document.Changed -= handler)
                .Select(_ => System.Reactive.Unit.Default)
                .StartWith(System.Reactive.Unit.Default)
                .Throttle(GitFileDiffSource.Delay, _scheduler)
                .Select(_ => Observable.FromAsync(ct => ReadBlocksAsync(document, ct))))
                .Subscribe(blocks => Publish(path, document, blocks));
        }
    }

    private async Task<IReadOnlyList<GitConflictBlock>> ReadBlocksAsync(IEditorDocument document, CancellationToken ct)
    {
        try
        {
            return GitConflictParser.Parse(await document.GetTextAsync(ct).ConfigureAwait(false));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger?.Error(GitStrings.LogConflictBlocksFailed, exception);
            return [];
        }
    }

    private void Publish(string path, IEditorDocument document, IReadOnlyList<GitConflictBlock> blocks)
    {
        lock (_gate)
        {
            if (!_disposed && _files.TryGetValue(path, out Tracked? file) && ReferenceEquals(file.Document, document))
            {
                _layer!.Set(path, ToMarks(blocks));
            }
        }
    }

    private sealed record Tracked(IEditorDocument Document, SerialDisposable Subscription);
}
