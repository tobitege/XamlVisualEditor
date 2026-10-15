namespace XamlVisualEditor.Sync;

/// <summary>A guarded text transition belonging to the same operation as a designer edit.</summary>
public sealed class DocumentCompanionChange
{
    private readonly Func<string> _readText;
    private readonly Action<string> _writeText;

    /// <summary>Creates a transition for a related editable document. Callbacks run synchronously on the caller's thread.</summary>
    public DocumentCompanionChange(string targetId, string before, string after,
        Func<string> readText, Action<string> writeText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetId);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(readText);
        ArgumentNullException.ThrowIfNull(writeText);
        TargetId = targetId;
        Before = before;
        After = after;
        _readText = readText;
        _writeText = writeText;
    }

    /// <summary>Gets the stable document identity, normally its absolute path.</summary>
    public string TargetId { get; }
    /// <summary>Gets the original document text.</summary>
    public string Before { get; }
    /// <summary>Gets the proposed document text.</summary>
    public string After { get; }

    internal void Validate(bool forward)
    {
        string expected = forward ? Before : After;
        if (!string.Equals(_readText(), expected, StringComparison.Ordinal))
            throw new InvalidOperationException($"The related document '{TargetId}' has changed. The grouped edit was not applied.");
    }

    private void RestoreInitialState(bool forward)
    {
        string initial = forward ? Before : After;
        string written = forward ? After : Before;
        string current = _readText();
        if (string.Equals(current, initial, StringComparison.Ordinal)) return;
        if (!string.Equals(current, written, StringComparison.Ordinal))
            throw new InvalidOperationException($"The related document '{TargetId}' changed during the operation and could not be restored automatically.");
        _writeText(initial);
    }

    internal static void ApplyAll(IReadOnlyList<DocumentCompanionChange> changes, bool forward)
    {
        foreach (DocumentCompanionChange change in changes) change.Validate(forward);
        int attempted = -1;
        try
        {
            for (int index = 0; index < changes.Count; index++)
            {
                attempted = index;
                changes[index].Validate(forward);
                changes[index]._writeText(forward ? changes[index].After : changes[index].Before);
            }
        }
        catch (Exception failure)
        {
            List<Exception> errors = new() { failure };
            for (int index = attempted; index >= 0; index--)
            {
                try { changes[index].RestoreInitialState(forward); }
                catch (Exception rollbackFailure) { errors.Add(rollbackFailure); }
            }
            if (errors.Count > 1) throw new AggregateException("The grouped edit failed and could not be fully restored.", errors);
            throw;
        }
    }
}
