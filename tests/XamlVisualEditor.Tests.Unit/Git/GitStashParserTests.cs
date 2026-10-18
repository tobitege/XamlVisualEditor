using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitStashParserTests
{
    [Fact]
    public void Parse_reads_four_fields_per_stash()
    {
        string output = "stash@{0}\0d700c97b926d69819f3923610db96a58a425282d\01792239885\0WIP on main: 14427ec i\0"
            + "stash@{12}\0751916346ec8bb71da85f7b442bef913db190321\01792239800\0On main: Grüße eins\0";

        IReadOnlyList<GitStashEntry> stashes = GitStashParser.Parse(output);

        Assert.Equal(
            [
                new GitStashEntry(0, "d700c97b926d69819f3923610db96a58a425282d", "WIP on main: 14427ec i", DateTimeOffset.FromUnixTimeSeconds(1792239885)),
                new GitStashEntry(12, "751916346ec8bb71da85f7b442bef913db190321", "On main: Grüße eins", DateTimeOffset.FromUnixTimeSeconds(1792239800))
            ],
            stashes);
        Assert.Equal("stash@{12}", stashes[1].Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("refs/stash\0abc\01\0message\0")]
    [InlineData("stash@{x}\0abc\01\0message\0")]
    [InlineData("stash@{0}\0abc\0no date\0message\0")]
    [InlineData("stash@{0}\0abc\01")]
    public void Parse_skips_incomplete_and_malformed_entries(string output)
    {
        Assert.Empty(GitStashParser.Parse(output));
    }
}
