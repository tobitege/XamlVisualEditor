using System;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Transfers caret and selection between an editor and its document view model without echo. The view model owns
/// both: a selection from the view model keeps the caret of the view model when it lies inside the selection;
/// otherwise the caret the editor ends up with is reported back.
/// </summary>
internal sealed class EditorSelectionSync : IDisposable
{
    private readonly TextEditor _editor;
    private readonly Action<int, int> _reportSelection;
    private readonly Action<Caret> _reportCaret;
    private bool _changingEditor;
    private bool _reportingSelection;

    /// <summary>Starts following the caret and selection of the editor.</summary>
    /// <param name="editor">The editor.</param>
    /// <param name="reportSelection">Writes start and length of the editor selection into the view model.</param>
    /// <param name="reportCaret">Writes the caret of the editor into the view model.</param>
    public EditorSelectionSync(TextEditor editor, Action<int, int> reportSelection, Action<Caret> reportCaret)
    {
        _editor = editor;
        _reportSelection = reportSelection;
        _reportCaret = reportCaret;
        _editor.TextArea.Caret.PositionChanged += OnCaretChanged;
        _editor.TextArea.SelectionChanged += OnSelectionChanged;
    }

    /// <summary>Changes the editor, for example its document, without reporting the caret and selection it resets.</summary>
    public void ChangeEditor(Action change)
    {
        bool outer = !_changingEditor;
        _changingEditor = true;
        try
        {
            change();
        }
        finally
        {
            if (outer)
            {
                _changingEditor = false;
            }
        }
    }

    /// <summary>Moves the caret of the editor to the caret of the view model.</summary>
    public void ApplyCaret(int offset)
    {
        ChangeEditor(() =>
        {
            _editor.TextArea.Caret.Offset = Math.Clamp(offset, 0, _editor.Document.TextLength);
            try
            {
                _editor.TextArea.Caret.BringCaretToView();
            }
            catch (ArgumentException)
            {
                // A document without a laid out view has no position to scroll to yet.
            }
        });
    }

    /// <summary>Shows the selection of the view model; a report from the editor in progress is not echoed back.</summary>
    public void ApplySelection(int start, int length, int caretOffset)
    {
        if (_reportingSelection)
        {
            return;
        }

        int textLength = _editor.Document.TextLength;
        int clampedStart = Math.Clamp(start, 0, textLength);
        int clampedLength = Math.Clamp(length, 0, textLength - clampedStart);
        ChangeEditor(() =>
        {
            if (clampedLength == 0)
            {
                // Selecting an empty range would move the caret to its start.
                _editor.TextArea.ClearSelection();
            }
            else
            {
                _editor.Select(clampedStart, clampedLength);
            }

            if (caretOffset >= clampedStart && caretOffset <= clampedStart + clampedLength)
            {
                _editor.TextArea.Caret.Offset = caretOffset;
            }
        });
        _reportCaret(_editor.TextArea.Caret);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _editor.TextArea.Caret.PositionChanged -= OnCaretChanged;
        _editor.TextArea.SelectionChanged -= OnSelectionChanged;
    }

    private void OnCaretChanged(object? sender, EventArgs e)
    {
        if (!_changingEditor)
        {
            _reportCaret(_editor.TextArea.Caret);
        }
    }

    // The view model reports the first of the two selection values before the second is written; that report must
    // not reach the editor.
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (_changingEditor || _reportingSelection)
        {
            return;
        }

        _reportingSelection = true;
        try
        {
            _reportSelection(_editor.SelectionStart, _editor.SelectionLength);
        }
        finally
        {
            _reportingSelection = false;
        }
    }
}
