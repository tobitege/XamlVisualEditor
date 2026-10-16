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

    /// <summary>
    /// Updates the header after the layout pass that follows a scroll, a size change or a change of the library
    /// containers. Only that one pass is observed, not every layout pass of the window.
    /// </summary>
    private sealed class Tracker : IDisposable
    {
        private readonly ScrollViewer _viewer;
        private readonly ContentControl _header;
        private readonly TranslateTransform _translation = new();
        private readonly IDisposable _contentSubscription;
        private ItemsControl? _groups;
        private bool _updatePending;

        public Tracker(ScrollViewer viewer, ContentControl header)
        {
            _viewer = viewer;
            _header = header;
            _header.RenderTransform = _translation;
            _viewer.ScrollChanged += RequestUpdate;
            _viewer.SizeChanged += RequestUpdate;
            _header.SizeChanged += RequestUpdate;
            _viewer.AttachedToVisualTree += RequestUpdate;
            _viewer.DetachedFromVisualTree += Detached;
            _contentSubscription = _viewer.GetObservable(ContentControl.ContentProperty).Subscribe(AttachGroups);
        }

        private void AttachGroups(object? content)
        {
            DetachGroups();
            _groups = content as ItemsControl;
            if (_groups is not null)
            {
                _groups.ContainerPrepared += RequestUpdate;
                _groups.ContainerClearing += RequestUpdate;
                _groups.ContainerIndexChanged += RequestUpdate;
            }
            RequestUpdate(this, EventArgs.Empty);
        }

        private void DetachGroups()
        {
            if (_groups is null) return;
            _groups.ContainerPrepared -= RequestUpdate;
            _groups.ContainerClearing -= RequestUpdate;
            _groups.ContainerIndexChanged -= RequestUpdate;
            _groups = null;
        }

        // Every trigger comes with a layout pass that arranges the containers at their new positions.
        private void RequestUpdate(object? sender, EventArgs args)
        {
            if (_updatePending) return;
            _updatePending = true;
            _viewer.LayoutUpdated += UpdateAfterLayout;
        }

        private void UpdateAfterLayout(object? sender, EventArgs args)
        {
            CancelUpdate();
            Update();
        }

        private void CancelUpdate()
        {
            _viewer.LayoutUpdated -= UpdateAfterLayout;
            _updatePending = false;
        }

        private void Update()
        {
            ToolboxAssemblyGroup? active = null;
            double bottom = 0;
            if (_groups is { } groups)
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
            CancelUpdate();
            _header.Content = null;
            _header.IsVisible = false;
        }

        public void Dispose()
        {
            _contentSubscription.Dispose();
            DetachGroups();
            CancelUpdate();
            _viewer.ScrollChanged -= RequestUpdate;
            _viewer.SizeChanged -= RequestUpdate;
            _header.SizeChanged -= RequestUpdate;
            _viewer.AttachedToVisualTree -= RequestUpdate;
            _viewer.DetachedFromVisualTree -= Detached;
            _header.Content = null;
            _header.IsVisible = false;
        }
    }
}
