using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XamlVisualEditor.Core;

/// <summary>
/// Builds a patch of selected lines from the bytes of a <c>git diff</c> of one file, for <c>git apply --recount</c>.
/// The bytes are copied, so line endings and encodings stay as git wrote them.
/// </summary>
public static class GitPatchBuilder
{
    /// <summary>
    /// Returns a patch with the selected added and removed lines of <paramref name="diff"/>, or null when the selection
    /// contains none. An unselected change line is turned into a context line on the side the patch is applied to and
    /// left out on the other side: forward, an unselected removed line stays as context and an unselected added line is
    /// left out; <paramref name="reverse"/> swaps the two. Hunks without a selected change line are left out, the hunk
    /// headers are copied unchanged. A partial selection of a new or deleted file names the file on both sides,
    /// because the patch then applies to existing content.
    /// </summary>
    /// <param name="diff">The output of <c>git diff</c> for one file.</param>
    /// <param name="firstLine">The 0-based index of the first selected line of <paramref name="diff"/>.</param>
    /// <param name="lineCount">The number of selected lines.</param>
    /// <param name="reverse">Whether the patch is applied with <c>--reverse</c>.</param>
    /// <exception cref="GitPatchRejectedException">The diff has more than one file header, or the selection keeps
    /// the last line of a file without line break in front of other lines.</exception>
    public static byte[]? Build(byte[] diff, int firstLine, int lineCount, bool reverse)
    {
        ArgumentNullException.ThrowIfNull(diff);
        ReadOnlyMemory<byte>[] lines = SplitLines(diff);
        int firstHunk = Array.FindIndex(lines, line => line.Span.StartsWith("@@"u8));
        if (firstHunk < 0)
        {
            return null;
        }

        if (lines.Count(line => line.Span.StartsWith("diff "u8)) > 1)
        {
            throw new GitPatchRejectedException(GitPatchRejection.MultipleFiles);
        }

        bool IsSelected(int line) => line >= firstLine && line - firstLine < lineCount;
        List<ReadOnlyMemory<byte>> hunks = new();
        bool isPartial = false;
        for (int start = firstHunk; start < lines.Length;)
        {
            int end = Array.FindIndex(lines, start + 1, line => line.Span.StartsWith("@@"u8));
            end = end < 0 ? lines.Length : end;
            isPartial |= AppendHunk(lines, start, end, IsSelected, reverse, hunks);
            start = end;
        }

        if (hunks.Count == 0)
        {
            return null;
        }

        using MemoryStream patch = new();
        foreach (ReadOnlyMemory<byte> line in HeaderOf(lines[..firstHunk], isPartial).Concat(hunks))
        {
            patch.Write(line.Span);
            patch.WriteByte((byte)'\n');
        }

        return patch.ToArray();
    }

    /// <summary>
    /// Appends the patch lines of the hunk <c>[start, end)</c> to <paramref name="output"/> when it has a selected
    /// change line; returns whether it has an unselected one.
    /// </summary>
    private static bool AppendHunk(
        ReadOnlyMemory<byte>[] lines,
        int start,
        int end,
        Func<int, bool> isSelected,
        bool reverse,
        List<ReadOnlyMemory<byte>> output)
    {
        List<ReadOnlyMemory<byte>> hunk = new() { lines[start] };
        bool hasSelected = false;
        bool hasUnselected = false;
        bool previousKept = true;
        for (int i = start + 1; i < end; i++)
        {
            ReadOnlyMemory<byte> line = lines[i];
            byte marker = MarkerOf(line);

            // The marker of a missing line break belongs to the line before it.
            if (marker == '\\')
            {
                if (previousKept)
                {
                    hunk.Add(line);
                }

                continue;
            }

            bool isChange = marker is (byte)'+' or (byte)'-';
            bool selected = isChange && isSelected(i);
            hasSelected |= selected;
            hasUnselected |= isChange && !selected;
            previousKept = !isChange || selected || (marker == '-') != reverse;
            if (previousKept)
            {
                hunk.Add(isChange && !selected ? ToContext(line) : line);
            }
        }

        if (!hasSelected)
        {
            return hasUnselected;
        }

        // A marker ends the file side of the line before it: context ends both sides, a removed line the old and an
        // added line the new side. A later line of the same side would be joined with it by git instead of rejected.
        for (int i = 2; i < hunk.Count; i++)
        {
            byte ended = MarkerOf(hunk[i - 1]);
            if (MarkerOf(hunk[i]) == '\\' && hunk.Skip(i + 1).Any(line => IsSameSide(ended, MarkerOf(line))))
            {
                throw new GitPatchRejectedException(GitPatchRejection.MissingNewline);
            }
        }

        output.AddRange(hunk);
        return hasUnselected;
    }

    /// <summary>Returns the first byte of a diff line; an empty line is a context line.</summary>
    private static byte MarkerOf(ReadOnlyMemory<byte> line) => line.Length == 0 ? (byte)' ' : line.Span[0];

    /// <summary>Returns whether a line with <paramref name="later"/> belongs to a file side of a line with <paramref name="ended"/>.</summary>
    private static bool IsSameSide(byte ended, byte later) =>
        later != '\\' && (ended == ' ' || later == ' ' || ended == later);

    /// <summary>
    /// Returns the file header lines without a mode change, which selected lines never apply. For a partial selection
    /// of a new or deleted file the mode line is left out and <c>/dev/null</c> is replaced by the name of the other
    /// side, which <c>git apply -p1</c> reads the same way.
    /// </summary>
    private static List<ReadOnlyMemory<byte>> HeaderOf(ReadOnlyMemory<byte>[] lines, bool isPartial)
    {
        List<ReadOnlyMemory<byte>> header = new(lines);
        header.RemoveAll(line => line.Span.StartsWith("old mode "u8) || line.Span.StartsWith("new mode "u8));
        int oldName = header.FindIndex(line => line.Span.StartsWith("--- "u8));
        int newName = header.FindIndex(line => line.Span.StartsWith("+++ "u8));
        if (!isPartial || oldName < 0 || newName < 0)
        {
            return header;
        }

        if (header[oldName].Span[4..].SequenceEqual("/dev/null"u8))
        {
            header[oldName] = Concat("--- "u8, header[newName].Span[4..]);
        }
        else if (header[newName].Span[4..].SequenceEqual("/dev/null"u8))
        {
            header[newName] = Concat("+++ "u8, header[oldName].Span[4..]);
        }

        header.RemoveAll(line => line.Span.StartsWith("new file mode "u8) || line.Span.StartsWith("deleted file mode "u8));
        return header;
    }

    private static ReadOnlyMemory<byte>[] SplitLines(byte[] diff)
    {
        List<ReadOnlyMemory<byte>> lines = new();
        int start = 0;
        while (start < diff.Length)
        {
            int end = Array.IndexOf(diff, (byte)'\n', start);
            end = end < 0 ? diff.Length : end;
            lines.Add(diff.AsMemory(start, end - start));
            start = end + 1;
        }

        return lines.ToArray();
    }

    private static ReadOnlyMemory<byte> ToContext(ReadOnlyMemory<byte> line) => Concat(" "u8, line.Span[1..]);

    private static ReadOnlyMemory<byte> Concat(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
    {
        byte[] result = new byte[first.Length + second.Length];
        first.CopyTo(result);
        second.CopyTo(result.AsSpan(first.Length));
        return result;
    }
}
