using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using AvaloniaEdit;
using AvaloniaEdit.CodeCompletion;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
using AvaloniaEdit.TextMate;
using Avalonia.Styling;
using ReactiveUI;
using System.Reactive.Disposables;
using Serilog;
using System.Reactive.Linq;
using TextMateSharp.Grammars;
using XamlVisualEditor.CodeEditor;
using XmlFoldingStrategy = XamlVisualEditor.CodeEditor.XmlFoldingStrategy;
using XamlVisualEditor.App.Services;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Designer.DragDrop;
using XamlVisualEditor.Xaml.Intellisense;

using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Code-behind for the XAML code editor view.
/// Sets up TextMate syntax highlighting for XML/XAML and wires CompletionWindow / InsightWindow.
/// The Document is assigned programmatically rather than via compiled binding
/// to ensure AvaloniaEdit's TextEditor properly initializes with the shared TextDocument.
/// </summary>
public sealed partial class XamlCodeEditorView : UserControl, IDisposable
{
    private TextMate.Installation? _textMateInstallation;
    private CompletionWindow? _completionWindow;
    private OverloadInsightWindow? _insightWindow;
    private TextEditor? _textEditor;
    private EditorSelectionSync? _selectionSync;
    private CompositeDisposable? _vmSubscriptions;
    private ThemeName? _currentTextMateTheme;
    private FoldingManager? _foldingManager;
    private readonly XmlFoldingStrategy _foldingStrategy = new();
    private IDisposable? _foldingSubscription;
    private readonly HoverRangeColorizer _hoverRangeColorizer = new();
    private CancellationTokenSource? _hoverCts;

    public XamlCodeEditorView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
        DetachedFromLogicalTree += OnDetachedFromLogicalTree;
        AddHandler(KeyDownEvent, OnHistoryKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        LostFocus += OnEditorLostFocus;
    }

    private void OnHistoryKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm ||
            (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 ||
            (e.KeyModifiers & KeyModifiers.Alt) != 0 || e.Key is not (Key.Z or Key.Y))
        {
            return;
        }

        bool redo = e.Key == Key.Y || (e.KeyModifiers & KeyModifiers.Shift) != 0;
        System.Windows.Input.ICommand command = redo ? vm.RedoCommand : vm.UndoCommand;
        if (command.CanExecute(null))
        {
            command.Execute(null);
        }

