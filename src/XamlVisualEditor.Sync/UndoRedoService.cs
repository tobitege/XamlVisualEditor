using XamlVisualEditor.Core;

namespace XamlVisualEditor.Sync;

/// <summary>
/// Manages undo/redo change records and complete document checkpoints.
/// </summary>
public sealed class UndoRedoService : IDisposable
{
    /// <summary>Gets the number of undo frames kept when no explicit depth is given.</summary>
    public const int DefaultMaxDepth = 100;

    // The most recent frame is the last node; the oldest frame is dropped first.
    private readonly LinkedList<UndoFrame> _undoStack = new();
    private readonly Stack<UndoFrame> _redoStack = new();
    private readonly List<AstChange> _currentBatch = new();
    private bool _isUndoRedoInProgress;
    private bool _isDisposed;

    /// <summary>Creates a service that keeps at most <see cref="DefaultMaxDepth"/> undo frames.</summary>
    public UndoRedoService() : this(DefaultMaxDepth)
    {
    }

    /// <summary>Creates a service that keeps at most <paramref name="maxDepth"/> undo frames and drops the oldest beyond it.</summary>
    public UndoRedoService(int maxDepth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        MaxDepth = maxDepth;
    }

    /// <summary>Gets the maximum number of undo frames kept.</summary>
    public int MaxDepth { get; }

    public bool CanUndoCompanion(DocumentCompanionChange change)
    {
        return _undoStack.Last is { } node &&
               System.Linq.Enumerable.Contains(node.Value.CompanionChanges, change);
    }

    public bool CanRedoCompanion(DocumentCompanionChange change)
    {
        return _redoStack.TryPeek(out var frame) &&
               System.Linq.Enumerable.Contains(frame.CompanionChanges, change);
    }

    /// <summary>
    /// Gets whether an undo operation is available.
    /// </summary>
    public bool CanUndo => _undoStack.Count > 0;

    /// <summary>
    /// Gets whether a redo operation is available.
    /// </summary>
    public bool CanRedo => _redoStack.Count > 0;

    /// <summary>
    /// Gets the number of items on the undo stack.
    /// </summary>
    public int UndoCount => _undoStack.Count;

    /// <summary>
    /// Gets the number of items on the redo stack.
    /// </summary>
    public int RedoCount => _redoStack.Count;

    /// <summary>Gets the complete document state associated with the most recent replay.</summary>
    public DocumentSnapshot? ReplaySnapshot { get; private set; }

    /// <summary>
    /// Fires when the undo/redo state changes.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>
    /// Records an AST change for undo tracking.
    /// </summary>
    public void RecordChange(AstChange change)
    {
        if (_isUndoRedoInProgress || _isDisposed)
        {
            return;
        }

        _currentBatch.Add(change);
    }

    /// <summary>
    /// Commits the current batch of changes as a single undo frame.
    /// </summary>
    public void CommitBatch(string description)
    {
        if (_currentBatch.Count == 0 || _isDisposed)
        {
            return;
        }

        UndoFrame frame = new()
        {
            Description = description,
            Changes = _currentBatch.ToList()
        };

        PushUndo(frame);
        _redoStack.Clear();
        _currentBatch.Clear();
        StateChanged?.Invoke();
    }

    /// <summary>Commits a complete document transition, including structural edits with no scalar change records.</summary>
    public bool CommitSnapshot(string description, DocumentSnapshot before, DocumentSnapshot after,
        IReadOnlyList<DocumentCompanionChange>? companionChanges = null)
    {
        if (_isDisposed) return false;
        if (string.Equals(before.Text, after.Text, StringComparison.Ordinal) && (companionChanges?.Count ?? 0) == 0)
        {
            _currentBatch.Clear();
            return false;
        }
        PushUndo(new UndoFrame
        {
            Description = description,
            Changes = _currentBatch.ToArray(),
            Before = before,
            After = after,
            CompanionChanges = companionChanges?.ToArray() ?? Array.Empty<DocumentCompanionChange>()
        });
        _currentBatch.Clear();
        _redoStack.Clear();
        StateChanged?.Invoke();
        return true;
    }

    /// <summary>Discards only changes belonging to an uncommitted operation.</summary>
    public void DiscardPendingChanges() => _currentBatch.Clear();

    /// <summary>
    /// Performs an undo operation, returning the changes to reverse.
    /// </summary>
    public IReadOnlyList<AstChange>? Undo(Action<DocumentSnapshot>? restoreSnapshot = null)
    {
        if (!CanUndo || _isDisposed)
        {
            return null;
        }

        _isUndoRedoInProgress = true;
        try
        {
            UndoFrame frame = _undoStack.Last!.Value;
            Replay(frame, forward: false, restoreSnapshot);
            _undoStack.RemoveLast();
            _redoStack.Push(frame);
            ReplaySnapshot = frame.Before;
            StateChanged?.Invoke();

            // Return reversed changes for applying
            return CreateInverseChanges(frame.Changes);
        }
        finally
        {
            _isUndoRedoInProgress = false;
        }
    }

