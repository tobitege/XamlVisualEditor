using System.Collections.Generic;
using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.SourceControl.UI.Models;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class DiffLineBuilderTests
{
    private const string TWO_FILE_DIFF =
        "diff --git a/a.txt b/a.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/a.txt\n" +
        "+++ b/a.txt\n" +
        "@@ -1,3 +1,3 @@\n" +
        " one\n" +
        "-two\n" +
        "+zwei\n" +
        " three\n" +
        "diff --git a/b.txt b/b.txt\n" +
        "--- a/b.txt\n" +
        "+++ b/b.txt\n" +
        "@@ -5 +5,2 @@\n" +
        "-last\n" +
        "\\ No newline at end of file\n" +
        "+last\n" +
        "+added\n";

    [Fact]
    public void Parse_classifies_lines_and_numbers_them_from_the_hunk_headers()
    {
        IReadOnlyList<DiffLine> lines = DiffLineBuilder.Parse(TWO_FILE_DIFF);

        Assert.Equal(
            new[]
            {
                DiffLineKind.FileHeader, DiffLineKind.FileHeader, DiffLineKind.FileHeader, DiffLineKind.FileHeader,
                DiffLineKind.HunkHeader, DiffLineKind.Context, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.Context,
                DiffLineKind.FileHeader, DiffLineKind.FileHeader, DiffLineKind.FileHeader,
                DiffLineKind.HunkHeader, DiffLineKind.Removed, DiffLineKind.NoNewline, DiffLineKind.Added, DiffLineKind.Added
            },
            lines.Select(line => line.Kind));
        Assert.Equal(new DiffLine(DiffLineKind.Context, " one", 1, 1), lines[5]);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "-two", 2, null), lines[6]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, "+zwei", null, 2), lines[7]);
        Assert.Equal(new DiffLine(DiffLineKind.Context, " three", 3, 3), lines[8]);
        Assert.Equal(new DiffLine(DiffLineKind.Removed, "-last", 5, null), lines[13]);
        Assert.Equal(new DiffLine(DiffLineKind.Added, "+added", null, 6), lines[16]);
    }

    [Fact]
    public void Parse_reads_header_lines_after_a_complete_hunk_as_headers_even_when_they_start_with_a_marker()
    {
        IReadOnlyList<DiffLine> lines = DiffLineBuilder.Parse("@@ -1 +1 @@\n-a\n+b\n--- a/next.txt\n+++ b/next.txt\n");

        Assert.Equal(
            new[] { DiffLineKind.HunkHeader, DiffLineKind.Removed, DiffLineKind.Added, DiffLineKind.FileHeader, DiffLineKind.FileHeader },
            lines.Select(line => line.Kind));
    }

    [Fact]
    public void Parse_keeps_lines_of_a_hunk_whose_header_has_no_numbers_and_returns_nothing_for_no_text()
    {
        IReadOnlyList<DiffLine> lines = DiffLineBuilder.Parse("@@ changed @@\r\n-old\r\n+new\r\n");

        Assert.Equal(new[] { DiffLineKind.HunkHeader, DiffLineKind.Removed, DiffLineKind.Added }, lines.Select(line => line.Kind));
        Assert.Equal("+new", lines[2].Text);
        Assert.Empty(DiffLineBuilder.Parse(null));
        Assert.Empty(DiffLineBuilder.Parse(string.Empty));
    }

    [Fact]
    public void FromGitDiff_adds_the_markers_and_keeps_the_line_numbers_of_the_parsed_diff()
    {
        GitDiff diff = GitDiffParser.ParseUnifiedDiff(TWO_FILE_DIFF);

        IReadOnlyList<DiffLine> fromParsed = DiffLineBuilder.FromGitDiff(diff);

        Assert.Equal(DiffLineBuilder.Parse(TWO_FILE_DIFF), fromParsed);
    }

    [Fact]
    public void FromLineDiff_makes_one_hunk_per_block_with_the_context_between_blocks_in_the_earlier_hunk()
    {
        string[] oldLines = ["1", "2", "3", "4", "5", "6", "7", "8", "9", "10"];
        string[] newLines = ["1", "two", "3", "4", "5", "6", "7", "9", "10", "11"];
        IReadOnlyList<LineDiffBlock> blocks = LineDiff.Compute(oldLines, newLines, 2000, CancellationToken.None);

        IReadOnlyList<DiffLine> lines = DiffLineBuilder.FromLineDiff(oldLines, newLines, blocks, 3);

        Assert.Equal(
            [
                "@@ -1,5 +1,5 @@", " 1", "-2", "+two", " 3", " 4", " 5",
                "@@ -6,5 +6,4 @@", " 6", " 7", "-8", " 9", " 10",
                "@@ -10,0 +10,1 @@", "+11"
            ],
            lines.Select(line => line.Text));
        // The headers count the lines of their hunks exactly as git's do.
        Assert.Equal(DiffLineBuilder.Parse(string.Join("\n", lines.Select(line => line.Text))), lines);
    }

    [Fact]
    public void FromLineDiff_of_the_whole_file_shows_every_line_once_and_keeps_the_hunk_count()
    {
        string[] oldLines = ["a", "b", "c", "d", "e", "f", "g", "h", "i", "j"];
        string[] newLines = ["a", "B", "c", "d", "e", "f", "g", "h", "I", "j"];
        IReadOnlyList<LineDiffBlock> blocks = LineDiff.Compute(oldLines, newLines, 2000, CancellationToken.None);

        IReadOnlyList<DiffLine> lines = DiffLineBuilder.FromLineDiff(oldLines, newLines, blocks, int.MaxValue);

        Assert.Equal(2, lines.Count(line => line.Kind == DiffLineKind.HunkHeader));
        Assert.Equal(newLines, lines.Where(line => line.NewLineNumber is not null).Select(line => line.Text[1..]));
        Assert.Equal(oldLines, lines.Where(line => line.OldLineNumber is not null).Select(line => line.Text[1..]));
        Assert.Empty(DiffLineBuilder.FromLineDiff(oldLines, oldLines, [], int.MaxValue));
    }

    [Fact]
    public void FromGitDiff_without_files_reads_the_raw_text()
    {
        GitDiff diff = new() { Files = [], RawText = "@@ -1 +1 @@\n-a\n+b\n" };

        Assert.Equal(3, DiffLineBuilder.FromGitDiff(diff).Count);
    }
}
