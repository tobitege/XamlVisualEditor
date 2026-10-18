namespace XamlVisualEditor.Extensions;

/// <summary>Display and editing options that the host applies to every text and code editor.</summary>
public sealed record TextEditorViewOptions
{
    /// <summary>Gets the smallest supported font size.</summary>
    public const double MIN_FONT_SIZE = 8d;

    /// <summary>Gets the largest supported font size.</summary>
    public const double MAX_FONT_SIZE = 48d;

    /// <summary>Gets the font family list; the first installed family wins.</summary>
    public string FontFamily { get; init; } = "Cascadia Code, Consolas, Menlo, monospace";

    /// <summary>Gets the font size in device-independent pixels.</summary>
    public double FontSize { get; init; } = 14d;

    /// <summary>Gets whether long lines wrap at the view edge.</summary>
    public bool WordWrap { get; init; }

    /// <summary>Gets whether the line number margin is shown.</summary>
    public bool ShowLineNumbers { get; init; } = true;

    /// <summary>Gets whether the folding margin is shown where the editor supports folding.</summary>
    public bool ShowFoldingMargin { get; init; } = true;

    /// <summary>Gets whether a vertical ruler marks the right margin.</summary>
    public bool ShowColumnRuler { get; init; }

    /// <summary>Gets the column of the right margin ruler.</summary>
    public int ColumnRulerPosition { get; init; } = 80;

    /// <summary>Gets whether the caret line is highlighted.</summary>
    public bool HighlightCurrentLine { get; init; }

    /// <summary>Gets whether spaces are shown as visible marks.</summary>
    public bool ShowSpaces { get; init; }

    /// <summary>Gets whether tabs are shown as visible marks.</summary>
    public bool ShowTabs { get; init; }

    /// <summary>Gets whether line ends are shown as visible marks.</summary>
    public bool ShowEndOfLine { get; init; }

    /// <summary>Gets whether control characters are drawn as boxes.</summary>
    public bool ShowBoxForControlCharacters { get; init; } = true;

    /// <summary>Gets whether wrapped lines keep the indentation of their first line.</summary>
    public bool InheritWordWrapIndentation { get; init; } = true;

    /// <summary>Gets the additional indentation of wrapped lines in characters.</summary>
    public double WordWrapIndentation { get; init; }

    /// <summary>Gets whether the Tab key inserts indentation instead of moving the focus.</summary>
    public bool AcceptsTab { get; init; } = true;

    /// <summary>Gets whether indentation is written as spaces.</summary>
    public bool ConvertTabsToSpaces { get; init; }

    /// <summary>Gets the width of one indentation level in characters.</summary>
    public int IndentationSize { get; init; } = 4;

    /// <summary>Gets whether Alt+drag selects a rectangle.</summary>
    public bool EnableRectangularSelection { get; init; } = true;

    /// <summary>Gets whether the caret may move beyond the end of a line.</summary>
    public bool EnableVirtualSpace { get; init; }

    /// <summary>Gets whether selected text can be moved by drag and drop.</summary>
    public bool EnableTextDragDrop { get; init; } = true;

    /// <summary>Gets whether cut and copy take the whole line when nothing is selected.</summary>
    public bool CutCopyWholeLine { get; init; } = true;

    /// <summary>Gets whether the mouse cursor hides while typing.</summary>
    public bool HideCursorWhileTyping { get; init; } = true;

    /// <summary>Gets whether the view can scroll below the last line.</summary>
    public bool AllowScrollBelowDocument { get; init; } = true;

    /// <summary>Gets whether web addresses are shown as links.</summary>
    public bool EnableHyperlinks { get; init; } = true;

    /// <summary>Gets whether e-mail addresses are shown as links.</summary>
    public bool EnableEmailHyperlinks { get; init; } = true;

    /// <summary>Gets whether a link opens only with the Ctrl key held.</summary>
    public bool RequireControlModifierForHyperlinkClick { get; init; } = true;

    /// <summary>Gets whether the Insert key toggles overwrite mode.</summary>
    public bool AllowToggleOverstrikeMode { get; init; }

    /// <summary>Gets whether a selection extends when the mouse button is released.</summary>
    public bool ExtendSelectionOnMouseUp { get; init; } = true;

    /// <summary>Gets whether a right click moves the caret to the clicked position.</summary>
    public bool RightClickMovesCaret { get; init; } = true;

    /// <summary>Gets the editor background as a color text such as "#1E1E1E"; <see langword="null"/> keeps the theme color.</summary>
    public string? BackgroundColor { get; init; }

