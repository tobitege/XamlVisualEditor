using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace XamlVisualEditor.GitExtension.Views;

/// <summary>Hides the diff pane and restores the width chosen with the splitter.</summary>
public sealed class GitDiffPaneBehavior
{
    private const double MIN_DIFF_WIDTH = 180;
    private const double SPLITTER_WIDTH = 4;

    /// <summary>Gets or sets whether the diff pane is open.</summary>
    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<GitDiffPaneBehavior, Grid, bool>("IsOpen", true);

    private static readonly AttachedProperty<GridLength> SavedListWidthProperty =
        AvaloniaProperty.RegisterAttached<GitDiffPaneBehavior, Grid, GridLength>(
            "SavedListWidth", new GridLength(2, GridUnitType.Star));

    private static readonly AttachedProperty<GridLength> SavedDiffWidthProperty =
        AvaloniaProperty.RegisterAttached<GitDiffPaneBehavior, Grid, GridLength>(
            "SavedDiffWidth", new GridLength(3, GridUnitType.Star));

    static GitDiffPaneBehavior()
    {
        IsOpenProperty.Changed.AddClassHandler<Grid>(OnIsOpenChanged);
    }

    /// <summary>Gets the state of the diff pane.</summary>
    public static bool GetIsOpen(Grid grid) => grid.GetValue(IsOpenProperty);

    /// <summary>Sets the state of the diff pane.</summary>
    public static void SetIsOpen(Grid grid, bool value) => grid.SetValue(IsOpenProperty, value);

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
        if (sender is not Grid grid) return;
        grid.AttachedToVisualTree -= OnAttachedToVisualTree;
        Apply(grid, GetIsOpen(grid));
    }

    private static void Apply(Grid grid, bool isOpen)
    {
        if (grid.ColumnDefinitions.Count < 3) return;
        ColumnDefinition list = grid.ColumnDefinitions[0];
        ColumnDefinition splitter = grid.ColumnDefinitions[1];
        ColumnDefinition diff = grid.ColumnDefinitions[2];

        if (isOpen)
        {
            list.Width = grid.GetValue(SavedListWidthProperty);
            splitter.Width = new GridLength(SPLITTER_WIDTH);
            diff.MinWidth = MIN_DIFF_WIDTH;
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
