using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.SourceControl.UI.Themes;

namespace XamlVisualEditor.GitExtension.Controls;

/// <summary>
/// TextEditor that renders git diffs with colored line types: the resources under the keys of
/// <see cref="DiffColors"/>, which an application or theme can define, else the defaults of <see cref="DiffColors"/>.
/// </summary>
public sealed class GitDiffTextEditor : TextEditor
{
    public static readonly StyledProperty<IList<GitDiffLineViewModel>?> DiffLinesProperty =
        AvaloniaProperty.Register<GitDiffTextEditor, IList<GitDiffLineViewModel>?>(nameof(DiffLines));

    private readonly DiffLineColorizer _colorizer;
    private INotifyCollectionChanged? _diffLinesNotifier;

    static GitDiffTextEditor()
    {
        DiffLinesProperty.Changed.AddClassHandler<GitDiffTextEditor>((editor, args) =>
            editor.OnDiffLinesChanged(args));
        ThemeVariantScope.ActualThemeVariantProperty.Changed.AddClassHandler<GitDiffTextEditor>((editor, _) =>
            editor.TextArea.TextView.Redraw());
    }

    public GitDiffTextEditor()
    {
        _colorizer = new DiffLineColorizer(this);
        TextArea.TextView.LineTransformers.Add(_colorizer);
        Document ??= new TextDocument();
        // A resource under a key of DiffColors may have changed here, above or in the application.
        ResourcesChanged += (_, _) => TextArea.TextView.Redraw();
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    public IList<GitDiffLineViewModel>? DiffLines
    {
        get => GetValue(DiffLinesProperty);
        set => SetValue(DiffLinesProperty, value);
    }

    private void OnDiffLinesChanged(AvaloniaPropertyChangedEventArgs args)
    {
        IList<GitDiffLineViewModel>? lines = args.NewValue as IList<GitDiffLineViewModel>;
        if (_diffLinesNotifier is not null)
        {
            _diffLinesNotifier.CollectionChanged -= OnDiffLinesCollectionChanged;
            _diffLinesNotifier = null;
        }

        if (lines is INotifyCollectionChanged notifier)
        {
            _diffLinesNotifier = notifier;
            _diffLinesNotifier.CollectionChanged += OnDiffLinesCollectionChanged;
        }

        _colorizer.SetLines(lines);
        RebuildText(lines);
    }

    private void OnDiffLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        RebuildText(DiffLines);
    }

    private void RebuildText(IList<GitDiffLineViewModel>? lines)
    {
        StringBuilder builder = new();
        if (lines is not null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                GitDiffLineViewModel line = lines[i];
                if (!string.IsNullOrWhiteSpace(line.Marker))
                {
                    builder.Append(line.Marker);
                    builder.Append(' ');
                }

                builder.Append(line.Text);
                if (i < lines.Count - 1)
                {
                    builder.AppendLine();
                }
            }
        }

        string text = builder.ToString();
        if (!string.Equals(Text, text, StringComparison.Ordinal))
        {
            Text = text;
        }

        TextArea.TextView.InvalidateLayer(KnownLayer.Text);
        TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private sealed class DiffLineColorizer : DocumentColorizingTransformer
    {
        private readonly GitDiffTextEditor _owner;
        private IList<GitDiffLineViewModel>? _lines;

        public DiffLineColorizer(GitDiffTextEditor owner)
        {
            _owner = owner;
        }

        public void SetLines(IList<GitDiffLineViewModel>? lines)
        {
            _lines = lines;
        }

        protected override void ColorizeLine(DocumentLine line)
        {
            if (_lines is null)
            {
                return;
            }

            int index = line.LineNumber - 1;
            if (index < 0 || index >= _lines.Count)
            {
                return;
            }

            GitDiffLineKind kind = _lines[index].Kind;
            IBrush? foreground = FindBrush(GetForegroundKey(kind));
            IBrush? background = FindBrush(GetBackgroundKey(kind));

            if (foreground is null && background is null)
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

        private IBrush? FindBrush(string? key) => key is null ? null : DiffColors.Find(_owner, key);

        private static string? GetForegroundKey(GitDiffLineKind kind)
        {
            return kind switch
            {
                GitDiffLineKind.Added => DiffColors.ADDED_FOREGROUND_KEY,
                GitDiffLineKind.Removed => DiffColors.REMOVED_FOREGROUND_KEY,
                GitDiffLineKind.HunkHeader => DiffColors.HUNK_HEADER_FOREGROUND_KEY,
                GitDiffLineKind.FileHeader => DiffColors.FILE_HEADER_FOREGROUND_KEY,
                GitDiffLineKind.NoNewline => DiffColors.NO_NEWLINE_FOREGROUND_KEY,
                _ => null
            };
        }

        private static string? GetBackgroundKey(GitDiffLineKind kind)
        {
            return kind switch
            {
                GitDiffLineKind.Added => DiffColors.ADDED_BACKGROUND_KEY,
                GitDiffLineKind.Removed => DiffColors.REMOVED_BACKGROUND_KEY,
                GitDiffLineKind.HunkHeader => DiffColors.HUNK_HEADER_BACKGROUND_KEY,
                _ => null
            };
        }
    }
}
