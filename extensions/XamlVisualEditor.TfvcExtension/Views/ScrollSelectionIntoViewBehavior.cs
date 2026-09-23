using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>Scrolls the selected row of a data grid into view whenever the selection changes.</summary>
public sealed class ScrollSelectionIntoViewBehavior
{
    /// <summary>Enables the behavior.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollSelectionIntoViewBehavior, DataGrid, bool>("IsEnabled");

    static ScrollSelectionIntoViewBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>(OnIsEnabledChanged);
    }

    /// <summary>Gets whether the behavior is enabled.</summary>
    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    /// <summary>Sets whether the behavior is enabled.</summary>
    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        grid.SelectionChanged -= OnSelectionChanged;
        if (e.NewValue is true)
        {
            grid.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.SelectedItem is { } item)
        {
            Dispatcher.UIThread.Post(() => grid.ScrollIntoView(item, null), DispatcherPriority.Background);
        }
    }
}
