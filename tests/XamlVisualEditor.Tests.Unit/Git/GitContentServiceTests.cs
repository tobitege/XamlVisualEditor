using System.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitContentServiceTests
{
    [Fact]
    public async Task File_content_comes_from_the_index_or_head_and_is_null_without_such_a_file()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("Größe ä.txt");
        repository.WriteFile("Größe ä.txt", "staged\n");
        repository.RunGit("add", "--", "Größe ä.txt");
        repository.WriteFile("Größe ä.txt", "working\n");
        repository.WriteFile("untracked.txt", "new\n");
        GitService service = new(options: repository.ProcessOptions);

        GitFileContent? index = await service.GetFileContentAsync(repository.Path, "Größe ä.txt", GitFileRevision.Index, 1024, ct);
        GitFileContent? head = await service.GetFileContentAsync(repository.Path, "Größe ä.txt", GitFileRevision.Head, 1024, ct);

        Assert.Equal("staged\n", Encoding.UTF8.GetString(index!.Bytes));
        Assert.False(index.IsTruncated);
        Assert.Equal("Größe ä.txt\n", Encoding.UTF8.GetString(head!.Bytes));
        Assert.Null(await service.GetFileContentAsync(repository.Path, "untracked.txt", GitFileRevision.Index, 1024, ct));
        Assert.Null(await service.GetFileContentAsync(repository.Path, "missing.txt", GitFileRevision.Head, 1024, ct));
    }

    [Fact]
    public async Task File_content_longer_than_the_limit_is_truncated()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "long.txt", string.Concat(Enumerable.Repeat("0123456789\n", 100)), "long");
        GitService service = new(options: repository.ProcessOptions);

        GitFileContent? content = await service.GetFileContentAsync(repository.Path, "long.txt", GitFileRevision.Head, 50, ct);

        Assert.True(content!.IsTruncated);
        Assert.True(content.Bytes.Length <= 50);
    }

    [Fact]
    public async Task Blame_names_the_authors_of_the_lines_and_marks_saved_uncommitted_lines()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create();
        repository.WriteFile("a.txt", "one\ntwo\n");
        repository.RunGit("add", "--", "a.txt");
        repository.RunGit("-c", "user.name=Anna", "commit", "-q", "-m", "First");
        repository.WriteFile("a.txt", "one\ntwo\nGrüße\n");
        repository.RunGit("-c", "user.name=Ben", "commit", "-q", "-am", "Second");
        repository.WriteFile("a.txt", "one\nchanged\nGrüße\n");
        GitService service = new(options: repository.ProcessOptions);

        GitBlame blame = await service.GetBlameAsync(repository.Path, "a.txt", 1024 * 1024, ct);

        Assert.Equal(["one", "changed", "Grüße"], blame.Lines);
        Assert.Equal(
            [(1, "Anna", false), (2, null, true), (3, "Ben", false)],
            blame.Hunks.Select(hunk => (hunk.StartLine, hunk.Commit.IsUncommitted ? null : hunk.Commit.AuthorName, hunk.Commit.IsUncommitted)));
        Assert.Equal("Second", blame.Hunks[2].Commit.Summary);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetBlameAsync(repository.Path, "missing.txt", 1024, ct));
    }
}
