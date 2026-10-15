namespace XamlVisualEditor.CodeEditor;

/// <summary>Finds where an XML element ends in its source text.</summary>
public static class XmlElementExtent
{
    /// <summary>
    /// Returns the offset just after the element whose start tag begins at <paramref name="startOffset"/>, that is
    /// after its "/&gt;" or after the "&gt;" of its end tag; or -1 when the text ends before the element does.
    /// Quoted attribute values, comments, CDATA sections and processing instructions are skipped.
    /// </summary>
    public static int FindEnd(string text, int startOffset)
    {
        if (startOffset < 0 || startOffset >= text.Length || text[startOffset] != '<')
            return -1;

        int depth = 0;
        int position = startOffset;
        while (position < text.Length)
        {
            if (text[position] != '<')
            {
                position++;
                continue;
            }

            if (Matches(text, position, "<!--"))
            {
                position = SkipPast(text, position + 4, "-->");
            }
            else if (Matches(text, position, "<![CDATA["))
            {
                position = SkipPast(text, position + 9, "]]>");
            }
            else if (Matches(text, position, "<?"))
            {
                position = SkipPast(text, position + 2, "?>");
            }
            else if (Matches(text, position, "</"))
            {
                position = SkipPast(text, position + 2, ">");
                depth--;
                if (depth == 0)
                    return position;
            }
            else
            {
                (position, bool selfClosing) = SkipStartTag(text, position + 1);
                if (position < 0)
                    return -1;
                if (!selfClosing)
                    depth++;
                else if (depth == 0)
                    return position;
            }

            if (position < 0)
                return -1;
        }

        return -1;
    }

    /// <summary>
    /// Returns the offset just after the "&gt;" of the start tag that begins at <paramref name="startOffset"/>, or -1
    /// when the text ends before the start tag does. Quoted attribute values are skipped.
    /// </summary>
    public static int FindStartTagEnd(string text, int startOffset)
    {
        if (startOffset < 0 || startOffset >= text.Length || text[startOffset] != '<')
            return -1;

        return SkipStartTag(text, startOffset + 1).End;
    }

    private static (int End, bool SelfClosing) SkipStartTag(string text, int position)
    {
        char quote = '\0';
        for (; position < text.Length; position++)
        {
            char character = text[position];
            if (quote != '\0')
            {
                if (character == quote)
                    quote = '\0';
            }
            else if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '>')
            {
                return (position + 1, text[position - 1] == '/');
            }
        }

        return (-1, false);
    }

    private static bool Matches(string text, int position, string value) =>
        string.CompareOrdinal(text, position, value, 0, value.Length) == 0;

    private static int SkipPast(string text, int position, string terminator)
    {
        int index = text.IndexOf(terminator, position, StringComparison.Ordinal);
        return index < 0 ? -1 : index + terminator.Length;
    }
}
