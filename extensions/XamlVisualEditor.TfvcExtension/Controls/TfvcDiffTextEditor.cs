using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Text;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.TfvcExtension.Controls;

/// <summary>Read-only text editor that renders a unified diff with colored line kinds.</summary>
public sealed class TfvcDiffTextEditor : TextEditor
{
    /// <summary>Lines to display.</summary>
    public static readonly StyledProperty<IList<TfvcDiffLine>?> DiffLinesProperty =
        AvaloniaProperty.Register<TfvcDiffTextEditor, IList<TfvcDiffLine>?>(nameof(DiffLines));

    private static readonly IBrush AddedBackground = new SolidColorBrush(Color.Parse("#331F8A4C"));
    private static readonly IBrush RemovedBackground = new SolidColorBrush(Color.Parse("#33E5534B"));

    private readonly DiffLineColorizer _colorizer = new();
    private INotifyCollectionChanged? _notifier;

    static TfvcDiffTextEditor()
    {
        DiffLinesProperty.Changed.AddClassHandler<TfvcDiffTextEditor>((editor, args) => editor.OnDiffLinesChanged(args));
    }

    /// <summary>Creates the editor.</summary>
    public TfvcDiffTextEditor()
    {
        TextArea.TextView.LineTransformers.Add(_colorizer);
        Document ??= new TextDocument();
        IsReadOnly = true;
    }

    /// <summary>Gets or sets the lines to display.</summary>
    public IList<TfvcDiffLine>? DiffLines
    {
        get => GetValue(DiffLinesProperty);
        set => SetValue(DiffLinesProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TextEditor);

    private void OnDiffLinesChanged(AvaloniaPropertyChangedEventArgs args)
    {
        if (_notifier is not null)
        {
            _notifier.CollectionChanged -= OnLinesCollectionChanged;
            _notifier = null;
        }

        if (args.NewValue is INotifyCollectionChanged notifier)
        {
            _notifier = notifier;
            _notifier.CollectionChanged += OnLinesCollectionChanged;
        }

        Rebuild();
    }

    private void OnLinesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        IList<TfvcDiffLine>? lines = DiffLines;
        _colorizer.Lines = lines;
        StringBuilder builder = new();
        if (lines is not null)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(lines[i].Text);
            }
        }

        Text = builder.ToString();
        TextArea.TextView.InvalidateLayer(KnownLayer.Text);
    }

    private sealed class DiffLineColorizer : DocumentColorizingTransformer
    {
        public IList<TfvcDiffLine>? Lines { get; set; }

        protected override void ColorizeLine(DocumentLine line)
        {
            IList<TfvcDiffLine>? lines = Lines;
            int index = line.LineNumber - 1;
            if (lines is null || index < 0 || index >= lines.Count)
            {
                return;
            }

            (IBrush? foreground, IBrush? background) = lines[index].Kind switch
            {
                TfvcDiffLineKind.Added => ((IBrush?)Brushes.ForestGreen, AddedBackground),
                TfvcDiffLineKind.Removed => (Brushes.IndianRed, RemovedBackground),
                TfvcDiffLineKind.Hunk => (Brushes.DodgerBlue, null),
                TfvcDiffLineKind.Header => (Brushes.SlateGray, null),
                _ => (null, null)
            };

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
    }
}
