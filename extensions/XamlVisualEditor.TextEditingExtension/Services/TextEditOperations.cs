using System.Globalization;
using XamlVisualEditor.Core;

namespace XamlVisualEditor.TextEditingExtension.Services;

/// <summary>Selection and caret of a document.</summary>
public readonly record struct TextSelection(int Start, int Length, int CaretOffset)
{
    /// <summary>Gets the offset after the selection.</summary>
    public int End => Start + Length;
}

/// <summary>Edits to apply to a document and the selection to show afterwards.</summary>
public sealed record TextEditResult(IReadOnlyList<TextEdit> Edits, TextSelection Selection);

/// <summary>
/// Computes text editing operations on a document text; the results are applied by the caller. Line operations
/// insert or remove text on each line instead of replacing the lines, so that marks on the untouched text stay in place.
/// </summary>
public static class TextEditOperations
{
    /// <summary>Gets the tab width that indentation columns are counted with when the editor gives none.</summary>
    public const int DEFAULT_TAB_SIZE = 4;

    // Commenting a block inserts ESCAPE_MARK behind the first character of BlockForbidden wherever that character is
    // followed by the second one or by ESCAPE_MARK; uncommenting removes one ESCAPE_MARK behind every such character.
    // In XML "a--b" becomes "a-!-b", "-!" becomes "-!!", and an inner "<!-- x -->" becomes "<!-!- x -!->". The
    // escaped text never contains BlockForbidden, and uncommenting restores every text exactly.
    private const char ESCAPE_MARK = '!';

    /// <summary>Turns the selected text into upper case.</summary>
    public static TextEditResult? ToUpperCase(string text, TextSelection selection)
    {
        return TransformSelection(text, selection, value => value.ToUpper(CultureInfo.CurrentCulture));
    }

    /// <summary>Turns the selected text into lower case.</summary>
    public static TextEditResult? ToLowerCase(string text, TextSelection selection)
    {
        return TransformSelection(text, selection, value => value.ToLower(CultureInfo.CurrentCulture));
    }

    /// <summary>Encloses the selection in round brackets and keeps the enclosed text selected.</summary>
    public static TextEditResult WrapInBrackets(string text, TextSelection selection)
    {
        TextSelection range = Clamp(text, selection);
        string inner = text.Substring(range.Start, range.Length);
        TextEdit edit = new() { Offset = range.Start, Length = range.Length, NewText = "(" + inner + ")" };
        return new TextEditResult([edit], new TextSelection(range.Start + 1, inner.Length, range.Start + 1 + inner.Length));
    }

