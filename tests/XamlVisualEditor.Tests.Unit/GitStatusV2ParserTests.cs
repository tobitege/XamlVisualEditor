using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class GitStatusV2ParserTests
{
    private const string SHA = "0123456789abcdef0123456789abcdef01234567";
    private const string BLOB = "e69de29bb2d1d6434b8b29ae775ad8c2e48c5391";

    private static string Output(params string[] entries) => string.Join("\0", entries) + "\0";

    private static string Ordinary(string status, string path) =>
        $"1 {status} N... 100644 100644 100644 {BLOB} {BLOB} {path}";

    private static string Unmerged(string status, string path) =>
        $"u {status} N... 100644 100644 100644 100644 {BLOB} {BLOB} {BLOB} {path}";

    [Fact]
    public void Parses_branch_upstream_and_ahead_behind()
    {
        string output = Output(
            "# branch.oid " + SHA,
            "# branch.head feature/x",
            "# branch.upstream origin/feature/x",
            "# branch.ab +3 -2");

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.Equal("/repo", status.RepositoryRoot);
        Assert.Equal("feature/x", status.BranchName);
        Assert.Equal("origin/feature/x", status.UpstreamName);
        Assert.Equal(3, status.AheadBy);
        Assert.Equal(2, status.BehindBy);
        Assert.Equal(SHA, status.HeadSha);
        Assert.False(status.IsDetached);
        Assert.False(status.IsUnborn);
        Assert.Equal(GitRepositoryOperation.None, status.Operation);
        Assert.Empty(status.Changes);
    }

    [Fact]
    public void Detached_head_names_the_abbreviated_commit()
    {
        string output = Output("# branch.oid " + SHA, "# branch.head (detached)");

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.True(status.IsDetached);
        Assert.Equal(SHA, status.HeadSha);
        Assert.Equal("0123456", status.BranchName);
        Assert.Null(status.UpstreamName);
    }

    [Fact]
    public void Unborn_branch_has_no_head_commit()
    {
        string output = Output("# branch.oid (initial)", "# branch.head main", "? new.txt");

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.True(status.IsUnborn);
        Assert.Null(status.HeadSha);
        Assert.Equal("main", status.BranchName);
        Assert.Single(status.Changes);
    }

    [Fact]
    public void Parses_ordinary_changes_with_spaces_and_umlauts_in_paths()
    {
        string output = Output(
            "# branch.oid " + SHA,
            "# branch.head main",
            Ordinary(".M", "src/Größe ä.xaml"),
            Ordinary("A.", "a b.txt"),
            Ordinary("MD", "docs/read me.md"));

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.Equal(3, status.Changes.Count);
        GitFileChange modified = status.Changes[0];
        Assert.Equal("src/Größe ä.xaml", modified.Path);
        Assert.Equal(GitChangeKind.None, modified.IndexStatus);
        Assert.Equal(GitChangeKind.Modified, modified.WorkTreeStatus);
        Assert.Null(modified.OldPath);

        GitFileChange added = status.Changes[1];
        Assert.Equal("a b.txt", added.Path);
        Assert.Equal(GitChangeKind.Added, added.IndexStatus);
        Assert.Equal(GitChangeKind.None, added.WorkTreeStatus);

        GitFileChange both = status.Changes[2];
        Assert.Equal("docs/read me.md", both.Path);
        Assert.Equal(GitChangeKind.Modified, both.IndexStatus);
        Assert.Equal(GitChangeKind.Deleted, both.WorkTreeStatus);
        Assert.False(both.IsConflicted);
    }

    [Fact]
    public void Rename_takes_the_original_path_from_the_next_entry()
    {
        string output = Output(
            $"2 R. N... 100644 100644 100644 {BLOB} {BLOB} R100 new name ä.txt",
            "old name.txt",
            $"2 C. N... 100644 100644 100644 {BLOB} {BLOB} C75 copy.txt",
            "source.txt",
            Ordinary(".M", "after.txt"));

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.Equal(3, status.Changes.Count);
        GitFileChange renamed = status.Changes[0];
        Assert.Equal("new name ä.txt", renamed.Path);
        Assert.Equal("old name.txt", renamed.OldPath);
        Assert.True(renamed.IsRenamed);
        Assert.Equal(GitChangeKind.Renamed, renamed.IndexStatus);
        Assert.Equal(GitChangeKind.None, renamed.WorkTreeStatus);

        GitFileChange copied = status.Changes[1];
        Assert.Equal("copy.txt", copied.Path);
        Assert.Equal("source.txt", copied.OldPath);
        Assert.True(copied.IsCopied);

        Assert.Equal("after.txt", status.Changes[2].Path);
    }

    [Theory]
    [InlineData("DD", GitConflictKind.BothDeleted)]
    [InlineData("AU", GitConflictKind.AddedByUs)]
    [InlineData("UD", GitConflictKind.DeletedByThem)]
    [InlineData("UA", GitConflictKind.AddedByThem)]
    [InlineData("DU", GitConflictKind.DeletedByUs)]
    [InlineData("AA", GitConflictKind.BothAdded)]
    [InlineData("UU", GitConflictKind.BothModified)]
    public void Maps_conflict_codes(string code, GitConflictKind expected)
    {
        string output = Output("# branch.oid " + SHA, "# branch.head main", Unmerged(code, "Größe ä.xaml"));

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output, GitRepositoryOperation.Merging);

        GitFileChange conflict = Assert.Single(status.Changes);
        Assert.Equal("Größe ä.xaml", conflict.Path);
        Assert.Equal(expected, conflict.ConflictKind);
        Assert.True(conflict.IsConflicted);
        Assert.Equal(GitChangeKind.Unmerged, conflict.IndexStatus);
        Assert.Equal(GitChangeKind.Unmerged, conflict.WorkTreeStatus);
        Assert.Equal(GitRepositoryOperation.Merging, status.Operation);
    }

    [Fact]
    public void Untracked_and_ignored_entries_are_work_tree_only()
    {
        string output = Output("? new file ü.txt", "? folder/", "! bin/out.dll");

        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", output);

        Assert.Equal(3, status.Changes.Count);
        GitFileChange untracked = status.Changes[0];
        Assert.Equal("new file ü.txt", untracked.Path);
        Assert.True(untracked.IsUntracked);
        Assert.Equal(GitChangeKind.None, untracked.IndexStatus);
        Assert.Equal(GitChangeKind.Untracked, untracked.WorkTreeStatus);
        Assert.Equal("folder/", status.Changes[1].Path);

        GitFileChange ignored = status.Changes[2];
        Assert.Equal("bin/out.dll", ignored.Path);
        Assert.True(ignored.IsIgnored);
        Assert.Equal(GitChangeKind.Ignored, ignored.WorkTreeStatus);
    }

    [Fact]
    public void Empty_output_gives_an_empty_status()
    {
        GitRepositoryStatus status = GitStatusParser.ParsePorcelainV2("/repo", string.Empty);

        Assert.Empty(status.Changes);
        Assert.Equal(string.Empty, status.BranchName);
        Assert.True(status.IsRepository);
    }
}
