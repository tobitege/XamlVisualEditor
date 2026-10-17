using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitHistoryServiceTests
{
    [Fact]
    public async Task Log_lists_the_commits_of_two_authors_newest_first_and_filters_by_author_and_message()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        CommitAs(repository, "Anna Äbi", "anna@example.invalid", "a.txt", "Fix (a+b) Überlauf");
        CommitAs(repository, "Bob", "bob@example.invalid", "b.txt", "Fix c");
        CommitAs(repository, "Anna Äbi", "anna@example.invalid", "c.txt", "Docs");
        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitCommitInfo> all = await service.GetLogAsync(repository.Path, new GitLogQuery(), ct);
        IReadOnlyList<GitCommitInfo> anna = await service.GetLogAsync(repository.Path, new GitLogQuery { Author = "ÄBI" }, ct);
        IReadOnlyList<GitCommitInfo> bobByEmail = await service.GetLogAsync(repository.Path, new GitLogQuery { Author = "BOB@example" }, ct);
        IReadOnlyList<GitCommitInfo> pattern = await service.GetLogAsync(
            repository.Path,
            new GitLogQuery { Author = "anna", MessageText = "(A+B) Überlauf" },
            ct);
        IReadOnlyList<GitCommitInfo> bothMustMatch = await service.GetLogAsync(
            repository.Path,
            new GitLogQuery { Author = "bob", MessageText = "(a+b)" },
            ct);

        Assert.Equal(new[] { "Docs", "Fix c", "Fix (a+b) Überlauf" }, all.Select(commit => commit.Subject));
        Assert.Equal(new[] { "Anna Äbi", "Bob", "Anna Äbi" }, all.Select(commit => commit.AuthorName));
        Assert.Equal(new[] { "Docs", "Fix (a+b) Überlauf" }, anna.Select(commit => commit.Subject));
        Assert.Equal(new[] { "Fix c" }, bobByEmail.Select(commit => commit.Subject));
        Assert.Equal(new[] { "Fix (a+b) Überlauf" }, pattern.Select(commit => commit.Subject));
        Assert.Empty(bothMustMatch);
    }

    [Fact]
    public async Task Log_pages_with_skip_and_max_count()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        for (int i = 1; i <= 5; i++)
        {
            repository.CommitFileIn(repository.Path, "f.txt", i + "\n", "c" + i);
        }

        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitCommitInfo> page = await service.GetLogAsync(repository.Path, new GitLogQuery { Skip = 1, MaxCount = 2 }, ct);
        IReadOnlyList<GitCommitInfo> last = await service.GetLogAsync(repository.Path, new GitLogQuery { Skip = 4, MaxCount = 2 }, ct);

        Assert.Equal(new[] { "c4", "c3" }, page.Select(commit => commit.Subject));
        Assert.Equal(new[] { "c1" }, last.Select(commit => commit.Subject));
    }

    [Fact]
    public async Task Folder_history_lists_the_commits_below_the_folder_and_file_history_follows_renames()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "src/a.txt", "line one\nline two\nline three\n", "add a");
        repository.CommitFileIn(repository.Path, "other.txt", "other\n", "add other");
        repository.RunGit("mv", "--", "src/a.txt", "src/b ä.txt");
        repository.RunGit("commit", "-q", "-m", "rename a");
        // log.follow must not make git follow a folder, which lists unrelated commits.
        repository.RunGit("config", "log.follow", "true");
        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitCommitInfo> folder = await service.GetLogAsync(repository.Path, new GitLogQuery { Path = "src" }, ct);
        IReadOnlyList<GitCommitInfo> followed = await service.GetLogAsync(
            repository.Path,
            new GitLogQuery { Path = "src/b ä.txt", Follow = true },
            ct);
        IReadOnlyList<GitCommitInfo> notFollowed = await service.GetLogAsync(repository.Path, new GitLogQuery { Path = "src/b ä.txt" }, ct);

        Assert.Equal(new[] { "rename a", "add a" }, folder.Select(commit => commit.Subject));
        Assert.Equal(new[] { "rename a", "add a" }, followed.Select(commit => commit.Subject));
        Assert.Equal(new[] { "rename a" }, notFollowed.Select(commit => commit.Subject));
    }

    [Fact]
    public async Task Log_starts_at_a_revision_and_reads_a_revision_that_looks_like_an_option_as_revision()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.RunGit("tag", "v1");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitCommitInfo> fromTag = await service.GetLogAsync(repository.Path, new GitLogQuery { Revision = "refs/tags/v1" }, ct);

        Assert.Equal(new[] { "init" }, fromTag.Select(commit => commit.Subject));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetLogAsync(repository.Path, new GitLogQuery { Revision = "--all" }, ct));
    }

    [Fact]
    public async Task A_repository_without_commits_has_an_empty_history()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        GitService service = new(options: repository.ProcessOptions);

        Assert.Empty(await service.GetLogAsync(repository.Path, new GitLogQuery(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Root_commit_lists_all_its_files_as_added_and_their_diff_adds_every_line()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt", "dir/b.txt");
        GitService service = new(options: repository.ProcessOptions);
        string sha = repository.RunGit("rev-parse", "HEAD").Trim();

        GitCommitDetails details = await service.GetCommitAsync(repository.Path, await HeadAsync(service, repository), ct);
        GitDiff diff = await service.GetCommitFileDiffAsync(repository.Path, details.Commit, details.Files[1], ct: ct);

        Assert.Equal(sha, details.Commit.Sha);
        Assert.True(details.Commit.IsRoot);
        Assert.Equal(
            new[] { ("a.txt", GitChangeKind.Added), ("dir/b.txt", GitChangeKind.Added) },
            details.Files.Select(file => (file.Path, file.Kind)));
        GitDiffFile file = Assert.Single(diff.Files);
        Assert.Equal("dir/b.txt", file.Path);
        GitDiffLine line = Assert.Single(Assert.Single(file.Hunks).Lines);
        Assert.Equal(GitDiffLineKind.Added, line.Kind);
        Assert.Equal("dir/b.txt", line.Text);
    }

    [Fact]
    public async Task Merge_commit_is_compared_with_its_first_parent()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("shared.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "shared.txt", "shared.txt\nfeature line\n", "feature change");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "main.txt", "main\n", "main change");
        repository.RunGit("merge", "-q", "--no-ff", "-m", "Merge feature", "feature");
        GitService service = new(options: repository.ProcessOptions);

        GitCommitDetails details = await service.GetCommitAsync(repository.Path, await HeadAsync(service, repository), ct);
        GitDiff diff = await service.GetCommitFileDiffAsync(repository.Path, details.Commit, details.Files[0], ct: ct);

        Assert.True(details.Commit.IsMerge);
        Assert.Equal(
            repository.RunGit("log", "-1", "--format=%p").Trim().Split(' '),
            details.Commit.ParentShortShas);
        Assert.Equal("Merge feature", details.Commit.Subject);
        GitCommitFileChange change = Assert.Single(details.Files);
        Assert.Equal(("shared.txt", GitChangeKind.Modified), (change.Path, change.Kind));
        GitDiffLine added = Assert.Single(Assert.Single(Assert.Single(diff.Files).Hunks).Lines, line => line.Kind == GitDiffLineKind.Added);
        Assert.Equal("feature line", added.Text);
    }

    [Fact]
    public async Task Renamed_file_has_its_old_path_and_its_diff_shows_the_rename_and_the_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "alt.txt", "1\n2\n3\n4\n5\n6\n7\n8\n", "add");
        repository.RunGit("mv", "--", "alt.txt", "neu ü.txt");
        repository.WriteFile("neu ü.txt", "1\n2\n3\n4\n5\n6\n7\nacht\n");
        repository.RunGit("add", "-A");
        repository.RunGit("commit", "-q", "-m", "rename");
        GitService service = new(options: repository.ProcessOptions);

        GitCommitDetails details = await service.GetCommitAsync(repository.Path, await HeadAsync(service, repository), ct);
        GitCommitFileChange renamed = Assert.Single(details.Files);
        GitDiff diff = await service.GetCommitFileDiffAsync(repository.Path, details.Commit, renamed, ct: ct);

        Assert.Equal(GitChangeKind.Renamed, renamed.Kind);
        Assert.Equal("alt.txt", renamed.OldPath);
        Assert.Equal("neu ü.txt", renamed.Path);
        Assert.InRange(renamed.Similarity ?? 0, 50, 99);
        GitDiffFile file = Assert.Single(diff.Files);
        Assert.Contains("rename from alt.txt", file.HeaderLines);
        Assert.Contains(Assert.Single(file.Hunks).Lines, line => line.Kind == GitDiffLineKind.Added && line.Text == "acht");
    }

    [Fact]
    public async Task An_unknown_commit_fails_with_gits_error()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        GitCommitInfo unknown = new() { Sha = "0123456789abcdef0123456789abcdef01234567" };

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.GetCommitAsync(repository.Path, unknown, TestContext.Current.CancellationToken));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Fact]
    public async Task Tags_are_listed_newest_first_with_annotation_and_target_commit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.WriteFile("a.txt", "a\n");
        repository.RunGit("add", "--", "a.txt");
        repository.RunGitAt(new DateTimeOffset(2025, 5, 1, 12, 0, 0, TimeSpan.Zero), "commit", "-q", "-m", "old");
        string commit = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.RunGit("tag", "v1");
        repository.RunGit("tag", "-a", "v2", "-m", "Version zwei");
        GitService service = new(options: repository.ProcessOptions);

        IReadOnlyList<GitTag> tags = await service.GetTagsAsync(repository.Path, ct);

        Assert.Equal(new[] { "v2", "v1" }, tags.Select(tag => tag.Name));
        Assert.True(tags[0].IsAnnotated);
        Assert.Equal("Version zwei", tags[0].Subject);
        Assert.Equal("Test", tags[0].TaggerName);
        Assert.False(tags[1].IsAnnotated);
        Assert.All(tags, tag => Assert.Equal(commit, tag.TargetSha));
        using GitTestRepository empty = GitTestRepository.Create();
        Assert.Empty(await new GitService(options: empty.ProcessOptions).GetTagsAsync(empty.Path, ct));
    }

    [Fact]
    public async Task File_history_pages_follow_a_rename_without_duplicates_or_gaps_in_the_order_of_git()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        // 186 commits: every third changes another file, commit 62 renames the file, the others change it.
        StringBuilder import = new();
        string path = "src/old.txt";
        string unchanged = string.Concat(Enumerable.Range(1, 30).Select(line => "unchanged line " + line + "\n"));
        for (int number = 1; number <= 186; number++)
        {
            import.Append("commit refs/heads/main\n")
                .Append("committer Test <test@example.invalid> ").Append(1_700_000_000 + number).Append(" +0000\n");
            AppendData(import, "c" + number);
            if (number == 62)
            {
                import.Append("R src/old.txt src/new.txt\n");
                path = "src/new.txt";
            }
            else
            {
                import.Append("M 100644 inline ").Append(number % 3 == 0 ? "other.txt" : path).Append('\n');
                AppendData(import, unchanged + "version " + number + "\n");
            }
        }

        repository.RunGitWithInput(import.ToString(), "fast-import", "--quiet");
        GitService service = new(options: repository.ProcessOptions);
        string[] expected = repository.RunGit("log", "--follow", "--format=%H", "--", "src/new.txt")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(124, expected.Length);
        Assert.Equal(expected, (await PageAsync(0, 100)).Concat(await PageAsync(100, 100)));
        Assert.Equal(expected[10..65], await PageAsync(10, 55));
        Assert.Equal(expected[62..72], await PageAsync(62, 10));
        Assert.Equal(expected[100..], await PageAsync(100, 101));
        Assert.Empty(await PageAsync(124, 100));

        async Task<IEnumerable<string>> PageAsync(int skip, int maxCount)
        {
            IReadOnlyList<GitCommitInfo> page = await service.GetLogAsync(
                repository.Path,
                new GitLogQuery { Path = "src/new.txt", Follow = true, Skip = skip, MaxCount = maxCount },
                ct);
            return page.Select(commit => commit.Sha).ToArray();
        }

        static void AppendData(StringBuilder builder, string data) =>
            builder.Append("data ").Append(Encoding.UTF8.GetByteCount(data)).Append('\n').Append(data).Append('\n');
    }

    [Fact]
    public async Task A_file_diff_is_read_only_up_to_the_line_limit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        StringBuilder content = new();
        for (int line = 1; line <= 200_000; line++)
        {
            content.Append("line ").Append(line).Append('\n');
        }

        repository.CommitFileIn(repository.Path, "big.txt", content.ToString(), "big");
        GitService service = new(options: repository.ProcessOptions);
        GitCommitInfo head = await HeadAsync(service, repository);
        GitCommitFileChange file = Assert.Single((await service.GetCommitAsync(repository.Path, head, ct)).Files);

        Stopwatch watch = Stopwatch.StartNew();
        GitDiff cut = await service.GetCommitFileDiffAsync(repository.Path, head, file, 20_000, ct: ct);
        TimeSpan cutTime = watch.Elapsed;
        watch.Restart();
        GitDiff whole = await service.GetCommitFileDiffAsync(repository.Path, head, file, ct: ct);
        TimeSpan wholeTime = watch.Elapsed;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"Diff of 200,006 lines: {cutTime.TotalMilliseconds:F0} ms for 20,000 lines, {wholeTime.TotalMilliseconds:F0} ms for all.");

        Assert.True(cut.IsTruncated);
        Assert.Equal(20_000, cut.RawText!.Count(character => character == '\n'));
        Assert.EndsWith("+line 19994\n", cut.RawText, StringComparison.Ordinal);
        Assert.Equal(19_994, Assert.Single(Assert.Single(cut.Files).Hunks).Lines.Count);
        Assert.False(whole.IsTruncated);
        Assert.Equal(200_000, Assert.Single(Assert.Single(whole.Files).Hunks).Lines.Count);

        // A diff of exactly as many lines as the limit is complete; one line less cuts it.
        using GitTestRepository small = GitTestRepository.Create("a.txt", "b.txt");
        GitService smallService = new(options: small.ProcessOptions);
        GitCommitInfo smallHead = await HeadAsync(smallService, small);
        GitCommitFileChange smallFile = (await smallService.GetCommitAsync(small.Path, smallHead, ct)).Files[0];
        GitDiff complete = await smallService.GetCommitFileDiffAsync(small.Path, smallHead, smallFile, ct: ct);
        string completeText = complete.RawText!;
        int lineCount = completeText.Count(character => character == '\n');
        GitDiff exact = await smallService.GetCommitFileDiffAsync(small.Path, smallHead, smallFile, lineCount, ct: ct);
        GitDiff shorter = await smallService.GetCommitFileDiffAsync(small.Path, smallHead, smallFile, lineCount - 1, ct: ct);

        Assert.Equal((false, completeText), (exact.IsTruncated, exact.RawText));
        Assert.True(shorter.IsTruncated);
        Assert.Equal(completeText[..(completeText.LastIndexOf('\n', completeText.Length - 2) + 1)], shorter.RawText);
    }

    [Fact]
    public async Task A_revision_resolves_to_the_commit_it_names_now_with_its_abbreviated_id()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        repository.RunGit("branch", "feature");
        repository.RunGit("tag", "-a", "v1", "-m", "release");
        string first = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        string second = repository.RunGit("rev-parse", "HEAD").Trim();

        GitCommitInfo branch = await service.ResolveCommitAsync(repository.Path, "feature", ct);
        GitCommitInfo tag = await service.ResolveCommitAsync(repository.Path, "v1", ct);
        GitCommitInfo head = await service.ResolveCommitAsync(repository.Path, "HEAD", ct);
        GitCommitInfo abbreviated = await service.ResolveCommitAsync(repository.Path, second[..10], ct);
        GitCommitInfo message = await service.ResolveCommitAsync(repository.Path, ":/second", ct);

        Assert.Equal((first, repository.RunGit("rev-parse", "--short", first).Trim()), (branch.Sha, branch.ShortSha));
        Assert.Equal(first, tag.Sha);
        Assert.Equal(second, head.Sha);
        Assert.Equal(second, abbreviated.Sha);
        Assert.Equal(second, message.Sha);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveCommitAsync(repository.Path, "no-such-branch", ct));
    }

    [Theory]
    [InlineData("HEAD~1..HEAD")]
    [InlineData("HEAD~1...HEAD")]
    [InlineData("^HEAD")]
    [InlineData("treetag")]
    [InlineData("blobtag")]
    [InlineData("anntree")]
    [InlineData("no-such-branch")]
    [InlineData("--all")]
    [InlineData("-n1")]
    [InlineData("--verify")]
    // git itself fails with "--default requires an argument" (exit code 128), so only the leading-dash check gives
    // the localized message.
    [InlineData("--default")]
    [InlineData(" ")]
    public async Task A_revision_that_names_no_single_commit_is_rejected_with_a_localized_message(string revision)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.CommitFileIn(repository.Path, "b.txt", "b\n", "second");
        repository.RunGit("tag", "treetag", repository.RunGit("rev-parse", "HEAD^{tree}").Trim());
        repository.RunGit("tag", "blobtag", repository.RunGit("rev-parse", "HEAD:a.txt").Trim());
        repository.RunGit("tag", "-a", "anntree", "-m", "ann tree", "HEAD^{tree}");
        GitService service = new(options: repository.ProcessOptions);

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveCommitAsync(repository.Path, revision, ct));

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, UiStrings.GitRevisionNotOneCommitFormat, revision), error.Message);
    }

    [Fact]
    public async Task A_revision_in_a_folder_that_is_not_a_repository_fails_with_the_message_of_git()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);
        Assert.False(repository.TryRunGitIn(repository.RootPath, out string gitError, "status"));

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ResolveCommitAsync(repository.RootPath, "HEAD", ct));

        Assert.Equal(gitError.Trim(), error.Message);
    }

    [Fact]
    public async Task A_file_diff_of_one_very_long_line_is_read_only_up_to_the_byte_limit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        // A minified file: 6 MB on a single line.
        StringBuilder content = new("{\"items\":[");
        while (content.Length < 6 * 1024 * 1024)
        {
            content.Append("{\"name\":\"Größe\",\"value\":12345},");
        }

        repository.CommitFileIn(repository.Path, "data.min.json", content.Append("{}]}").ToString(), "minified");
        GitService service = new(options: repository.ProcessOptions);
        GitCommitInfo head = await HeadAsync(service, repository);
        GitCommitFileChange file = Assert.Single((await service.GetCommitAsync(repository.Path, head, ct)).Files);
        const int BYTE_LIMIT = 4 * 1024 * 1024;

        GitDiff cut = await service.GetCommitFileDiffAsync(repository.Path, head, file, 20_000, BYTE_LIMIT, ct);
        GitDiff byLinesOnly = await service.GetCommitFileDiffAsync(repository.Path, head, file, 20_000, ct: ct);

        // The diff ends with the last complete line within the limit, the hunk header; the long line is not read.
        Assert.True(cut.IsTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(cut.RawText!) <= BYTE_LIMIT);
        Assert.Matches(@"\n@@ -0,0 \+1 @@\n\z", cut.RawText);
        Assert.Empty(Assert.Single(Assert.Single(cut.Files).Hunks).Lines);
        // A line limit alone reads the whole line.
        Assert.False(byLinesOnly.IsTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(byLinesOnly.RawText!) > 6 * 1024 * 1024);
    }

    [Fact]
    public async Task Tags_of_trees_blobs_and_tags_name_the_type_of_their_object_and_abbreviate_it_as_git_does()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        string commit = repository.RunGit("rev-parse", "HEAD").Trim();
        string tree = repository.RunGit("rev-parse", "HEAD^{tree}").Trim();
        string blob = repository.RunGit("rev-parse", "HEAD:a.txt").Trim();
        repository.RunGit("tag", "treetag", tree);
        repository.RunGit("tag", "blobtag", blob);
        repository.RunGit("tag", "-a", "anntree", "-m", "ann tree", tree);
        repository.RunGit("tag", "-a", "ann", "-m", "annotated");
        repository.RunGit("tag", "-a", "nested", "-m", "nested", "ann");
        GitService service = new(options: repository.ProcessOptions);

        Dictionary<string, GitTag> tags = (await service.GetTagsAsync(repository.Path, ct)).ToDictionary(tag => tag.Name);

        Assert.Equal(("tree", tree, false, false), Describe(tags["treetag"]));
        Assert.Equal(("blob", blob, false, false), Describe(tags["blobtag"]));
        Assert.Equal(("tree", tree, true, false), Describe(tags["anntree"]));
        Assert.Equal(("commit", commit, true, true), Describe(tags["ann"]));
        Assert.Equal(repository.RunGit("rev-parse", "--short", tree).Trim(), tags["treetag"].TargetShortSha);
        Assert.Equal(repository.RunGit("rev-parse", "--short", "HEAD").Trim(), tags["ann"].TargetShortSha);
        // Git peels a tag of a tag down to the commit; a git that peels one level names the tag object.
        GitTag nested = tags["nested"];
        Assert.True(nested.IsAnnotated);
        Assert.True(
            Describe(nested) == ("commit", commit, true, true) || nested.TargetType == "tag",
            $"nested points to a {nested.TargetType} {nested.TargetSha}.");

        static (string Type, string Sha, bool IsAnnotated, bool IsCommit) Describe(GitTag tag) =>
            (tag.TargetType, tag.TargetSha, tag.IsAnnotated, tag.IsCommit);
    }

    private static async Task<GitCommitInfo> HeadAsync(GitService service, GitTestRepository repository) =>
        Assert.Single(await service.GetLogAsync(
            repository.Path,
            new GitLogQuery { MaxCount = 1 },
            TestContext.Current.CancellationToken));

    private static void CommitAs(GitTestRepository repository, string name, string email, string file, string message)
    {
        repository.WriteFile(file, file + "\n");
        repository.RunGit("add", "--", file);
        repository.RunGit("-c", "user.name=" + name, "-c", "user.email=" + email, "commit", "-q", "-m", message);
    }
}
