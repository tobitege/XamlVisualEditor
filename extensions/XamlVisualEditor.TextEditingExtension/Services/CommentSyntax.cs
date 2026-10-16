namespace XamlVisualEditor.TextEditingExtension.Services;

/// <summary>
/// Comment and string markers of a language. <see cref="LinePrefix"/> comments single lines; a language without it
/// comments blocks between <see cref="BlockStart"/> and <see cref="BlockEnd"/>. <see cref="StringQuotes"/> lists the
/// characters that open a one-line string literal.
/// </summary>
public sealed record CommentSyntax(string? LinePrefix, string? BlockStart, string? BlockEnd, string StringQuotes)
{
    private static readonly CommentSyntax Markup = new(null, "<!--", "-->", string.Empty) { BlockForbidden = "--" };
    private static readonly CommentSyntax Slashes = new("//", "/*", "*/", "\"'");
    private static readonly CommentSyntax Hash = new("#", null, null, "\"'");
    private static readonly CommentSyntax PowerShell = new("#", "<#", "#>", "\"'") { StringEscape = '`', DoubledQuotes = true };
    private static readonly CommentSyntax Sql = new("--", "/*", "*/", "\"'") { StringEscape = null, DoubledQuotes = true };
    private static readonly CommentSyntax Dashes = new("--", null, null, "\"'");
    private static readonly CommentSyntax Semicolon = new(";", null, null, "\"") { StringEscape = null };
    private static readonly CommentSyntax Apostrophe = new("'", null, null, "\"")
    {
        StringEscape = null,
        DoubledQuotes = true,
        OtherLinePrefixes = ["REM"]
    };
    private static readonly CommentSyntax Rem = new("REM", null, null, "\"")
    {
        StringEscape = null,
        OtherLinePrefixes = ["@REM", "::"]
    };
    private static readonly CommentSyntax Stylesheet = new(null, "/*", "*/", "\"'") { BlockForbidden = "*/" };

