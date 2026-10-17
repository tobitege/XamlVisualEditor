using System.Diagnostics;
using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LineDiffTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("a", new[] { "a" })]
    [InlineData("a\n", new[] { "a" })]
    [InlineData("a\r\nb\rc\nd", new[] { "a", "b", "c", "d" })]
    [InlineData("a\n\n", new[] { "a", "" })]
    [InlineData("\r\n", new[] { "" })]
    [InlineData("a\r\r\nb", new[] { "a", "", "b" })]
    public void SplitLines_splits_at_every_line_terminator_and_ignores_the_final_one(string text, string[] expected)
    {
        Assert.Equal(expected, LineDiff.SplitLines(text));
    }

    [Theory]
    [InlineData("a b c", "a b c", "")]
    [InlineData("a b c", "x a b c", "0+0:0+1")]
    [InlineData("a b c", "a x b c", "1+0:1+1")]
    [InlineData("a b c", "a b c x", "3+0:3+1")]
    [InlineData("a b c", "b c", "0+1:0+0")]
    [InlineData("a b c", "a c", "1+1:1+0")]
    [InlineData("a b c", "a b", "2+1:2+0")]
    [InlineData("a b c", "x b c", "0+1:0+1")]
    [InlineData("a b c", "a x c", "1+1:1+1")]
    [InlineData("a b c", "a b x", "2+1:2+1")]
    [InlineData("", "a b", "0+0:0+2")]
    [InlineData("a b", "", "0+2:0+0")]
    [InlineData("a b c d e", "a x c y e", "1+1:1+1 3+1:3+1")]
    [InlineData("a b c d e f", "a c d x y f", "1+1:1+0 4+1:3+2")]
    public void Compute_returns_the_change_blocks(string oldText, string newText, string expected)
    {
        IReadOnlyList<LineDiffBlock> blocks = LineDiff.Compute(Words(oldText), Words(newText), 2000, CancellationToken.None);

        Assert.Equal(expected, Format(blocks));
    }

    [Fact]
    public void Compute_finds_a_shortest_edit_for_random_texts()
    {
        Random random = new(1234);
        for (int run = 0; run < 500; run++)
        {
            string[] oldLines = RandomLines(random, random.Next(0, 12));
            string[] newLines = RandomLines(random, random.Next(0, 12));

            IReadOnlyList<LineDiffBlock> blocks = LineDiff.Compute(oldLines, newLines, 2000, CancellationToken.None);

            Assert.Equal(newLines, Apply(oldLines, newLines, blocks));
            int changed = blocks.Sum(block => block.OldCount + block.NewCount);
            Assert.Equal(oldLines.Length + newLines.Length - (2 * LongestCommonSubsequence(oldLines, newLines)), changed);
        }
    }

    [Fact]
    public void Compute_makes_one_block_of_the_middle_when_the_edit_distance_exceeds_the_limit()
    {
        string[] oldLines = ["head", "a", "b", "c", "tail"];
        string[] newLines = ["head", "x", "y", "z", "tail"];

        Assert.Equal("1+3:1+3", Format(LineDiff.Compute(oldLines, newLines, 5, CancellationToken.None)));
        Assert.Equal("1+3:1+3", Format(LineDiff.Compute(oldLines, newLines, 6, CancellationToken.None)));
    }

    [Fact]
    public void Compute_stops_when_cancelled()
    {
        string[] oldLines = Enumerable.Range(0, 1000).Select(index => $"old {index}").ToArray();
        string[] newLines = Enumerable.Range(0, 1000).Select(index => $"new {index}").ToArray();

        Assert.Throws<OperationCanceledException>(() => LineDiff.Compute(oldLines, newLines, 2000, new CancellationToken(true)));
    }

    /// <summary>
    /// A 10,000-line file with 200 scattered edits, and a full rewrite of 100,000 lines that falls back to one block,
    /// stay far below a frame budget on any test machine.
    /// </summary>
    [Fact]
    public void Compute_is_fast_for_large_files()
    {
        string[] oldLines = Enumerable.Range(0, 10_000).Select(index => $"    <Element Index=\"{index}\" />").ToArray();
        string[] newLines = (string[])oldLines.Clone();
        for (int index = 25; index < newLines.Length; index += 50)
        {
            newLines[index] = $"    <Changed Index=\"{index}\" />";
        }

        List<string> inserted = newLines.ToList();
        inserted.Insert(5_000, "inserted");
        Compute(oldLines, inserted, out _);
        IReadOnlyList<LineDiffBlock> blocks = Compute(oldLines, inserted, out TimeSpan scattered);
        Assert.Equal(201, blocks.Count);
        Assert.Equal(inserted, Apply(oldLines, inserted, blocks));

        string[] rewritten = Enumerable.Range(0, 100_000).Select(index => $"line {index}").ToArray();
        string[] replaced = Enumerable.Range(0, 100_000).Select(index => $"other {index}").ToArray();
        LineDiffBlock whole = Assert.Single(Compute(rewritten, replaced, out TimeSpan rewrite));
        Assert.Equal(new LineDiffBlock(0, 100_000, 0, 100_000), whole);

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"10,000 lines with 201 changes: {scattered.TotalMilliseconds:F1} ms; 100,000 rewritten lines: {rewrite.TotalMilliseconds:F1} ms");
        Assert.True(scattered < TimeSpan.FromMilliseconds(500), $"{scattered.TotalMilliseconds} ms");
        Assert.True(rewrite < TimeSpan.FromSeconds(5), $"{rewrite.TotalMilliseconds} ms");
    }

    private static IReadOnlyList<LineDiffBlock> Compute(string[] oldLines, IReadOnlyList<string> newLines, out TimeSpan elapsed)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        IReadOnlyList<LineDiffBlock> blocks = LineDiff.Compute(oldLines, newLines, 2000, CancellationToken.None);
        elapsed = stopwatch.Elapsed;
        return blocks;
    }

    private static string[] Words(string text) => text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    private static string Format(IReadOnlyList<LineDiffBlock> blocks) =>
        string.Join(" ", blocks.Select(block => $"{block.OldStart}+{block.OldCount}:{block.NewStart}+{block.NewCount}"));

    private static string[] RandomLines(Random random, int count) =>
        Enumerable.Range(0, count).Select(_ => ((char)('a' + random.Next(4))).ToString()).ToArray();

    // Rebuilds the new lines from the old ones: unchanged runs between the blocks come from the old lines.
    private static List<string> Apply(IReadOnlyList<string> oldLines, IReadOnlyList<string> newLines, IReadOnlyList<LineDiffBlock> blocks)
    {
        List<string> result = new();
        int oldIndex = 0;
        foreach (LineDiffBlock block in blocks)
        {
            Assert.Equal(block.OldStart - oldIndex, block.NewStart - result.Count);
            result.AddRange(oldLines.Skip(oldIndex).Take(block.OldStart - oldIndex));
            result.AddRange(newLines.Skip(block.NewStart).Take(block.NewCount));
            oldIndex = block.OldStart + block.OldCount;
        }

        result.AddRange(oldLines.Skip(oldIndex));
        return result;
    }

    private static int LongestCommonSubsequence(string[] first, string[] second)
    {
        int[,] lengths = new int[first.Length + 1, second.Length + 1];
        for (int i = 1; i <= first.Length; i++)
        {
            for (int j = 1; j <= second.Length; j++)
            {
                lengths[i, j] = first[i - 1] == second[j - 1]
                    ? lengths[i - 1, j - 1] + 1
                    : Math.Max(lengths[i - 1, j], lengths[i, j - 1]);
            }
        }

        return lengths[first.Length, second.Length];
    }
}
