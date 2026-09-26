using System.Collections;
using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Gives every column of a grid the sort comparer of its view model, also columns added later; columns take no
/// bindings of their own, because they have no data context. Null restores the sorting by the sort member path.
/// </summary>
public static class DataGridSortComparer
{
    /// <summary>Defines the comparer for the column header sorting.</summary>
    public static readonly AttachedProperty<IComparer?> ComparerProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IComparer?>("Comparer", typeof(DataGridSortComparer));

    private static readonly AttachedProperty<bool> IsTrackingColumnsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, bool>("IsTrackingColumns", typeof(DataGridSortComparer));

    static DataGridSortComparer()
    {
        ComparerProperty.Changed.AddClassHandler<DataGrid>((grid, _) =>
        {
            if (!grid.GetValue(IsTrackingColumnsProperty) && grid.Columns is INotifyCollectionChanged columns)
            {
                grid.SetValue(IsTrackingColumnsProperty, true);
                columns.CollectionChanged += (_, e) =>
                {
                    if (e.Action != NotifyCollectionChangedAction.Remove) Apply(grid);
                };
            }

            Apply(grid);
        });
    }

    public static IComparer? GetComparer(DataGrid grid) => grid.GetValue(ComparerProperty);

    public static void SetComparer(DataGrid grid, IComparer? value) => grid.SetValue(ComparerProperty, value);

    private static void Apply(DataGrid grid)
    {
        IComparer? comparer = grid.GetValue(ComparerProperty);
        foreach (DataGridColumn column in grid.Columns)
        {
            column.CustomSortComparer = comparer;
        }
    }
}
