using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.SourceControl.UI.Themes;

namespace XamlVisualEditor.SourceControl.UI.Controls;

/// <summary>
/// Read-only text editor that shows a unified diff with colored line kinds and a margin with the old and new line
/// numbers. It tracks the hunk at the caret in <see cref="CurrentHunkIndex"/>, scrolls to a hunk when that property is
/// set, reports the selected diff lines in <see cref="SelectedLineRange"/> and shows at most
/// <see cref="MaxLineCount"/> lines, followed by a notice when the diff is longer. The line colors are the color
/// properties when set, else the resources under the keys of <see cref="DiffColors"/>, which an application or theme
/// can define, else the defaults of <see cref="DiffColors"/>.
/// </summary>
public sealed class UnifiedDiffEditor : TextEditor
{
    /// <summary>The number of lines <see cref="MaxLineCount"/> has unless set.</summary>
    public const int DEFAULT_MAX_LINE_COUNT = 20_000;

    /// <summary>Defines the <see cref="DiffLines"/> property.</summary>
    public static readonly StyledProperty<IReadOnlyList<DiffLine>?> DiffLinesProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IReadOnlyList<DiffLine>?>(nameof(DiffLines));

    /// <summary>Defines the <see cref="MaxLineCount"/> property.</summary>
    public static readonly StyledProperty<int> MaxLineCountProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, int>(nameof(MaxLineCount), DEFAULT_MAX_LINE_COUNT);

    /// <summary>Defines the <see cref="HasMoreLines"/> property.</summary>
    public static readonly StyledProperty<bool> HasMoreLinesProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, bool>(nameof(HasMoreLines));

    /// <summary>Defines the <see cref="TruncationNotice"/> property.</summary>
    public static readonly StyledProperty<string?> TruncationNoticeProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, string?>(nameof(TruncationNotice));

