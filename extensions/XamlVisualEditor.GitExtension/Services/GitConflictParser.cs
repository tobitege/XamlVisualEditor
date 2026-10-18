namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// One block of conflict markers in a text. The offsets cover whole lines with their line breaks, so a side can
/// replace the block without changing the line endings around it.
/// </summary>
/// <param name="Start">Offset of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line.</param>
/// <param name="End">Offset after the line break of the <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c> line, or the end of the text.</param>
/// <param name="StartLine">1-based line of the <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c> line.</param>
/// <param name="LineCount">Number of lines from the first to the last marker line.</param>
/// <param name="Ours">The lines of the current branch, between the first marker and the base or separator.</param>
/// <param name="Theirs">The lines of the merged commit, between the separator and the last marker.</param>
/// <param name="OursLabel">The text after <c>&lt;&lt;&lt;&lt;&lt;&lt;&lt;</c>, such as HEAD.</param>
/// <param name="TheirsLabel">The text after <c>&gt;&gt;&gt;&gt;&gt;&gt;&gt;</c>, such as the branch name.</param>
internal sealed record GitConflictBlock(
    int Start,
    int End,
    int StartLine,
    int LineCount,
    string Ours,
    string Theirs,
    string OursLabel,
    string TheirsLabel);

/// <summary>The side that replaces a conflict block.</summary>
internal enum GitConflictResolution
{
    Ours,
    Theirs,
    Both
}

/// <summary>
/// Finds the conflict blocks git writes into a file: a line that starts with exactly seven <c>&lt;</c>, an optional
/// base section after seven <c>|</c> (diff3 and zdiff3 style), a line of exactly seven <c>=</c> and a line that starts
/// with exactly seven <c>&gt;</c>. A block without its end is not a block.
/// </summary>
internal static class GitConflictParser
{
    private const int MARKER_LENGTH = 7;

    private enum Section
    {
        Outside,
        Ours,
        Base,
        Theirs
    }

    /// <summary>Returns the conflict blocks of <paramref name="text"/> in order.</summary>
    public static IReadOnlyList<GitConflictBlock> Parse(string text)
    {
        List<GitConflictBlock> blocks = new();
        Section section = Section.Outside;
        int blockStart = 0, blockLine = 0, oursStart = 0, oursEnd = 0, theirsStart = 0;
        string oursLabel = string.Empty;
        int line = 0;
        int offset = 0;
        while (offset < text.Length)
        {
            line++;
            int contentEnd = text.IndexOfAny(['\r', '\n'], offset);
            if (contentEnd < 0)
            {
                contentEnd = text.Length;
            }

            int next = contentEnd;
            if (next < text.Length && text[next] == '\r')
            {
                next++;
            }

            if (next < text.Length && text[next] == '\n')
            {
                next++;
            }

            ReadOnlySpan<char> content = text.AsSpan(offset, contentEnd - offset);
            if (IsMarker(content, '<'))
            {
                // A new start inside a block discards the unfinished block.
                section = Section.Ours;
                blockStart = offset;
                blockLine = line;
                oursStart = next;
                oursLabel = Label(content);
            }
            else if (section == Section.Ours && IsMarker(content, '|'))
            {
                section = Section.Base;
                oursEnd = offset;
            }
            else if (section is Section.Ours or Section.Base && content.SequenceEqual("======="))
            {
                if (section == Section.Ours)
                {
                    oursEnd = offset;
                }

                section = Section.Theirs;
                theirsStart = next;
            }
            else if (section == Section.Theirs && IsMarker(content, '>'))
            {
                blocks.Add(new GitConflictBlock(
                    blockStart,
                    next,
                    blockLine,
                    line - blockLine + 1,
                    text[oursStart..oursEnd],
                    text[theirsStart..offset],
                    oursLabel,
                    Label(content)));
                section = Section.Outside;
            }

            offset = next;
        }

        return blocks;
    }

    /// <summary>Returns whether <paramref name="text"/> contains a conflict block.</summary>
    public static bool HasConflict(string text) => Parse(text).Count > 0;

    /// <summary>Returns the text that replaces <paramref name="block"/> for <paramref name="resolution"/>.</summary>
    public static string Resolve(GitConflictBlock block, GitConflictResolution resolution) => resolution switch
    {
        GitConflictResolution.Ours => block.Ours,
        GitConflictResolution.Theirs => block.Theirs,
        _ => block.Ours + block.Theirs
    };

    /// <summary>Returns the block that contains the 1-based <paramref name="line"/>, or null.</summary>
    public static GitConflictBlock? FindAt(IReadOnlyList<GitConflictBlock> blocks, int line) =>
        blocks.FirstOrDefault(block => line >= block.StartLine && line < block.StartLine + block.LineCount);

    // Exactly seven marker characters, then the end of the line or a space before the label.
    private static bool IsMarker(ReadOnlySpan<char> content, char marker)
    {
        if (content.Length < MARKER_LENGTH || content[..MARKER_LENGTH].ContainsAnyExcept(marker))
        {
            return false;
        }

        return content.Length == MARKER_LENGTH || content[MARKER_LENGTH] == ' ';
    }

    private static string Label(ReadOnlySpan<char> content) =>
        content.Length > MARKER_LENGTH ? content[(MARKER_LENGTH + 1)..].Trim().ToString() : string.Empty;
}
