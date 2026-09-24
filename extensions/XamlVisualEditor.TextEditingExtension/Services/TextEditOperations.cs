using System.Globalization;
using System.Text;
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

/// <summary>Computes text editing operations on a document text; the results are applied by the caller.</summary>
public static class TextEditOperations
{
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

    /// <summary>Comments the selected lines, or uncomments them when every non-blank line is already commented.</summary>
    public static TextEditResult? ToggleComment(string text, TextSelection selection, CommentSyntax syntax)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        bool commented = syntax.IsBlock
            ? IsBlockCommented(block.Text, syntax)
            : block.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).All(line => IsLineCommented(line, syntax.LinePrefix!));
        return commented ? Uncomment(text, selection, syntax) : Comment(text, selection, syntax);
    }

    /// <summary>Comments the selected lines.</summary>
    public static TextEditResult? Comment(string text, TextSelection selection, CommentSyntax syntax)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        if (string.IsNullOrWhiteSpace(block.Text))
        {
            return null;
        }

        if (syntax.IsBlock)
        {
            int indent = LeadingWhitespace(block.Text);
            string replaced = block.Text[..indent] + syntax.BlockStart + " " + block.Text[indent..] + " " + syntax.BlockEnd;
            return block.Replace(replaced, selection);
        }

        int column = block.Lines.Where(line => !string.IsNullOrWhiteSpace(line)).Min(LeadingWhitespace);
        string prefix = syntax.LinePrefix + " ";
        return block.Replace(block.MapLines(line => string.IsNullOrWhiteSpace(line) ? line : line.Insert(column, prefix)), selection);
    }

    /// <summary>Removes the comment markers of the selected lines.</summary>
    public static TextEditResult? Uncomment(string text, TextSelection selection, CommentSyntax syntax)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        string uncommented = syntax.IsBlock
            ? RemoveBlockComment(block.Text, syntax)
            : block.MapLines(line => RemoveLineComment(line, syntax.LinePrefix!));
        return uncommented == block.Text ? null : block.Replace(uncommented, selection);
    }

    /// <summary>Indents every non-blank selected line by one level.</summary>
    public static TextEditResult? Indent(string text, TextSelection selection, string indentUnit)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        string indented = block.MapLines(line => line.Length == 0 ? line : indentUnit + line);
        return indented == block.Text ? null : block.Replace(indented, selection);
    }

    /// <summary>Removes one indentation level (a tab or up to one level of spaces) from every selected line.</summary>
    public static TextEditResult? Unindent(string text, TextSelection selection, int indentationSize)
    {
        LineBlock block = LineBlock.FromSelection(text, selection);
        string unindented = block.MapLines(line =>
        {
            if (line.StartsWith('\t'))
            {
                return line[1..];
            }

            int spaces = 0;
            while (spaces < indentationSize && spaces < line.Length && line[spaces] == ' ')
            {
                spaces++;
            }

            return line[spaces..];
        });
        return unindented == block.Text ? null : block.Replace(unindented, selection);
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
        if (syntax.LinePrefix is { } prefix && string.CompareOrdinal(text, index, prefix, 0, prefix.Length) == 0)
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

        // A string literal ends at its closing quote or, when unterminated, at the end of the line.
        char quote = text[index];
        for (int position = index + 1; position < text.Length; position++)
        {
            if (text[position] == '\\')
            {
                position++;
            }
            else if (text[position] == quote)
            {
                return position + 1;
            }
            else if (text[position] is '\r' or '\n')
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

    private static bool IsBracket(char value) => value is '(' or ')' or '[' or ']' or '{' or '}';

    private static bool IsLineCommented(string line, string prefix)
    {
        return line.AsSpan().TrimStart().StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string RemoveLineComment(string line, string prefix)
    {
        if (!IsLineCommented(line, prefix))
        {
            return line;
        }

        int index = line.IndexOf(prefix, StringComparison.Ordinal);
        int length = index + prefix.Length < line.Length && line[index + prefix.Length] == ' ' ? prefix.Length + 1 : prefix.Length;
        return line.Remove(index, length);
    }

    private static bool IsBlockCommented(string block, CommentSyntax syntax)
    {
        string trimmed = block.Trim();
        return trimmed.Length >= syntax.BlockStart!.Length + syntax.BlockEnd!.Length
            && trimmed.StartsWith(syntax.BlockStart, StringComparison.Ordinal)
            && trimmed.EndsWith(syntax.BlockEnd, StringComparison.Ordinal);
    }

    // Removes the outer block markers and the single space the commenting added inside each of them.
    private static string RemoveBlockComment(string block, CommentSyntax syntax)
    {
        if (!IsBlockCommented(block, syntax))
        {
            return block;
        }

        int start = block.IndexOf(syntax.BlockStart!, StringComparison.Ordinal);
        int end = block.LastIndexOf(syntax.BlockEnd!, StringComparison.Ordinal);
        int innerStart = start + syntax.BlockStart!.Length;
        int innerEnd = end;
        if (innerStart < innerEnd && block[innerStart] == ' ')
        {
            innerStart++;
        }

        if (innerEnd > innerStart && block[innerEnd - 1] == ' ')
        {
            innerEnd--;
        }

        return block[..start] + block[innerStart..innerEnd] + block[(end + syntax.BlockEnd!.Length)..];
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

    /// <summary>The whole lines a selection touches, without the line break after the last one.</summary>
    private sealed class LineBlock
    {
        private LineBlock(int start, string text, string delimiter)
        {
            Start = start;
            Text = text;
            Delimiter = delimiter;
            Lines = SplitLines(text);
        }

        public int Start { get; }

        public string Text { get; }

        public string Delimiter { get; }

        public IReadOnlyList<string> Lines { get; }

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

        public string MapLines(Func<string, string> map)
        {
            StringBuilder builder = new(Text.Length + Lines.Count * 4);
            int position = 0;
            foreach (string line in Lines)
            {
                builder.Append(map(line));
                position += line.Length;
                int breakLength = LineBreakLength(Text, position);
                builder.Append(Text, position, breakLength);
                position += breakLength;
            }

            return builder.ToString();
        }

        public TextEditResult Replace(string replacement, TextSelection original)
        {
            TextEdit edit = new() { Offset = Start, Length = Text.Length, NewText = replacement };
            if (original.Length > 0)
            {
                return new TextEditResult([edit], new TextSelection(Start, replacement.Length, Start + replacement.Length));
            }

            // A caret keeps its position relative to the end of its line.
            int caretFromEnd = Start + Text.Length - original.CaretOffset;
            int caret = Math.Clamp(Start + replacement.Length - caretFromEnd, Start, Start + replacement.Length);
            return new TextEditResult([edit], new TextSelection(caret, 0, caret));
        }

        private static List<string> SplitLines(string text)
        {
            List<string> lines = new();
            int start = 0;
            int index = 0;
            while (index < text.Length)
            {
                int breakLength = LineBreakLength(text, index);
                if (breakLength == 0)
                {
                    index++;
                    continue;
                }

                lines.Add(text[start..index]);
                index += breakLength;
                start = index;
            }

            lines.Add(text[start..]);
            return lines;
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
