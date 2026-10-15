using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Folding;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Applies the text editor view options to the registered editors and runs view actions on the editor of the active
/// document. The options are the only source of font size, wrapping and line numbers: the document view models
/// show them, and a change of one of them in a document view model becomes the new option.
/// </summary>
public sealed class TextEditorViewHost : ITextEditorViewHost, IDisposable
{
    /// <summary>Gets the application resource key under which editors find the host.</summary>
    public const string RESOURCE_KEY = "XveTextEditorViewHost";

    // Color options are set with animation priority: they win over the local values of the theme and, when removed,
    // leave whatever the theme set.
    private static readonly AttachedProperty<IDisposable?> BackgroundOverrideProperty =
        AvaloniaProperty.RegisterAttached<TextEditorViewHost, TextEditor, IDisposable?>("BackgroundOverride");

    private static readonly AttachedProperty<IDisposable?> LineNumbersOverrideProperty =
        AvaloniaProperty.RegisterAttached<TextEditorViewHost, TextEditor, IDisposable?>("LineNumbersOverride");

    private static readonly AttachedProperty<IDisposable?> RulerOverrideProperty =
        AvaloniaProperty.RegisterAttached<TextEditorViewHost, TextEditor, IDisposable?>("RulerOverride");

    private static readonly AttachedProperty<ITextEditorDisplayState?> DisplayStateProperty =
        AvaloniaProperty.RegisterAttached<TextEditorViewHost, TextEditor, ITextEditorDisplayState?>("DisplayState");

    private readonly List<WeakReference<TextEditor>> _editors = new();
    private readonly IContextKeys? _contextKeys;
    private readonly IDisposable _activeDocumentSubscription;
    private object? _activeDocument;
    private TextEditorViewOptions _options = new();
    private bool _applying;

    /// <summary>Creates the host.</summary>
    /// <param name="contextMenuItems">The items of the editor context menu.</param>
    /// <param name="activeDocument">Reports the view model of the active document, which is the data context of its editor.</param>
    /// <param name="contextKeys">Receives <see cref="TextEditorContextKeys.HasActiveEditor"/>.</param>
    public TextEditorViewHost(
        ObservableCollection<ExtensionMenuItemViewModel> contextMenuItems,
        IObservable<object?> activeDocument,
        IContextKeys? contextKeys = null)
    {
        ContextMenuItems = contextMenuItems;
        _contextKeys = contextKeys;
        _activeDocumentSubscription = activeDocument.Subscribe(document =>
        {
            _activeDocument = document;
            RefreshActiveEditorKey();
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _activeDocumentSubscription.Dispose();
    }

    /// <inheritdoc />
    public TextEditorViewOptions Options => _options;

    /// <inheritdoc />
    public bool HasActiveEditor => TryGetActiveEditor(out _);

    /// <summary>Gets the items of the editor context menu.</summary>
    public ObservableCollection<ExtensionMenuItemViewModel> ContextMenuItems { get; }

    /// <inheritdoc />
    public event EventHandler? OptionsChanged;

    /// <inheritdoc />
    public void SetOptions(TextEditorViewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        TextEditorViewOptions normalized = options.Normalize();
        if (Dispatcher.UIThread.CheckAccess())
        {
            SetOptionsCore(normalized);
            return;
        }

        Dispatcher.UIThread.Post(() => SetOptionsCore(normalized));
    }

    /// <inheritdoc />
    public Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Dispatcher.UIThread.CheckAccess())
        {
            return Task.FromResult(Execute(action));
        }

        return Dispatcher.UIThread.InvokeAsync(() => Execute(action)).GetTask();
    }

    /// <summary>Adds an editor and applies the options to it.</summary>
    public void Register(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        _editors.RemoveAll(reference => !reference.TryGetTarget(out TextEditor? existing) || ReferenceEquals(existing, editor));
        _editors.Add(new WeakReference<TextEditor>(editor));
        Apply(editor);
    }

    /// <summary>Removes an editor.</summary>
    public void Unregister(TextEditor editor)
    {
        _editors.RemoveAll(reference => !reference.TryGetTarget(out TextEditor? existing) || ReferenceEquals(existing, editor));
        Follow(editor, null);
        RefreshActiveEditorKey();
    }

