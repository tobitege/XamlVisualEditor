using System.Globalization;
using XamlVisualEditor.Core;
using CoreDiffLine = XamlVisualEditor.Core.GitDiffLine;
using CoreDiffLineKind = XamlVisualEditor.Core.GitDiffLineKind;

namespace XamlVisualEditor.SourceControl.UI.Models;

/// <summary>Builds the lines of a unified diff for <see cref="Controls.UnifiedDiffEditor"/>.</summary>
public static class DiffLineBuilder
{
    /// <summary>
    /// Returns the lines of a parsed git diff: the header lines and hunks of every file. A diff without parsed files
    /// is read from its raw text.
    /// </summary>
    public static IReadOnlyList<DiffLine> FromGitDiff(GitDiff diff)
    {
        ArgumentNullException.ThrowIfNull(diff);
        if (diff.Files.Count == 0)
        {
            return Parse(diff.RawText);
        }

        List<DiffLine> lines = new();
        foreach (GitDiffFile file in diff.Files)
        {
            foreach (string header in file.HeaderLines)
            {
                lines.Add(new DiffLine(DiffLineKind.FileHeader, header));
            }

            foreach (GitDiffHunk hunk in file.Hunks)
            {
                lines.Add(new DiffLine(DiffLineKind.HunkHeader, hunk.Header));
                foreach (CoreDiffLine line in hunk.Lines)
                {
                    lines.Add(line.Kind switch
                    {
                        CoreDiffLineKind.Added => new DiffLine(DiffLineKind.Added, "+" + line.Text, null, line.NewLine),
                        CoreDiffLineKind.Removed => new DiffLine(DiffLineKind.Removed, "-" + line.Text, line.OldLine, null),
                        CoreDiffLineKind.NoNewline => new DiffLine(DiffLineKind.NoNewline, line.Text),
                        _ => new DiffLine(DiffLineKind.Context, " " + line.Text, line.OldLine, line.NewLine)
                    });
                }
            }
        }

        return lines;
    }

    /// <summary>
    /// Splits the text of a unified diff into classified lines with their old and new line numbers. Hunk lines are
    /// counted against the line counts of their hunk header, so header lines of a following file are never taken
    /// for removed or added lines.
    /// </summary>
    public static IReadOnlyList<DiffLine> Parse(string? diff)
    {
        List<DiffLine> lines = new();
        if (string.IsNullOrEmpty(diff))
        {
            return lines;
        }

        string[] rawLines = diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        int count = rawLines[^1].Length == 0 ? rawLines.Length - 1 : rawLines.Length;
        int oldLine = 0;
        int newLine = 0;
        int oldRemaining = 0;
        int newRemaining = 0;
        for (int i = 0; i < count; i++)
        {
            string line = rawLines[i];
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (TryParseHunkHeader(line, out int oldStart, out int oldCount, out int newStart, out int newCount))
                {
                    (oldLine, oldRemaining, newLine, newRemaining) = (oldStart, oldCount, newStart, newCount);
                }
                else
                {
                    (oldLine, oldRemaining, newLine, newRemaining) = (0, int.MaxValue, 0, int.MaxValue);
                }

                lines.Add(new DiffLine(DiffLineKind.HunkHeader, line));
                continue;
            }

            // The marker follows the last line of a hunk, when its line counts are used up.
            if (line.StartsWith('\\'))
            {
                lines.Add(new DiffLine(DiffLineKind.NoNewline, line));
                continue;
            }

            bool inHunk = oldRemaining > 0 || newRemaining > 0;
            if (inHunk && line.StartsWith('+') && newRemaining > 0)
            {
                lines.Add(new DiffLine(DiffLineKind.Added, line, null, newLine++));
                newRemaining--;
            }
            else if (inHunk && line.StartsWith('-') && oldRemaining > 0)
            {
                lines.Add(new DiffLine(DiffLineKind.Removed, line, oldLine++, null));
                oldRemaining--;
            }
            else if (inHunk && (line.Length == 0 || line[0] == ' ') && oldRemaining > 0 && newRemaining > 0)
            {
                lines.Add(new DiffLine(DiffLineKind.Context, line, oldLine++, newLine++));
                oldRemaining--;
                newRemaining--;
            }
            else
            {
                oldRemaining = 0;
                newRemaining = 0;
                lines.Add(new DiffLine(DiffLineKind.FileHeader, line));
            }
        }

        return lines;
    }

    /// <summary>Reads <c>@@ -oldStart[,oldCount] +newStart[,newCount] @@</c>; a missing count is 1.</summary>
    private static bool TryParseHunkHeader(string header, out int oldStart, out int oldCount, out int newStart, out int newCount)
    {
        oldStart = oldCount = newStart = newCount = 0;
        string[] parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
            && parts[0] == "@@"
            && TryParseRange(parts[1], '-', out oldStart, out oldCount)
            && TryParseRange(parts[2], '+', out newStart, out newCount);
    }

    private static bool TryParseRange(string text, char marker, out int start, out int count)
    {
        start = 0;
        count = 1;
        if (text.Length < 2 || text[0] != marker)
        {
            return false;
        }

        ReadOnlySpan<char> range = text.AsSpan(1);
        int comma = range.IndexOf(',');
        return comma < 0
            ? int.TryParse(range, NumberStyles.None, CultureInfo.InvariantCulture, out start)
            : int.TryParse(range[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out start)
              && int.TryParse(range[(comma + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }
}
