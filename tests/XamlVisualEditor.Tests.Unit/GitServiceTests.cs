using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class GitServiceTests
{
    [Fact]
    public async Task Discard_keeps_a_staged_new_file_and_restores_its_staged_content()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("tracked.txt");

        string newFile = repository.WriteFile("new.txt", "staged\n");
        repository.RunGit("add", "--", "new.txt");
        File.WriteAllText(newFile, "staged\nedited\n");

        GitService service = new(options: repository.ProcessOptions);
        GitRepositoryStatus before = await service.GetStatusAsync(repository.Path, ct);
        GitFileChange added = Assert.Single(before.Changes, change => change.Path == "new.txt");
        Assert.Equal(GitChangeKind.Added, added.IndexStatus);
        Assert.Equal(GitChangeKind.Modified, added.WorkTreeStatus);

        await service.DiscardAsync(repository.Path, new[] { "new.txt" }, ct);

        Assert.True(File.Exists(newFile));
        Assert.Equal("staged\n", File.ReadAllText(newFile));
        GitRepositoryStatus after = await service.GetStatusAsync(repository.Path, ct);
        GitFileChange staged = Assert.Single(after.Changes, change => change.Path == "new.txt");
        Assert.Equal(GitChangeKind.Added, staged.IndexStatus);
        Assert.Equal(GitChangeKind.None, staged.WorkTreeStatus);
    }

    [Fact]
    public async Task Discard_treats_glob_characters_in_a_path_literally()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("Item[1].xaml", "Item1.xaml");
        string bracketFile = repository.WriteFile("Item[1].xaml", "edited\n");
        string plainFile = repository.WriteFile("Item1.xaml", "edited\n");

        await new GitService(options: repository.ProcessOptions).DiscardAsync(repository.Path, new[] { "Item[1].xaml" }, ct);

        Assert.Equal("Item[1].xaml\n", File.ReadAllText(bracketFile));
        Assert.Equal("edited\n", File.ReadAllText(plainFile));
    }

    [Fact]
    public async Task Failing_git_commands_are_reported_to_the_caller()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("tracked.txt");
        GitService service = new(options: repository.ProcessOptions);

        InvalidOperationException discard = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.DiscardAsync(repository.Path, new[] { "missing.xaml" }, ct));
        Assert.Contains("missing.xaml", discard.Message);
        InvalidOperationException stage = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StageAsync(repository.Path, new[] { "missing.xaml" }, ct));
        Assert.Contains("missing.xaml", stage.Message);
    }
}
