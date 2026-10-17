using System.Text;
using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitPatchBuilderTests
{
    private const string Header = "diff --git a/f.txt b/f.txt\nindex 1111111..2222222 100644\n--- a/f.txt\n+++ b/f.txt\n";

    // Two hunks with CRLF lines and a Latin-1 byte, which must reach the patch unchanged.
    private const string TwoHunks = Header
        + "@@ -1,3 +1,3 @@\n a\r\n-b\r\n+Bä\r\n c\r\n"
        + "@@ -10,3 +10,4 @@ context\n x\n y\n+z\n w\n";

    private const string Mixed = Header + "@@ -1,4 +1,4 @@\n a\n-b\n-c\n+B\n+C\n d\n";

    // Old "a\nb" without a final line break, new "a\nb\nc\n".
    private const string MissingNewline = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n"
        + "@@ -1,2 +1,3 @@\n a\n-b\n\\ No newline at end of file\n+b\n+c\n";

    [Fact]
    public void Selecting_everything_returns_the_bytes_git_wrote()
    {
        byte[] diff = Bytes(TwoHunks);

        Assert.Equal(diff, GitPatchBuilder.Build(diff, 0, LineCount(TwoHunks), reverse: false));
        Assert.Equal(diff, GitPatchBuilder.Build(diff, 0, LineCount(TwoHunks), reverse: true));
    }

    [Fact]
    public void A_hunk_is_kept_byte_for_byte_and_hunks_without_selected_changes_are_left_out()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(TwoHunks), Index(TwoHunks, "-b\r"), 2, reverse: false)!;

        Assert.Equal(Bytes(Header + "@@ -1,3 +1,3 @@\n a\r\n-b\r\n+Bä\r\n c\r\n"), patch);
    }

    [Fact]
    public void Forward_keeps_unselected_removed_lines_as_context_and_leaves_out_unselected_added_lines()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(Mixed), Index(Mixed, "-c"), 2, reverse: false)!;

        Assert.Equal(Header + "@@ -1,4 +1,4 @@\n a\n b\n-c\n+B\n d\n", Text(patch));
    }

    [Fact]
    public void Reverse_keeps_unselected_added_lines_as_context_and_leaves_out_unselected_removed_lines()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(Mixed), Index(Mixed, "-c"), 2, reverse: true)!;

        Assert.Equal(Header + "@@ -1,4 +1,4 @@\n a\n-c\n+B\n C\n d\n", Text(patch));
    }

    [Fact]
    public void The_missing_newline_marker_is_left_out_with_its_line()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(MissingNewline), Index(MissingNewline, "+c"), 1, reverse: true)!;

        Assert.Equal("diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,3 @@\n a\n b\n+c\n", Text(patch));
    }

    [Fact]
    public void The_missing_newline_marker_stays_with_a_selected_removed_line()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(MissingNewline), Index(MissingNewline, "-b"), 1, reverse: false)!;

        Assert.Equal(
            "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,3 @@\n a\n-b\n\\ No newline at end of file\n",
            Text(patch));
    }

    [Theory]
    [InlineData("+b")]
    [InlineData("+c")]
    public void A_context_line_without_line_break_in_front_of_other_lines_is_rejected(string selected)
    {
        GitPatchRejectedException rejected = Assert.Throws<GitPatchRejectedException>(
            () => GitPatchBuilder.Build(Bytes(MissingNewline), Index(MissingNewline, selected), 1, reverse: false));

        Assert.Equal(GitPatchRejection.MissingNewline, rejected.Reason);
    }

    [Fact]
    public void A_removed_last_line_without_line_break_in_front_of_context_is_rejected_in_reverse()
    {
        // Old "a\nb" and new "a\nc", both without a final line break.
        string diff = "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n"
            + "@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+c\n\\ No newline at end of file\n";

        GitPatchRejectedException rejected = Assert.Throws<GitPatchRejectedException>(
            () => GitPatchBuilder.Build(Bytes(diff), Index(diff, "-b"), 1, reverse: true));

        Assert.Equal(GitPatchRejection.MissingNewline, rejected.Reason);
        Assert.Equal(diff, Text(GitPatchBuilder.Build(Bytes(diff), 0, LineCount(diff), reverse: true)!));
        Assert.Equal(diff, Text(GitPatchBuilder.Build(Bytes(diff), 0, LineCount(diff), reverse: false)!));
    }

    [Fact]
    public void A_mode_change_is_left_out_of_every_patch()
    {
        string diff = "diff --git a/f.txt b/f.txt\nold mode 100644\nnew mode 100755\nindex 1111111..2222222\n--- a/f.txt\n+++ b/f.txt\n"
            + "@@ -1,2 +1,2 @@\n a\n-b\n+B\n";

        Assert.Equal(
            "diff --git a/f.txt b/f.txt\nindex 1111111..2222222\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n a\n-b\n+B\n",
            Text(GitPatchBuilder.Build(Bytes(diff), 0, LineCount(diff), reverse: true)!));
    }

    [Fact]
    public void A_partial_selection_of_a_new_file_names_the_file_on_both_sides()
    {
        string diff = "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..5804e55\n--- /dev/null\n+++ b/n.txt\n"
            + "@@ -0,0 +1,3 @@\n+n1\n+n2\n+n3\n";

        byte[] partial = GitPatchBuilder.Build(Bytes(diff), Index(diff, "+n2"), 1, reverse: true)!;
        byte[] whole = GitPatchBuilder.Build(Bytes(diff), 0, LineCount(diff), reverse: true)!;

        Assert.Equal(
            "diff --git a/n.txt b/n.txt\nindex 0000000..5804e55\n--- b/n.txt\n+++ b/n.txt\n@@ -0,0 +1,3 @@\n n1\n+n2\n n3\n",
            Text(partial));
        Assert.Equal(diff, Text(whole));
    }

    [Fact]
    public void A_partial_selection_of_a_deleted_file_names_the_file_on_both_sides()
    {
        string diff = "diff --git a/d.txt b/d.txt\ndeleted file mode 100644\nindex 5804e55..0000000\n--- a/d.txt\n+++ /dev/null\n"
            + "@@ -1,3 +0,0 @@\n-d1\n-d2\n-d3\n";

        byte[] partial = GitPatchBuilder.Build(Bytes(diff), Index(diff, "-d2"), 1, reverse: false)!;

        Assert.Equal(
            "diff --git a/d.txt b/d.txt\nindex 5804e55..0000000\n--- a/d.txt\n+++ a/d.txt\n@@ -1,3 +0,0 @@\n d1\n-d2\n d3\n",
            Text(partial));
    }

    [Fact]
    public void A_selection_over_two_hunks_keeps_both()
    {
        byte[] patch = GitPatchBuilder.Build(Bytes(TwoHunks), Index(TwoHunks, "+Bä\r"), 6, reverse: false)!;

        Assert.Equal(
            Bytes(Header + "@@ -1,3 +1,3 @@\n a\r\n b\r\n+Bä\r\n c\r\n@@ -10,3 +10,4 @@ context\n x\n y\n+z\n w\n"),
            patch);
    }

    [Fact]
    public void An_empty_context_line_is_context()
    {
        string diff = Header + "@@ -1,3 +1,2 @@\n\n-b\n-c\n+B\n";

        byte[] patch = GitPatchBuilder.Build(Bytes(diff), Index(diff, "-c"), 2, reverse: false)!;

        Assert.Equal(Header + "@@ -1,3 +1,2 @@\n\n b\n-c\n+B\n", Text(patch));
    }

    [Fact]
    public void A_binary_diff_and_a_selection_of_header_lines_give_no_patch()
    {
        string binary = "diff --git a/x.png b/x.png\nindex 1111111..2222222 100644\nBinary files a/x.png and b/x.png differ\n";

        Assert.Null(GitPatchBuilder.Build(Bytes(binary), 0, LineCount(binary), reverse: false));
        Assert.Null(GitPatchBuilder.Build(Bytes(Mixed), 0, 5, reverse: false));
    }

    [Fact]
    public void A_diff_with_two_file_headers_is_rejected()
    {
        string typeChange = "diff --git a/l b/l\ndeleted file mode 120000\n--- a/l\n+++ /dev/null\n@@ -1 +0,0 @@\n-t\n"
            + "\\ No newline at end of file\ndiff --git a/l b/l\nnew file mode 100644\n--- /dev/null\n+++ b/l\n@@ -0,0 +1 @@\n+x\n";

        GitPatchRejectedException rejected = Assert.Throws<GitPatchRejectedException>(
            () => GitPatchBuilder.Build(Bytes(typeChange), 0, LineCount(typeChange), reverse: false));

        Assert.Equal(GitPatchRejection.MultipleFiles, rejected.Reason);
    }

    // Latin-1 maps every char below 256 to the byte of the same value, so the strings above stand for exact bytes.
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Text(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static int LineCount(string diff) => diff.TrimEnd('\n').Split('\n').Length;

    private static int Index(string diff, string line) => Array.IndexOf(diff.Split('\n'), line);
}