        e.Handled = true;
    }

    private void OnEditorLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            vm.FlushPendingTextChanges();
        }
    }

    // The dock caches the view of a hidden tab and attaches it again, so a detach keeps the editor set up with its
    // foldings and tokenized lines; only the popups and the pending hover request end here.
    private void OnDetachedFromLogicalTree(object? sender, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        _completionWindow?.Close();
        _completionWindow = null;
        _insightWindow?.Close();
        _insightWindow = null;

        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = null;
    }

    /// <summary>Releases the editor setup when the view is discarded, for example when its tab closes.</summary>
    public void Dispose()
    {
        // Dispose TextMate native resources
        _textMateInstallation?.Dispose();
        _textMateInstallation = null;
        _currentTextMateTheme = null;
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;

        _completionWindow?.Close();
        _completionWindow = null;
        _insightWindow?.Close();
        _insightWindow = null;

        // Unsubscribe event handlers
        if (_textEditor is not null)
        {
            _textEditor.TextArea.TextEntered -= OnTextEntered;
            _textEditor.TextArea.TextEntering -= OnTextEntering;
            _textEditor.TextArea.TextView.PointerMoved -= OnPointerMoved;
            _textEditor.RemoveHandler(DragDrop.DragOverEvent, OnDragOver);
            _textEditor.RemoveHandler(DragDrop.DropEvent, OnDrop);
        }

        _selectionSync?.Dispose();
        _selectionSync = null;

        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = null;

        _foldingSubscription?.Dispose();
        _foldingSubscription = null;
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }

        _vmSubscriptions?.Dispose();
        _vmSubscriptions = null;

        _textEditor = null;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        // When the DataContext changes, re-bind the document
        if (_textEditor is not null && DataContext is CodeEditorViewModel vm)
        {
            BindViewModel(vm);
        }
    }

    // Dock keeps the view of a document while its tab is hidden; a load after the view was disposed sets the editor
    // up again.
    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_textEditor is not null)
        {
            return;
        }

        _textEditor = this.FindControl<TextEditor>("TextEditor");
        if (_textEditor is null)
        {
            return;
        }

        // Wire up caret and selection tracking
        _selectionSync = new EditorSelectionSync(_textEditor, ReportSelection, ReportCaret);

        // Wire text input for auto-completion triggers
        _textEditor.TextArea.TextEntered += OnTextEntered;
        _textEditor.TextArea.TextEntering += OnTextEntering;

        _textEditor.AddHandler(DragDrop.DragOverEvent,
            OnDragOver,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);
        _textEditor.AddHandler(DragDrop.DropEvent,
            OnDrop,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);

        // Bind the ViewModel if already set
        if (DataContext is CodeEditorViewModel codeVm)
        {
            BindViewModel(codeVm);
        }

        // Install TextMate with XML grammar for XAML highlighting, after the document is set: installing it on the
        // empty initial document first creates a second tokenizer, which waits for the grammar the first one
        // compiles and then tokenizes nothing until the view scrolls.
        ActualThemeVariantChanged -= OnActualThemeVariantChanged;
        ActualThemeVariantChanged += OnActualThemeVariantChanged;
        ApplyTextMateTheme();
    }

    // Runs after the caret sync has brought the caret just into view, so a line at the viewport edge moves to the
    // middle; a line that already lies in the middle part of the viewport stays where it is.
    private void RevealLine(int line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_textEditor?.Document is { } document && line >= 1 && line <= document.LineCount)
                _textEditor.ScrollToLine(line);
        }, DispatcherPriority.Background);
    }

    private void OnActualThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyTextMateTheme();
    }

    private void ApplyTextMateTheme()
    {
        if (_textEditor is null)
        {
            return;
        }

        ThemeName targetTheme = ResolveTextMateTheme(ActualThemeVariant);
        if (_textMateInstallation is not null && _currentTextMateTheme == targetTheme)
        {
            return;
        }

        _textMateInstallation?.Dispose();

        RegistryOptions registryOptions = new(targetTheme);
        _textMateInstallation = _textEditor.InstallTextMate(registryOptions);
        TextMateEditorColors.Apply(_textEditor, _textMateInstallation);
        _textMateInstallation.SetGrammar(registryOptions.GetScopeByLanguageId("xml"));
        _currentTextMateTheme = targetTheme;
    }

    private static ThemeName ResolveTextMateTheme(ThemeVariant themeVariant)
    {
        return themeVariant == ThemeVariant.Light
            ? ThemeName.LightPlus
            : ThemeName.DarkPlus;
    }

    /// <summary>
    /// Sets the shared TextDocument on the TextEditor and subscribes
    /// to ViewModel property changes for font size, word wrap, etc.
    /// </summary>
    private void BindViewModel(CodeEditorViewModel vm)
    {
        if (_textEditor is null || _selectionSync is not { } selectionSync)
        {
            return;
        }

        _vmSubscriptions?.Dispose();
        _vmSubscriptions = new CompositeDisposable();

        // Set the shared TextDocument directly — this is the critical line. The editor resets its caret and
        // selection for the new document; the view model keeps its own, which the subscriptions below apply.
        selectionSync.ChangeEditor(() => _textEditor.Document = vm.Document);
        if (_textEditor.TextArea.LeftMargins.FirstOrDefault(m => m is FoldingMargin) is null)
        {
            _textEditor.TextArea.LeftMargins.Insert(0, new FoldingMargin());
        }

        _foldingManager ??= FoldingManager.Install(_textEditor.TextArea);

        // Next to the text, so the marker of the selected element sits between the line numbers and the code.
        if (_textEditor.TextArea.LeftMargins.OfType<SelectedElementMargin>().FirstOrDefault() is not { } selectedElementMargin)
        {
            selectedElementMargin = new SelectedElementMargin();
            _textEditor.TextArea.LeftMargins.Add(selectedElementMargin);
        }

        _vmSubscriptions.Add(vm.WhenAnyValue(x => x.SelectedElementLines)
            .Subscribe(lines => selectedElementMargin.Lines = lines));
        _vmSubscriptions.Add(vm.WhenAnyValue(x => x.DiagnosticLines)
            .Subscribe(lines => selectedElementMargin.DiagnosticLines = lines));
        _vmSubscriptions.Add(vm.RevealLineRequests.Subscribe(RevealLine));

        _foldingSubscription?.Dispose();
        _foldingSubscription = Observable.FromEventPattern<EventHandler, EventArgs>(
                h => vm.Document.TextChanged += h,
                h => vm.Document.TextChanged -= h)
            .Throttle(TimeSpan.FromMilliseconds(200))
            .Subscribe(_ => Dispatcher.UIThread.Post(UpdateFoldings));
        _vmSubscriptions.Add(_foldingSubscription);
        UpdateFoldings();

        // Sync editor properties from the ViewModel
        _textEditor.FontSize = vm.FontSize;
        _textEditor.ShowLineNumbers = vm.ShowLineNumbers;
        _textEditor.WordWrap = vm.WordWrap;

        // Subscribe to ViewModel property changes
        IDisposable fontSizeSubscription = vm.WhenAnyValue(x => x.FontSize)
            .Subscribe(size => _textEditor.FontSize = size);
        _vmSubscriptions.Add(fontSizeSubscription);

        IDisposable lineNumbersSubscription = vm.WhenAnyValue(x => x.ShowLineNumbers)
            .Subscribe(show => _textEditor.ShowLineNumbers = show);
        _vmSubscriptions.Add(lineNumbersSubscription);

        IDisposable wordWrapSubscription = vm.WhenAnyValue(x => x.WordWrap)
            .Subscribe(wrap => _textEditor.WordWrap = wrap);
        _vmSubscriptions.Add(wordWrapSubscription);

        IDisposable caretSubscription = vm.WhenAnyValue(x => x.CaretOffset)
            .Subscribe(selectionSync.ApplyCaret);
        _vmSubscriptions.Add(caretSubscription);

        // The pair can still hold the old value of the property that changes second, so the selection is read
        // from the view model itself.
        IDisposable selectionSubscription = vm.WhenAnyValue(x => x.SelectionStart, x => x.SelectionLength)
            .Subscribe(_ => selectionSync.ApplySelection(vm.SelectionStart, vm.SelectionLength, vm.CaretOffset));
        _vmSubscriptions.Add(selectionSubscription);

        // Add diagnostic colorizer for error squiggles
        if (!_textEditor.TextArea.TextView.LineTransformers.Contains(vm.DiagnosticColorizer))
        {
            _textEditor.TextArea.TextView.LineTransformers.Add(vm.DiagnosticColorizer);
        }

        if (!_textEditor.TextArea.TextView.LineTransformers.Contains(vm.ExecutionLineColorizer))
        {
            _textEditor.TextArea.TextView.LineTransformers.Add(vm.ExecutionLineColorizer);
        }

        if (!_textEditor.TextArea.TextView.LineTransformers.Contains(vm.BreakpointLineColorizer))
        {
            _textEditor.TextArea.TextView.LineTransformers.Add(vm.BreakpointLineColorizer);
        }

        if (!_textEditor.TextArea.TextView.LineTransformers.Contains(vm.SemanticTokenColorizer))
        {
            _textEditor.TextArea.TextView.LineTransformers.Add(vm.SemanticTokenColorizer);
        }

        if (!_textEditor.TextArea.TextView.LineTransformers.Contains(_hoverRangeColorizer))
        {
            _textEditor.TextArea.TextView.LineTransformers.Add(_hoverRangeColorizer);
        }

        IDisposable executionLineSubscription = vm.WhenAnyValue(x => x.ExecutionLine)
            .Subscribe(line =>
            {
                vm.ExecutionLineColorizer.LineNumber = line;
                _textEditor.TextArea.TextView.InvalidateVisual();
            });
        _vmSubscriptions.Add(executionLineSubscription);

        IDisposable breakpointHighlightSubscription = vm.WhenAnyValue(x => x.BreakpointHighlightVersion)
            .Subscribe(_ => _textEditor.TextArea.TextView.InvalidateVisual());
        _vmSubscriptions.Add(breakpointHighlightSubscription);

        IDisposable semanticTokenSubscription = vm.WhenAnyValue(x => x.SemanticTokenVersion)
            .Subscribe(_ => _textEditor.TextArea.TextView.InvalidateVisual());
        _vmSubscriptions.Add(semanticTokenSubscription);

        _textEditor.TextArea.TextView.PointerMoved -= OnPointerMoved;
        _textEditor.TextArea.TextView.PointerMoved += OnPointerMoved;
    }

    private void ReportCaret(Caret caret)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            vm.CaretOffset = caret.Offset;
            vm.CurrentLine = caret.Line;
            vm.CurrentColumn = caret.Column;
        }
    }

    // Edit commands and extensions read the selection from the view model.
    private void ReportSelection(int start, int length)
    {
        if (DataContext is CodeEditorViewModel vm)
        {
            vm.SelectionStart = start;
            vm.SelectionLength = length;
        }
    }

    private void UpdateHoverHighlight(DocumentLine line, XamlDiagnostic diag)
    {
        if (_textEditor?.Document is null)
        {
            return;
        }

        int start = line.Offset + Math.Max(0, diag.Column - 1);
        int end = diag.Length > 0
            ? Math.Min(line.EndOffset, start + diag.Length)
            : line.EndOffset;

        int length = _textEditor.Document.TextLength;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, 0, length);

        _hoverRangeColorizer.UpdateRange(start, end);
        _textEditor.TextArea.TextView.InvalidateVisual();
    }

    private void ClearHoverHighlight()
    {
        if (_textEditor is null)
        {
            return;
        }

        _hoverRangeColorizer.Clear();
        _textEditor.TextArea.TextView.InvalidateVisual();
    }

    /// <summary>Gets how often the folding regions were computed again for the text.</summary>
    internal int FoldingUpdateCount { get; private set; }

    private void UpdateFoldings()
    {
        if (_textEditor?.Document is null || _foldingManager is null)
        {
            return;
        }

        FoldingUpdateCount++;
        TextDocument document = _textEditor.Document;
        int length = document.TextLength;
        if (length == 0)
        {
            try
            {
                _foldingManager.UpdateFoldings(Array.Empty<NewFolding>(), -1);
            }
            catch (ArgumentException)
            {
            }
            return;
        }
        IEnumerable<NewFolding> foldings = _foldingStrategy.CreateNewFoldings(document)
            .Select(folding =>
            {
                int start = Math.Clamp(folding.StartOffset, 0, length);
                int end = Math.Clamp(folding.EndOffset, 0, length);
                return new NewFolding(start, end) { Name = folding.Name };
            })
            .Where(folding => folding.EndOffset > folding.StartOffset)
            .OrderBy(folding => folding.StartOffset)
            .ToList();

        int currentLength = document.TextLength;
        // Regions folded by the user from a selection are not produced by the strategy; they are passed in again so
        // that the update keeps them, and one of them replaces a strategy region with the same start.
        List<FoldingSection> manualSections = _foldingManager.AllFoldings
            .Where(section => TextEditorViewHost.IsManualFolding(section) && section.Length > 0)
            .ToList();
        List<NewFolding> safeFoldings = foldings
            .Where(folding => folding.StartOffset >= 0 && folding.EndOffset <= currentLength)
            .Where(folding => manualSections.All(section => section.StartOffset != folding.StartOffset))
            .Concat(manualSections.Select(section => new NewFolding(section.StartOffset, section.EndOffset)
            {
                Name = section.Title,
                DefaultClosed = section.IsFolded
            }))
            .OrderBy(folding => folding.StartOffset)
            .ToList();
        List<(int Start, int End, string Title, bool IsFolded)> manual = manualSections
            .Select(section => (section.StartOffset, section.EndOffset, section.Title, section.IsFolded))
            .ToList();

        try
        {
            _foldingManager.UpdateFoldings(safeFoldings, -1);
        }
        catch (ArgumentException)
        {
        }

        // The update matches existing regions by start offset only, so the manual marker and fold state are set again.
        foreach ((int start, int end, string title, bool isFolded) in manual)
        {
            if (_foldingManager.AllFoldings.FirstOrDefault(candidate =>
                    candidate.StartOffset == start && candidate.EndOffset == end) is { } section)
            {
                TextEditorViewHost.MarkManualFolding(section);
                section.Title = title;
                section.IsFolded = isFolded;
            }
        }
    }

    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        // When the completion window is open and the user types something,
        // let the window handle insertion (e.g., completing with Enter/Tab)
        if (_completionWindow is not null && e.Text is { Length: > 0 })
        {
            if (TryHandleCommitCharacter(e, e.Text[0]))
            {
                return;
            }

            if (!char.IsLetterOrDigit(e.Text[0]) && e.Text[0] != '.' && e.Text[0] != ':' && e.Text[0] != '_')
            {
                _completionWindow.CompletionList.RequestInsertion(e);
            }
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        IDataTransfer data = e.DataTransfer;
        if (!data.Contains(DesignerDataFormats.ToolboxItem))
        {
            return;
        }

        e.DragEffects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (_textEditor is null || _textEditor.Document is null)
        {
            return;
        }

        IDataTransfer data = e.DataTransfer;
        if (!data.Contains(DesignerDataFormats.ToolboxItem))
        {
            return;
        }

        string? typeName = data.TryGetValue(DesignerDataFormats.ToolboxItem);
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return;
        }

        int offset = _textEditor.TextArea.Caret.Offset;
        int insertedLength = InsertToolboxSnippet(_textEditor.Document, offset, typeName.Trim());
        _textEditor.TextArea.Caret.Offset = Math.Clamp(offset + insertedLength, 0, _textEditor.Document.TextLength);
        _textEditor.Focus();
        e.Handled = true;
    }

    private static int InsertToolboxSnippet(TextDocument document, int offset, string typeName)
    {
        int clampedOffset = Math.Clamp(offset, 0, document.TextLength);
        DocumentLine line = document.GetLineByOffset(clampedOffset);
        string lineText = document.GetText(line);

        string indent = GetLineIndent(lineText);
        bool needsLeadingNewLine = clampedOffset > line.Offset && clampedOffset < line.EndOffset;
        bool needsTrailingNewLine = clampedOffset < document.TextLength &&
                                   !IsNewLineAt(document, clampedOffset);

        string snippet = BuildSnippet(typeName, indent, needsLeadingNewLine, needsTrailingNewLine);

        document.Insert(clampedOffset, snippet);
        return snippet.Length;
    }

    private static string BuildSnippet(string typeName, string indent, bool leadingNewLine, bool trailingNewLine)
    {
        string newline = Environment.NewLine;
        string snippet = $"{indent}<{typeName} />";

        if (leadingNewLine)
        {
            snippet = newline + snippet;
        }

        if (trailingNewLine)
        {
            snippet += newline;
        }

        return snippet;
    }

    private static string GetLineIndent(string lineText)
    {
        int count = 0;
        while (count < lineText.Length && char.IsWhiteSpace(lineText[count]))
        {
            if (lineText[count] == '\r' || lineText[count] == '\n')
            {
                break;
            }
            count++;
        }

        return count == 0 ? string.Empty : lineText.Substring(0, count);
    }

    private static bool IsNewLineAt(TextDocument document, int offset)
    {
        if (offset < 0 || offset >= document.TextLength)
        {
            return false;
        }

        char current = document.GetCharAt(offset);
        return current == '\n' || current == '\r';
    }

    private async void OnTextEntered(object? sender, TextInputEventArgs e)
    {
        if (DataContext is not CodeEditorViewModel vm || _textEditor is null)
        {
            return;
        }

        // Determine trigger character
        char trigger = e.Text is { Length: > 0 } ? e.Text[0] : '\0';

        // Trigger completion on '<', ' ' (inside tag), '=' or '"' (attribute values), ':'
        if (trigger is '<' or ' ' or '=' or '"' or ':' or '/')
        {
            await ShowCompletionWindowAsync(vm, CompletionTrigger.CharacterTyped, trigger);
        }

        if (trigger is '(' or ',')
        {
            await ShowSignatureHelpAsync(vm);
        }

        if (trigger == '{')
        {
            bool shown = await ShowSignatureHelpAsync(vm);
            if (!shown)
            {
                ShowInsightWindow();
            }
        }
    }

    private async Task ShowCompletionWindowAsync(
        CodeEditorViewModel vm,
        CompletionTrigger trigger,
        char triggerCharacter)
    {
        if (_textEditor is null)
        {
            return;
        }

        // Close existing window
        _completionWindow?.Close();
        _completionWindow = null;

        string text = _textEditor.Document.Text;
        int offset = _textEditor.CaretOffset;

        if (offset < 0 || offset > text.Length)
        {
            return;
        }

        CompletionContext context = new()
        {
            TextBefore = text[..offset],
            DocumentText = text,
            FilePath = vm.FilePath,
            LanguageId = vm.LanguageId,
            Offset = offset,
            Trigger = trigger,
            TriggerCharacter = triggerCharacter
        };

        IReadOnlyList<CompletionItem> items = await vm.GetCompletionsAsync(context);

        if (items.Count == 0)
        {
            return;
        }

        _completionWindow = new CompletionWindow(_textEditor.TextArea);
        IList<ICompletionData> data = _completionWindow.CompletionList.CompletionData;

        foreach (CompletionItem item in items)
        {
            data.Add(new AvaloniaCompletionData(item));
        }

        _completionWindow.Show();
        _completionWindow.Closed += (_, _) => _completionWindow = null;
    }

    private bool TryHandleCommitCharacter(TextInputEventArgs e, char character)
    {
        if (_completionWindow?.CompletionList.SelectedItem is not ICommitCharactersProvider provider)
        {
            return false;
        }

        if (provider.CommitCharacters.Count == 0)
        {
            return false;
        }

        if (!provider.CommitCharacters.Contains(character))
        {
            return false;
        }

        _completionWindow.CompletionList.RequestInsertion(e);

        return true;
    }

    private async Task<bool> ShowSignatureHelpAsync(CodeEditorViewModel vm)
    {
        if (_textEditor is null)
        {
            return false;
        }

        _insightWindow?.Close();
        _insightWindow = null;

        LanguageSignatureHelp? help = await vm.GetSignatureHelpAsync(_textEditor.CaretOffset);
        if (help is null || help.Signatures.Count == 0)
        {
            return false;
        }

        _insightWindow = new OverloadInsightWindow(_textEditor.TextArea)
        {
            Provider = new LanguageSignatureHelpProvider(help)
        };
        _insightWindow.Show();
        _insightWindow.Closed += (_, _) => _insightWindow = null;
        return true;
    }

    private void ShowInsightWindow()
    {
        if (_textEditor is null)
        {
            return;
        }

        _insightWindow?.Close();
        _insightWindow = null;

        _insightWindow = new OverloadInsightWindow(_textEditor.TextArea)
        {
            Provider = new MarkupExtensionInsightProvider()
        };
        _insightWindow.Show();
        _insightWindow.Closed += (_, _) => _insightWindow = null;
    }

    private async void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_textEditor is null || DataContext is not CodeEditorViewModel vm)
        {
            return;
        }

        int? offset = GetOffsetFromPoint(_textEditor, e.GetPosition(_textEditor.TextArea.TextView));
        if (offset is null || offset < 0 || offset >= _textEditor.Document.TextLength)
        {
            ToolTip.SetTip(_textEditor, null);
            ClearHoverHighlight();
            return;
        }

        DocumentLine line = _textEditor.Document.GetLineByOffset(offset.Value);
        int col = offset.Value - line.Offset + 1;

        XamlDiagnostic? diag = vm.DiagnosticColorizer.GetDiagnosticAt(line.LineNumber, col);
        if (diag is not null)
        {
            string severity = diag.Severity.ToString();
            ToolTip.SetTip(_textEditor, $"[{severity}] {diag.Message}");
            UpdateHoverHighlight(line, diag);
            return;
        }

        _hoverCts?.Cancel();
        _hoverCts?.Dispose();
        _hoverCts = new CancellationTokenSource();
        CancellationToken token = _hoverCts.Token;

        try
        {
            LanguageHover? hover = await vm.GetHoverAsync(offset.Value, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            string? formatted = FormatHoverText(hover?.Contents);
            ToolTip.SetTip(_textEditor, formatted);
            UpdateHoverHighlight(vm, hover);
        }
        catch (OperationCanceledException)
        {
            // Ignore canceled hover requests (e.g., rapid mouse movement or LSP shutdown).
        }
    }

    private void UpdateHoverHighlight(CodeEditorViewModel vm, LanguageHover? hover)
    {
        if (_textEditor?.Document is null || hover?.Range is null)
        {
            ClearHoverHighlight();
            return;
        }

        LanguageTextRange range = hover.Range.Value;
        int startOffset = vm.GetOffsetForLineColumn(range.Start.Line, range.Start.Column);
        int endOffset = vm.GetOffsetForLineColumn(range.End.Line, range.End.Column);
        int length = _textEditor.Document.TextLength;
        startOffset = Math.Clamp(startOffset, 0, length);
        endOffset = Math.Clamp(endOffset, 0, length);

        _hoverRangeColorizer.UpdateRange(startOffset, endOffset);
        _textEditor.TextArea.TextView.InvalidateVisual();
    }

    private static string? FormatHoverText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string formatted = text;

        formatted = formatted.Replace("```", string.Empty, StringComparison.Ordinal)
            .Replace("`", string.Empty, StringComparison.Ordinal)
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal);

        formatted = Regex.Replace(formatted, "\\[([^\\]]+)\\]\\([^\\)]+\\)", "$1");

        return formatted.Trim();
    }

    private static int? GetOffsetFromPoint(TextEditor editor, Avalonia.Point point)
    {
        try
        {
            AvaloniaEdit.Rendering.TextView textView = editor.TextArea.TextView;
            Avalonia.Point tvPos = point;
            AvaloniaEdit.Rendering.VisualLine? visualLine = textView.GetVisualLineFromVisualTop(tvPos.Y + textView.ScrollOffset.Y);
            if (visualLine is null)
            {
                return null;
            }

            int vc = visualLine.GetVisualColumn(tvPos);
            return visualLine.GetRelativeOffset(vc) + visualLine.FirstDocumentLine.Offset;
        }
        catch (Exception ex)
        {
            Log.Logger.Warning("Editor position lookup failed: {Message}", ex.Message);
            return null;
        }
    }
}

