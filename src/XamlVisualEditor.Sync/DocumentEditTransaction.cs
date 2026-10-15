namespace XamlVisualEditor.Sync;

/// <summary>Groups document mutations; disposal without commit restores the state before the operation.</summary>
public sealed class DocumentEditTransaction : IDisposable
{
    private readonly Action _commit;
    private readonly Action _rollback;
    private readonly Func<bool> _groupCompleted;
    private readonly Action<DocumentCompanionChange> _enlist;
    private bool _completed;

    internal DocumentEditTransaction(Action commit, Action rollback, Func<bool> groupCompleted,
        Action<DocumentCompanionChange> enlist)
    {
        _commit = commit;
        _rollback = rollback;
        _groupCompleted = groupCompleted;
        _enlist = enlist;
    }

    /// <summary>Gets whether this transaction or its enclosing operation has finished.</summary>
    public bool IsCompleted => _completed || _groupCompleted();

    /// <summary>Adds a related text transition, applied only when the outermost transaction commits.</summary>
    public void Enlist(DocumentCompanionChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (IsCompleted) throw new InvalidOperationException("The edit transaction has already completed.");
        _enlist(change);
    }

    /// <summary>Completes this scope. The outermost scope creates one undo entry.</summary>
    public void Commit()
    {
        if (IsCompleted) return;
        _completed = true;
        _commit();
    }

    /// <summary>Restores the operation's initial state without consuming previous undo history.</summary>
    public void Rollback()
    {
        if (IsCompleted) return;
        _completed = true;
        _rollback();
    }

    /// <inheritdoc />
    public void Dispose() => Rollback();
}
