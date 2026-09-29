using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace XamlVisualEditor.ToolboxExtension.Views;

/// <summary>Keeps the library header visible at the top of the shared scroll area.</summary>
public sealed class ToolboxStickyHeaderBehavior
{
    /// <summary>Target for the pinned library header.</summary>
    public static readonly AttachedProperty<ContentControl?> HeaderProperty =
        AvaloniaProperty.RegisterAttached<ToolboxStickyHeaderBehavior, ScrollViewer, ContentControl?>("Header");
    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<ToolboxStickyHeaderBehavior, ScrollViewer, Tracker?>("Tracker");

    static ToolboxStickyHeaderBehavior() => HeaderProperty.Changed.AddClassHandler<ScrollViewer>(HeaderChanged);

    /// <summary>Gets the target for the pinned header.</summary>
    public static ContentControl? GetHeader(ScrollViewer viewer) => viewer.GetValue(HeaderProperty);

    /// <summary>Sets the target for the pinned header.</summary>
    public static void SetHeader(ScrollViewer viewer, ContentControl? value) => viewer.SetValue(HeaderProperty, value);

    private static void HeaderChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs args)
    {
        viewer.GetValue(TrackerProperty)?.Dispose();
        viewer.SetValue(TrackerProperty, args.NewValue is ContentControl header ? new Tracker(viewer, header) : null);
    }

    private sealed class Tracker : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private readonly ContentControl _header;
        private readonly TranslateTransform _translation = new();

        public Tracker(ScrollViewer viewer, ContentControl header)
        {
            _viewer = viewer;
            _header = header;
            _header.RenderTransform = _translation;
            _viewer.ScrollChanged += Update;
            _viewer.LayoutUpdated += Update;
            _viewer.DetachedFromVisualTree += Detached;
        }

        private void Update(object? sender, EventArgs args)
        {
            ToolboxAssemblyGroup? active = null;
            double bottom = 0;
            if (_viewer.Content is ItemsControl groups)
            {
                if (_header.Width != groups.Bounds.Width) _header.Width = groups.Bounds.Width;
                // Visit only the library containers, not the individual control rows.
                foreach (Control container in groups.GetRealizedContainers())
                {
                    Expander? expander = container as Expander ?? (container as ContentPresenter)?.Child as Expander;
                    if (expander?.DataContext is not ToolboxAssemblyGroup group
                        || expander.TranslatePoint(default, _viewer) is not { } position) continue;
                    double end = position.Y + expander.Bounds.Height;
                    if (position.Y < -0.5 && end > 0)
                    {
                        active = group;
                        bottom = end;
                        break;
                    }
                }
            }
            if (!ReferenceEquals(_header.Content, active)) _header.Content = active;
            _header.IsVisible = active is not null;
            // The next library header pushes the current one out of the visible area.
            _translation.Y = active is null ? 0 : Math.Min(0, bottom - _header.Bounds.Height);
        }

        private void Detached(object? sender, VisualTreeAttachmentEventArgs args)
        {
            _header.Content = null;
            _header.IsVisible = false;
        }

        public void Dispose()
        {
            _viewer.ScrollChanged -= Update;
            _viewer.LayoutUpdated -= Update;
            _viewer.DetachedFromVisualTree -= Detached;
            _header.Content = null;
            _header.IsVisible = false;
        }
    }
}
