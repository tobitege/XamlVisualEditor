using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.TextEditingExtension.Services;
using Xunit;
using TextEditingExtensionEntry = XamlVisualEditor.TextEditingExtension.TextEditingExtension;

namespace XamlVisualEditor.Tests.Unit.Extensions.TextEditing;

public sealed class TextEditOperationsTests
{
    private static readonly CommentSyntax CSharp = CommentSyntax.Resolve("File.cs", null)!;
    private static readonly CommentSyntax Xaml = CommentSyntax.Resolve("View.axaml", null)!;

    [Fact]
    public void CommentInsertsThePrefixAtTheSmallestIndentation()
    {
        string text = "class A\n{\n    int a;\n        int b;\n}\n";
        TextSelection selection = Select(text, "    int a;\n        int b;");

        TextEditResult result = TextEditOperations.Comment(text, selection, CSharp)!;

        Assert.Equal("class A\n{\n    // int a;\n    //     int b;\n}\n", Apply(text, result));
    }

    [Fact]
    public void ToggleCommentUncommentsWhenEveryLineIsCommented()
    {
        string text = "a\r\n  // b\r\n  // c\r\nd";
        TextSelection selection = Select(text, "  // b\r\n  // c");

        TextEditResult result = TextEditOperations.ToggleComment(text, selection, CSharp)!;

        Assert.Equal("a\r\n  b\r\n  c\r\nd", Apply(text, result));
    }

    [Fact]
    public void ToggleCommentCommentsWhenOneLineIsNotCommented()
    {
        string text = "// a\nb";

        TextEditResult result = TextEditOperations.ToggleComment(text, new TextSelection(0, text.Length, text.Length), CSharp)!;

        Assert.Equal("// // a\n// b", Apply(text, result));
    }

    [Fact]
    public void MarkupCommentsEncloseTheLinesAndUncommentRemovesTheMarkers()
    {
        string text = "<Grid>\n  <Button />\n</Grid>";
        TextSelection selection = Select(text, "<Button />");

        TextEditResult commented = TextEditOperations.ToggleComment(text, selection, Xaml)!;
        string commentedText = Apply(text, commented);
        TextEditResult uncommented = TextEditOperations.ToggleComment(commentedText, commented.Selection, Xaml)!;

        Assert.Equal("<Grid>\n  <!-- <Button /> -->\n</Grid>", commentedText);
        Assert.Equal(text, Apply(commentedText, uncommented));
    }

    [Fact]
    public void SelectionEndingAtALineStartLeavesThatLineUntouched()
    {
        string text = "a\nb\nc";

        TextEditResult result = TextEditOperations.Comment(text, new TextSelection(0, 2, 2), CSharp)!;

        Assert.Equal("// a\nb\nc", Apply(text, result));
    }

    [Fact]
    public void OffsetInsideALineBreakBelongsToTheLineTheBreakEnds()
    {
        string text = "a\r\nb";

        TextEditResult result = TextEditOperations.Comment(text, new TextSelection(2, 0, 2), CSharp)!;

        Assert.Equal("// a\r\nb", Apply(text, result));
    }

    [Fact]
    public void CaretOnlyCommentKeepsTheCaretOnItsCharacter()
    {
        string text = "x = 1;";

        TextEditResult result = TextEditOperations.Comment(text, new TextSelection(4, 0, 4), CSharp)!;

        Assert.Equal("// x = 1;", Apply(text, result));
        Assert.Equal(new TextSelection(7, 0, 7), result.Selection);
    }

    [Fact]
    public void ToggleOverSeparateMarkupCommentsCommentsTheLineBetweenThem()
    {
        string text = "<!-- <A/> -->\n<B/>\n<!-- <C/> -->";

        TextEditResult commented = TextEditOperations.ToggleComment(text, new TextSelection(0, text.Length, text.Length), Xaml)!;
        string commentedText = Apply(text, commented);
        TextEditResult uncommented = TextEditOperations.ToggleComment(commentedText, commented.Selection, Xaml)!;

        Assert.Equal("<!-- <A/> -->\n<!-- <B/> -->\n<!-- <C/> -->", commentedText);
        Assert.Equal("<A/>\n<B/>\n<C/>", Apply(commentedText, uncommented));
    }

