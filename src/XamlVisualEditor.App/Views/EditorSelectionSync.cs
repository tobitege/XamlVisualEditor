using System;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

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
    private bool _waitingForLayout;
    private bool _disposed;

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
            BringCaretToView();
        });
    }

    // Before its first layout the text view has neither its width nor the width of the gutter, so scrolling would
    // move the view by a wrong distance; the caret then comes into view once the text view has built its lines.
    private void BringCaretToView()
    {
        TextView view = _editor.TextArea.TextView;
        if (view.VisualLinesValid && view.Bounds.Width > 0)
        {
            _editor.TextArea.Caret.BringCaretToView();
            return;
        }

        if (_waitingForLayout)
        {
            return;
        }

        _waitingForLayout = true;
        view.VisualLinesChanged += OnFirstVisualLines;
    }

    private void OnFirstVisualLines(object? sender, EventArgs e)
    {
        TextView view = _editor.TextArea.TextView;
        if (!view.VisualLinesValid || view.Bounds.Width <= 0)
        {
            return;
        }

        view.VisualLinesChanged -= OnFirstVisualLines;
        Dispatcher.UIThread.Post(() =>
        {
            _waitingForLayout = false;
            if (!_disposed)
            {
                _editor.TextArea.Caret.BringCaretToView();
            }
        }, DispatcherPriority.Background);
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
        _disposed = true;
        _editor.TextArea.TextView.VisualLinesChanged -= OnFirstVisualLines;
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
