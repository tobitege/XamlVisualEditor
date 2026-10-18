using System.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitPatchServiceTests
{
    private const string Twelve = "l1\nl2\nl3\nl4\nl5\nl6\nl7\nl8\nl9\nl10\nl11\nl12\n";

    [Fact]
    public async Task Staging_one_of_two_hunks_stages_exactly_its_lines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", Twelve);
        repository.WriteFile("f.txt", Twelve.Replace("l2\n", "X2\n").Replace("l11\n", "Y11\n"));
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Stage, 0), ct);

        Assert.Equal(Twelve.Replace("l2\n", "X2\n"), await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task Staging_one_line_of_a_mixed_hunk_with_crlf_keeps_the_line_endings()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", "a\r\nb\r\nc\r\n");
        repository.WriteFile("f.txt", "a\r\nB\r\nc\r\nd\r\n");
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Lines(diff, "f.txt", GitPatchAction.Stage, "+d\r", 1), ct);

        Assert.Equal("a\r\nb\r\nc\r\nd\r\n", await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task Staging_a_hunk_of_a_latin1_file_keeps_its_bytes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", "x\n");
        File.WriteAllBytes(Path.Combine(repository.Path, "f.txt"), Encoding.Latin1.GetBytes("x\nGrüße\n"));
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Stage, 0), ct);

        GitFileContent? index = await service.GetFileContentAsync(repository.Path, "f.txt", GitFileRevision.Index, 1024, ct);
        Assert.Equal(Encoding.Latin1.GetBytes("x\nGrüße\n"), index!.Bytes);
    }

    [Fact]
    public async Task Unstaging_one_line_of_a_staged_new_file_keeps_the_other_lines_staged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("base.txt");
        repository.WriteFile("Neue Datei ä.txt", "n1\nn2\nn3\n");
        repository.RunGit("add", "--", "Neue Datei ä.txt");
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "Neue Datei ä.txt", staged: true, ct);

        await service.ApplySelectionAsync(repository.Path, Lines(diff, "Neue Datei ä.txt", GitPatchAction.Unstage, "+n2", 1), ct);

        Assert.Equal("n1\nn3\n", await IndexTextAsync(service, repository, "Neue Datei ä.txt", ct));
        Assert.Equal("n1\nn2\nn3\n", File.ReadAllText(Path.Combine(repository.Path, "Neue Datei ä.txt")));
    }

    [Fact]
    public async Task Staging_one_line_of_a_deletion_keeps_the_file_in_the_index()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("d.txt", "d1\nd2\nd3\n");
        File.Delete(Path.Combine(repository.Path, "d.txt"));
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "d.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Lines(diff, "d.txt", GitPatchAction.Stage, "-d2", 1), ct);

        Assert.Equal("d1\nd3\n", await IndexTextAsync(service, repository, "d.txt", ct));
    }

    [Fact]
    public async Task Discarding_a_hunk_with_autocrlf_restores_the_crlf_working_file()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.RunGit("config", "core.autocrlf", "true");
        repository.WriteFile("f.txt", "a\r\nb\r\nc\r\n");
        repository.RunGit("add", "--", "f.txt");
        repository.RunGit("commit", "-q", "-m", "crlf");
        repository.WriteFile("f.txt", "a\r\nB\r\nc\r\n");
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Discard, 0), ct);

        Assert.Equal(Encoding.ASCII.GetBytes("a\r\nb\r\nc\r\n"), File.ReadAllBytes(Path.Combine(repository.Path, "f.txt")));
        Assert.Equal("a\nb\nc\n", await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task Restoring_a_last_line_without_line_break_in_front_of_another_line_is_rejected_and_changes_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", "a\nb");
        repository.WriteFile("f.txt", "a\nc");
        GitService service = new(options: repository.ProcessOptions);
        string worktreeDiff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        GitPatchRejectedException discard = await Assert.ThrowsAsync<GitPatchRejectedException>(
            () => service.ApplySelectionAsync(repository.Path, Lines(worktreeDiff, "f.txt", GitPatchAction.Discard, "-b", 1), ct));
        repository.RunGit("add", "--", "f.txt");
        string stagedDiff = await ShownDiffAsync(service, repository, "f.txt", staged: true, ct);
        GitPatchRejectedException unstage = await Assert.ThrowsAsync<GitPatchRejectedException>(
            () => service.ApplySelectionAsync(repository.Path, Lines(stagedDiff, "f.txt", GitPatchAction.Unstage, "-b", 1), ct));

        Assert.Equal((GitPatchRejection.MissingNewline, GitPatchRejection.MissingNewline), (discard.Reason, unstage.Reason));
        Assert.Equal("a\nc", File.ReadAllText(Path.Combine(repository.Path, "f.txt")));
        Assert.Equal("a\nc", await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task Unstaging_one_line_keeps_a_staged_mode_change()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", Twelve);
        repository.WriteFile("f.txt", Twelve.Replace("l2\n", "X2\n").Replace("l11\n", "Y11\n"));
        repository.RunGit("add", "--", "f.txt");
        repository.RunGit("update-index", "--chmod=+x", "--", "f.txt");
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: true, ct);

        await service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Unstage, 0), ct);

        Assert.StartsWith("100755 ", repository.RunGit("ls-files", "-s", "--", "f.txt"), StringComparison.Ordinal);
        Assert.Equal(Twelve.Replace("l11\n", "Y11\n"), await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task A_changed_diff_is_rejected_and_the_index_stays_unchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", Twelve);
        repository.WriteFile("f.txt", Twelve.Replace("l2\n", "X2\n"));
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);
        repository.WriteFile("f.txt", Twelve.Replace("l2\n", "X2\n").Replace("l4\n", "X4\n"));
        string indexBefore = repository.RunGit("ls-files", "-s");

        GitPatchRejectedException rejected = await Assert.ThrowsAsync<GitPatchRejectedException>(
            () => service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Stage, 0), ct));

        Assert.Equal(GitPatchRejection.DiffChanged, rejected.Reason);
        Assert.Equal(indexBefore, repository.RunGit("ls-files", "-s"));
    }

    [Fact]
    public async Task A_failed_apply_leaves_the_index_unchanged()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", Twelve);
        repository.WriteFile("f.txt", Twelve.Replace("l2\n", "X2\n"));
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);
        string indexBefore = repository.RunGit("ls-files", "-s");
        string lockFile = Path.Combine(repository.Path, ".git", "index.lock");
        File.WriteAllText(lockFile, string.Empty);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Stage, 0), ct));

        File.Delete(lockFile);
        Assert.Equal(indexBefore, repository.RunGit("ls-files", "-s"));
    }

    [Fact]
    public async Task Repository_settings_neither_change_the_shown_diff_nor_the_staged_lines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateWithFile("f.txt", "a\nb\n");
        repository.RunGit("config", "apply.whitespace", "fix");
        repository.RunGit("config", "apply.ignoreWhitespace", "change");
        repository.RunGit("config", "diff.noprefix", "true");
        repository.RunGit("config", "diff.external", "false");
        repository.WriteFile("f.txt", "a\nb  \nc\t\n");
        GitService service = new(options: repository.ProcessOptions);
        string diff = await ShownDiffAsync(service, repository, "f.txt", staged: false, ct);

        await service.ApplySelectionAsync(repository.Path, Hunk(diff, "f.txt", GitPatchAction.Stage, 0), ct);

        Assert.Contains("--- a/f.txt\n+++ b/f.txt\n", diff, StringComparison.Ordinal);
        Assert.Equal("a\nb  \nc\t\n", await IndexTextAsync(service, repository, "f.txt", ct));
    }

    [Fact]
    public async Task Amend_replaces_the_message_without_staged_changes()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitService service = new(options: repository.ProcessOptions);

        await service.AmendAsync(repository.Path, "Größe geändert\n\nZweite Zeile", ct);

        Assert.Equal("Größe geändert\n\nZweite Zeile", repository.RunGit("log", "-1", "--format=%B").TrimEnd('\n'));
        Assert.Equal("1", repository.RunGit("rev-list", "--count", "HEAD").Trim());
    }

    [Fact]
    public async Task Amend_without_a_commit_fails()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        GitService service = new(options: repository.ProcessOptions);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.AmendAsync(repository.Path, "message", ct));
    }

    private static GitTestRepository CreateWithFile(string path, string content)
    {
        GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, path, content, "init");
        return repository;
    }

    private static async Task<string> ShownDiffAsync(GitService service, GitTestRepository repository, string path, bool staged, CancellationToken ct)
    {
        GitDiff diff = await service.GetDiffAsync(repository.Path, new GitDiffRequest { Path = path, Staged = staged }, ct);
        return diff.RawText!;
    }

    private static async Task<string> IndexTextAsync(GitService service, GitTestRepository repository, string path, CancellationToken ct)
    {
        GitFileContent? content = await service.GetFileContentAsync(repository.Path, path, GitFileRevision.Index, 1024 * 1024, ct);
        return Encoding.UTF8.GetString(content!.Bytes);
    }

    /// <summary>Selects all lines of hunk <paramref name="hunk"/>, as the panel does without a selection.</summary>
    private static GitDiffSelection Hunk(string diff, string path, GitPatchAction action, int hunk)
    {
        string[] lines = diff.TrimEnd('\n').Split('\n');
        int[] starts = Enumerable.Range(0, lines.Length).Where(i => lines[i].StartsWith("@@", StringComparison.Ordinal)).ToArray();
        int end = hunk + 1 < starts.Length ? starts[hunk + 1] : lines.Length;
        return new GitDiffSelection(path, action, diff, starts[hunk], end - starts[hunk]);
    }

    private static GitDiffSelection Lines(string diff, string path, GitPatchAction action, string firstLine, int count) =>
        new(path, action, diff, Array.IndexOf(diff.Split('\n'), firstLine), count);
}