    /// <summary>Defines the <see cref="CurrentHunkIndex"/> property.</summary>
    public static readonly StyledProperty<int> CurrentHunkIndexProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, int>(nameof(CurrentHunkIndex), -1, defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Defines the <see cref="HunkCount"/> property.</summary>
    public static readonly DirectProperty<UnifiedDiffEditor, int> HunkCountProperty =
        AvaloniaProperty.RegisterDirect<UnifiedDiffEditor, int>(nameof(HunkCount), editor => editor.HunkCount);

    /// <summary>Defines the <see cref="IsTruncated"/> property.</summary>
    public static readonly DirectProperty<UnifiedDiffEditor, bool> IsTruncatedProperty =
        AvaloniaProperty.RegisterDirect<UnifiedDiffEditor, bool>(nameof(IsTruncated), editor => editor.IsTruncated);

    /// <summary>Defines the <see cref="SelectedLineRange"/> property.</summary>
    public static readonly DirectProperty<UnifiedDiffEditor, DiffLineRange?> SelectedLineRangeProperty =
        AvaloniaProperty.RegisterDirect<UnifiedDiffEditor, DiffLineRange?>(nameof(SelectedLineRange), editor => editor.SelectedLineRange);

    /// <summary>Defines the <see cref="AddedForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> AddedForegroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(AddedForeground));

    /// <summary>Defines the <see cref="AddedBackground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> AddedBackgroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(AddedBackground));

    /// <summary>Defines the <see cref="RemovedForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> RemovedForegroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(RemovedForeground));

    /// <summary>Defines the <see cref="RemovedBackground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> RemovedBackgroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(RemovedBackground));

    /// <summary>Defines the <see cref="HunkHeaderForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> HunkHeaderForegroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(HunkHeaderForeground));

    /// <summary>Defines the <see cref="HunkHeaderBackground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> HunkHeaderBackgroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(HunkHeaderBackground));

    /// <summary>Defines the <see cref="FileHeaderForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> FileHeaderForegroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(FileHeaderForeground));

    /// <summary>Defines the <see cref="NoNewlineForeground"/> property.</summary>
    public static readonly StyledProperty<IBrush?> NoNewlineForegroundProperty =
        AvaloniaProperty.Register<UnifiedDiffEditor, IBrush?>(nameof(NoNewlineForeground));

    private readonly DiffLineNumberMargin _lineNumbers;
    private IReadOnlyList<DiffLine> _shownLines = Array.Empty<DiffLine>();
    private int[] _hunkStarts = Array.Empty<int>();
    private INotifyCollectionChanged? _notifier;
    private int _hunkCount;
    private bool _isTruncated;
    private DiffLineRange? _selectedLineRange;
    private bool _updatingHunk;

    /// <summary>Creates the editor.</summary>
    public UnifiedDiffEditor()
    {
        Document ??= new TextDocument();
        IsReadOnly = true;
        ShowLineNumbers = false;
        TextArea.TextView.LineTransformers.Add(new DiffLineColorizer(this));
        _lineNumbers = new DiffLineNumberMargin(this);
        TextArea.LeftMargins.Insert(0, _lineNumbers);
        TextArea.Caret.PositionChanged += (_, _) => OnCaretMoved();
        TextArea.SelectionChanged += (_, _) => UpdateSelectedLineRange();
        // A resource under a key of DiffColors may have changed here, above or in the application.
        ResourcesChanged += (_, _) => TextArea.TextView.Redraw();
    }

    /// <summary>Gets or sets the lines of the diff. A collection that raises change notifications is shown again on every change.</summary>
    public IReadOnlyList<DiffLine>? DiffLines
    {
        get => GetValue(DiffLinesProperty);
        set => SetValue(DiffLinesProperty, value);
    }

    /// <summary>Gets or sets the maximum number of shown lines; a longer diff is cut there and followed by a notice.</summary>
    public int MaxLineCount
    {
        get => GetValue(MaxLineCountProperty);
        set => SetValue(MaxLineCountProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the source of <see cref="DiffLines"/> has more lines than the list holds, for example
    /// because reading stopped at a limit; the diff is then shown as cut, with the notice.
    /// </summary>
    public bool HasMoreLines
    {
        get => GetValue(HasMoreLinesProperty);
        set => SetValue(HasMoreLinesProperty, value);
    }

    /// <summary>Gets or sets the line shown below a cut diff. Null shows no notice.</summary>
    public string? TruncationNotice
    {
        get => GetValue(TruncationNoticeProperty);
        set => SetValue(TruncationNoticeProperty, value);
    }

    /// <summary>
    /// Gets or sets the 0-based index of the hunk at the caret, or -1 before the first hunk. Setting it moves the caret
    /// to that hunk header and scrolls it into view.
    /// </summary>
    public int CurrentHunkIndex
    {
        get => GetValue(CurrentHunkIndexProperty);
        set => SetValue(CurrentHunkIndexProperty, value);
    }

    /// <summary>Gets the number of shown hunks.</summary>
    public int HunkCount
    {
        get => _hunkCount;
        private set => SetAndRaise(HunkCountProperty, ref _hunkCount, value);
    }

    /// <summary>
    /// Gets whether the diff has more lines than <see cref="MaxLineCount"/>, or <see cref="HasMoreLines"/> is set, and
    /// is shown cut.
    /// </summary>
    public bool IsTruncated
    {
        get => _isTruncated;
        private set => SetAndRaise(IsTruncatedProperty, ref _isTruncated, value);
    }

    /// <summary>Gets the diff lines the selection touches, or null without a selection.</summary>
    public DiffLineRange? SelectedLineRange
    {
        get => _selectedLineRange;
        private set => SetAndRaise(SelectedLineRangeProperty, ref _selectedLineRange, value);
    }

    /// <summary>Gets the shown lines: the first <see cref="MaxLineCount"/> lines of <see cref="DiffLines"/>.</summary>
    public IReadOnlyList<DiffLine> ShownLines => _shownLines;

    /// <summary>Gets or sets the foreground of added lines.</summary>
    public IBrush? AddedForeground
    {
        get => GetValue(AddedForegroundProperty);
        set => SetValue(AddedForegroundProperty, value);
    }

    /// <summary>Gets or sets the background of added lines.</summary>
    public IBrush? AddedBackground
    {
        get => GetValue(AddedBackgroundProperty);
        set => SetValue(AddedBackgroundProperty, value);
    }

    /// <summary>Gets or sets the foreground of removed lines.</summary>
    public IBrush? RemovedForeground
    {
        get => GetValue(RemovedForegroundProperty);
        set => SetValue(RemovedForegroundProperty, value);
    }

    /// <summary>Gets or sets the background of removed lines.</summary>
    public IBrush? RemovedBackground
    {
        get => GetValue(RemovedBackgroundProperty);
        set => SetValue(RemovedBackgroundProperty, value);
    }

    /// <summary>Gets or sets the foreground of hunk headers.</summary>
    public IBrush? HunkHeaderForeground
    {
        get => GetValue(HunkHeaderForegroundProperty);
        set => SetValue(HunkHeaderForegroundProperty, value);
    }

    /// <summary>Gets or sets the background of hunk headers.</summary>
    public IBrush? HunkHeaderBackground
    {
        get => GetValue(HunkHeaderBackgroundProperty);
        set => SetValue(HunkHeaderBackgroundProperty, value);
    }

    /// <summary>Gets or sets the foreground of file headers and of the truncation notice.</summary>
    public IBrush? FileHeaderForeground
    {
        get => GetValue(FileHeaderForegroundProperty);
        set => SetValue(FileHeaderForegroundProperty, value);
    }

    /// <summary>Gets or sets the foreground of the marker of a missing newline at the end of a file.</summary>
    public IBrush? NoNewlineForeground
    {
        get => GetValue(NoNewlineForegroundProperty);
        set => SetValue(NoNewlineForegroundProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextEditor);

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DiffLinesProperty)
        {
            if (_notifier is not null)
            {
                _notifier.CollectionChanged -= OnLinesCollectionChanged;
            }

            _notifier = change.NewValue as INotifyCollectionChanged;
            if (_notifier is not null)
            {
                _notifier.CollectionChanged += OnLinesCollectionChanged;
            }

            Rebuild();
        }
        else if (change.Property == MaxLineCountProperty
                 || change.Property == HasMoreLinesProperty
                 || change.Property == TruncationNoticeProperty)
        {
            Rebuild();
        }
        else if (change.Property == CurrentHunkIndexProperty)
        {
            if (!_updatingHunk)
            {
                MoveToHunk(change.GetNewValue<int>());
            }
        }
        else if (change.Property == AddedForegroundProperty
                 || change.Property == AddedBackgroundProperty
                 || change.Property == RemovedForegroundProperty
                 || change.Property == RemovedBackgroundProperty
                 || change.Property == HunkHeaderForegroundProperty
                 || change.Property == HunkHeaderBackgroundProperty
                 || change.Property == FileHeaderForegroundProperty
                 || change.Property == NoNewlineForegroundProperty
                 || change.Property == ThemeVariantScope.ActualThemeVariantProperty)
        {
            TextArea.TextView.Redraw();
        }
    }

    /// <summary>Returns <paramref name="brush"/>, or the theme resource <paramref name="key"/> when it is null.</summary>
    private IBrush? ResolveBrush(IBrush? brush, string key) => brush ?? DiffColors.Find(this, key);

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        IReadOnlyList<DiffLine> lines = DiffLines ?? Array.Empty<DiffLine>();
        int maxLineCount = Math.Max(1, MaxLineCount);
        int shownCount = Math.Min(lines.Count, maxLineCount);
        DiffLine[] shown = new DiffLine[shownCount];
        List<int> hunkStarts = new();
        StringBuilder text = new();
        for (int i = 0; i < shownCount; i++)
        {
            shown[i] = lines[i];
            if (shown[i].Kind == DiffLineKind.HunkHeader)
            {
                hunkStarts.Add(i);
            }

            if (i > 0)
            {
                text.Append('\n');
            }

            text.Append(shown[i].Text);
        }

        bool isTruncated = lines.Count > maxLineCount || HasMoreLines;
        if (isTruncated && TruncationNotice is { } notice)
        {
            text.Append('\n').Append(notice);
        }

        _shownLines = shown;
        _hunkStarts = hunkStarts.ToArray();
        IsTruncated = isTruncated;
        HunkCount = _hunkStarts.Length;
        _updatingHunk = true;
        try
        {
            Text = text.ToString();
            SetCurrentValue(CurrentHunkIndexProperty, -1);
        }
        finally
        {
            _updatingHunk = false;
        }

        _lineNumbers.InvalidateMeasure();
        TextArea.TextView.Redraw();
    }

    private void MoveToHunk(int index)
    {
        if (index < 0 || index >= _hunkStarts.Length)
        {
            return;
        }

        int line = _hunkStarts[index] + 1;
        _updatingHunk = true;
        try
        {
            TextArea.ClearSelection();
            TextArea.Caret.Line = line;
            TextArea.Caret.Column = 1;
        }
        finally
        {
            _updatingHunk = false;
        }

        ScrollToLine(line);
    }

    private void OnCaretMoved()
    {
        if (_updatingHunk)
        {
            return;
        }

        int lineIndex = TextArea.Caret.Line - 1;
        int hunk = -1;
        for (int i = 0; i < _hunkStarts.Length && _hunkStarts[i] <= lineIndex; i++)
        {
            hunk = i;
        }

        if (hunk == CurrentHunkIndex)
        {
            return;
        }

        _updatingHunk = true;
        try
        {
            SetCurrentValue(CurrentHunkIndexProperty, hunk);
        }
        finally
        {
            _updatingHunk = false;
        }
    }

    private void UpdateSelectedLineRange()
    {
        if (TextArea.Selection.IsEmpty || _shownLines.Count == 0 || Document is null)
        {
            SelectedLineRange = null;
            return;
        }

        ISegment segment = TextArea.Selection.SurroundingSegment;
        int first = Document.GetLineByOffset(segment.Offset).LineNumber - 1;
        int last = Math.Min(Document.GetLineByOffset(segment.EndOffset).LineNumber - 1, _shownLines.Count - 1);
        SelectedLineRange = first > last ? null : new DiffLineRange(first, last - first + 1);
    }

    private sealed class DiffLineColorizer : DocumentColorizingTransformer
    {
        private readonly UnifiedDiffEditor _owner;

        public DiffLineColorizer(UnifiedDiffEditor owner)
        {
            _owner = owner;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            IReadOnlyList<DiffLine> lines = _owner._shownLines;
            int index = line.LineNumber - 1;
            (IBrush? foreground, IBrush? background) = index < lines.Count
                ? lines[index].Kind switch
                {
                    DiffLineKind.Added => (
                        _owner.ResolveBrush(_owner.AddedForeground, DiffColors.ADDED_FOREGROUND_KEY),
                        _owner.ResolveBrush(_owner.AddedBackground, DiffColors.ADDED_BACKGROUND_KEY)),
                    DiffLineKind.Removed => (
                        _owner.ResolveBrush(_owner.RemovedForeground, DiffColors.REMOVED_FOREGROUND_KEY),
                        _owner.ResolveBrush(_owner.RemovedBackground, DiffColors.REMOVED_BACKGROUND_KEY)),
                    DiffLineKind.HunkHeader => (
                        _owner.ResolveBrush(_owner.HunkHeaderForeground, DiffColors.HUNK_HEADER_FOREGROUND_KEY),
                        _owner.ResolveBrush(_owner.HunkHeaderBackground, DiffColors.HUNK_HEADER_BACKGROUND_KEY)),
                    DiffLineKind.FileHeader => (_owner.ResolveBrush(_owner.FileHeaderForeground, DiffColors.FILE_HEADER_FOREGROUND_KEY), null),
                    DiffLineKind.NoNewline => (_owner.ResolveBrush(_owner.NoNewlineForeground, DiffColors.NO_NEWLINE_FOREGROUND_KEY), null),
                    _ => ((IBrush?)null, (IBrush?)null)
                }
                : (_owner.ResolveBrush(_owner.FileHeaderForeground, DiffColors.FILE_HEADER_FOREGROUND_KEY), null);

            if ((foreground is null && background is null) || line.Length == 0)
            {
                return;
            }

            ChangeLinePart(line.Offset, line.EndOffset, element =>
            {
                if (foreground is not null)
                {
                    element.TextRunProperties.SetForegroundBrush(foreground);
                }

                if (background is not null)
                {
                    element.TextRunProperties.SetBackgroundBrush(background);
                }
            });
        }
    }
}
