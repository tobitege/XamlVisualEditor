using System;
using System.Collections.Generic;
using System.Linq;
using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitRefParserTests
{
    [Fact]
    public void Parses_local_branches_before_remote_branches_with_current_upstream_and_tracking()
    {
        string output =
            Record(" ", "refs/remotes/origin/main", "", "", "", "remote tip")
            + Record("*", "refs/heads/main", "refs/remotes/origin/main", "ahead 2, behind 1", "", "Größe geändert")
            + Record(" ", "refs/heads/feature/x", "", "", "", "feature work");

        IReadOnlyList<GitBranch> branches = GitRefParser.ParseBranches(output, ["origin"]);

        Assert.Equal(new[] { "main", "feature/x", "origin/main" }, branches.Select(branch => branch.Name));
        GitBranch main = branches[0];
        Assert.True(main.IsCurrent);
        Assert.False(main.IsRemote);
        Assert.Null(main.RemoteName);
        Assert.Equal("refs/heads/main", main.FullName);
        Assert.Equal("origin/main", main.UpstreamName);
        Assert.Equal(2, main.AheadBy);
        Assert.Equal(1, main.BehindBy);
        Assert.False(main.IsUpstreamGone);
        Assert.Equal("Größe geändert", main.Subject);

        GitBranch feature = branches[1];
        Assert.False(feature.IsCurrent);
        Assert.Null(feature.UpstreamName);
        Assert.Equal(0, feature.AheadBy);
        Assert.Equal("feature/x", feature.LocalName);

        GitBranch remote = branches[2];
        Assert.True(remote.IsRemote);
        Assert.Equal("origin", remote.RemoteName);
        Assert.Equal("main", remote.LocalName);
        Assert.Equal("refs/remotes/origin/main", remote.FullName);
    }

    [Fact]
    public void Skips_symbolic_references_unknown_references_and_malformed_records()
    {
        string output =
            Record(" ", "refs/remotes/origin/HEAD", "", "", "refs/remotes/origin/main", "tip")
            + Record(" ", "refs/tags/v1", "", "", "", "tag")
            + "*\0refs/heads/broken\u001e\n"
            + Record(" ", "refs/remotes/origin/feature/y", "", "", "", "");

        GitBranch branch = Assert.Single(GitRefParser.ParseBranches(output, ["origin"]));

        Assert.Equal("origin/feature/y", branch.Name);
        Assert.Equal("feature/y", branch.LocalName);
        Assert.Equal(string.Empty, branch.Subject);
    }

    [Fact]
    public void Keeps_subjects_with_tabs_line_feeds_and_record_like_text()
    {
        string output = Record("*", "refs/heads/main", "", "", "", "tab\there; braces {0} and 100%");

        Assert.Equal("tab\there; braces {0} and 100%", Assert.Single(GitRefParser.ParseBranches(output, [])).Subject);
    }

    [Fact]
    public void The_remote_of_a_remote_branch_is_the_longest_configured_remote_name_it_starts_with()
    {
        string output =
            Record(" ", "refs/remotes/team/fork/main", "", "", "", "")
            + Record(" ", "refs/remotes/team/feature/x", "", "", "", "")
            + Record(" ", "refs/remotes/removed/topic", "", "", "", "");

        IReadOnlyList<GitBranch> branches = GitRefParser.ParseBranches(output, ["team", "team/fork", "team/fo"]);

        Assert.Equal(new[] { "team/fork", "team", "removed" }, branches.Select(branch => branch.RemoteName));
        Assert.Equal(new[] { "main", "feature/x", "topic" }, branches.Select(branch => branch.LocalName));
    }

    [Theory]
    [InlineData("", 0, 0, false)]
    [InlineData("gone", 0, 0, true)]
    [InlineData("ahead 3", 3, 0, false)]
    [InlineData("behind 12", 0, 12, false)]
    [InlineData("ahead 1, behind 2", 1, 2, false)]
    [InlineData("ahead x", 0, 0, false)]
    public void ParseTrack_reads_ahead_behind_and_gone(string track, int ahead, int behind, bool gone)
    {
        Assert.Equal((ahead, behind, gone), GitRefParser.ParseTrack(track));
    }

    [Fact]
    public void Upstream_gone_is_reported_without_counts()
    {
        string output = Record(" ", "refs/heads/old", "refs/remotes/origin/old", "gone", "", "s");

        GitBranch branch = Assert.Single(GitRefParser.ParseBranches(output, ["origin"]));

        Assert.True(branch.IsUpstreamGone);
        Assert.Equal("origin/old", branch.UpstreamName);
    }

    [Fact]
    public void ParseRemotes_returns_one_entry_per_remote_with_fetch_and_push_url()
    {
        const string output =
            "origin\thttps://user:secret@example.com/r.git (fetch)\r\n"
            + "origin\thttps://example.com/push.git (push)\r\n"
            + "backup\tD:\\remotes\\a b.git (fetch)\n"
            + "backup\tD:\\remotes\\a b.git (push)\n"
            + "garbage line\n";

        IReadOnlyList<GitRemote> remotes = GitRefParser.ParseRemotes(output);

        Assert.Equal(
            new[]
            {
                new GitRemote("origin", "https://user:secret@example.com/r.git", "https://example.com/push.git"),
                new GitRemote("backup", "D:\\remotes\\a b.git", "D:\\remotes\\a b.git")
            },
            remotes);
    }

    [Fact]
    public void SelectDefault_prefers_origin_then_the_first_remote()
    {
        GitRemote backup = new("backup", "b", "b");
        GitRemote origin = new("origin", "o", "o");

        Assert.Same(origin, GitRemote.SelectDefault([backup, origin]));
        Assert.Same(backup, GitRemote.SelectDefault([backup]));
        Assert.Null(GitRemote.SelectDefault([]));
    }

    [Theory]
    [InlineData("refs/heads/feature/x", "feature/x")]
    [InlineData("refs/remotes/origin/main", "origin/main")]
    [InlineData("main", "main")]
    public void ShortenBranchReference_removes_the_reference_prefix(string fullName, string expected)
    {
        Assert.Equal(expected, GitRefParser.ShortenBranchReference(fullName));
    }

    private static string Record(params string[] fields) => string.Join('\0', fields) + "\u001e\n";
}
