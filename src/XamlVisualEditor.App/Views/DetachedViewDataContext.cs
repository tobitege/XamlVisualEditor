using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.App.Views;

/// <summary>Detaches a panel view from its view model while the view is out of the visual tree.</summary>
/// <remarks>
/// Dock builds a new view each time a tool tab is shown again, while the view model stays the same. The hidden view
/// lives on until it is collected and keeps its two-way bindings: a data grid in it then answers each selection of
/// the new view with its own old selection, and the new view can no longer select anything. The view gives up its
/// data context when it leaves the visual tree and takes it back if it returns.
/// </remarks>
internal static class DetachedViewDataContext
{
    private static readonly AttachedProperty<bool> IsReleasedProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsReleased", typeof(DetachedViewDataContext));

    private static readonly AttachedProperty<object?> LocalDataContextProperty =
        AvaloniaProperty.RegisterAttached<Control, object?>("LocalDataContext", typeof(DetachedViewDataContext));

    private static readonly AttachedProperty<bool> HadLocalDataContextProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("HadLocalDataContext", typeof(DetachedViewDataContext));

    public static Control Apply(Control view)
    {
        view.DetachedFromVisualTree += OnDetachedFromVisualTree;
        view.AttachedToVisualTree += OnAttachedToVisualTree;
        return view;
    }

    private static void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control view || view.GetValue(IsReleasedProperty))
        {
            return;
        }

        bool hadLocal = view.IsSet(StyledElement.DataContextProperty);
        view.SetValue(HadLocalDataContextProperty, hadLocal);
        view.SetValue(LocalDataContextProperty, hadLocal ? view.DataContext : null);
        view.SetValue(IsReleasedProperty, true);
        view.SetValue(StyledElement.DataContextProperty, null);
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control view || !view.GetValue(IsReleasedProperty))
        {
            return;
        }

        view.SetValue(IsReleasedProperty, false);
        if (view.GetValue(HadLocalDataContextProperty))
        {
            view.SetValue(StyledElement.DataContextProperty, view.GetValue(LocalDataContextProperty));
        }
        else
        {
            view.ClearValue(StyledElement.DataContextProperty);
        }

        view.ClearValue(LocalDataContextProperty);
        view.ClearValue(HadLocalDataContextProperty);
    }
}
