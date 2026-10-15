using System.Threading;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using XamlVisualEditor.App.Views;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Lets users widen grid columns beyond the visible width. Star columns fill a grid only until a column header
/// is pressed; the grid then keeps the current widths in pixels, so a resize grows the grid and scrolls instead
/// of squeezing the columns to the right of the resized one. This applies to the grids of the editor only.
/// </summary>
internal static class DataGridColumnSizing
{
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        InputElement.PointerPressedEvent.AddClassHandler<DataGridColumnHeader>(
            static (header, _) => FreezeStarColumns(header.FindAncestorOfType<DataGrid>()), RoutingStrategies.Tunnel);
    }

    internal static void FreezeStarColumns(DataGrid? grid)
    {
        // Grids of the document rendered in the design surface keep the column widths their XAML defines.
        if (grid is null || grid.FindAncestorOfType<DesignSurfaceView>() is not null) return;
        foreach (DataGridColumn column in grid.Columns)
        {
            if (column.IsVisible && column.Width.IsStar) column.Width = new DataGridLength(column.ActualWidth);
        }
    }
}
