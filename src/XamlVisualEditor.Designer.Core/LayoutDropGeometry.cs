using Avalonia;

namespace XamlVisualEditor.Designer.Core;

/// <summary>A target area or insertion line in a designer coordinate system.</summary>
public readonly record struct LayoutDropIndicator(Rect Bounds, Point? LineStart = null, Point? LineEnd = null)
{
    /// <summary>Transforms the indicator into the receiving surface coordinates.</summary>
    public LayoutDropIndicator Transform(Matrix matrix)
    {
        static Point Map(Point point, Matrix m) => new(
            point.X * m.M11 + point.Y * m.M21 + m.M31,
            point.X * m.M12 + point.Y * m.M22 + m.M32);
        return new(Bounds.TransformToAABB(matrix), LineStart is { } start ? Map(start, matrix) : null,
            LineEnd is { } end ? Map(end, matrix) : null);
    }
}

/// <summary>Calculates drop positions from measured layout tracks and children.</summary>
public static class LayoutDropGeometry
{
    /// <summary>Finds the track nearest the pointer, including the space between tracks.</summary>
    public static int TrackIndex(IReadOnlyList<double> sizes, double spacing, double position)
    {
        double end = 0;
        for (int index = 0; index < sizes.Count; index++)
        {
            end += sizes[index];
            if (position < end + spacing / 2) return index;
            end += spacing;
        }
        return Math.Max(0, sizes.Count - 1);
    }

    /// <summary>Returns the measured cell, or the implicit single cell when no tracks are declared.</summary>
    public static LayoutDropIndicator GridCell(Size size, IReadOnlyList<double> rows, IReadOnlyList<double> columns,
        double rowSpacing, double columnSpacing, int row, int column)
    {
        row = Math.Clamp(row, 0, Math.Max(0, rows.Count - 1));
        column = Math.Clamp(column, 0, Math.Max(0, columns.Count - 1));
        double x = column * columnSpacing, y = row * rowSpacing;
        for (int i = 0; i < column; i++) x += columns[i];
        for (int i = 0; i < row; i++) y += rows[i];
        return new(new Rect(x, y, columns.Count == 0 ? size.Width : columns[column],
            rows.Count == 0 ? size.Height : rows[row]));
    }

    /// <summary>Finds the source insertion index from measured sibling centers.</summary>
    public static int StackIndex(IReadOnlyList<Rect?> children, bool horizontal, Point position)
    {
        for (int i = 0; i < children.Count; i++)
            if (children[i] is { } child && (horizontal ? position.X < child.Center.X : position.Y < child.Center.Y)) return i;
        return children.Count;
    }

    /// <summary>Places a line in the gap before, between, or after visible siblings.</summary>
    public static LayoutDropIndicator StackInsertion(Size size, IReadOnlyList<Rect?> children, int index,
        bool horizontal, double spacing)
    {
        index = Math.Clamp(index, 0, children.Count);
        Rect? before = null, after = null;
        for (int i = index - 1; i >= 0; i--)
            if (children[i] is { } child && (horizontal ? child.Width : child.Height) > 0) { before = child; break; }
        for (int i = index; i < children.Count; i++)
            if (children[i] is { } child && (horizontal ? child.Width : child.Height) > 0) { after = child; break; }
        double? previous = before is { } prev ? horizontal ? prev.Right : prev.Bottom : null;
        double? next = after is { } following ? horizontal ? following.Left : following.Top : null;
        double position = previous is { } p && next is { } n ? (p + n) / 2
            : next is { } start ? Math.Max(0, start - spacing / 2) : previous is { } end ? end + spacing / 2 : 0;
        Point first = horizontal ? new(position, 0) : new(0, position);
        Point last = horizontal ? new(position, size.Height) : new(size.Width, position);
        return new(new Rect(first, last), first, last);
    }
}