    /// <summary>Gets the line number color; <see langword="null"/> keeps the theme color.</summary>
    public string? LineNumbersColor { get; init; }

    /// <summary>Gets the right margin ruler color; <see langword="null"/> keeps the theme color.</summary>
    public string? ColumnRulerColor { get; init; }

    /// <summary>Returns the options with every numeric value in its supported range.</summary>
    public TextEditorViewOptions Normalize()
    {
        return this with
        {
            FontFamily = string.IsNullOrWhiteSpace(FontFamily) ? new TextEditorViewOptions().FontFamily : FontFamily.Trim(),
            FontSize = double.IsFinite(FontSize) ? Math.Clamp(FontSize, MIN_FONT_SIZE, MAX_FONT_SIZE) : 14d,
            ColumnRulerPosition = Math.Clamp(ColumnRulerPosition, 20, 400),
            IndentationSize = Math.Clamp(IndentationSize, 1, 16),
            WordWrapIndentation = double.IsFinite(WordWrapIndentation) ? Math.Clamp(WordWrapIndentation, 0d, 32d) : 0d,
            BackgroundColor = string.IsNullOrWhiteSpace(BackgroundColor) ? null : BackgroundColor.Trim(),
            LineNumbersColor = string.IsNullOrWhiteSpace(LineNumbersColor) ? null : LineNumbersColor.Trim(),
            ColumnRulerColor = string.IsNullOrWhiteSpace(ColumnRulerColor) ? null : ColumnRulerColor.Trim()
        };
    }
}

/// <summary>Actions that need the editor control itself rather than the document text.</summary>
public enum TextEditorViewAction
{
    /// <summary>Opens the search bar of the editor.</summary>
    Find,

    /// <summary>Opens the search bar of the editor with the replace field.</summary>
    Replace,

    /// <summary>Expands every folded region.</summary>
    ExpandAllFoldings,

    /// <summary>Collapses every foldable region.</summary>
    CollapseAllFoldings,

    /// <summary>Collapses the innermost region at the caret, or the selected lines.</summary>
    CollapseSelection
}

/// <summary>Context keys that the host sets for its text editors.</summary>
public static class TextEditorContextKeys
{
    /// <summary>True while the active document shows an editor that can run view actions.</summary>
    public const string HasActiveEditor = "textEditor.hasActiveEditor";
}

/// <summary>Applies view options to the host editors and runs view actions on the editor of the active document.</summary>
public interface ITextEditorViewHost
{
    /// <summary>Gets the options the editors currently show.</summary>
    TextEditorViewOptions Options { get; }

    /// <summary>Gets whether the active document shows an editor; <see cref="TextEditorContextKeys.HasActiveEditor"/> follows it.</summary>
    bool HasActiveEditor { get; }

    /// <summary>Applies options to every open and future editor.</summary>
    void SetOptions(TextEditorViewOptions options);

    /// <summary>Runs an action on the editor of the active document; returns false when no editor could run it.</summary>
    Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken);

    /// <summary>Raised after the options changed.</summary>
    event EventHandler? OptionsChanged;

    /// <summary>
    /// Creates an empty layer of line marks, which the editors of a file show in a margin beside the line numbers.
    /// A click on a mark runs <paramref name="clickCommandId"/> with the full path and the 1-based line as arguments.
    /// Returns null when the host draws no line marks.
    /// </summary>
    ITextEditorLineMarkLayer? CreateLineMarkLayer(string? clickCommandId = null) => null;
}

/// <summary>The colour class of a line mark; the host maps each kind to a theme brush.</summary>
public enum TextEditorLineMarkKind
{
    Added,
    Modified,
    Deleted
}

/// <summary>
/// Marks the lines <paramref name="StartLine"/> to <paramref name="StartLine"/> + <paramref name="LineCount"/> - 1
/// (1-based). A <paramref name="LineCount"/> of 0 marks the boundary above <paramref name="StartLine"/>, for example
/// where lines were removed.
/// </summary>
public sealed record TextEditorLineMark(int StartLine, int LineCount, TextEditorLineMarkKind Kind);

/// <summary>The line marks of one source; disposing the layer removes them.</summary>
public interface ITextEditorLineMarkLayer : IDisposable
{
    /// <summary>
    /// Replaces the marks of one file, given by its full path; an empty list removes them. An editor shows the marks of
    /// the first layer, in creation order, that has marks for its file. Callable from any thread; the host copies the
    /// marks and never calls back into the extension.
    /// </summary>
    void Set(string filePath, IReadOnlyList<TextEditorLineMark> marks);
}