    [Fact]
    public void CommentAroundAnInnerMarkupCommentKeepsItAndCommentsTheRest()
    {
        string text = "<Grid>\n  <!-- <A/> -->\n  <B/>\n</Grid>";

        TextEditResult result = TextEditOperations.Comment(text, new TextSelection(0, text.Length, text.Length), Xaml)!;

        Assert.Equal("<!-- <Grid> -->\n  <!-- <A/> -->\n  <!-- <B/>\n</Grid> -->", Apply(text, result));
        Assert.Null(TextEditOperations.Comment(Apply(text, result), new TextSelection(0, 0, 0), Xaml));
    }

    [Fact]
    public void SelectionInsideAMarkupCommentIsLeftUnchanged()
    {
        string text = "<!-- <A/>\n<B/> -->";
        TextSelection secondLine = Select(text, "<B/> -->");

        Assert.Null(TextEditOperations.ToggleComment(text, secondLine, Xaml));
        Assert.Null(TextEditOperations.Uncomment(text, secondLine, Xaml));
    }

    [Fact]
    public void RemPrefixIgnoresCaseAndNeedsAWordBoundary()
    {
        CommentSyntax batch = CommentSyntax.Resolve("build.cmd", null)!;
        string lower = "rem echo a\nREM\techo b";
        string word = "REMOVE=1";

        string uncommented = Apply(lower, TextEditOperations.ToggleComment(lower, new TextSelection(0, lower.Length, lower.Length), batch)!);
        string commented = Apply(word, TextEditOperations.ToggleComment(word, new TextSelection(0, 0, 0), batch)!);

        Assert.Equal("echo a\n\techo b", uncommented);
        Assert.Equal("REM REMOVE=1", commented);
        Assert.Equal(new TextSelection(8, 1, 9), TextEditOperations.FindMatchingBracket("(PREMIUM)", 0, batch));
        Assert.Null(TextEditOperations.FindMatchingBracket("REM (\n)", 4, batch));
    }

    [Fact]
    public void IndentSkipsBlankLinesAndUnindentRemovesOneLevel()
    {
        string text = "a\n\n  \n\tb\n      c";
        TextSelection all = new(0, text.Length, text.Length);

        string indented = Apply(text, TextEditOperations.Indent(text, all, "    ")!);
        string unindented = Apply(text, TextEditOperations.Unindent(text, all, 4)!);

        Assert.Equal("    a\n\n  \n    \tb\n          c", indented);
        Assert.Equal("a\n\n\nb\n  c", unindented);
    }

    [Fact]
    public void SortLinesKeepsTheLineBreaksOfTheDocument()
    {
        string text = "pear\r\nApple\r\nbanana\r\n";
        TextSelection selection = new(0, text.Length, text.Length);

        string ascending = Apply(text, TextEditOperations.SortLines(text, selection, ascending: true)!);
        string descending = Apply(text, TextEditOperations.SortLines(text, selection, ascending: false)!);

        Assert.Equal("Apple\r\nbanana\r\npear\r\n", ascending);
        Assert.Equal("pear\r\nbanana\r\nApple\r\n", descending);
    }

    [Fact]
    public void CaseChangesNeedASelection()
    {
        string text = "Hello World";

        Assert.Null(TextEditOperations.ToUpperCase(text, new TextSelection(3, 0, 3)));
        Assert.Equal("HELLO World", Apply(text, TextEditOperations.ToUpperCase(text, new TextSelection(0, 5, 5))!));
        Assert.Equal("hello World", Apply(text, TextEditOperations.ToLowerCase(text, new TextSelection(0, 5, 5))!));
    }

