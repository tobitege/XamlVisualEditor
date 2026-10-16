using ReactiveUI.Primitives.Signals;
using ReactiveUI.Primitives.Concurrency;
using RxVoid = ReactiveUI.Primitives.RxVoid;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Extensions;

namespace XamlVisualEditor.TextEditingExtension.ViewModels;

/// <summary>Edits the text editor view options with a live preview; only saving keeps the changes.</summary>
public sealed partial class TextEditorOptionsViewModel : ReactiveObject, IDisposable
{
    private static readonly TimeSpan DefaultPreviewDelay = TimeSpan.FromMilliseconds(150);

    private readonly TextEditorViewOptions _original;
    private readonly Action<TextEditorViewOptions> _preview;
    private readonly SerialDisposable _previewSubscription = new();
    private bool _loading;

    /// <summary>Creates the view model for the current options.</summary>
    /// <param name="current">The options the editors show now.</param>
    /// <param name="fontFamilies">The installed font families offered for selection.</param>
    /// <param name="preview">Shows options in the editors while the dialog is open.</param>
    /// <param name="previewScheduler">Runs the preview; by default the UI thread.</param>
    /// <param name="previewDelay">Waits for a pause in the changes before the preview, so typing does not relayout every editor.</param>
    public TextEditorOptionsViewModel(
        TextEditorViewOptions current,
        IReadOnlyList<string> fontFamilies,
        Action<TextEditorViewOptions> preview,
        ISequencer? previewScheduler = null,
        TimeSpan? previewDelay = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(fontFamilies);
        ArgumentNullException.ThrowIfNull(preview);
        _original = current.Normalize();
        _preview = preview;
        FontFamilies = fontFamilies
            .Append(_original.FontFamily)
            .Append(new TextEditorViewOptions().FontFamily)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        BackgroundColors = EditorColorChoice.CreateChoices(_original.BackgroundColor);
        LineNumbersColors = EditorColorChoice.CreateChoices(_original.LineNumbersColor);
        ColumnRulerColors = EditorColorChoice.CreateChoices(_original.ColumnRulerColor);
        Load(_original);

        // Closing stops the preview first, so that no delayed preview follows the result.
        SaveCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _previewSubscription.Dispose();
            Result = BuildOptions();
            await CloseInteraction.Handle(true);
        });
        CancelCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            _previewSubscription.Dispose();
            _preview(_original);
            await CloseInteraction.Handle(false);
        });
        ResetCommand = ReactiveCommand.Create(() => Load(new TextEditorViewOptions()));

        _previewSubscription.Disposable = Changed
            .Where(_ => !_loading)
            .EmitIfQuiet(previewDelay ?? DefaultPreviewDelay, previewScheduler ?? RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => _preview(BuildOptions()));
    }

    /// <summary>Gets the offered font families.</summary>
    public IReadOnlyList<string> FontFamilies { get; }

    /// <summary>Gets the offered background colors.</summary>
    public IReadOnlyList<EditorColorChoice> BackgroundColors { get; }

    /// <summary>Gets the offered line number colors.</summary>
    public IReadOnlyList<EditorColorChoice> LineNumbersColors { get; }

    /// <summary>Gets the offered ruler colors.</summary>
    public IReadOnlyList<EditorColorChoice> ColumnRulerColors { get; }

    /// <summary>Gets the saved options after <see cref="SaveCommand"/>; <see langword="null"/> otherwise.</summary>
    public TextEditorViewOptions? Result { get; private set; }

    /// <summary>Saves the options and closes the dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SaveCommand { get; }

    /// <summary>Restores the previous options and closes the dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CancelCommand { get; }

    /// <summary>Loads the default options into the dialog.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ResetCommand { get; }

    /// <summary>Asks the view to close with the saved state.</summary>
    public Interaction<bool, RxVoid> CloseInteraction { get; } = new();

    [Reactive]
    public partial string FontFamily { get; set; } = string.Empty;

    [Reactive]
    public partial double FontSize { get; set; }

    [Reactive]
    public partial bool WordWrap { get; set; }

    [Reactive]
    public partial bool ShowLineNumbers { get; set; }

    [Reactive]
    public partial bool ShowFoldingMargin { get; set; }

    [Reactive]
    public partial bool ShowColumnRuler { get; set; }

    [Reactive]
    public partial int ColumnRulerPosition { get; set; }

    [Reactive]
    public partial bool HighlightCurrentLine { get; set; }

    [Reactive]
    public partial bool ShowSpaces { get; set; }

    [Reactive]
    public partial bool ShowTabs { get; set; }

    [Reactive]
    public partial bool ShowEndOfLine { get; set; }

    [Reactive]
    public partial bool ShowBoxForControlCharacters { get; set; }

    [Reactive]
    public partial bool InheritWordWrapIndentation { get; set; }

    [Reactive]
    public partial double WordWrapIndentation { get; set; }

    [Reactive]
    public partial bool AcceptsTab { get; set; }

    [Reactive]
    public partial bool ConvertTabsToSpaces { get; set; }

    [Reactive]
    public partial int IndentationSize { get; set; }

    [Reactive]
    public partial bool EnableRectangularSelection { get; set; }

    [Reactive]
    public partial bool EnableVirtualSpace { get; set; }

    [Reactive]
    public partial bool EnableTextDragDrop { get; set; }

    [Reactive]
    public partial bool CutCopyWholeLine { get; set; }

    [Reactive]
    public partial bool HideCursorWhileTyping { get; set; }

    [Reactive]
    public partial bool AllowScrollBelowDocument { get; set; }

    [Reactive]
    public partial bool EnableHyperlinks { get; set; }

    [Reactive]
    public partial bool EnableEmailHyperlinks { get; set; }

    [Reactive]
    public partial bool RequireControlModifierForHyperlinkClick { get; set; }

    [Reactive]
    public partial bool AllowToggleOverstrikeMode { get; set; }

    [Reactive]
    public partial bool ExtendSelectionOnMouseUp { get; set; }

    [Reactive]
    public partial bool RightClickMovesCaret { get; set; }

    [Reactive]
    public partial EditorColorChoice? BackgroundColor { get; set; }

    [Reactive]
    public partial EditorColorChoice? LineNumbersColor { get; set; }

    [Reactive]
    public partial EditorColorChoice? ColumnRulerColor { get; set; }

    /// <summary>Builds the options from the dialog state.</summary>
    public TextEditorViewOptions BuildOptions()
    {
        return new TextEditorViewOptions
        {
            FontFamily = FontFamily,
            FontSize = FontSize,
            WordWrap = WordWrap,
            ShowLineNumbers = ShowLineNumbers,
            ShowFoldingMargin = ShowFoldingMargin,
            ShowColumnRuler = ShowColumnRuler,
            ColumnRulerPosition = ColumnRulerPosition,
            HighlightCurrentLine = HighlightCurrentLine,
            ShowSpaces = ShowSpaces,
            ShowTabs = ShowTabs,
            ShowEndOfLine = ShowEndOfLine,
            ShowBoxForControlCharacters = ShowBoxForControlCharacters,
            InheritWordWrapIndentation = InheritWordWrapIndentation,
            WordWrapIndentation = WordWrapIndentation,
            AcceptsTab = AcceptsTab,
            ConvertTabsToSpaces = ConvertTabsToSpaces,
            IndentationSize = IndentationSize,
            EnableRectangularSelection = EnableRectangularSelection,
            EnableVirtualSpace = EnableVirtualSpace,
            EnableTextDragDrop = EnableTextDragDrop,
            CutCopyWholeLine = CutCopyWholeLine,
            HideCursorWhileTyping = HideCursorWhileTyping,
            AllowScrollBelowDocument = AllowScrollBelowDocument,
            EnableHyperlinks = EnableHyperlinks,
            EnableEmailHyperlinks = EnableEmailHyperlinks,
            RequireControlModifierForHyperlinkClick = RequireControlModifierForHyperlinkClick,
            AllowToggleOverstrikeMode = AllowToggleOverstrikeMode,
            ExtendSelectionOnMouseUp = ExtendSelectionOnMouseUp,
            RightClickMovesCaret = RightClickMovesCaret,
            BackgroundColor = BackgroundColor?.Value,
            LineNumbersColor = LineNumbersColor?.Value,
            ColumnRulerColor = ColumnRulerColor?.Value
        }.Normalize();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _previewSubscription.Dispose();
    }

    private void Load(TextEditorViewOptions options)
    {
        _loading = true;
        try
        {
            FontFamily = options.FontFamily;
            FontSize = options.FontSize;
            WordWrap = options.WordWrap;
            ShowLineNumbers = options.ShowLineNumbers;
            ShowFoldingMargin = options.ShowFoldingMargin;
            ShowColumnRuler = options.ShowColumnRuler;
            ColumnRulerPosition = options.ColumnRulerPosition;
            HighlightCurrentLine = options.HighlightCurrentLine;
            ShowSpaces = options.ShowSpaces;
            ShowTabs = options.ShowTabs;
            ShowEndOfLine = options.ShowEndOfLine;
            ShowBoxForControlCharacters = options.ShowBoxForControlCharacters;
            InheritWordWrapIndentation = options.InheritWordWrapIndentation;
            WordWrapIndentation = options.WordWrapIndentation;
            AcceptsTab = options.AcceptsTab;
            ConvertTabsToSpaces = options.ConvertTabsToSpaces;
            IndentationSize = options.IndentationSize;
            EnableRectangularSelection = options.EnableRectangularSelection;
            EnableVirtualSpace = options.EnableVirtualSpace;
            EnableTextDragDrop = options.EnableTextDragDrop;
            CutCopyWholeLine = options.CutCopyWholeLine;
            HideCursorWhileTyping = options.HideCursorWhileTyping;
            AllowScrollBelowDocument = options.AllowScrollBelowDocument;
            EnableHyperlinks = options.EnableHyperlinks;
            EnableEmailHyperlinks = options.EnableEmailHyperlinks;
            RequireControlModifierForHyperlinkClick = options.RequireControlModifierForHyperlinkClick;
            AllowToggleOverstrikeMode = options.AllowToggleOverstrikeMode;
            ExtendSelectionOnMouseUp = options.ExtendSelectionOnMouseUp;
            RightClickMovesCaret = options.RightClickMovesCaret;
            BackgroundColor = EditorColorChoice.Find(BackgroundColors, options.BackgroundColor) ?? BackgroundColors[0];
            LineNumbersColor = EditorColorChoice.Find(LineNumbersColors, options.LineNumbersColor) ?? LineNumbersColors[0];
            ColumnRulerColor = EditorColorChoice.Find(ColumnRulerColors, options.ColumnRulerColor) ?? ColumnRulerColors[0];
        }
        finally
        {
            _loading = false;
        }

        if (!ReferenceEquals(options, _original))
        {
            _preview(BuildOptions());
        }
    }
}