    /// <summary>Applies the current options to one editor and follows the display state of its document.</summary>
    public void Apply(TextEditor editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        TextEditorViewOptions options = _options;
        ITextEditorDisplayState? state = editor.DataContext as ITextEditorDisplayState;
        Follow(editor, state);
        RefreshActiveEditorKey();

        _applying = true;
        try
        {
            if (state is not null)
            {
                state.FontSize = options.FontSize;
                state.WordWrap = options.WordWrap;
                state.ShowLineNumbers = options.ShowLineNumbers;
            }
            else
            {
                editor.FontSize = options.FontSize;
                editor.WordWrap = options.WordWrap;
                editor.ShowLineNumbers = options.ShowLineNumbers;
            }
        }
        finally
        {
            _applying = false;
        }

        editor.FontFamily = new FontFamily(options.FontFamily);

        TextEditorOptions editorOptions = editor.Options;
        editorOptions.ShowColumnRulers = options.ShowColumnRuler;
        editorOptions.ColumnRulerPositions = new[] { options.ColumnRulerPosition };
        editorOptions.HighlightCurrentLine = options.HighlightCurrentLine;
        editorOptions.ShowSpaces = options.ShowSpaces;
        editorOptions.ShowTabs = options.ShowTabs;
        editorOptions.ShowEndOfLine = options.ShowEndOfLine;
        editorOptions.ShowBoxForControlCharacters = options.ShowBoxForControlCharacters;
        editorOptions.InheritWordWrapIndentation = options.InheritWordWrapIndentation;
        editorOptions.WordWrapIndentation = options.WordWrapIndentation;
        editorOptions.AcceptsTab = options.AcceptsTab;
        editorOptions.ConvertTabsToSpaces = options.ConvertTabsToSpaces;
        editorOptions.IndentationSize = options.IndentationSize;
        editorOptions.EnableRectangularSelection = options.EnableRectangularSelection;
        editorOptions.EnableVirtualSpace = options.EnableVirtualSpace;
        editorOptions.EnableTextDragDrop = options.EnableTextDragDrop;
        editorOptions.CutCopyWholeLine = options.CutCopyWholeLine;
        editorOptions.HideCursorWhileTyping = options.HideCursorWhileTyping;
        editorOptions.AllowScrollBelowDocument = options.AllowScrollBelowDocument;
        editorOptions.EnableHyperlinks = options.EnableHyperlinks;
        editorOptions.EnableEmailHyperlinks = options.EnableEmailHyperlinks;
        editorOptions.RequireControlModifierForHyperlinkClick = options.RequireControlModifierForHyperlinkClick;
        editorOptions.AllowToggleOverstrikeMode = options.AllowToggleOverstrikeMode;
        editorOptions.ExtendSelectionOnMouseUp = options.ExtendSelectionOnMouseUp;
        editor.TextArea.RightClickMovesCaret = options.RightClickMovesCaret;

        foreach (FoldingMargin margin in editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.IsVisible = options.ShowFoldingMargin;
        }

        Override(editor, BackgroundOverrideProperty, editor, TextEditor.BackgroundProperty, ParseBrush(options.BackgroundColor));
        Override(editor, LineNumbersOverrideProperty, editor, TextEditor.LineNumbersForegroundProperty, ParseBrush(options.LineNumbersColor));
        Override(editor, RulerOverrideProperty, editor.TextArea.TextView, TextView.ColumnRulerPenProperty,
            ParseBrush(options.ColumnRulerColor) is { } ruler ? new Pen(ruler) : null);
    }

    private void SetOptionsCore(TextEditorViewOptions options)
    {
        if (options == _options)
        {
            return;
        }

        _options = options;
        _editors.RemoveAll(reference => !reference.TryGetTarget(out _));
        foreach (WeakReference<TextEditor> reference in _editors.ToList())
        {
            if (reference.TryGetTarget(out TextEditor? editor))
            {
                Apply(editor);
            }
        }

        OptionsChanged?.Invoke(this, EventArgs.Empty);
    }

    // The document view model of an editor changes its display state through the document toolbar.
    private void Follow(TextEditor editor, ITextEditorDisplayState? state)
    {
        ITextEditorDisplayState? followed = editor.GetValue(DisplayStateProperty);
        if (ReferenceEquals(followed, state))
        {
            return;
        }

        if (followed is not null)
        {
            followed.PropertyChanged -= OnDisplayStateChanged;
        }

        if (state is not null)
        {
            state.PropertyChanged += OnDisplayStateChanged;
        }

        editor.SetValue(DisplayStateProperty, state);
    }

