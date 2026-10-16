using System.Globalization;
using System.Text;
using XamlVisualEditor.Xaml.Ast;

namespace XamlVisualEditor.Shell.ViewModels;

/// <summary>Rewrites actual name references, not quoted format strings or escaped markup literals.</summary>
internal static class XamlNameReferences
{
    internal static (string Text, bool Found) RewriteSelector(string text, IReadOnlyDictionary<string, string> names)
    {
        List<(int Start, int Length, string Value)> edits = new();
        for (int position = 0; position < text.Length; position++)
        {
            if (text[position] == '\\') { position++; continue; }
            if (text[position] is '\'' or '"')
            {
                char quote = text[position++];
                while (position < text.Length && text[position] != quote)
                {
                    if (text[position] == '\\') position++;
                    position++;
                }
                continue;
            }
            if (text[position] == '[')
            {
                int start = position + 1;
                char quote = '\0';
                for (position++; position < text.Length; position++)
                {
                    char character = text[position];
                    if (character == '\\') { position++; continue; }
                    if (quote != '\0') { if (character == quote) quote = '\0'; continue; }
                    if (character is '\'' or '"') quote = character;
                    else if (character == ']') break;
                }
                if (position == text.Length) break;
                int equals = text.IndexOf('=', start, position - start);
                if (equals >= 0 && text[start..equals].Trim() == "Name")
                {
                    int valueStart = equals + 1;
                    int valueEnd = position;
                    while (valueStart < valueEnd && char.IsWhiteSpace(text[valueStart])) valueStart++;
                    while (valueEnd > valueStart && char.IsWhiteSpace(text[valueEnd - 1])) valueEnd--;
                    if (valueEnd - valueStart >= 2 && text[valueStart] is '\'' or '"' && text[valueEnd - 1] == text[valueStart])
                    { valueStart++; valueEnd--; }
                    Add(valueStart, valueEnd);
                }
                continue;
            }
            if (text[position] != '#') continue;
            int nameStart = position + 1;
            int nameEnd = nameStart;
            while (nameEnd < text.Length && IsNameCharacter(text[nameEnd])) nameEnd++;
            Add(nameStart, nameEnd);
            position = nameEnd - 1;
        }
        if (edits.Count == 0) return (text, false);
        StringBuilder result = new(text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return (result.ToString(), true);

        void Add(int start, int end)
        {
            if (names.TryGetValue(text[start..end], out string? replacement)) edits.Add((start, end - start, replacement));
        }
    }

    internal static (string Text, bool Found) Rewrite(MutableAstObjectNode owner, MutableAstPropertyNode property,
        IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, string> namespaces)
    {
        if (property.Value is not MutableAstTextNode value) return (string.Empty, false);
        string text = value.Text;
        List<(int Start, int Length, string Value)> edits = new();
        string propertyName = property.PropertyName[(property.PropertyName.LastIndexOf('.') + 1)..];
        if (text.StartsWith('{') && !text.StartsWith("{}", StringComparison.Ordinal)) Visit(0);
        else if (IsBinding(owner.TypeName) && IsBindingNamespace(owner.XmlNamespace))
        {
            if (propertyName == "ElementName") ReplaceName(0, text.Length);
            else if (propertyName == "Path") ReplacePath(0, text.Length);
        }
        else if (owner.XmlNamespace == DesignerNameEditor.XamlNamespace && owner.TypeName == "Reference"
                 && propertyName == "Name") ReplaceName(0, text.Length);

        if (edits.Count == 0) return (text, false);
        StringBuilder result = new(text);
        foreach (var edit in edits.OrderByDescending(edit => edit.Start))
            result.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Value);
        return (result.ToString(), true);

        void ReplaceName(int start, int end)
        {
            if (names.TryGetValue(text[start..end], out string? replacement)) edits.Add((start, end - start, replacement));
        }

        void ReplacePath(int start, int end)
        {
            if (start < end && text[start] == '!') start++;
            if (start >= end || text[start] != '#') return;
            int nameStart = ++start;
            while (start < end && IsNameCharacter(text[start])) start++;
            ReplaceName(nameStart, start);
        }

        void Visit(int opening)
        {
            int closing = FindEnd(text, opening);
            if (closing < 0) return;
            int headerStart = opening + 1;
            while (headerStart < closing && char.IsWhiteSpace(text[headerStart])) headerStart++;
            int headerEnd = headerStart;
            while (headerEnd < closing && !char.IsWhiteSpace(text[headerEnd]) && text[headerEnd] != ',') headerEnd++;
            string header = text[headerStart..headerEnd];
            int colon = header.IndexOf(':');
            string localName = header[(colon + 1)..];
            string? xmlNamespace = colon < 0 ? "https://github.com/avaloniaui"
                : namespaces.GetValueOrDefault(header[..colon]);
            bool binding = IsBinding(localName) && IsBindingNamespace(xmlNamespace);
            bool reference = localName == "Reference" && xmlNamespace == DesignerNameEditor.XamlNamespace;
            int argumentStart = headerEnd;
            char quote = '\0';
            for (int position = argumentStart; position < closing; position++)
            {
                char character = text[position];
                if (quote != '\0')
                {
                    if (character == '\\') position++;
                    else if (character == quote) quote = '\0';
                    continue;
                }
                if (character is '\'' or '"') quote = character;
                else if (character == '{')
                {
                    int end = FindEnd(text, position);
                    if (end < 0) return;
                    position = end;
                }
                else if (character == ',')
                {
                    Argument(argumentStart, position, binding, reference);
                    argumentStart = position + 1;
                }
            }
            Argument(argumentStart, closing, binding, reference);
        }

        void Argument(int start, int end, bool binding, bool reference)
        {
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (start == end) return;
            string key = string.Empty;
            int equals = text.IndexOf('=', start, end - start);
            if (equals >= 0 && text[start..equals].Trim().All(character => char.IsLetter(character) || character == '_'))
            {
                key = text[start..equals].Trim();
                start = equals + 1;
                while (start < end && char.IsWhiteSpace(text[start])) start++;
            }
            bool quoted = end - start >= 2 && text[start] is '\'' or '"' && text[end - 1] == text[start];
            int valueStart = quoted ? start + 1 : start;
            int valueEnd = quoted ? end - 1 : end;
            if (binding && key == "ElementName" || reference && key is "" or "Name") ReplaceName(valueStart, valueEnd);
            else if (binding && key is "" or "Path") ReplacePath(valueStart, valueEnd);
            if (!quoted && valueEnd - valueStart > 1 && text[valueStart] == '{' && text[valueStart + 1] != '}') Visit(valueStart);
        }
    }

    private static bool IsBinding(string name) => name is "Binding" or "ReflectionBinding" or "CompiledBinding"
        or "ReflectionBindingExtension" or "CompiledBindingExtension";

    private static bool IsBindingNamespace(string? value) => value is "https://github.com/avaloniaui" or "using:Avalonia.Data"
        or "clr-namespace:Avalonia.Data" || value?.StartsWith("clr-namespace:Avalonia.Data;", StringComparison.Ordinal) == true;

    private static bool IsNameCharacter(char character) => character == '_' || char.IsLetterOrDigit(character)
        || char.GetUnicodeCategory(character) is UnicodeCategory.LetterNumber or UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.Format;

    private static int FindEnd(string text, int opening)
    {
        int depth = 0;
        char quote = '\0';
        for (int position = opening; position < text.Length; position++)
        {
            char character = text[position];
            if (quote != '\0')
            {
                if (character == '\\') position++;
                else if (character == quote) quote = '\0';
            }
            else if (character is '\'' or '"') quote = character;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return position;
        }
        return -1;
    }
}