    [Fact]
    public void WrapInBracketsKeepsTheEnclosedTextSelected()
    {
        string text = "a + b";

        TextEditResult result = TextEditOperations.WrapInBrackets(text, new TextSelection(0, 5, 5));

        Assert.Equal("(a + b)", Apply(text, result));
        Assert.Equal(new TextSelection(1, 5, 6), result.Selection);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 10)]
    [InlineData(11, 0)]
    [InlineData(4, 6)]
    [InlineData(7, 4)]
    [InlineData(9, 2)]
    public void MatchingBracketSkipsNestedPairs(int caret, int expected)
    {
        string text = "(a[b{c}d]e)";

        TextSelection? match = TextEditOperations.FindMatchingBracket(text, caret, null);

        Assert.Equal(new TextSelection(expected, 1, expected + 1), match);
    }

    [Fact]
    public void MatchingBracketIgnoresBracketsInStringsAndComments()
    {
        string text = "f(\")\", ')', /* ) */ x) // )";

        TextSelection? fromCode = TextEditOperations.FindMatchingBracket(text, 1, CSharp);
        TextSelection? fromString = TextEditOperations.FindMatchingBracket(text, text.IndexOf("\")", StringComparison.Ordinal) + 1, CSharp);

        Assert.Equal(text.IndexOf("x)", StringComparison.Ordinal) + 1, fromCode?.Start);
        Assert.Null(fromString);
    }

    [Fact]
    public void MatchingBracketIsMissingWithoutABracketAtTheCaret()
    {
        Assert.Null(TextEditOperations.FindMatchingBracket("abc", 1, null));
        Assert.Null(TextEditOperations.FindMatchingBracket("(abc", 0, null));
    }

    [Fact]
    public void LineStartAndLineColumnHandleAllLineBreaks()
    {
        string text = "a\r\nb\nc\rd";

        Assert.Equal(0, TextEditOperations.GetLineStartOffset(text, 1));
        Assert.Equal(3, TextEditOperations.GetLineStartOffset(text, 2));
        Assert.Equal(5, TextEditOperations.GetLineStartOffset(text, 3));
        Assert.Equal(7, TextEditOperations.GetLineStartOffset(text, 4));
        Assert.Equal(text.Length, TextEditOperations.GetLineStartOffset(text, 9));
        Assert.Equal((4, 1), TextEditOperations.GetLineAndColumn(text, 7));
        Assert.Equal((1, 2), TextEditOperations.GetLineAndColumn(text, 1));
    }

    [Theory]
    [InlineData("A.cs", null, "//")]
    [InlineData("script.PS1", null, "#")]
    [InlineData("query.sql", null, "--")]
    [InlineData("build.cmd", null, "REM")]
    [InlineData("Dockerfile", null, "#")]
    [InlineData("untitled", "python", "#")]
    public void CommentSyntaxResolvesLinePrefixes(string path, string? languageId, string prefix)
    {
        CommentSyntax? syntax = CommentSyntax.Resolve(path, languageId);

        Assert.NotNull(syntax);
        Assert.Equal(prefix, syntax.LinePrefix);
    }

    [Fact]
    public void CommentSyntaxIsUnknownForPlainText()
    {
        Assert.Null(CommentSyntax.Resolve("notes.txt", "plaintext"));
        Assert.True(CommentSyntax.Resolve("App.xaml", null)!.IsBlock);
        Assert.Equal("/*", CommentSyntax.Resolve("site.css", null)!.BlockStart);
    }

    [Fact]
    public async Task EditCommandAppliesTheEditsAndThenSelectsTheResult()
    {
        EditableDocument document = new(Path.GetFullPath("View.axaml"), "<Grid>\n  <Button />\n</Grid>")
        {
            SelectionStart = 9,
            SelectionLength = 10,
            CaretOffset = 19
        };
        ExtensionContext context = CreateContext(new SingleDocumentEditor(document));
        await new TextEditingExtensionEntry().ActivateAsync(context, CancellationToken.None);

        await context.Commands.ExecuteAsync(TextEditingExtensionEntry.TOGGLE_COMMENT_COMMAND_ID, null, CancellationToken.None);

        Assert.Equal("<Grid>\n  <!-- <Button /> -->\n</Grid>", document.Text);
        Assert.Equal(["edit", "select 7 21 28"], document.Calls);
    }

    [Fact]
    public async Task SelectionOnlyCommandSelectsWithoutEditing()
    {
        EditableDocument document = new(Path.GetFullPath("Code.cs"), "f(a)") { CaretOffset = 1 };
        ExtensionContext context = CreateContext(new SingleDocumentEditor(document));
        await new TextEditingExtensionEntry().ActivateAsync(context, CancellationToken.None);

        await context.Commands.ExecuteAsync(TextEditingExtensionEntry.MATCHING_BRACKET_COMMAND_ID, null, CancellationToken.None);

        Assert.Equal("f(a)", document.Text);
        Assert.Equal(["select 3 1 4"], document.Calls);
    }

    private static TextSelection Select(string text, string part)
    {
        int start = text.IndexOf(part, StringComparison.Ordinal);
        return new TextSelection(start, part.Length, start + part.Length);
    }

    private static string Apply(string text, TextEditResult result) => Apply(text, result.Edits);

    private static string Apply(string text, IEnumerable<TextEdit> edits)
    {
        foreach (TextEdit edit in edits.OrderByDescending(edit => edit.Offset))
        {
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
        }

        return text;
    }

    private static ExtensionContext CreateContext(IEditorServices editor)
    {
        return new ExtensionContext(
            "test.text-editing",
            "/tmp",
            new CommandRegistry(),
            new CommandMetadataRegistry(),
            new ExtensionContributionRegistry(),
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            editor,
            null!,
            null!,
            null!,
            null!,
            null!,
            null!,
            new InMemoryExtensionStorage(),
            new NullLogger(),
            new List<IDisposable>());
    }

    private sealed class SingleDocumentEditor : IEditorServices
    {
        public SingleDocumentEditor(IEditorDocument document)
        {
            ActiveDocument = document;
        }

        public IEditorDocument? ActiveDocument { get; }

        public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged
        {
            add { }
            remove { }
        }

        public IReadOnlyList<IEditorDocument> GetOpenDocuments() => ActiveDocument is null ? [] : [ActiveDocument];

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

        public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) =>
            Task.FromResult<IEditorDocument?>(null);

        public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
    }

    // Records the edits and selections in the order the command makes them.
    private sealed class EditableDocument : IEditorDocument
    {
        public EditableDocument(string filePath, string text)
        {
            FilePath = filePath;
            Text = text;
        }

        public string FilePath { get; }

        public string? LanguageId => null;

        public string Text { get; private set; }

        public List<string> Calls { get; } = new();

        public int CaretOffset { get; set; }

        public int SelectionStart { get; set; }

        public int SelectionLength { get; set; }

        public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

        public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;

        public void SetSelection(int start, int length, int caretOffset)
        {
            Calls.Add($"select {start} {length} {caretOffset}");
            SelectionStart = start;
            SelectionLength = length;
            CaretOffset = caretOffset;
            SelectionChanged?.Invoke(this, new EditorSelectionChangedEventArgs { FilePath = FilePath, SelectionStart = start, SelectionLength = length });
        }

        public Task<string> GetTextAsync(CancellationToken ct) => Task.FromResult(Text);

        public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct)
        {
            Calls.Add("edit");
            Text = Apply(Text, edits);
            Changed?.Invoke(this, new EditorDocumentChangedEventArgs { FilePath = FilePath });
            return Task.CompletedTask;
        }
    }

    private sealed class NullLogger : IExtensionLogger
    {
        public void Info(string message)
        {
        }

        public void Warn(string message)
        {
        }

        public void Error(string message, Exception? exception = null)
        {
        }
    }
}
