using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Dock.Avalonia.Controls;
using Dock.Model.Core;

namespace XamlVisualEditor.App.Styles;

/// <summary>Takes the tool dock away from its chrome while the dock has no tools.</summary>
/// <remarks>
/// The Dock chrome template binds the title, the pin and close buttons and the pinned state to
/// <c>ActiveDockable.*</c> of its tool dock. The active dockable becomes null when the last tool of a dock closes,
/// gets pinned or leaves the auto-hide preview, and each of these bindings then logs an error. The chrome drops its
/// data context as soon as the tool list of the dock is empty, which always happens before the active dockable is
/// cleared, and binds the dock again once a tool is active; a tool is added before it becomes active.
/// The tab strip in the chrome content keeps the dock, because the chrome can let go of the dock in the middle
/// of a tool list change that the tab strip has not seen yet. A dock that keeps tools without an active one
/// stays bound, so its tabs remain. A chrome that leaves the visual tree keeps its state: binding the empty dock
/// again at that point would log the same errors.
/// </remarks>
public sealed class ToolChromeDataContext
{
    public static readonly AttachedProperty<bool> ReleaseWhenEmptyProperty =
        AvaloniaProperty.RegisterAttached<ToolChromeDataContext, ToolChromeControl, bool>("ReleaseWhenEmpty");

    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<ToolChromeDataContext, ToolChromeControl, Tracker?>("Tracker");

    private static readonly AttachedProperty<bool> IsReleasedProperty =
        AvaloniaProperty.RegisterAttached<ToolChromeDataContext, ToolChromeControl, bool>("IsReleased");

    static ToolChromeDataContext()
    {
        ReleaseWhenEmptyProperty.Changed.AddClassHandler<ToolChromeControl>(OnReleaseWhenEmptyChanged);
    }

    public static bool GetReleaseWhenEmpty(ToolChromeControl chrome)
    {
        return chrome.GetValue(ReleaseWhenEmptyProperty);
    }

    public static void SetReleaseWhenEmpty(ToolChromeControl chrome, bool value)
    {
        chrome.SetValue(ReleaseWhenEmptyProperty, value);
    }

    private static void OnReleaseWhenEmptyChanged(ToolChromeControl chrome, AvaloniaPropertyChangedEventArgs e)
    {
        chrome.GetValue(TrackerProperty)?.Dispose();
        chrome.SetValue(TrackerProperty, e.GetNewValue<bool>() ? new Tracker(chrome) : null);
    }

    private sealed class Tracker : IDisposable
    {
        private readonly ToolChromeControl _chrome;
        private StyledElement? _dockControl;
        private IDock? _dock;
        private INotifyCollectionChanged? _dockables;

        public Tracker(ToolChromeControl chrome)
        {
            _chrome = chrome;
            chrome.AttachedToVisualTree += OnAttachedToVisualTree;
            chrome.DetachedFromVisualTree += OnDetachedFromVisualTree;
            if (TopLevel.GetTopLevel(chrome) is not null)
            {
                Attach();
            }
        }

        public void Dispose()
        {
            _chrome.AttachedToVisualTree -= OnAttachedToVisualTree;
            _chrome.DetachedFromVisualTree -= OnDetachedFromVisualTree;
            if (_dock?.ActiveDockable is not null)
            {
                SetReleased(false);
            }

            Detach();
        }

        private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Attach();
        }

        private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Detach();
        }

        // The chrome is part of the ToolDockControl template; that control carries the dock as its data context.
        private void Attach()
        {
            Detach();
            _dockControl = _chrome.TemplatedParent as StyledElement;
            if (_dockControl is null)
            {
                return;
            }

            _dockControl.PropertyChanged += OnDockControlPropertyChanged;
            Track(_dockControl.DataContext as IDock);
        }

        private void Detach()
        {
            if (_dockControl is not null)
            {
                _dockControl.PropertyChanged -= OnDockControlPropertyChanged;
                _dockControl = null;
            }

            Track(null);
        }

        private void OnDockControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == StyledElement.DataContextProperty)
            {
                Track(e.GetNewValue<object?>() as IDock);
            }
        }

        private void Track(IDock? dock)
        {
            if (_dock is INotifyPropertyChanged previous)
            {
                previous.PropertyChanged -= OnDockPropertyChanged;
            }

            _dock = dock;
            if (dock is INotifyPropertyChanged current)
            {
                current.PropertyChanged += OnDockPropertyChanged;
            }

            TrackDockables(dock?.VisibleDockables);
            if (dock is not null)
            {
                SetReleased(!HasDockables(dock));
            }
        }

        private void TrackDockables(IList<IDockable>? dockables)
        {
            if (_dockables is not null)
            {
                _dockables.CollectionChanged -= OnDockablesChanged;
            }

            _dockables = dockables as INotifyCollectionChanged;
            if (_dockables is not null)
            {
                _dockables.CollectionChanged += OnDockablesChanged;
            }
        }

        private void OnDockPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(IDock.VisibleDockables))
            {
                TrackDockables(_dock?.VisibleDockables);
                Update();
            }
            else if (e.PropertyName == nameof(IDock.ActiveDockable))
            {
                Update();
            }
        }

        private void OnDockablesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Update();
        }

        private void Update()
        {
            if (_dock is null)
            {
                return;
            }

            if (!HasDockables(_dock))
            {
                SetReleased(true);
            }
            else if (_dock.ActiveDockable is not null)
            {
                SetReleased(false);
            }
        }

        private static bool HasDockables(IDock dock)
        {
            return dock.VisibleDockables is { Count: > 0 };
        }

        private void SetReleased(bool released)
        {
            StyledElement? content = _chrome.Content as StyledElement;
            if (released)
            {
                content?.SetValue(StyledElement.DataContextProperty, _dock);
                if (!_chrome.GetValue(IsReleasedProperty))
                {
                    _chrome.SetValue(IsReleasedProperty, true);
                    _chrome.SetValue(StyledElement.DataContextProperty, null);
                }
            }
            else if (_chrome.GetValue(IsReleasedProperty))
            {
                _chrome.ClearValue(IsReleasedProperty);
                _chrome.ClearValue(StyledElement.DataContextProperty);
                content?.ClearValue(StyledElement.DataContextProperty);
            }
        }
    }
}
