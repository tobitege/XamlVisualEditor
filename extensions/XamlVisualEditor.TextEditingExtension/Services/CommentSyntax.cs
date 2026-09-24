namespace XamlVisualEditor.TextEditingExtension.Services;

/// <summary>
/// Comment and string markers of a language. <see cref="LinePrefix"/> comments single lines; a language without it
/// comments blocks between <see cref="BlockStart"/> and <see cref="BlockEnd"/>. <see cref="StringQuotes"/> lists the
/// characters that open a one-line string literal.
/// </summary>
public sealed record CommentSyntax(string? LinePrefix, string? BlockStart, string? BlockEnd, string StringQuotes)
{
    private static readonly CommentSyntax Markup = new(null, "<!--", "-->", string.Empty);
    private static readonly CommentSyntax Slashes = new("//", "/*", "*/", "\"'");
    private static readonly CommentSyntax Hash = new("#", null, null, "\"'");
    private static readonly CommentSyntax Dashes = new("--", "/*", "*/", "\"'");
    private static readonly CommentSyntax Semicolon = new(";", null, null, "\"");
    private static readonly CommentSyntax Apostrophe = new("'", null, null, "\"");
    private static readonly CommentSyntax Rem = new("REM", null, null, "\"");
    private static readonly CommentSyntax Stylesheet = new(null, "/*", "*/", "\"'");

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
        [".py"] = Hash, [".sh"] = Hash, [".bash"] = Hash, [".ps1"] = Hash, [".psm1"] = Hash, [".psd1"] = Hash,
        [".yml"] = Hash, [".yaml"] = Hash, [".toml"] = Hash, [".rb"] = Hash, [".pl"] = Hash, [".r"] = Hash,
        [".cmake"] = Hash, [".properties"] = Hash, [".editorconfig"] = Hash, [".gitignore"] = Hash,
        [".gitattributes"] = Hash, [".dockerfile"] = Hash, [".conf"] = Hash,
        [".sql"] = Dashes, [".lua"] = Dashes, [".hs"] = Dashes,
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
        ["jsonc"] = Slashes, ["python"] = Hash, ["powershell"] = Hash, ["shellscript"] = Hash, ["yaml"] = Hash,
        ["toml"] = Hash, ["ruby"] = Hash, ["sql"] = Dashes, ["lua"] = Dashes, ["ini"] = Semicolon,
        ["vb"] = Apostrophe, ["bat"] = Rem, ["css"] = Stylesheet
    };

    /// <summary>Gets whether the language comments whole blocks rather than single lines.</summary>
    public bool IsBlock => LinePrefix is null;

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
}
