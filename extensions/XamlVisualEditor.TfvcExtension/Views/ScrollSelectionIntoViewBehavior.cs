using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.DataGridHierarchical;
using Avalonia.Threading;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>
/// Scrolls the selected row of a data grid into view whenever a different item gets selected.
/// A hierarchical grid raises selection changes for the same item when rows are inserted above or
/// below it (expanding a folder); those must not scroll back to the selected row.
/// </summary>
public sealed class ScrollSelectionIntoViewBehavior
{
    /// <summary>Enables the behavior.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollSelectionIntoViewBehavior, DataGrid, bool>("IsEnabled");

    private static readonly AttachedProperty<object?> ScrolledItemProperty =
        AvaloniaProperty.RegisterAttached<ScrollSelectionIntoViewBehavior, DataGrid, object?>("ScrolledItem");

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
        grid.ClearValue(ScrolledItemProperty);
        if (e.NewValue is true)
        {
            grid.SelectionChanged += OnSelectionChanged;
        }
    }

    private static void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid grid)
        {
            return;
        }

        object? item = grid.SelectedItem;
        // Hierarchical rows wrap the item in a node that the grid may recreate; the item identifies the row.
        object? key = item is HierarchicalNode node ? node.Item : item;
        if (Equals(key, grid.GetValue(ScrolledItemProperty)))
        {
            return;
        }

        grid.SetValue(ScrolledItemProperty, key);
        if (item is not null)
        {
            Dispatcher.UIThread.Post(() => grid.ScrollIntoView(item, null), DispatcherPriority.Background);
        }
    }
}
