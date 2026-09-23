using System;
using System.Collections.Generic;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>Kind of a unified diff line.</summary>
public enum TfvcDiffLineKind
{
    /// <summary>Unchanged line.</summary>
    Context,

    /// <summary>File header line.</summary>
    Header,

    /// <summary>Hunk header line starting with <c>@@</c>.</summary>
    Hunk,

    /// <summary>Added line.</summary>
    Added,

    /// <summary>Removed line.</summary>
    Removed
}

/// <summary>One line of a unified diff.</summary>
/// <param name="Kind">Line kind.</param>
/// <param name="Text">Line text including the diff marker.</param>
public sealed record TfvcDiffLine(TfvcDiffLineKind Kind, string Text)
{
    /// <summary>Splits unified diff output into classified lines.</summary>
    public static IReadOnlyList<TfvcDiffLine> Parse(string? diff)
    {
        List<TfvcDiffLine> lines = new();
        if (string.IsNullOrEmpty(diff))
        {
            return lines;
        }

        bool inHunk = false;
        foreach (string line in diff.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            TfvcDiffLineKind kind;
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                kind = TfvcDiffLineKind.Hunk;
                inHunk = true;
            }
            else if (!inHunk)
            {
                kind = TfvcDiffLineKind.Header;
            }
            else if (line.StartsWith('+'))
            {
                kind = TfvcDiffLineKind.Added;
            }
            else if (line.StartsWith('-'))
            {
                kind = TfvcDiffLineKind.Removed;
            }
            else
            {
                kind = TfvcDiffLineKind.Context;
            }

            lines.Add(new TfvcDiffLine(kind, line));
        }

        if (lines.Count > 0 && lines[^1].Text.Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}
