using Avalonia.Input;
using XamlVisualEditor.Designer.Adorners;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

/// <summary>Pointer cursors for the resize handles and the move border of a designer selection.</summary>
internal static class DesignerCursors
{
    private static Cursor? _move;
    private static Cursor? _topLeft;
    private static Cursor? _topRight;
    private static Cursor? _bottomLeft;
    private static Cursor? _bottomRight;
    private static Cursor? _left;
    private static Cursor? _right;
    private static Cursor? _top;
    private static Cursor? _bottom;

    public static Cursor Move => _move ??= new Cursor(StandardCursorType.SizeAll);

    public static Cursor ForEdges(PreviewerResizeEdges edges) => edges switch
    {
        PreviewerResizeEdges.Left | PreviewerResizeEdges.Top => _topLeft ??= new Cursor(StandardCursorType.TopLeftCorner),
        PreviewerResizeEdges.Right | PreviewerResizeEdges.Top => _topRight ??= new Cursor(StandardCursorType.TopRightCorner),
        PreviewerResizeEdges.Left | PreviewerResizeEdges.Bottom => _bottomLeft ??= new Cursor(StandardCursorType.BottomLeftCorner),
        PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom => _bottomRight ??= new Cursor(StandardCursorType.BottomRightCorner),
        PreviewerResizeEdges.Left => _left ??= new Cursor(StandardCursorType.LeftSide),
        PreviewerResizeEdges.Right => _right ??= new Cursor(StandardCursorType.RightSide),
        PreviewerResizeEdges.Top => _top ??= new Cursor(StandardCursorType.TopSide),
        _ => _bottom ??= new Cursor(StandardCursorType.BottomSide)
    };

    public static Cursor ForDirection(ResizeDirection direction) => ForEdges(direction switch
    {
        ResizeDirection.TopLeft => PreviewerResizeEdges.Left | PreviewerResizeEdges.Top,
        ResizeDirection.TopRight => PreviewerResizeEdges.Right | PreviewerResizeEdges.Top,
        ResizeDirection.BottomLeft => PreviewerResizeEdges.Left | PreviewerResizeEdges.Bottom,
        ResizeDirection.BottomRight => PreviewerResizeEdges.Right | PreviewerResizeEdges.Bottom,
        ResizeDirection.Left => PreviewerResizeEdges.Left,
        ResizeDirection.Right => PreviewerResizeEdges.Right,
        ResizeDirection.Top => PreviewerResizeEdges.Top,
        _ => PreviewerResizeEdges.Bottom
    });
}
