using XamlVisualEditor.Core;
using XamlVisualEditor.TextEditingExtension.Services;
using Xunit;

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
    public void IndentSkipsEmptyLinesAndUnindentRemovesOneLevel()
    {
        string text = "a\n\n\tb\n      c";
        TextSelection all = new(0, text.Length, text.Length);

        string indented = Apply(text, TextEditOperations.Indent(text, all, "    ")!);
        string unindented = Apply(text, TextEditOperations.Unindent(text, all, 4)!);

        Assert.Equal("    a\n\n    \tb\n          c", indented);
        Assert.Equal("a\n\nb\n  c", unindented);
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

    private static TextSelection Select(string text, string part)
    {
        int start = text.IndexOf(part, StringComparison.Ordinal);
        return new TextSelection(start, part.Length, start + part.Length);
    }

    private static string Apply(string text, TextEditResult result)
    {
        foreach (TextEdit edit in result.Edits.OrderByDescending(edit => edit.Offset))
        {
            text = text.Remove(edit.Offset, edit.Length).Insert(edit.Offset, edit.NewText);
        }

        return text;
    }
}