    private static readonly Dictionary<string, CommentSyntax> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".xml"] = Markup, [".xaml"] = Markup, [".axaml"] = Markup, [".html"] = Markup, [".htm"] = Markup,
        [".xhtml"] = Markup, [".svg"] = Markup, [".md"] = Markup, [".resx"] = Markup, [".config"] = Markup,
        [".manifest"] = Markup, [".nuspec"] = Markup, [".slnx"] = Markup, [".csproj"] = Markup,
        [".vbproj"] = Markup, [".fsproj"] = Markup, [".props"] = Markup, [".targets"] = Markup,
        [".cs"] = Slashes, [".csx"] = Slashes, [".fs"] = Slashes, [".fsx"] = Slashes, [".js"] = Slashes,
        [".mjs"] = Slashes, [".jsx"] = Slashes, [".ts"] = Slashes, [".tsx"] = Slashes, [".java"] = Slashes,
        [".kt"] = Slashes, [".c"] = Slashes, [".h"] = Slashes, [".cpp"] = Slashes, [".hpp"] = Slashes,
        [".cc"] = Slashes, [".go"] = Slashes, [".rs"] = Slashes, [".swift"] = Slashes, [".dart"] = Slashes,
        [".jsonc"] = Slashes, [".scss"] = Slashes, [".less"] = Slashes, [".razor"] = Slashes,
        [".py"] = Hash, [".sh"] = Hash, [".bash"] = Hash, [".ps1"] = PowerShell, [".psm1"] = PowerShell,
        [".psd1"] = PowerShell, [".yml"] = Hash, [".yaml"] = Hash, [".toml"] = Hash, [".rb"] = Hash, [".pl"] = Hash,
        [".r"] = Hash, [".cmake"] = Hash, [".properties"] = Hash, [".editorconfig"] = Hash, [".gitignore"] = Hash,
        [".gitattributes"] = Hash, [".dockerfile"] = Hash, [".conf"] = Hash,
        [".sql"] = Sql, [".lua"] = Dashes, [".hs"] = Dashes,
        [".ini"] = Semicolon, [".asm"] = Semicolon,
        [".vb"] = Apostrophe, [".vbs"] = Apostrophe, [".bas"] = Apostrophe,
        [".bat"] = Rem, [".cmd"] = Rem,
        [".css"] = Stylesheet
    };

    private static readonly Dictionary<string, CommentSyntax> ByLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        ["xml"] = Markup, ["xaml"] = Markup, ["axaml"] = Markup, ["html"] = Markup, ["markdown"] = Markup,
        ["csharp"] = Slashes, ["fsharp"] = Slashes, ["javascript"] = Slashes, ["typescript"] = Slashes,
        ["java"] = Slashes, ["cpp"] = Slashes, ["c"] = Slashes, ["go"] = Slashes, ["rust"] = Slashes,
        ["jsonc"] = Slashes, ["python"] = Hash, ["powershell"] = PowerShell, ["shellscript"] = Hash, ["yaml"] = Hash,
        ["toml"] = Hash, ["ruby"] = Hash, ["sql"] = Sql, ["lua"] = Dashes, ["ini"] = Semicolon,
        ["vb"] = Apostrophe, ["bat"] = Rem, ["css"] = Stylesheet
    };

    /// <summary>Gets whether the language comments whole blocks rather than single lines.</summary>
    public bool IsBlock => LinePrefix is null;

    /// <summary>Gets further markers that start a line comment; uncommenting removes them like <see cref="LinePrefix"/>.</summary>
    public IReadOnlyList<string> OtherLinePrefixes { get; init; } = [];

    /// <summary>
    /// Gets the two characters that must not appear inside a block comment, such as "--" in XML; commenting a block
    /// escapes them.
    /// </summary>
    public string? BlockForbidden { get; init; }

    /// <summary>Gets the character that escapes the next character in a string literal; <see langword="null"/> for none.</summary>
    public char? StringEscape { get; init; } = '\\';

    /// <summary>Gets whether a doubled quote stands for one quote inside a string literal, as in VB and SQL.</summary>
    public bool DoubledQuotes { get; init; }

    /// <summary>Gets whether a line comment starts at the index.</summary>
    public bool StartsLineComment(string text, int index)
    {
        return MatchLineComment(text, index) > 0;
    }

    /// <summary>
    /// Gets the length of the line comment marker that starts at the index, or 0 when none starts there. A marker that
    /// ends in a letter, such as REM, is a command word: it ignores case, stands first on its line and is followed by
    /// whitespace or the end of the line. A marker that starts with a colon, such as the batch label "::", also stands
    /// first on its line.
    /// </summary>
    public int MatchLineComment(string text, int index)
    {
        if (LinePrefix is not { } prefix)
        {
            return 0;
        }

        int length = MatchPrefix(text, index, prefix);
        for (int other = 0; length == 0 && other < OtherLinePrefixes.Count; other++)
        {
            length = MatchPrefix(text, index, OtherLinePrefixes[other]);
        }

        return length;
    }

    /// <summary>Resolves the syntax of a document; <see langword="null"/> when the language has no known comments.</summary>
    public static CommentSyntax? Resolve(string? filePath, string? languageId)
    {
        if (!string.IsNullOrWhiteSpace(filePath)
            && ByExtension.TryGetValue(Path.GetExtension(filePath), out CommentSyntax? byExtension))
        {
            return byExtension;
        }

        if (!string.IsNullOrWhiteSpace(filePath)
            && string.Equals(Path.GetFileName(filePath), "Dockerfile", StringComparison.OrdinalIgnoreCase))
        {
            return Hash;
        }

        return !string.IsNullOrWhiteSpace(languageId) && ByLanguage.TryGetValue(languageId, out CommentSyntax? byLanguage)
            ? byLanguage
            : null;
    }

    private static int MatchPrefix(string text, int index, string prefix)
    {
        if (index < 0 || index + prefix.Length > text.Length)
        {
            return 0;
        }

        bool word = char.IsLetter(prefix[^1]);
        if (!word && prefix[0] != ':')
        {
            return string.CompareOrdinal(text, index, prefix, 0, prefix.Length) == 0 ? prefix.Length : 0;
        }

        int end = index + prefix.Length;
        if (string.Compare(text, index, prefix, 0, prefix.Length, StringComparison.OrdinalIgnoreCase) != 0
            || (word && end < text.Length && !char.IsWhiteSpace(text[end])))
        {
            return 0;
        }

        for (int position = index - 1; position >= 0 && text[position] is not ('\r' or '\n'); position--)
        {
            if (!char.IsWhiteSpace(text[position]))
            {
                return 0;
            }
        }

        return prefix.Length;
    }
}
