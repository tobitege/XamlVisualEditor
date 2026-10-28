using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Xaml.Ast;
using XamlVisualEditor.Xaml.Parsing;
using XamlVisualEditor.Xaml.Serialization;

namespace XamlVisualEditor.Sync;

/// <summary>
/// Central bi-directional sync engine that coordinates all mutation sources
/// (code editor, designer, property editor, tree view, collaboration) through
/// a single source-of-truth mutable AST.
/// </summary>
/// <remarks>
/// The engine is not thread-safe. Its members are called from one thread at a time, normally the UI thread;
/// <see cref="LoadAsync"/> may run on a worker thread while no other member is in use, and the awaiting caller
/// resumes on the owning thread. <c>_isSyncing</c> only guards against re-entrant calls from sync event handlers.
/// </remarks>
public sealed class SyncEngine : ReactiveObject, ISyncEngine
{
    private readonly IXamlParsingService _parser;
    private readonly IXamlSerializationService _serializer;
    private readonly AstNodeMap? _nodeMap;
    private readonly UndoRedoService _undoRedo;
    private readonly Subject<SyncEvent> _syncSubject = new();
    private readonly List<AstChange> _pendingChanges = new();

    private MutableAstDocument? _document;
    private bool _isSyncing;
    private bool _isDisposed;
    private string _lastKnownText = string.Empty;
    private string _synchronizedAst = string.Empty;
    private DocumentSnapshot? _checkpoint;
    private EditGroup? _editGroup;
    private IReadOnlyList<XamlDiagnostic> _diagnostics = Array.Empty<XamlDiagnostic>();

    /// <summary>
    /// Creates a new sync engine with the specified parsing and serialization services.
    /// </summary>
    public SyncEngine(IXamlParsingService parser, IXamlSerializationService serializer)
    {
        _parser = parser;
        _serializer = serializer;
        _undoRedo = new UndoRedoService();
    }

    /// <summary>
    /// Creates a new sync engine with the specified parsing, serialization services, and node map.
    /// </summary>
    public SyncEngine(IXamlParsingService parser, IXamlSerializationService serializer, AstNodeMap nodeMap)
    {
        _parser = parser;
        _serializer = serializer;
        _nodeMap = nodeMap;
        _undoRedo = new UndoRedoService();
    }

    /// <summary>
    /// Creates a new sync engine with the specified parsing, serialization services, node map, and undo/redo service.
    /// </summary>
    public SyncEngine(IXamlParsingService parser, IXamlSerializationService serializer, AstNodeMap nodeMap, UndoRedoService undoRedo)
    {
        _parser = parser;
        _serializer = serializer;
        _nodeMap = nodeMap;
        _undoRedo = undoRedo;
    }

    /// <inheritdoc />
    public IXamlDocumentModel? Document => _document;

    /// <summary>
    /// Gets the mutable AST document.
    /// </summary>
    public MutableAstDocument? CurrentDocument => _document;

    /// <summary>
    /// Gets the undo/redo service.
    /// </summary>
    public UndoRedoService UndoRedo => _undoRedo;

    /// <summary>
    /// Gets the current XAML text.
    /// </summary>
    public string? CurrentText => string.IsNullOrEmpty(_lastKnownText) ? null : _lastKnownText;

    /// <summary>Gets whether a visual edit can safely modify the current AST.</summary>
    public bool CanEditAst => !_isDisposed && _document?.Root is not null
        && !_diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    /// <inheritdoc />
    public event Action<SyncEvent>? SyncCompleted;

    /// <summary>
    /// Gets an observable stream of sync events.
    /// </summary>
    public IObservable<SyncEvent> SyncEvents => _syncSubject.AsObservable();

