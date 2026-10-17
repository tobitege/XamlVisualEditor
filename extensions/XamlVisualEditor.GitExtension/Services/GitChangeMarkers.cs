using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Marks the lines of every open document that differ from the index, as <c>git add</c> would take them: added,
/// modified and the places of removed lines. A click on a mark runs <see cref="GitCommands.COMPARE_WITH_INDEX"/>.
/// The marks follow the open documents and the switch, which is saved in the extension storage.
/// </summary>
public sealed class GitChangeMarkers : IDisposable
{
    private readonly object _gate = new();
    private readonly GitSession _session;
    private readonly IEditorServices? _editor;
    private readonly ITextEditorLineMarkLayer? _layer;
    private readonly GitSettingSwitch _switch;
    private readonly IScheduler _scheduler;
    private readonly IExtensionLogger? _logger;
    private readonly Dictionary<string, SerialDisposable> _files = new(FileSystemPathComparison.Comparer);
    private bool _disposed;

    /// <summary>Creates the marks, switched on until <see cref="LoadAsync"/> reads the saved switch.</summary>
    /// <param name="session">The Git session.</param>
    /// <param name="editor">Provides the open documents; null marks nothing.</param>
    /// <param name="textEditorView">Draws the marks; null, or a host without line marks, marks nothing.</param>
    /// <param name="settings">Saves the switch; null keeps it in memory.</param>
    /// <param name="contextKeys">Receives <see cref="GitSettingsStore.CHANGE_MARKERS_KEY"/>.</param>
    /// <param name="logger">Receives failures.</param>
    /// <param name="scheduler">Times <see cref="GitFileDiffSource.Delay"/>; <see cref="Scheduler.Default"/> when null.</param>
    public GitChangeMarkers(
        GitSession session,
        IEditorServices? editor,
        ITextEditorViewHost? textEditorView,
        GitSettingsStore? settings = null,
        IContextKeys? contextKeys = null,
        IExtensionLogger? logger = null,
        IScheduler? scheduler = null)
    {
        _session = session;
        _editor = editor;
        _logger = logger;
        _scheduler = scheduler ?? Scheduler.Default;
        _layer = textEditorView?.CreateLineMarkLayer(GitCommands.COMPARE_WITH_INDEX);
        _switch = new GitSettingSwitch(GitSettingsStore.CHANGE_MARKERS_KEY, true, settings, contextKeys, logger);
        _switch.Changed += (_, _) => Sync();
        if (_editor is not null)
        {
            _editor.ActiveDocumentChanged += OnActiveDocumentChanged;
        }

        Sync();
    }

    /// <summary>Gets whether the editors show the marks.</summary>
    public bool IsEnabled => _switch.IsEnabled;

    /// <summary>Reads the saved switch.</summary>
    public Task LoadAsync(CancellationToken cancellationToken = default) => _switch.LoadAsync(cancellationToken);

    /// <summary>Switches the marks on or off and saves the switch.</summary>
    public Task ToggleAsync() => _switch.ToggleAsync();

    /// <summary>Converts change blocks into marks: a block without old lines is added, one without new lines removed.</summary>
    public static IReadOnlyList<TextEditorLineMark> ToMarks(IReadOnlyList<LineDiffBlock> blocks)
    {
        return blocks
            .Select(block => new TextEditorLineMark(block.NewStart + 1, block.NewCount, block switch
            {
                { NewCount: 0 } => TextEditorLineMarkKind.Deleted,
                { OldCount: 0 } => TextEditorLineMarkKind.Added,
                _ => TextEditorLineMarkKind.Modified
            }))
            .ToArray();
    }

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
            foreach (SerialDisposable file in _files.Values)
            {
                file.Dispose();
            }

            _files.Clear();
        }

        if (_editor is not null)
        {
            _editor.ActiveDocumentChanged -= OnActiveDocumentChanged;
        }

        _layer?.Dispose();
    }

    private void OnActiveDocumentChanged(object? sender, EditorActiveDocumentChangedEventArgs e) => Sync();

    // Compares every open document while the switch is on; a closed document, or every document when it is off, loses
    // its comparison and its marks.
    private void Sync()
    {
        if (_editor is null || _layer is null)
        {
            return;
        }

        HashSet<string> open = _switch.IsEnabled
            ? _editor.GetOpenDocuments().Select(document => Path.GetFullPath(document.FilePath)).ToHashSet(FileSystemPathComparison.Comparer)
            : [];
        List<IDisposable> closed = new();
        List<(string Path, SerialDisposable File)> opened = new();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (string path in _files.Keys.Where(path => !open.Contains(path)).ToArray())
            {
                _files.Remove(path, out SerialDisposable? file);
                closed.Add(file!);
                _layer.Set(path, []);
            }

            foreach (string path in open.Where(path => !_files.ContainsKey(path)))
            {
                SerialDisposable file = new();
                _files.Add(path, file);
                opened.Add((path, file));
            }
        }

        // Subscribing reads the open documents again, which waits for the UI thread when called from another thread,
        // so it runs outside the lock. A file closed or disposed in between disposes its subscription at once.
        closed.ForEach(file => file.Dispose());
        foreach ((string path, SerialDisposable file) in opened)
        {
            file.Disposable = GitFileDiffSource
                .Observe(_session, _editor, path, GitFileRevision.Index, _scheduler, logger: _logger)
                .Subscribe(diff => Publish(path, diff));
        }
    }

    private void Publish(string path, GitFileDiff diff)
    {
        lock (_gate)
        {
            if (!_disposed && _files.ContainsKey(path))
            {
                _layer!.Set(path, ToMarks(diff.Blocks));
            }
        }
    }
}
