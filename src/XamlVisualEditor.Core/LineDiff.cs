namespace XamlVisualEditor.Core;

/// <summary>
/// One change block of a line diff: <paramref name="OldCount"/> old lines from <paramref name="OldStart"/> are replaced
/// by <paramref name="NewCount"/> new lines from <paramref name="NewStart"/>. Indexes are 0-based; a count of 0 marks an
/// insertion or deletion at that index.
/// </summary>
public readonly record struct LineDiffBlock(int OldStart, int OldCount, int NewStart, int NewCount);

/// <summary>Compares two texts line by line.</summary>
public static class LineDiff
{
    /// <summary>
    /// Splits a text at \r\n, \n and \r, like the editor does; a terminator at the end of the text starts no further line.
    /// </summary>
    public static string[] SplitLines(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<string> lines = new();
        ReadOnlySpan<char> rest = text;
        while (!rest.IsEmpty)
        {
            int end = rest.IndexOfAny('\r', '\n');
            if (end < 0)
            {
                lines.Add(rest.ToString());
                break;
            }

            lines.Add(rest[..end].ToString());
            int terminatorLength = rest[end] == '\r' && end + 1 < rest.Length && rest[end + 1] == '\n' ? 2 : 1;
            rest = rest[(end + terminatorLength)..];
        }

        return lines.ToArray();
    }

    /// <summary>
    /// Computes the change blocks between two line lists, ordered by position. The common start and end are cut off
    /// first; the lines between them are compared with the Myers algorithm. When they need more than
    /// <paramref name="maxEditDistance"/> inserted and deleted lines, they become one block.
    /// </summary>
    public static IReadOnlyList<LineDiffBlock> Compute(
        IReadOnlyList<string> oldLines,
        IReadOnlyList<string> newLines,
        int maxEditDistance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(oldLines);
        ArgumentNullException.ThrowIfNull(newLines);
        ArgumentOutOfRangeException.ThrowIfNegative(maxEditDistance);

        int shorter = Math.Min(oldLines.Count, newLines.Count);
        int prefix = 0;
        while (prefix < shorter && string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        int suffix = 0;
        while (suffix < shorter - prefix
            && string.Equals(oldLines[oldLines.Count - 1 - suffix], newLines[newLines.Count - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        int oldCount = oldLines.Count - prefix - suffix;
        int newCount = newLines.Count - prefix - suffix;
        if (oldCount == 0 && newCount == 0)
        {
            return [];
        }

        LineDiffBlock whole = new(prefix, oldCount, prefix, newCount);
        if (oldCount == 0 || newCount == 0)
        {
            return [whole];
        }

        // Equal lines get equal numbers, so the comparison loop compares integers.
        Dictionary<string, int> ids = new(StringComparer.Ordinal);
        int[] a = ToIds(oldLines, prefix, oldCount, ids);
        int[] b = ToIds(newLines, prefix, newCount, ids);
        List<int[]>? trace = Trace(a, b, Math.Min(maxEditDistance, oldCount + newCount), cancellationToken);
        return trace is null ? [whole] : ToBlocks(trace, a.Length, b.Length, prefix);
    }

    private static int[] ToIds(IReadOnlyList<string> lines, int start, int count, Dictionary<string, int> ids)
    {
        int[] result = new int[count];
        for (int index = 0; index < count; index++)
        {
            string line = lines[start + index];
            if (!ids.TryGetValue(line, out int id))
            {
                id = ids.Count;
                ids.Add(line, id);
            }

            result[index] = id;
        }

        return result;
    }

    // Myers' greedy forward search. Entry d of the result holds the furthest x reached on the diagonals -d..d after d
    // edits; null when more than maxDistance edits are needed.
    private static List<int[]>? Trace(int[] a, int[] b, int maxDistance, CancellationToken cancellationToken)
    {
        int offset = maxDistance + 1;
        int[] furthest = new int[(2 * maxDistance) + 3];
        List<int[]> trace = new();
        for (int d = 0; d <= maxDistance; d++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int k = -d; k <= d; k += 2)
            {
                bool down = k == -d || (k != d && furthest[offset + k - 1] < furthest[offset + k + 1]);
                int x = down ? furthest[offset + k + 1] : furthest[offset + k - 1] + 1;
                int y = x - k;
                while (x < a.Length && y < b.Length && a[x] == b[y])
                {
                    x++;
                    y++;
                }

                furthest[offset + k] = x;
                if (x >= a.Length && y >= b.Length)
                {
                    trace.Add(furthest[(offset - d)..(offset + d + 1)]);
                    return trace;
                }
            }

            trace.Add(furthest[(offset - d)..(offset + d + 1)]);
        }

        return null;
    }

    // Walks the trace back from the end, collects one inserted or deleted line per step and joins adjacent ones.
    private static List<LineDiffBlock> ToBlocks(List<int[]> trace, int oldLength, int newLength, int prefix)
    {
        List<(int X, int Y, bool Insert)> edits = new(trace.Count);
        int x = oldLength;
        int y = newLength;
        for (int d = trace.Count - 1; d > 0; d--)
        {
            int[] previous = trace[d - 1];
            int k = x - y;
            bool down = k == -d || (k != d && previous[k - 1 + d - 1] < previous[k + 1 + d - 1]);
            int previousK = down ? k + 1 : k - 1;
            x = previous[previousK + d - 1];
            y = x - previousK;
            edits.Add((x, y, down));
        }

        edits.Reverse();
        List<LineDiffBlock> blocks = new();
        foreach ((int editX, int editY, bool insert) in edits)
        {
            int oldStart = prefix + editX;
            int newStart = prefix + editY;
            if (blocks.Count > 0 && blocks[^1] is var last
                && last.OldStart + last.OldCount == oldStart && last.NewStart + last.NewCount == newStart)
            {
                blocks[^1] = insert ? last with { NewCount = last.NewCount + 1 } : last with { OldCount = last.OldCount + 1 };
                continue;
            }

            blocks.Add(insert ? new(oldStart, 0, newStart, 1) : new(oldStart, 1, newStart, 0));
        }

        return blocks;
    }
}
