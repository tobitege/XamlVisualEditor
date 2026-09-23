using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>Selects the data grid row under the pointer on a right click, so context menu commands act on that row.</summary>
public sealed class RightClickSelectBehavior
{
    /// <summary>Enables the behavior.</summary>
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<RightClickSelectBehavior, DataGrid, bool>("IsEnabled");

    static RightClickSelectBehavior()
    {
        IsEnabledProperty.Changed.AddClassHandler<DataGrid>(OnIsEnabledChanged);
    }

    /// <summary>Gets whether the behavior is enabled.</summary>
    public static bool GetIsEnabled(DataGrid grid) => grid.GetValue(IsEnabledProperty);

    /// <summary>Sets whether the behavior is enabled.</summary>
    public static void SetIsEnabled(DataGrid grid, bool value) => grid.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(DataGrid grid, AvaloniaPropertyChangedEventArgs e)
    {
        grid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        if (e.NewValue is true)
        {
            grid.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !e.GetCurrentPoint(grid).Properties.IsRightButtonPressed)
        {
            return;
        }

        DataGridRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row?.DataContext is { } item && !ReferenceEquals(grid.SelectedItem, item))
        {
            grid.SelectedItem = item;
        }
    }
}
