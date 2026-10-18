using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitBlameParserTests
{
    private const string FIRST = "1111111111111111111111111111111111111111";
    private const string SECOND = "2222222222222222222222222222222222222222";
    private const string ZERO = "0000000000000000000000000000000000000000";

    [Fact]
    public void Parse_reads_the_commit_fields_once_and_groups_consecutive_lines_into_hunks()
    {
        string output = string.Join("\n",
            FIRST + " 1 1 2",
            "author Anna Müller",
            "author-mail <anna@example.invalid>",
            "author-time 1700000000",
            "author-tz +0130",
            "committer Anna Müller",
            "summary First commit",
            "boundary",
            "filename a.txt",
            "\tGrüße",
            FIRST + " 2 2",
            "\t\tindented\r",
            SECOND + " 3 3 1",
            "author Ben",
            "author-time 1700003600",
            "author-tz -0800",
            "summary Second commit",
            "previous " + FIRST + " a.txt",
            "filename a.txt",
            "\tsecond",
            FIRST + " 4 4 1",
            "filename a.txt",
            "\tlast",
            ZERO + " 5 5 1",
            "author Not Committed Yet",
            "author-time 1700007200",
            "author-tz +0000",
            "summary Version of a.txt from a.txt",
            "filename a.txt",
            "\t",
            string.Empty);

        GitBlame blame = GitBlameParser.Parse(output);

        Assert.Equal(["Grüße", "\tindented", "second", "last", ""], blame.Lines);
        Assert.Equal(
            [(1, 2, FIRST), (3, 1, SECOND), (4, 1, FIRST), (5, 1, ZERO)],
            blame.Hunks.Select(hunk => (hunk.StartLine, hunk.LineCount, hunk.Commit.Sha)));
        GitBlameCommit first = blame.Hunks[0].Commit;
        Assert.Same(first, blame.Hunks[2].Commit);
        Assert.Equal("Anna Müller", first.AuthorName);
        Assert.Equal("First commit", first.Summary);
        Assert.Equal(new DateTimeOffset(2023, 11, 14, 23, 43, 20, TimeSpan.FromMinutes(90)), first.AuthorDate);
        Assert.Equal(TimeSpan.FromHours(-8), blame.Hunks[1].Commit.AuthorDate.Offset);
        Assert.False(first.IsUncommitted);
        Assert.True(blame.Hunks[3].Commit.IsUncommitted);
        Assert.False(blame.IsTruncated);
    }

    [Fact]
    public void Parse_of_empty_output_has_no_lines_and_passes_the_truncation_on()
    {
        GitBlame blame = GitBlameParser.Parse(string.Empty, isTruncated: true);

        Assert.Empty(blame.Lines);
        Assert.Empty(blame.Hunks);
        Assert.True(blame.IsTruncated);
    }
}
