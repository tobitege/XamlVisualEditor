using Avalonia;
using Avalonia.Controls.Recycling;
using Avalonia.VisualTree;
using Avalonia.Xaml.Interactivity;
using Dock.Avalonia.Controls;
using Dock.Model.Core;
using Dock.Model.Core.Events;
using XamlVisualEditor.Designer.Rendering;

namespace XamlVisualEditor.App.Views;

/// <summary>Removes the cached view of a closed tab from the view cache of the dock.</summary>
/// <remarks>
/// A style in EditorTheme gives every dock control a view cache, so a tab keeps its view, scroll position and
/// editor state while it is hidden. Dock empties the cache only when the whole layout is replaced, so the view of a
/// closed tab leaves it here.
/// </remarks>
public sealed class DockViewRecyclingBehavior : Behavior<DockControl>
{
    private IFactory? _factory;

    protected override void OnAttached()
    {
        base.OnAttached();
        if (AssociatedObject is not { } dockControl)
        {
            return;
        }

        dockControl.PropertyChanged += OnDockControlPropertyChanged;
        TrackFactory(dockControl.Factory);
    }

    protected override void OnDetaching()
    {
        if (AssociatedObject is { } dockControl)
        {
            dockControl.PropertyChanged -= OnDockControlPropertyChanged;
        }

        TrackFactory(null);
        base.OnDetaching();
    }

    private void OnDockControlPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == DockControl.FactoryProperty)
        {
            TrackFactory(e.GetNewValue<IFactory?>());
        }
    }

    private void TrackFactory(IFactory? factory)
    {
        if (_factory is not null)
        {
            _factory.DockableClosed -= OnDockableClosed;
        }

        _factory = factory;
        if (_factory is not null)
        {
            _factory.DockableClosed += OnDockableClosed;
        }
    }

    // Dock replaces the cache of the style with its own cache per factory, so the current one is read here.
    private void OnDockableClosed(object? sender, DockableClosedEventArgs e)
    {
        if (e.Dockable is { } dockable
            && AssociatedObject is { } dockControl
            && ControlRecyclingDataTemplate.GetControlRecycling(dockControl) is ControlRecycling recycling)
        {
            // Cached code editors keep their editor setup across detaches and release it only here.
            if (recycling.TryGetValue(dockable, out object? cached) && cached is Visual view)
            {
                foreach (XamlCodeEditorView editor in view.GetSelfAndVisualDescendants().OfType<XamlCodeEditorView>().ToArray())
                {
                    editor.Dispose();
                }

                // A window preview keeps its hidden window across detaches as well.
                foreach (DesignWindowContentHost host in view.GetSelfAndVisualDescendants().OfType<DesignWindowContentHost>().ToArray())
                {
                    host.Dispose();
                }
            }

            recycling.Remove(dockable);
        }
    }
}
