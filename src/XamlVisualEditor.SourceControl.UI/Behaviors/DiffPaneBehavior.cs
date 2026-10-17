using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace XamlVisualEditor.SourceControl.UI.Behaviors;

/// <summary>
/// Hides the diff pane in the third column of a grid whose second column holds the splitter, and restores the widths
/// chosen with the splitter when the pane opens again.
/// </summary>
public sealed class DiffPaneBehavior
{
    private const double SPLITTER_WIDTH = 4;

    /// <summary>Gets or sets whether the diff pane is open.</summary>
    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<DiffPaneBehavior, Grid, bool>("IsOpen", true);

    /// <summary>Gets or sets the minimum width of the open diff pane.</summary>
    public static readonly AttachedProperty<double> MinPaneWidthProperty =
        AvaloniaProperty.RegisterAttached<DiffPaneBehavior, Grid, double>("MinPaneWidth", 180);

    private static readonly AttachedProperty<GridLength> SavedListWidthProperty =
        AvaloniaProperty.RegisterAttached<DiffPaneBehavior, Grid, GridLength>(
            "SavedListWidth", new GridLength(2, GridUnitType.Star));

    private static readonly AttachedProperty<GridLength> SavedDiffWidthProperty =
        AvaloniaProperty.RegisterAttached<DiffPaneBehavior, Grid, GridLength>(
            "SavedDiffWidth", new GridLength(3, GridUnitType.Star));

    static DiffPaneBehavior()
    {
        IsOpenProperty.Changed.AddClassHandler<Grid>(OnIsOpenChanged);
    }

    /// <summary>Gets the state of the diff pane.</summary>
    public static bool GetIsOpen(Grid grid) => grid.GetValue(IsOpenProperty);

    /// <summary>Sets the state of the diff pane.</summary>
    public static void SetIsOpen(Grid grid, bool value) => grid.SetValue(IsOpenProperty, value);

    /// <summary>Gets the minimum width of the open diff pane.</summary>
    public static double GetMinPaneWidth(Grid grid) => grid.GetValue(MinPaneWidthProperty);

    /// <summary>Sets the minimum width of the open diff pane.</summary>
    public static void SetMinPaneWidth(Grid grid, double value) => grid.SetValue(MinPaneWidthProperty, value);

    private static void OnIsOpenChanged(Grid grid, AvaloniaPropertyChangedEventArgs args)
    {
        if (grid.ColumnDefinitions.Count < 3)
        {
            grid.AttachedToVisualTree -= OnAttachedToVisualTree;
            grid.AttachedToVisualTree += OnAttachedToVisualTree;
            return;
        }

        Apply(grid, args.NewValue is true);
    }

    private static void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs args)
    {
        if (sender is not Grid grid)
        {
            return;
        }

        grid.AttachedToVisualTree -= OnAttachedToVisualTree;
        Apply(grid, GetIsOpen(grid));
    }

    private static void Apply(Grid grid, bool isOpen)
    {
        if (grid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        ColumnDefinition list = grid.ColumnDefinitions[0];
        ColumnDefinition splitter = grid.ColumnDefinitions[1];
        ColumnDefinition diff = grid.ColumnDefinitions[2];
        if (isOpen)
        {
            list.Width = grid.GetValue(SavedListWidthProperty);
            splitter.Width = new GridLength(SPLITTER_WIDTH);
            diff.MinWidth = GetMinPaneWidth(grid);
            diff.Width = grid.GetValue(SavedDiffWidthProperty);
        }
        else
        {
            grid.SetValue(SavedListWidthProperty, list.Width);
            grid.SetValue(SavedDiffWidthProperty, diff.Width);
            list.Width = new GridLength(1, GridUnitType.Star);
            splitter.Width = new GridLength(0);
            diff.MinWidth = 0;
            diff.Width = new GridLength(0);
        }
    }
}
