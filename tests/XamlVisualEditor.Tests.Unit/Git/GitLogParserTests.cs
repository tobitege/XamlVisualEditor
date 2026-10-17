using System;
using System.Collections.Generic;
using System.Linq;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitLogParserTests
{
    private static readonly DateTimeOffset FirstDate = new(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset SecondDate = new(2026, 2, 3, 4, 5, 6, TimeSpan.Zero);

    [Fact]
    public void ParseLog_reads_real_git_output_with_umlauts_multi_line_messages_merges_and_refs()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.WriteFile("a.txt", "a\n");
        repository.RunGit("add", "--", "a.txt");
        repository.RunGitAt(FirstDate, "-c", "user.name=Jürgen Öz", "-c", "user.email=j@example.invalid",
            "commit", "-q", "-m", "Erster Commit: Größe", "-m", "Zweiter Absatz\nmit Zeile");
        repository.RunGit("tag", "v1");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.WriteFile("b.txt", "b\n");
        repository.RunGit("add", "--", "b.txt");
        repository.RunGit("commit", "-q", "-m", "feature work");
        repository.RunGit("switch", "-q", "main");
        repository.RunGitAt(SecondDate, "merge", "-q", "--no-ff", "-m", "Merge feature", "feature");

        IReadOnlyList<GitCommitInfo> commits = GitLogParser.ParseLog(
            repository.RunGit("log", "--format=" + GitLogParser.LOG_FORMAT, "-z", "--encoding=UTF-8"));

        Assert.Equal(new[] { "Merge feature", "feature work", "Erster Commit: Größe" }, commits.Select(commit => commit.Subject));
        GitCommitInfo merge = commits[0];
        Assert.True(merge.IsMerge);
        Assert.Equal(2, merge.ParentShas.Count);
        Assert.Equal(commits[2].Sha, merge.ParentShas[0]);
        Assert.Equal(commits[1].Sha, merge.ParentShas[1]);
        Assert.Equal(new[] { commits[2].ShortSha, commits[1].ShortSha }, merge.ParentShortShas);
        Assert.Contains("HEAD -> main", merge.Refs);
        Assert.Equal(SecondDate, merge.AuthorDate);
        Assert.Equal(SecondDate, merge.CommitterDate);
        Assert.StartsWith(merge.ShortSha, merge.Sha, StringComparison.Ordinal);
        Assert.Equal(40, merge.Sha.Length);

        GitCommitInfo root = commits[2];
        Assert.True(root.IsRoot);
        Assert.Equal("Jürgen Öz", root.AuthorName);
        Assert.Equal("j@example.invalid", root.AuthorEmail);
        Assert.Equal(FirstDate, root.AuthorDate);
        Assert.Equal("Erster Commit: Größe\n\nZweiter Absatz\nmit Zeile", root.Message);
        Assert.Equal(new[] { "tag: v1" }, root.Refs);
        Assert.Equal(new[] { "feature" }, commits[1].Refs);
    }

    [Fact]
    public void ParseLog_skips_text_outside_records_and_stops_at_an_incomplete_record()
    {
        string complete = "\u001eabc\0ab\0\0\0Ann\0a@x\0" + "2026-01-02T03:04:05+02:00\0Ann\0" + "2026-01-02T03:04:05+02:00\0\0subject\0subject\n\0";

        IReadOnlyList<GitCommitInfo> commits = GitLogParser.ParseLog("noise\0" + complete + "\u001edef\0de\0");

        GitCommitInfo commit = Assert.Single(commits);
        Assert.Equal("abc", commit.Sha);
        Assert.Empty(commit.ParentShas);
        Assert.Empty(commit.Refs);
        Assert.Equal("subject", commit.Message);
        Assert.Empty(GitLogParser.ParseLog(string.Empty));
    }

    [Fact]
    public void ParseNameStatus_reads_added_modified_deleted_and_renamed_files_of_diff_tree()
    {
        using GitTestRepository repository = GitTestRepository.Create("keep.txt", "gone.txt", "old name.txt");
        repository.RunGit("mv", "--", "old name.txt", "neuer Name ä.txt");
        repository.RunGit("rm", "-q", "--", "gone.txt");
        repository.WriteFile("keep.txt", "changed\n");
        repository.WriteFile("dir/new.txt", "new\n");
        repository.RunGit("add", "-A");
        repository.RunGit("commit", "-q", "-m", "change");

        IReadOnlyList<GitCommitFileChange> files = GitLogParser.ParseNameStatus(
            repository.RunGit("diff-tree", "-r", "-z", "--name-status", "-M", "HEAD~1", "HEAD"));

        Assert.Equal(4, files.Count);
        Assert.Contains(new GitCommitFileChange { Path = "dir/new.txt", Kind = GitChangeKind.Added }, files);
        Assert.Contains(new GitCommitFileChange { Path = "gone.txt", Kind = GitChangeKind.Deleted }, files);
        Assert.Contains(new GitCommitFileChange { Path = "keep.txt", Kind = GitChangeKind.Modified }, files);
        Assert.Contains(
            new GitCommitFileChange { Path = "neuer Name ä.txt", OldPath = "old name.txt", Kind = GitChangeKind.Renamed, Similarity = 100 },
            files);
    }

    [Fact]
    public void ParseTags_reads_annotated_and_lightweight_tags_of_for_each_ref()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.WriteFile("a.txt", "a\n");
        repository.RunGit("add", "--", "a.txt");
        repository.RunGitAt(FirstDate, "commit", "-q", "-m", "first");
        string commit = repository.RunGit("rev-parse", "HEAD").Trim();
        repository.RunGit("tag", "leicht");
        repository.RunGitAt(SecondDate, "-c", "user.name=Tägger", "tag", "-a", "v2.0", "-m", "Release zwei");

        IReadOnlyList<GitTag> tags = GitRefParser.ParseTags(
            repository.RunGit("for-each-ref", "--sort=-creatordate", "--format=" + GitRefParser.TAG_FORMAT, "refs/tags"));

        Assert.Equal(new[] { "v2.0", "leicht" }, tags.Select(tag => tag.Name));
        GitTag annotated = tags[0];
        Assert.True(annotated.IsAnnotated);
        Assert.Equal("refs/tags/v2.0", annotated.FullName);
        Assert.Equal(commit, annotated.TargetSha);
        Assert.StartsWith(annotated.TargetShortSha, commit, StringComparison.Ordinal);
        Assert.Equal(GitTag.COMMIT_TYPE, annotated.TargetType);
        Assert.Equal("Tägger", annotated.TaggerName);
        Assert.Equal("Release zwei", annotated.Subject);
        Assert.Equal(SecondDate, annotated.Date);
        GitTag lightweight = tags[1];
        Assert.False(lightweight.IsAnnotated);
        Assert.Equal(commit, lightweight.TargetSha);
        Assert.Equal(annotated.TargetShortSha, lightweight.TargetShortSha);
        Assert.True(lightweight.IsCommit);
        Assert.Equal(string.Empty, lightweight.TaggerName);
        Assert.Equal("first", lightweight.Subject);
        Assert.Equal(FirstDate, lightweight.Date);
    }
}