    private void OnDisplayStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_applying || sender is not ITextEditorDisplayState state)
        {
            return;
        }

        TextEditorViewOptions changed = e.PropertyName switch
        {
            nameof(ITextEditorDisplayState.FontSize) => _options with { FontSize = state.FontSize },
            nameof(ITextEditorDisplayState.WordWrap) => _options with { WordWrap = state.WordWrap },
            nameof(ITextEditorDisplayState.ShowLineNumbers) => _options with { ShowLineNumbers = state.ShowLineNumbers },
            _ => _options
        };
        SetOptions(changed);
    }

    private static void Override<T>(
        TextEditor editor,
        AttachedProperty<IDisposable?> slot,
        AvaloniaObject target,
        StyledProperty<T> property,
        T value)
        where T : class?
    {
        editor.GetValue(slot)?.Dispose();
        editor.SetValue(slot, value is null ? null : target.SetValue(property, value, BindingPriority.Animation));
    }

    private static IBrush? ParseBrush(string? color)
    {
        return !string.IsNullOrWhiteSpace(color) && Color.TryParse(color, out Color parsed)
            ? new SolidColorBrush(parsed)
            : null;
    }

    private bool Execute(TextEditorViewAction action)
    {
        if (!TryGetActiveEditor(out TextEditor editor))
        {
            return false;
        }

        return action switch
        {
            TextEditorViewAction.Find => OpenSearch(editor, replace: false),
            TextEditorViewAction.Replace => OpenSearch(editor, replace: true),
            TextEditorViewAction.ExpandAllFoldings => SetAllFoldings(editor, isFolded: false),
            TextEditorViewAction.CollapseAllFoldings => SetAllFoldings(editor, isFolded: true),
            TextEditorViewAction.CollapseSelection => CollapseSelection(editor),
            _ => false
        };
    }

    private static bool OpenSearch(TextEditor editor, bool replace)
    {
        if (editor.SearchPanel is not { } searchPanel)
        {
            return false;
        }

        string selected = editor.SelectedText;
        if (selected.Length > 0 && selected.IndexOfAny(['\r', '\n']) < 0)
        {
            searchPanel.SearchPattern = selected;
        }

        searchPanel.IsReplaceMode = replace && !editor.IsReadOnly;
        searchPanel.Open();
        searchPanel.Reactivate();
        return true;
    }

    private static FoldingManager? GetFoldingManager(TextEditor editor)
    {
        return editor.TextArea.TextView.GetService(typeof(FoldingManager)) as FoldingManager;
    }

    private static bool SetAllFoldings(TextEditor editor, bool isFolded)
    {
        if (GetFoldingManager(editor) is not { } manager)
        {
            return false;
        }

        foreach (FoldingSection folding in manager.AllFoldings)
        {
            folding.IsFolded = isFolded;
        }

        return true;
    }

    // Without a selection the innermost region at the caret folds; a selection folds the regions it touches, or
    // becomes a folded region of its own when it touches none.
    private static bool CollapseSelection(TextEditor editor)
    {
        if (GetFoldingManager(editor) is not { } manager || editor.Document is not { LineCount: > 0 } document)
        {
            return false;
        }

        if (editor.SelectionLength <= 0)
        {
            int caretOffset = Math.Clamp(editor.TextArea.Caret.Offset, 0, document.TextLength);
            FoldingSection? innermost = manager.GetFoldingsContaining(caretOffset)
                .OrderBy(section => section.Length)
                .FirstOrDefault();
            if (innermost is not null)
            {
                innermost.IsFolded = true;
            }

            return innermost is not null;
        }

        (int startOffset, int endOffset) = GetSelectedLines(document, editor.SelectionStart, editor.SelectionLength);
        List<FoldingSection> touched = manager.AllFoldings
            .Where(section => section.StartOffset <= endOffset && section.EndOffset >= startOffset)
            .ToList();
        if (touched.Count == 0 && endOffset > startOffset)
        {
            FoldingSection created = manager.CreateFolding(startOffset, endOffset);
            created.Title = "...";
            touched.Add(created);
        }

        foreach (FoldingSection folding in touched)
        {
            folding.IsFolded = true;
        }

        return touched.Count > 0;
    }

    // The selected lines without blank lines at either end.
    private static (int Start, int End) GetSelectedLines(TextDocument document, int selectionStart, int selectionLength)
    {
        DocumentLine first = document.GetLineByOffset(selectionStart);
        DocumentLine last = document.GetLineByOffset(Math.Max(selectionStart, selectionStart + selectionLength - 1));
        while (first.LineNumber < last.LineNumber && IsBlank(document, first))
        {
            first = first.NextLine;
        }

        while (last.LineNumber > first.LineNumber && IsBlank(document, last))
        {
            last = last.PreviousLine;
        }

        return (first.Offset, last.EndOffset);
    }

    private static bool IsBlank(TextDocument document, DocumentLine line)
    {
        return string.IsNullOrWhiteSpace(document.GetText(line.Offset, line.Length));
    }

    // Menus disable the view actions while the active document shows no editor, for example the designer alone.
    private void RefreshActiveEditorKey()
    {
        _contextKeys?.Set(TextEditorContextKeys.HasActiveEditor, TryGetActiveEditor(out _));
    }

    private bool TryGetActiveEditor(out TextEditor editor)
    {
        object? document = _activeDocument;
        foreach (WeakReference<TextEditor> reference in _editors)
        {
            if (document is not null
                && reference.TryGetTarget(out TextEditor? candidate)
                && ReferenceEquals(candidate.DataContext, document)
                && TopLevel.GetTopLevel(candidate) is not null)
            {
                editor = candidate;
                return true;
            }
        }

        editor = null!;
        return false;
    }
}