/// <summary>
/// Bridges the internal CompletionItem to AvaloniaEdit's ICompletionData.
/// </summary>
internal sealed class AvaloniaCompletionData : ICompletionData
    , ICommitCharactersProvider
{
    private readonly CompletionItem _item;

    public AvaloniaCompletionData(CompletionItem item)
    {
        _item = item;
    }

    public string Text => _item.InsertText ?? _item.DisplayText;

    public object Content => _item.DisplayText;

    public object? Description => _item.Description;

    public double Priority => _item.Priority;

    public IReadOnlyList<char> CommitCharacters => _item.CommitCharacters ?? Array.Empty<char>();

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        if (_item.TextEdit is not null)
        {
            int offset = Math.Clamp(_item.TextEdit.Offset, 0, textArea.Document.TextLength);
            int length = Math.Clamp(_item.TextEdit.Length, 0, textArea.Document.TextLength - offset);
            textArea.Document.Replace(offset, length, _item.TextEdit.NewText);
            return;
        }

        textArea.Document.Replace(completionSegment, Text);
    }

    // AvaloniaEdit ICompletionData requires an Image property (can be null)
    public Avalonia.Media.IImage? Image => null;
}

/// <summary>
/// Basic insight provider for markup extension parameter hints.
/// </summary>
internal sealed class MarkupExtensionInsightProvider : IOverloadProvider
{
    private static readonly string[] s_hints =
    [
        "{Binding Path=, ElementName=, Mode=, Converter=}",
        "{StaticResource ResourceKey}",
        "{DynamicResource ResourceKey}",
        "{TemplateBinding Property}",
        "{x:Static Member}",
        "{x:Type TypeName}"
    ];

    private int _selectedIndex;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            _selectedIndex = value;
            OnPropertyChanged(nameof(SelectedIndex));
            OnPropertyChanged(nameof(CurrentHeader));
            OnPropertyChanged(nameof(CurrentContent));
            OnPropertyChanged(nameof(CurrentIndexText));
        }
    }

    public int Count => s_hints.Length;

    public string CurrentIndexText => $"{SelectedIndex + 1} of {Count}";

    public object CurrentHeader => s_hints[SelectedIndex];

    public object CurrentContent => UiStrings.MarkupParameterHints;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
}
