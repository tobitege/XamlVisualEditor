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

    /// <summary>
    /// Returns the measured cells a control with the given spans occupies from <paramref name="row"/> and
    /// <paramref name="column"/>, or the implicit single cell when no tracks are declared.
    /// </summary>
    public static LayoutDropIndicator GridCell(Size size, IReadOnlyList<double> rows, IReadOnlyList<double> columns,
        double rowSpacing, double columnSpacing, int row, int column, int rowSpan = 1, int columnSpan = 1)
    {
        row = Math.Clamp(row, 0, Math.Max(0, rows.Count - 1));
        column = Math.Clamp(column, 0, Math.Max(0, columns.Count - 1));
        int lastRow = Math.Min(rows.Count, row + Math.Max(1, rowSpan)) - 1;
        int lastColumn = Math.Min(columns.Count, column + Math.Max(1, columnSpan)) - 1;
        return new(new Rect(TrackStart(columns, columnSpacing, column), TrackStart(rows, rowSpacing, row),
            columns.Count == 0 ? size.Width : TrackStart(columns, columnSpacing, lastColumn) + columns[lastColumn] - TrackStart(columns, columnSpacing, column),
            rows.Count == 0 ? size.Height : TrackStart(rows, rowSpacing, lastRow) + rows[lastRow] - TrackStart(rows, rowSpacing, row)));
    }

    /// <summary>
    /// Clamps a start track so that a control spanning <paramref name="span"/> tracks stays inside the grid,
    /// as the layout edit that places the control does.
    /// </summary>
    public static int SpanStart(int index, int trackCount, int span) =>
        Math.Clamp(index, 0, Math.Max(0, Math.Max(1, trackCount) - Math.Max(1, span)));

    private static double TrackStart(IReadOnlyList<double> sizes, double spacing, int index)
    {
        double start = index * spacing;
        for (int i = 0; i < index; i++) start += sizes[i];
        return start;
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
