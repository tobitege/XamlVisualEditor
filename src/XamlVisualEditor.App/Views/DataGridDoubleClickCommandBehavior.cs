using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace XamlVisualEditor.App.Views;

/// <summary>Executes the attached command on a double click on a DataGrid row.</summary>
public sealed class DataGridDoubleClickCommandBehavior
{
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<DataGridDoubleClickCommandBehavior, DataGrid, ICommand?>("Command");

    static DataGridDoubleClickCommandBehavior()
    {
        CommandProperty.Changed.AddClassHandler<DataGrid>(OnCommandChanged);
    }

    public static ICommand? GetCommand(DataGrid dataGrid)
    {
        return dataGrid.GetValue(CommandProperty);
    }

    public static void SetCommand(DataGrid dataGrid, ICommand? value)
    {
        dataGrid.SetValue(CommandProperty, value);
    }

    private static void OnCommandChanged(DataGrid dataGrid, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.NewValue is not null && e.OldValue is null)
        {
            dataGrid.AddHandler(
                InputElement.PointerPressedEvent,
                OnPointerPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true);
        }
        else if (e.NewValue is null && e.OldValue is not null)
        {
            dataGrid.RemoveHandler(InputElement.PointerPressedEvent, OnPointerPressed);
        }
    }

    private static void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid dataGrid)
        {
            return;
        }

        PointerPoint point = e.GetCurrentPoint(dataGrid);
        if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 2)
        {
            return;
        }

        DataGridRow? row = GetRowFromEvent(dataGrid, e);
        if (row?.DataContext is null)
        {
            return;
        }

        if (!Equals(dataGrid.SelectedItem, row.DataContext))
        {
            dataGrid.SelectedItem = row.DataContext;
        }

        ICommand? command = GetCommand(dataGrid);
        if (command is not null && command.CanExecute(null))
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private static DataGridRow? GetRowFromEvent(DataGrid dataGrid, PointerPressedEventArgs e)
    {
        Visual? source = e.Source as Visual;
        DataGridRow? row = source?.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        if (row is null)
        {
            Point position = e.GetPosition(dataGrid);
            if (dataGrid.InputHitTest(position) is Visual hit)
            {
                row = hit.GetVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
            }
        }

        return row;
    }
}
