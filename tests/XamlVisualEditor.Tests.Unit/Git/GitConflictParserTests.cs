using XamlVisualEditor.GitExtension.Services;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitConflictParserTests
{
    [Fact]
    public void A_block_is_found_with_its_lines_sides_and_labels()
    {
        string text = "first\n<<<<<<< HEAD\nours 1\nours 2\n=======\ntheirs\n>>>>>>> feature/x\nlast\n";

        GitConflictBlock block = Assert.Single(GitConflictParser.Parse(text));

        Assert.Equal(2, block.StartLine);
        Assert.Equal(6, block.LineCount);
        Assert.Equal("ours 1\nours 2\n", block.Ours);
        Assert.Equal("theirs\n", block.Theirs);
        Assert.Equal("HEAD", block.OursLabel);
        Assert.Equal("feature/x", block.TheirsLabel);
        Assert.Equal("first\n", text[..block.Start]);
        Assert.Equal("last\n", text[block.End..]);
    }

    // The resolution is passed as its number: a theory parameter cannot have the internal enum type.
    [Theory]
    [InlineData(0, "a\r\nmine\r\nz\r\n")]
    [InlineData(1, "a\r\nyours\r\nz\r\n")]
    [InlineData(2, "a\r\nmine\r\nyours\r\nz\r\n")]
    public void A_resolution_replaces_the_block_and_keeps_crlf(int resolution, string expected)
    {
        string text = "a\r\n<<<<<<< HEAD\r\nmine\r\n=======\r\nyours\r\n>>>>>>> topic\r\nz\r\n";
        GitConflictBlock block = Assert.Single(GitConflictParser.Parse(text));

        string resolved = text[..block.Start] + GitConflictParser.Resolve(block, (GitConflictResolution)resolution) + text[block.End..];

        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void The_base_section_of_diff3_belongs_to_neither_side()
    {
        string text = "<<<<<<< HEAD\nmine\n||||||| merged common ancestors\nbase\n=======\nyours\n>>>>>>> topic\n";

        GitConflictBlock block = Assert.Single(GitConflictParser.Parse(text));

        Assert.Equal("mine\n", block.Ours);
        Assert.Equal("yours\n", block.Theirs);
        Assert.Equal(7, block.LineCount);
    }

    [Fact]
    public void A_block_at_the_end_without_line_break_ends_at_the_end_of_the_text()
    {
        string text = "<<<<<<< HEAD\nmine\n=======\nyours\n>>>>>>> topic";

        GitConflictBlock block = Assert.Single(GitConflictParser.Parse(text));

        Assert.Equal(text.Length, block.End);
        Assert.Equal("yours\n", GitConflictParser.Resolve(block, GitConflictResolution.Theirs));
    }

    [Theory]
    [InlineData("<<<<<<< HEAD\nmine\n=======\nyours\n")]
    [InlineData("<<<<<<<< HEAD\nmine\n=======\nyours\n>>>>>>> topic\n")]
    [InlineData("x <<<<<<< HEAD\nmine\n=======\nyours\n>>>>>>> topic\n")]
    [InlineData("<<<<<<< HEAD\nmine\n======= text\nyours\n>>>>>>> topic\n")]
    public void Unterminated_blocks_and_marker_like_lines_are_no_blocks(string text)
    {
        Assert.Empty(GitConflictParser.Parse(text));
        Assert.False(GitConflictParser.HasConflict(text));
    }

    [Fact]
    public void Several_blocks_are_found_in_order_and_by_line()
    {
        string text = "<<<<<<< HEAD\na\n=======\nb\n>>>>>>> t\nmiddle\n<<<<<<< HEAD\nc\n=======\nd\n>>>>>>> t\n";

        IReadOnlyList<GitConflictBlock> blocks = GitConflictParser.Parse(text);

        Assert.Equal([1, 7], blocks.Select(block => block.StartLine));
        Assert.Same(blocks[0], GitConflictParser.FindAt(blocks, 5));
        Assert.Null(GitConflictParser.FindAt(blocks, 6));
        Assert.Same(blocks[1], GitConflictParser.FindAt(blocks, 7));
    }
}