    /// <summary>
    /// Uncomments the selected lines when they are exactly one block comment or when every non-blank line is a line
    /// comment; otherwise comments them.
    /// </summary>
    public static TextEditResult? ToggleComment(string text, TextSelection selection, CommentSyntax syntax, int tabSize = DEFAULT_TAB_SIZE)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        bool commented = IsSingleBlockComment(block.Text, syntax)
            || (!syntax.IsBlock && block.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).All(line => IsLineCommented(line, syntax)));
        return commented ? Uncomment(text, selection, syntax) : Comment(text, selection, syntax, tabSize);
    }

    /// <summary>
    /// Comments the selected lines. A block comment encloses all of them, comments inside included, whose markers are
    /// escaped until the enclosing comment is removed; a selection that starts or ends inside a comment is left
    /// unchanged. A line comment starts at the indentation column of the least indented line.
    /// </summary>
    public static TextEditResult? Comment(string text, TextSelection selection, CommentSyntax syntax, int tabSize = DEFAULT_TAB_SIZE)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        if (string.IsNullOrWhiteSpace(block.Text))
        {
            return null;
        }

        if (syntax.IsBlock)
        {
            return CommentBlock(block, syntax) is { } edits ? block.Apply(edits, selection) : null;
        }

        int size = Math.Max(1, tabSize);
        int column = block.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).Min(line => IndentColumns(line, size));
        string prefix = syntax.LinePrefix + " ";
        List<TextEdit> lineEdits = new();
        for (int index = 0; index < block.Lines.Count; index++)
        {
            string line = block.Lines[index];
            if (!string.IsNullOrWhiteSpace(line))
            {
                lineEdits.Add(Insert(block.LineStarts[index] + IndexAtColumn(line, column, size), prefix));
            }
        }

        return block.Apply(lineEdits, selection);
    }

    /// <summary>Removes the comment markers of the selected lines or of the block comments they consist of.</summary>
    public static TextEditResult? Uncomment(string text, TextSelection selection, CommentSyntax syntax)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        if (syntax.IsBlock || IsSingleBlockComment(block.Text, syntax))
        {
            return RemoveBlockComments(block, syntax) is { } edits ? block.Apply(edits, selection) : null;
        }

        List<TextEdit> lineEdits = new();
        for (int index = 0; index < block.Lines.Count; index++)
        {
            string line = block.Lines[index];
            int markerStart = LeadingWhitespace(line);
            int marker = syntax.MatchLineComment(line, markerStart);
            if (marker > 0)
            {
                int length = markerStart + marker < line.Length && line[markerStart + marker] == ' ' ? marker + 1 : marker;
                lineEdits.Add(Remove(block.LineStarts[index] + markerStart, length));
            }
        }

        return block.Apply(lineEdits, selection);
    }

    /// <summary>Indents every non-blank selected line by one level.</summary>
    public static TextEditResult? Indent(string text, TextSelection selection, string indentUnit)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        List<TextEdit> edits = new();
        for (int index = 0; index < block.Lines.Count; index++)
        {
            if (!string.IsNullOrWhiteSpace(block.Lines[index]) && indentUnit.Length > 0)
            {
                edits.Add(Insert(block.LineStarts[index], indentUnit));
            }
        }

        return block.Apply(edits, selection);
    }

    /// <summary>Removes one indentation level (a tab or up to one level of spaces) from every selected line.</summary>
    public static TextEditResult? Unindent(string text, TextSelection selection, int indentationSize)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        List<TextEdit> edits = new();
        for (int index = 0; index < block.Lines.Count; index++)
        {
            string line = block.Lines[index];
            int length = line.StartsWith('\t') ? 1 : 0;
            if (length == 0)
            {
                while (length < indentationSize && length < line.Length && line[length] == ' ')
                {
                    length++;
                }
            }

            if (length > 0)
            {
                edits.Add(Remove(block.LineStarts[index], length));
            }
        }

        return block.Apply(edits, selection);
    }

    /// <summary>Sorts the selected lines alphabetically, ignoring case.</summary>
    public static TextEditResult? SortLines(string text, TextSelection selection, bool ascending)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        if (block.Lines.Count < 2)
        {
            return null;
        }

        IOrderedEnumerable<string> ordered = ascending
            ? block.Lines.OrderBy(line => line, StringComparer.CurrentCultureIgnoreCase)
            : block.Lines.OrderByDescending(line => line, StringComparer.CurrentCultureIgnoreCase);
        string sorted = string.Join(block.Delimiter, ordered.ThenBy(line => line, StringComparer.Ordinal));
        if (sorted == block.Text)
        {
            return null;
        }

        TextEdit edit = new() { Offset = block.Start, Length = block.Text.Length, NewText = sorted };
        return new TextEditResult([edit], new TextSelection(block.Start, sorted.Length, block.Start + sorted.Length));
    }

    /// <summary>
    /// Selects the bracket that matches the bracket at or before the caret and puts the caret behind it. Brackets
    /// inside a comment or string literal only match within that comment or literal.
    /// </summary>
    public static TextSelection? FindMatchingBracket(string text, int caretOffset, CommentSyntax? syntax)
    {
        int offset = Math.Clamp(caretOffset, 0, text.Length);
        int bracket = offset < text.Length && IsBracket(text[offset])
            ? offset
            : offset > 0 && IsBracket(text[offset - 1]) ? offset - 1 : -1;
        if (bracket < 0)
        {
            return null;
        }

        (char open, char close, int step) = text[bracket] switch
        {
            '(' => ('(', ')', 1),
            '[' => ('[', ']', 1),
            '{' => ('{', '}', 1),
            ')' => (')', '(', -1),
            ']' => (']', '[', -1),
            _ => ('}', '{', -1)
        };

        int[] regions = MapRegions(text, syntax);
        int region = regions[bracket];
        int depth = 0;
        for (int index = bracket; index >= 0 && index < text.Length; index += step)
        {
            if (regions[index] != region)
            {
                continue;
            }

            depth += text[index] == open ? 1 : text[index] == close ? -1 : 0;
            if (depth == 0)
            {
                return new TextSelection(index, 1, index + 1);
            }
        }

        return null;
    }

    /// <summary>Gets the offset of the first character of a one-based line number, clamped to the document.</summary>
    public static int GetLineStartOffset(string text, int lineNumber)
    {
        int line = 1;
        for (int index = 0; index < text.Length && line < lineNumber; index++)
        {
            if (text[index] == '\n' || (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n')))
            {
                line++;
                if (line == lineNumber)
                {
                    return index + 1;
                }
            }
        }

        return lineNumber <= 1 ? 0 : text.Length;
    }

    /// <summary>Gets the one-based line and column of an offset.</summary>
    public static (int Line, int Column) GetLineAndColumn(string text, int offset)
    {
        int end = Math.Clamp(offset, 0, text.Length);
        int line = 1;
        int lineStart = 0;
        for (int index = 0; index < end; index++)
        {
            if (text[index] == '\n' || (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n')))
            {
                line++;
                lineStart = index + 1;
            }
        }

        return (line, end - lineStart + 1);
    }

    // Numbers every comment and string literal; code keeps region 0. Without a known syntax the whole text is code.
    private static int[] MapRegions(string text, CommentSyntax? syntax)
    {
        int[] regions = new int[text.Length];
        if (syntax is null)
        {
            return regions;
        }

        int region = 0;
        int index = 0;
        while (index < text.Length)
        {
            int end = FindRegionEnd(text, index, syntax);
            if (end <= index)
            {
                index++;
                continue;
            }

            region++;
            Array.Fill(regions, region, index, end - index);
            index = end;
        }

        return regions;
    }

    // Returns the end of the comment or string literal that starts at the index, or the index when none starts there.
    private static int FindRegionEnd(string text, int index, CommentSyntax syntax)
    {
        if (syntax.StartsLineComment(text, index))
        {
            return LineEnd(text, index);
        }

        if (syntax.BlockStart is { } blockStart && string.CompareOrdinal(text, index, blockStart, 0, blockStart.Length) == 0)
        {
            int close = text.IndexOf(syntax.BlockEnd!, index + blockStart.Length, StringComparison.Ordinal);
            return close < 0 ? text.Length : close + syntax.BlockEnd!.Length;
        }

        if (!syntax.StringQuotes.Contains(text[index], StringComparison.Ordinal))
        {
            return index;
        }

        // A string literal ends at its closing quote or, when unterminated, at the end of the line. The language decides
        // whether an escape character or a doubled quote keeps a quote inside the literal.
        char quote = text[index];
        for (int position = index + 1; position < text.Length; position++)
        {
            char current = text[position];
            if (current == syntax.StringEscape)
            {
                position++;
            }
            else if (current == quote)
            {
                if (syntax.DoubledQuotes && position + 1 < text.Length && text[position + 1] == quote)
                {
                    position++;
                    continue;
                }

                return position + 1;
            }
            else if (current is '\r' or '\n')
            {
                return position;
            }
        }

        return text.Length;
    }

    private static int LineEnd(string text, int offset)
    {
        int index = Math.Clamp(offset, 0, text.Length);
        while (index < text.Length && text[index] is not ('\n' or '\r'))
        {
            index++;
        }

        return index;
    }

    private static TextEditResult? TransformSelection(string text, TextSelection selection, Func<string, string> transform)
    {
        TextSelection range = Clamp(text, selection);
        if (range.Length == 0)
        {
            return null;
        }

        string original = text.Substring(range.Start, range.Length);
        string transformed = transform(original);
        if (string.Equals(original, transformed, StringComparison.Ordinal))
        {
            return null;
        }

        TextEdit edit = new() { Offset = range.Start, Length = range.Length, NewText = transformed };
        return new TextEditResult([edit], new TextSelection(range.Start, transformed.Length, range.Start + transformed.Length));
    }

    private static TextSelection Clamp(string text, TextSelection selection)
    {
        int start = Math.Clamp(selection.Start, 0, text.Length);
        int length = Math.Clamp(selection.Length, 0, text.Length - start);
        return new TextSelection(start, length, Math.Clamp(selection.CaretOffset, 0, text.Length));
    }

    private static TextEdit Insert(int offset, string value) => new() { Offset = offset, Length = 0, NewText = value };

    private static TextEdit Remove(int offset, int length) => new() { Offset = offset, Length = length, NewText = string.Empty };

    private static bool IsBracket(char value) => value is '(' or ')' or '[' or ']' or '{' or '}';

    private static bool IsLineCommented(string line, CommentSyntax syntax)
    {
        return syntax.StartsLineComment(line, LeadingWhitespace(line));
    }

    // A block is commented when it is one block comment with nothing but whitespace around it.
    private static bool IsSingleBlockComment(string block, CommentSyntax syntax)
    {
        return syntax.BlockStart is not null
            && SplitComments(block, syntax) is { } parts
            && parts.Count(part => part.IsComment) == 1
            && parts.All(part => part.IsComment || string.IsNullOrWhiteSpace(block.Substring(part.Start, part.Length)));
    }

    // One comment encloses the block without the whitespace around it, separated from the markers by a space.
    private static List<TextEdit>? CommentBlock(LineBlock block, CommentSyntax syntax)
    {
        string value = block.Text;
        if (SplitComments(value, syntax) is null)
        {
            return null;
        }

        int start = value.Length - value.TrimStart().Length;
        int end = value.TrimEnd().Length;
        List<TextEdit> edits = [Insert(block.Start + start, syntax.BlockStart + " ")];
        if (syntax.BlockForbidden is { } forbidden)
        {
            for (int index = start; index < end - 1; index++)
            {
                if (value[index] == forbidden[0] && (value[index + 1] == forbidden[1] || value[index + 1] == ESCAPE_MARK))
                {
                    edits.Add(Insert(block.Start + index + 1, ESCAPE_MARK.ToString()));
                }
            }
        }

        edits.Add(Insert(block.Start + end, " " + syntax.BlockEnd));
        return edits;
    }

    // Removes the markers of every comment, the single space the commenting added inside each of them and the escapes.
    private static List<TextEdit>? RemoveBlockComments(LineBlock block, CommentSyntax syntax)
    {
        string value = block.Text;
        if (SplitComments(value, syntax) is not { } parts)
        {
            return null;
        }

        List<TextEdit> edits = new();
        foreach (BlockPart part in parts.Where(part => part.IsComment))
        {
            int innerStart = part.Start + syntax.BlockStart!.Length;
            int innerEnd = part.Start + part.Length - syntax.BlockEnd!.Length;
            if (innerStart < innerEnd && value[innerStart] == ' ')
            {
                innerStart++;
            }

            if (innerEnd > innerStart && value[innerEnd - 1] == ' ')
            {
                innerEnd--;
            }

            edits.Add(Remove(block.Start + part.Start, innerStart - part.Start));
            if (syntax.BlockForbidden is { } forbidden)
            {
                for (int index = innerStart; index < innerEnd - 1; index++)
                {
                    if (value[index] == forbidden[0] && value[index + 1] == ESCAPE_MARK)
                    {
                        edits.Add(Remove(block.Start + index + 1, 1));
                        index++;
                    }
                }
            }

            edits.Add(Remove(block.Start + innerEnd, part.Start + part.Length - innerEnd));
        }

        return edits;
    }

    // Splits a block into its complete comments and the text between them. Returns null when a marker has no partner
    // inside the block, as when the selection starts or ends within a comment.
    private static List<BlockPart>? SplitComments(string block, CommentSyntax syntax)
    {
        string start = syntax.BlockStart!;
        string end = syntax.BlockEnd!;
        List<BlockPart> parts = new();
        int position = 0;
        while (position < block.Length)
        {
            int open = block.IndexOf(start, position, StringComparison.Ordinal);
            int gap = (open < 0 ? block.Length : open) - position;
            if (block.IndexOf(end, position, gap, StringComparison.Ordinal) >= 0)
            {
                return null;
            }

            if (gap > 0)
            {
                parts.Add(new BlockPart(position, gap, IsComment: false));
            }

            if (open < 0)
            {
                break;
            }

            int close = block.IndexOf(end, open + start.Length, StringComparison.Ordinal);
            if (close < 0)
            {
                return null;
            }

            parts.Add(new BlockPart(open, close + end.Length - open, IsComment: true));
            position = close + end.Length;
        }

        return parts;
    }

    private static int LeadingWhitespace(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] is ' ' or '\t')
        {
            count++;
        }

        return count;
    }

    // A tab advances to the next multiple of the tab size.
    private static int NextColumn(int column, char value, int tabSize)
    {
        return value == '\t' ? (column / tabSize + 1) * tabSize : column + 1;
    }

    private static int IndentColumns(string line, int tabSize)
    {
        int column = 0;
        for (int index = 0; index < LeadingWhitespace(line); index++)
        {
            column = NextColumn(column, line[index], tabSize);
        }

        return column;
    }

    // The last index in the indentation whose column does not exceed the given column; a tab that spans the column
    // stays behind the inserted text.
    private static int IndexAtColumn(string line, int column, int tabSize)
    {
        int indentation = LeadingWhitespace(line);
        int index = 0;
        int current = 0;
        while (index < indentation && NextColumn(current, line[index], tabSize) <= column)
        {
            current = NextColumn(current, line[index], tabSize);
            index++;
        }

        return index;
    }

    /// <summary>A complete block comment or the text between two of them.</summary>
    private readonly record struct BlockPart(int Start, int Length, bool IsComment);

    /// <summary>The whole lines a selection touches, without the line break after the last one.</summary>
    private sealed class LineBlock
    {
        private LineBlock(int start, string text, string delimiter)
        {
            Start = start;
            Text = text;
            Delimiter = delimiter;
            List<string> lines = new();
            List<int> lineStarts = new();
            int lineStart = 0;
            int index = 0;
            while (index < text.Length)
            {
                int breakLength = LineBreakLength(text, index);
                if (breakLength == 0)
                {
                    index++;
                    continue;
                }

                lines.Add(text[lineStart..index]);
                lineStarts.Add(start + lineStart);
                index += breakLength;
                lineStart = index;
            }

            lines.Add(text[lineStart..]);
            lineStarts.Add(start + lineStart);
            Lines = lines;
            LineStarts = lineStarts;
        }

        public int Start { get; }

        public string Text { get; }

        public string Delimiter { get; }

        public IReadOnlyList<string> Lines { get; }

        /// <summary>Gets the document offset of each line in <see cref="Lines"/>.</summary>
        public IReadOnlyList<int> LineStarts { get; }

        public static LineBlock FromSelection(string text, TextSelection selection)
        {
            TextSelection range = Clamp(text, selection);
            int blockStart = LineStart(text, range.Length == 0 ? range.CaretOffset : range.Start);

            // A selection that ends at the start of a line does not include that line.
            int last = range.Length == 0 ? blockStart : range.End;
            if (range.Length > 0 && IsLineStart(text, last))
            {
                last = PreviousLineEnd(text, last);
            }

            int blockEnd = LineEnd(text, Math.Max(last, blockStart));
            return new LineBlock(blockStart, text[blockStart..blockEnd], DetectDelimiter(text));
        }

        // A selection selects the changed lines; a caret keeps its position relative to the end of its line.
        public TextEditResult? Apply(IReadOnlyList<TextEdit> edits, TextSelection original)
        {
            if (edits.Count == 0)
            {
                return null;
            }

            int length = Text.Length + edits.Sum(edit => edit.NewText.Length - edit.Length);
            if (original.Length > 0)
            {
                return new TextEditResult(edits, new TextSelection(Start, length, Start + length));
            }

            int caretFromEnd = Start + Text.Length - original.CaretOffset;
            int caret = Math.Clamp(Start + length - caretFromEnd, Start, Start + length);
            return new TextEditResult(edits, new TextSelection(caret, 0, caret));
        }

        private static int LineBreakLength(string text, int index)
        {
            if (index >= text.Length)
            {
                return 0;
            }

            return text[index] switch
            {
                '\r' when index + 1 < text.Length && text[index + 1] == '\n' => 2,
                '\r' or '\n' => 1,
                _ => 0
            };
        }

        private static bool IsLineStart(string text, int offset)
        {
            return offset > 0 && offset <= text.Length && text[offset - 1] is '\n' or '\r';
        }

        private static int PreviousLineEnd(string text, int lineStart)
        {
            int end = lineStart - 1;
            return end > 0 && text[end] == '\n' && text[end - 1] == '\r' ? end - 1 : end;
        }

        // An offset between the two characters of "\r\n" belongs to the line the break ends.
        private static int LineStart(string text, int offset)
        {
            int index = Math.Clamp(offset, 0, text.Length);
            if (index > 0 && index < text.Length && text[index - 1] == '\r' && text[index] == '\n')
            {
                index--;
            }

            while (index > 0 && text[index - 1] is not ('\n' or '\r'))
            {
                index--;
            }

            return index;
        }

        private static string DetectDelimiter(string text)
        {
            int index = text.IndexOfAny(['\r', '\n']);
            if (index < 0)
            {
                return Environment.NewLine;
            }

            return text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : text[index].ToString();
        }
    }
}