    /// <inheritdoc />
    public Task LoadAsync(string xamlText, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ParseResult result = _parser.Parse(xamlText, new XamlParserOptions
        {
            UseTolerantParser = false
        });
        ct.ThrowIfCancellationRequested();
        MutableAstDocument? document = result.Document as MutableAstDocument;
        string serialized = document is null ? string.Empty : _serializer.Serialize(document);

        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (_editGroup is not null) _editGroup.Completed = true;
        _editGroup = null;
        _undoRedo.Clear();
        SetDocument(document);
        _diagnostics = result.Diagnostics;
        _lastKnownText = xamlText;
        _synchronizedAst = serialized;
        _checkpoint = CaptureSnapshot();

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<string> SaveAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (_editGroup is not null) throw new InvalidOperationException("Finish or cancel the current edit before saving.");
        CommitUndoBatch(UiStrings.Edit);
        return Task.FromResult(_lastKnownText);
    }

    /// <inheritdoc />
    public void NotifyTextChanged(string newText, SyncSource source)
    {
        if (_isSyncing || _isDisposed) return;
        if (_editGroup is not null) RollbackEdit(_editGroup);
        ReadText(newText, source, Array.Empty<DocumentCompanionChange>());
    }

    /// <summary>
    /// Replaces the text and writes the related document changes as one undo entry, which restores all of them.
    /// Pending changes of the document are committed as their own entry first; then the method throws without
    /// changing anything when the current text is not <paramref name="expectedText"/> or a related document has
    /// changed.
    /// </summary>
    public void ReplaceText(string expectedText, string newText,
        IReadOnlyList<DocumentCompanionChange> companionChanges, SyncSource source)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!CanReplaceText) throw new InvalidOperationException(UiStrings.DesignerEditInProgress);
        CommitUndoBatch(UiStrings.Edit);
        if (!string.Equals(_lastKnownText, expectedText, StringComparison.Ordinal))
            throw new InvalidOperationException(UiStrings.DesignerTextChangedBeforeEdit);
        ReadText(newText, source, companionChanges);
    }

    /// <summary>Gets whether <see cref="ReplaceText"/> can run, which it cannot during an unfinished edit.</summary>
    public bool CanReplaceText => !_isDisposed && !_isSyncing && _editGroup is null;

    private void ReadText(string newText, SyncSource source, IReadOnlyList<DocumentCompanionChange> companionChanges)
    {
        _isSyncing = true;

        try
        {
            // Re-parse the text
            ParseResult result = _parser.Parse(newText, new XamlParserOptions
            {
                UseTolerantParser = true
            });

            DocumentCompanionChange.ApplyAll(companionChanges, forward: true);
            SetDocument(result.Document as MutableAstDocument);
            _lastKnownText = newText;
            _diagnostics = result.Diagnostics;
            _synchronizedAst = _document is null ? string.Empty : _serializer.Serialize(_document);
            RecordSnapshot(UiStrings.TextEdit, companionChanges);
            Publish(source, Array.Empty<AstChange>());
        }
        finally
        {
            _isSyncing = false;
        }
    }

    /// <inheritdoc />
    public void NotifyAstChanged(AstChange change, SyncSource source)
    {
        if (_isSyncing || _isDisposed) return;
        _isSyncing = true;

        try
        {
            _pendingChanges.Add(change);

            // Commit as an undoable operation
            string description = change switch
            {
                NodeAdded na => string.Format(UiStrings.UndoAddNode, na.NodeTypeName),
                NodeRemoved => UiStrings.UndoRemoveNode,
                NodeMoved => UiStrings.UndoMoveNode,
                PropertyValueChanged pvc => string.Format(UiStrings.UndoSetProperty, pvc.PropertyName),
                TextContentChanged => UiStrings.UndoEditText,
                _ => UiStrings.Edit
            };

            if (_document is not null && !CanEditAst)
            {
                _pendingChanges.Clear();
                RestoreCurrentText(source);
                return;
            }

            // Serialize the current AST to text
            if (_document is not null)
            {
                IReadOnlyList<TextEdit> edits = _serializer.ComputeMinimalEdits(
                    _document, _lastKnownText, _pendingChanges);

                // Apply edits to get new text
                string newText = ApplyEdits(_lastKnownText, edits);
                _lastKnownText = newText;
                _synchronizedAst = _serializer.Serialize(_document);
                RefreshSourcePositions();
                RecordSnapshot(description);
                RefreshNodeMap();

                SyncEvent syncEvent = new()
                {
                    Source = source,
                    Changes = _pendingChanges.ToList(),
                    UpdatedText = newText,
                    Diagnostics = _diagnostics
                };

                _pendingChanges.Clear();
                SyncCompleted?.Invoke(syncEvent);
                _syncSubject.OnNext(syncEvent);
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    /// <summary>
    /// Gets the current XAML text representation.
    /// </summary>
    public string GetCurrentText() => _lastKnownText;

    /// <summary>
    /// Notifies the sync engine that the AST document has changed (e.g., from designer or collaboration).
    /// Re-serializes the document to produce updated XAML text.
    /// </summary>
    /// <returns>
    /// False when the change was not written to the text: while <see cref="CanEditAst"/> is false, a changed
    /// AST is refused and replaced by a new reading of the unchanged text.
    /// </returns>
    public bool NotifyAstChanged(MutableAstDocument document, SyncSource source)
    {
        if (_isSyncing || _isDisposed) return false;
        _isSyncing = true;

        try
        {
            if (document != _document)
            {
                SetDocument(document);
            }

            if (!SynchronizeAst())
            {
                RestoreCurrentText(source);
                return false;
            }

            RecordSnapshot(UiStrings.Edit);

            SyncEvent syncEvent = new()
            {
                Source = source,
                Changes = Array.Empty<AstChange>(),
                UpdatedText = _lastKnownText,
                Diagnostics = _diagnostics
            };

            SyncCompleted?.Invoke(syncEvent);
            _syncSubject.OnNext(syncEvent);
            return true;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void SetDocument(MutableAstDocument? doc)
    {
        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
        }

        _document = doc;
        if (_document is not null) _document.Changed += OnDocumentChanged;

        // Clear stale entries and register all nodes in the node map
        if (_nodeMap is not null)
        {
            _nodeMap.Clear();
            if (doc?.Root is not null)
            {
                _nodeMap.RegisterTree(doc.Root);
            }
        }
    }

    private void OnDocumentChanged(AstChange change)
    {
        // Record changes for undo/redo when not replaying undo/redo
        if (!_isSyncing) _undoRedo.RecordChange(change);
    }

    /// <summary>
    /// Commits any pending AST changes as a single undoable operation.
    /// </summary>
    public void CommitUndoBatch(string description)
    {
        if (_isDisposed || _isSyncing || _editGroup is not null) return;
        _isSyncing = true;
        try
        {
            if (!SynchronizeAst())
            {
                RestoreCurrentText(SyncSource.DesignSurface);
                return;
            }

            if (RecordSnapshot(description)) Publish(SyncSource.DesignSurface, Array.Empty<AstChange>());
        }
        finally
        {
            _isSyncing = false;
        }
    }

    /// <summary>Begins an atomic edit. Nested scopes share one checkpoint and undo entry.</summary>
    public DocumentEditTransaction BeginEditTransaction(string description, SyncSource source = SyncSource.DesignSurface)
    {
        if (_isDisposed) throw new ObjectDisposedException(nameof(SyncEngine));
        if (!CanEditAst) throw new InvalidOperationException(UiStrings.DesignerResolveXamlErrors);
        if (_editGroup is null)
        {
            CommitUndoBatch(UiStrings.Edit);
            _editGroup = new EditGroup(_checkpoint!, description, source);
        }
        EditGroup group = _editGroup;
        group.Depth++;
        return new DocumentEditTransaction(() => CommitEdit(group), () => RollbackEdit(group), () => group.Completed,
            change =>
            {
                if (change.Before == change.After) return;
                if (group.CompanionChanges.Any(existing => FileSystemPathComparison.Equals(existing.TargetId, change.TargetId)))
                    throw new InvalidOperationException($"The related document '{change.TargetId}' is already part of this edit.");
                group.CompanionChanges.Add(change);
            });
    }

    /// <summary>
    /// Restores the complete document checkpoint before the last committed operation.
    /// </summary>
    public void Undo(DocumentCompanionChange requiredChange)
    {
        if (!_undoRedo.CanUndoCompanion(requiredChange))
        {
            throw new InvalidOperationException(UiStrings.DesignerUndoLaterChangesFirst);
        }

        Undo();
    }

    public void Redo(DocumentCompanionChange requiredChange)
    {
        if (!_undoRedo.CanRedoCompanion(requiredChange))
        {
            throw new InvalidOperationException(UiStrings.DesignerRedoEarlierChangesFirst);
        }

        Redo();
    }

    public void Undo()
    {
        if (_editGroup is not null)
        {
            RollbackEdit(_editGroup);
            return;
        }
        _undoRedo.Undo(snapshot => RestoreSnapshot(snapshot, SyncSource.DesignSurface));
    }

    /// <summary>
    /// Restores the complete document checkpoint after the last undone operation.
    /// </summary>
    public void Redo()
    {
        if (_editGroup is not null) return;
        _undoRedo.Redo(snapshot => RestoreSnapshot(snapshot, SyncSource.DesignSurface));
    }

    // Writes AST changes into the text. Returns false without changing the text when the AST differs from
    // the text but CanEditAst is false: the AST is then a tolerant partial reading of text with errors, and
    // writing it back would replace the unread rest of the text.
    private bool SynchronizeAst()
    {
        if (_document is null) return true;
        string canonical = _serializer.Serialize(_document);
        if (!string.Equals(canonical, _synchronizedAst, StringComparison.Ordinal))
        {
            if (!CanEditAst) return false;
            _lastKnownText = ApplyEdits(_lastKnownText,
                XamlSerializationService.ComputeMinimalEdits(_lastKnownText, canonical));
            RefreshSourcePositions();
        }
        _synchronizedAst = canonical;
        RefreshNodeMap();
        return true;
    }

    // Discards AST changes that were refused by reading the AST again from the unchanged text.
    private void RestoreCurrentText(SyncSource source)
    {
        DocumentSnapshot snapshot = _checkpoint is { } checkpoint && string.Equals(checkpoint.Text, _lastKnownText, StringComparison.Ordinal)
            ? checkpoint
            : new DocumentSnapshot(_lastKnownText, _synchronizedAst,
                System.Collections.Immutable.ImmutableDictionary<string, Guid>.Empty);
        RestoreSnapshot(snapshot, source);
    }

    private void RefreshSourcePositions()
    {
        if (_document is null) return;
        ParseResult parsed = _parser.Parse(_lastKnownText, new XamlParserOptions { UseTolerantParser = false });
        _diagnostics = parsed.Diagnostics;
        AstNodeIdentities.CopySourcePositions(_document, parsed.Document as MutableAstDocument);
    }

    private void RefreshNodeMap()
    {
        if (_nodeMap is null) return;
        _nodeMap.Clear();
        if (_document?.Root is not null) _nodeMap.RegisterTree(_document.Root);
    }

    private DocumentSnapshot CaptureSnapshot()
    {
        return new DocumentSnapshot(_lastKnownText, _synchronizedAst,
            _document is null ? System.Collections.Immutable.ImmutableDictionary<string, Guid>.Empty
                : AstNodeIdentities.Capture(_document, _checkpoint?.NodeIdentities));
    }

    private bool RecordSnapshot(string description, IReadOnlyList<DocumentCompanionChange>? companionChanges = null)
    {
        if (_editGroup is not null) return false;
        DocumentSnapshot after = CaptureSnapshot();
        DocumentSnapshot before = _checkpoint ?? new DocumentSnapshot(string.Empty, string.Empty,
            System.Collections.Immutable.ImmutableDictionary<string, Guid>.Empty);
        _checkpoint = after;
        return _undoRedo.CommitSnapshot(description, before, after, companionChanges);
    }

    private void RestoreSnapshot(DocumentSnapshot snapshot, SyncSource source)
    {
        _isSyncing = true;
        try
        {
            ParseResult parsed = _parser.Parse(snapshot.Text, new XamlParserOptions { UseTolerantParser = true });
            MutableAstDocument? document = parsed.Document as MutableAstDocument;
            if (document is not null) AstNodeIdentities.Restore(document, snapshot.NodeIdentities);
            SetDocument(document);
            _lastKnownText = snapshot.Text;
            _synchronizedAst = snapshot.CanonicalText;
            _checkpoint = snapshot;
            _diagnostics = parsed.Diagnostics;
            _pendingChanges.Clear();
            _undoRedo.DiscardPendingChanges();
            Publish(source, Array.Empty<AstChange>());
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void CommitEdit(EditGroup group)
    {
        if (group.Completed || !ReferenceEquals(_editGroup, group)) return;
        if (--group.Depth > 0) return;
        _isSyncing = true;
        bool companionsApplied = false;
        try
        {
            if (!SynchronizeAst()) throw new InvalidOperationException(UiStrings.DesignerResolveXamlErrors);
            DocumentSnapshot after = CaptureSnapshot();
            DocumentCompanionChange.ApplyAll(group.CompanionChanges, forward: true);
            companionsApplied = true;
            _undoRedo.CommitSnapshot(group.Description, group.Before, after, group.CompanionChanges);
            _checkpoint = after;
            group.Completed = true;
            _editGroup = null;
            Publish(group.Source, Array.Empty<AstChange>());
        }
        catch (Exception failure) when (!group.Completed)
        {
            group.Completed = true;
            _editGroup = null;
            List<Exception> errors = new() { failure };
            if (companionsApplied)
            {
                try { DocumentCompanionChange.ApplyAll(group.CompanionChanges, forward: false); }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            try { RestoreSnapshot(group.Before, group.Source); }
            catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            if (errors.Count > 1) throw new AggregateException("The grouped edit could not be fully restored.", errors);
            throw;
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void RollbackEdit(EditGroup group)
    {
        if (group.Completed || !ReferenceEquals(_editGroup, group)) return;
        group.Completed = true;
        _editGroup = null;
        RestoreSnapshot(group.Before, group.Source);
    }

    private void Publish(SyncSource source, IReadOnlyList<AstChange> changes)
    {
        SyncEvent syncEvent = new()
        {
            Source = source,
            Changes = changes,
            UpdatedText = _lastKnownText,
            Diagnostics = _diagnostics
        };

        SyncCompleted?.Invoke(syncEvent);
        _syncSubject.OnNext(syncEvent);
    }

    private sealed class EditGroup(DocumentSnapshot before, string description, SyncSource source)
    {
        public DocumentSnapshot Before { get; } = before;
        public string Description { get; } = description;
        public SyncSource Source { get; } = source;
        public int Depth { get; set; }
        public bool Completed { get; set; }
        public List<DocumentCompanionChange> CompanionChanges { get; } = new();
    }

    private static string ApplyEdits(string text, IReadOnlyList<TextEdit> edits)
    {
        if (edits.Count == 0) return text;

        // Apply edits in reverse offset order to maintain positions
        List<TextEdit> sorted = edits.OrderByDescending(e => e.Offset).ToList();
        System.Text.StringBuilder sb = new(text);

        foreach (TextEdit edit in sorted)
        {
            sb.Remove(edit.Offset, edit.Length);
            sb.Insert(edit.Offset, edit.NewText);
        }

        return sb.ToString();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        if (_editGroup is not null) _editGroup.Completed = true;
        _editGroup = null;

        if (_document is not null)
        {
            _document.Changed -= OnDocumentChanged;
        }

        _undoRedo.Dispose();
        _syncSubject.OnCompleted();
        _syncSubject.Dispose();
    }
}