    /// <summary>
    /// Performs a redo operation, returning the changes to reapply.
    /// </summary>
    public IReadOnlyList<AstChange>? Redo(Action<DocumentSnapshot>? restoreSnapshot = null)
    {
        if (!CanRedo || _isDisposed)
        {
            return null;
        }

        _isUndoRedoInProgress = true;
        try
        {
            UndoFrame frame = _redoStack.Peek();
            Replay(frame, forward: true, restoreSnapshot);
            _redoStack.Pop();
            PushUndo(frame);
            ReplaySnapshot = frame.After;
            StateChanged?.Invoke();

            // Return original changes for reapplying
            return frame.Changes;
        }
        finally
        {
            _isUndoRedoInProgress = false;
        }
    }

    /// <summary>
    /// Clears all undo and redo history.
    /// </summary>
    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
        _currentBatch.Clear();
        ReplaySnapshot = null;
        StateChanged?.Invoke();
    }

    private void PushUndo(UndoFrame frame)
    {
        _undoStack.AddLast(frame);
        while (_undoStack.Count > MaxDepth) _undoStack.RemoveFirst();
    }

    private static void Replay(UndoFrame frame, bool forward, Action<DocumentSnapshot>? restoreSnapshot)
    {
        DocumentSnapshot? target = forward ? frame.After : frame.Before;
        DocumentSnapshot? original = forward ? frame.Before : frame.After;
        if (restoreSnapshot is not null && (target is null || original is null))
            throw new InvalidOperationException("This undo entry has no document snapshots.");
        DocumentCompanionChange.ApplyAll(frame.CompanionChanges, forward);
        try
        {
            if (restoreSnapshot is not null) restoreSnapshot(target!);
        }
        catch (Exception failure)
        {
            List<Exception> errors = new() { failure };
            try { DocumentCompanionChange.ApplyAll(frame.CompanionChanges, !forward); }
            catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            try { restoreSnapshot!(original!); }
            catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            if (errors.Count > 1) throw new AggregateException("The grouped history operation could not be fully restored.", errors);
            throw;
        }
    }

    private static IReadOnlyList<AstChange> CreateInverseChanges(IReadOnlyList<AstChange> changes)
    {
        List<AstChange> inverse = new(changes.Count);

        // Process in reverse order
        for (int i = changes.Count - 1; i >= 0; i--)
        {
            AstChange change = changes[i];
            AstChange? inverseChange = change switch
            {
                NodeAdded added => new NodeRemoved(added.NodeId, added.ParentId, added.Index),
                NodeRemoved removed => new NodeAdded(removed.NodeId, removed.ParentId, removed.Index, removed.NodeTypeName),
                PropertyValueChanged propChanged => new PropertyValueChanged(
                    propChanged.NodeId, propChanged.PropertyName, propChanged.NewValue, propChanged.OldValue),
                TextContentChanged textChanged => new TextContentChanged(
                    textChanged.NodeId, textChanged.NewText, textChanged.OldText),
                NodeMoved moved => new NodeMoved(
                    moved.NodeId, moved.NewParentId, moved.NewIndex, moved.OldParentId, moved.OldIndex),
                _ => null
            };

            if (inverseChange is not null)
            {
                inverse.Add(inverseChange);
            }
        }

        return inverse;
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        Clear();
    }
}

/// <summary>
/// Represents a single undoable operation consisting of one or more AST changes.
/// </summary>
public sealed class UndoFrame
{
    /// <summary>
    /// Gets the human-readable description of the operation.
    /// </summary>
    public required string Description { get; init; }

    /// <summary>
    /// Gets the AST changes that comprise this operation.
    /// </summary>
    public required IReadOnlyList<AstChange> Changes { get; init; }

    /// <summary>Gets the document before the operation, when recorded by a sync engine.</summary>
    public DocumentSnapshot? Before { get; init; }

    /// <summary>Gets the document after the operation, when recorded by a sync engine.</summary>
    public DocumentSnapshot? After { get; init; }

    /// <summary>Gets related document transitions replayed with this frame.</summary>
    public IReadOnlyList<DocumentCompanionChange> CompanionChanges { get; init; } = Array.Empty<DocumentCompanionChange>();
}

/// <summary>Stores complete text and node identity state for a document checkpoint.</summary>
public sealed record DocumentSnapshot(
    string Text,
    string CanonicalText,
    System.Collections.Immutable.ImmutableDictionary<string, Guid> NodeIdentities);
