using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Selects the rows that a view model reveals like a click would: selected, scrolled into view and focused. The grid
/// does this only after its next layout, because it rejects a row of freshly expanded tree nodes before that.
/// </summary>
public static class DataGridReveal
{
    /// <summary>Defines the rows to reveal, one item of the grid per notification.</summary>
    public static readonly AttachedProperty<IObservable<object>?> RequestsProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IObservable<object>?>("Requests", typeof(DataGridReveal));

    private static readonly AttachedProperty<IDisposable?> SubscriptionProperty =
        AvaloniaProperty.RegisterAttached<DataGrid, IDisposable?>("Subscription", typeof(DataGridReveal));

    static DataGridReveal()
    {
        RequestsProperty.Changed.AddClassHandler<DataGrid>(OnRequestsChanged);
    }

    public static IObservable<object>? GetRequests(DataGrid grid) => grid.GetValue(RequestsProperty);

    public static void SetRequests(DataGrid grid, IObservable<object>? value) => grid.SetValue(RequestsProperty, value);

    private static void OnRequestsChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        grid.GetValue(SubscriptionProperty)?.Dispose();
        grid.SetValue(SubscriptionProperty, (e.NewValue as IObservable<object>)?.Subscribe(item =>
            Dispatcher.UIThread.Post(() => Reveal(grid, item), DispatcherPriority.Background)));
    }

    private static void Reveal(DataGrid grid, object item)
    {
        // Clearing first makes the grid rebuild the row highlight even when the item is already its selected item,
        // for example after sorting moved the rows.
        grid.SelectedItem = null!;
        grid.SelectedItem = item;
        grid.ScrollIntoView(item, null);
        grid.Focus();
    }
}
