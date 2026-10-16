using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using AvaloniaEdit.Document;
using XamlVisualEditor.Core;
using XamlVisualEditor.Sync;

namespace XamlVisualEditor.Shell.ViewModels;

public sealed partial class TextDocumentViewModel
{
    private object _historyPosition = new();
    private readonly List<WeakReference<LinkedTextChange>> _linkedTextChanges = new();

    private void TrackDocumentHistory()
    {
        Document.Changed += OnDocumentHistoryChanged;
        _disposables.Add(Disposable.Create(() => Document.Changed -= OnDocumentHistoryChanged));
    }

    private void OnDocumentHistoryChanged(object? sender, DocumentChangeEventArgs args)
    {
        if (!Document.UndoStack.AcceptChanges)
        {
            return;
        }

        object before = _historyPosition;
        object after = new();
        Document.UndoStack.Push(new HistoryPositionChange(this, before, after));
        _historyPosition = after;
    }

    public void Undo() => ReplayDocumentHistory(forward: false);

    public void Redo() => ReplayDocumentHistory(forward: true);

    private void ReplayDocumentHistory(bool forward)
    {
        if (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(TextDocumentViewModel));
        }

        for (int index = _linkedTextChanges.Count - 1; index >= 0; index--)
        {
            if (!_linkedTextChanges[index].TryGetTarget(out LinkedTextChange? change))
            {
                _linkedTextChanges.RemoveAt(index);
                continue;
            }

            if (change.IsNext(forward))
            {
                change.Replay(forward);
                return;
            }
        }

        if (forward)
        {
            Document.UndoStack.Redo();
        }
        else
        {
            Document.UndoStack.Undo();
        }
    }

    internal DocumentCompanionChange CreateLinkedChange(
        string before, string after, IReadOnlyList<TextEdit> edits, Action<bool> replayTransaction)
    {
        ArgumentNullException.ThrowIfNull(replayTransaction);
        if (IsDisposed || !string.Equals(Document.Text, before, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The document changed while preparing the refactoring: {FilePath}");
        }

        LinkedTextChange change = new(this, before, after, edits, replayTransaction);
        _linkedTextChanges.Add(new WeakReference<LinkedTextChange>(change));
        return new DocumentCompanionChange(FilePath, before, after, change.ReadText, change.WriteText);
    }

    private sealed class HistoryPositionChange(
        TextDocumentViewModel owner, object before, object after) : IUndoableOperation
    {
        public void Undo() => owner._historyPosition = before;

        public void Redo() => owner._historyPosition = after;
    }

    private sealed class LinkedTextChange(
        TextDocumentViewModel owner, string before, string after, IReadOnlyList<TextEdit> edits,
        Action<bool> replayTransaction)
    {
        private readonly object _beforePosition = owner._historyPosition;
        private object? _afterPosition;
        private bool _isApplied;

        public bool IsNext(bool forward)
        {
            return _afterPosition is not null && forward != _isApplied &&
                   ReferenceEquals(owner._historyPosition, forward ? _beforePosition : _afterPosition);
        }

        public void Replay(bool forward)
        {
            ReadText();
            // The transaction owner checks its own history before replaying all documents.
            replayTransaction(forward);
        }

        public string ReadText()
        {
            object? expectedPosition = _isApplied ? _afterPosition : _beforePosition;
            if (owner.IsDisposed || !ReferenceEquals(owner._historyPosition, expectedPosition))
            {
                throw new InvalidOperationException($"Undo later changes before replaying this refactoring: {owner.FilePath}");
            }

            if (_afterPosition is not null &&
                (_isApplied ? !owner.Document.UndoStack.CanUndo : !owner.Document.UndoStack.CanRedo))
            {
                throw new InvalidOperationException($"The refactoring is no longer available in the document history: {owner.FilePath}");
            }

            return owner.Document.Text;
        }

        public void WriteText(string text)
        {
            ReadText();
            bool forward = string.Equals(text, after, StringComparison.Ordinal);
            if (!forward && !string.Equals(text, before, StringComparison.Ordinal))
            {
                throw new ArgumentException("The text does not belong to this refactoring.", nameof(text));
            }

            if (_afterPosition is null)
            {
                if (!forward)
                {
                    return;
                }

                try
                {
                    owner.ApplyTextEdits(edits);
                }
                finally
                {
                    if (string.Equals(owner.Document.Text, after, StringComparison.Ordinal))
                    {
                        _afterPosition = owner._historyPosition;
                        _isApplied = true;
                    }
                }

                return;
            }

            if (forward == _isApplied)
            {
                return;
            }

            try
            {
                if (forward)
                {
                    owner.Document.UndoStack.Redo();
                }
                else
                {
                    owner.Document.UndoStack.Undo();
                }
            }
            finally
            {
                _isApplied = ReferenceEquals(owner._historyPosition, _afterPosition);
            }
        }
    }
}
