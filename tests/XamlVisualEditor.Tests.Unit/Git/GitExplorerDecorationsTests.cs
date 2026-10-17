using System;
using System.IO;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
using TestExtensionStorage = XamlVisualEditor.Tests.Unit.Tfvc.InMemoryExtensionStorage;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitExplorerDecorationsTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private static readonly ExplorerDecoration Folder = new(ExplorerDecorationKind.Modified);

    [Fact]
    public async Task Every_change_gets_its_letter_and_every_folder_above_it_the_colour_Modified()
    {
        using GitTestRepository repository = GitTestRepository.Create(
            "src/Views/Main.axaml", "src/Gone.cs", "src/Old.cs", "docs/readme.md", "Größe ä.xaml");
        repository.WriteFile("src/Views/Main.axaml", "changed\n");
        repository.WriteFile("src/Added.cs", "added\n");
        repository.RunGit("add", "--", "src/Added.cs");
        repository.RunGit("mv", "src/Old.cs", "src/New.cs");
        File.Delete(Path.Combine(repository.Path, "src", "Gone.cs"));
        repository.WriteFile("Größe ä.xaml", "changed\n");
        repository.WriteFile("notes/new/a.txt", "untracked\n");
        repository.WriteFile("src/Views/loose.txt", "untracked\n");
        ExplorerDecorationRegistry registry = new();
        using GitSession session = CreateSession(repository, out TestExtensionStorage storage);
        using GitExplorerDecorations decorations = new(session, registry, new GitSettingsStore(storage));

        await session.RefreshAsync().WaitAsync(WaitLimit);

        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Modified, "M", GitStrings.ChangeModified), Find("src/Views/Main.axaml"));
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Added, "A", GitStrings.ChangeAdded), Find("src/Added.cs"));
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Added, "R", GitStrings.ChangeRenamed), Find("src/New.cs"));
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Deleted, "D", GitStrings.ChangeDeleted), Find("src/Gone.cs"));
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Modified, "M", GitStrings.ChangeModified), Find("Größe ä.xaml"));
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Added, "U", GitStrings.ChangeUntracked), Find("src/Views/loose.txt"));
        // git reports a new folder as one entry; the files inside it stay undecorated.
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Added, "U", GitStrings.ChangeUntracked), Find("notes"));
        Assert.Null(Find("notes/new"));
        Assert.Null(Find("notes/new/a.txt"));
        Assert.Equal(Folder, Find("src/Views"));
        Assert.Equal(Folder, Find("src"));
        Assert.Equal(Folder, registry.Find(repository.Path));
        Assert.Null(Find("docs"));
        Assert.Null(Find("docs/readme.md"));

        ExplorerDecoration? Find(string relativePath) =>
            registry.Find(Path.Combine(repository.Path, relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    [Fact]
    public async Task A_conflict_colours_every_folder_above_it_Conflicted()
    {
        using GitTestRepository repository = GitTestRepository.Create("dir/sub/c.txt", "other/b.txt");
        repository.RunGit("switch", "-q", "-c", "feature");
        repository.CommitFileIn(repository.Path, "dir/sub/c.txt", "feature\n", "feature");
        repository.RunGit("switch", "-q", "main");
        repository.CommitFileIn(repository.Path, "dir/sub/c.txt", "main\n", "main");
        Assert.False(repository.TryRunGit(out _, "merge", "-q", "feature"));
        repository.WriteFile("other/b.txt", "changed\n");
        ExplorerDecorationRegistry registry = new();
        using GitSession session = CreateSession(repository, out _);
        using GitExplorerDecorations decorations = new(session, registry);

        await session.RefreshAsync().WaitAsync(WaitLimit);

        ExplorerDecoration conflictFolder = new(ExplorerDecorationKind.Conflicted);
        Assert.Equal(new ExplorerDecoration(ExplorerDecorationKind.Conflicted, "!", GitStrings.ChangeConflicted),
            registry.Find(Path.Combine(repository.Path, "dir", "sub", "c.txt")));
        Assert.Equal(conflictFolder, registry.Find(Path.Combine(repository.Path, "dir", "sub")));
        Assert.Equal(conflictFolder, registry.Find(Path.Combine(repository.Path, "dir")));
        Assert.Equal(conflictFolder, registry.Find(repository.Path));
        Assert.Equal(Folder, registry.Find(Path.Combine(repository.Path, "other")));
    }

    [Fact]
    public async Task The_switch_clears_and_restores_the_decorations_and_is_saved()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("a.txt", "changed\n");
        string file = Path.Combine(repository.Path, "a.txt");
        ExplorerDecorationRegistry registry = new();
        ContextKeyRegistry contextKeys = new();
        using GitSession session = CreateSession(repository, out TestExtensionStorage storage);
        using (GitExplorerDecorations decorations = new(session, registry, new GitSettingsStore(storage), contextKeys))
        {
            await session.RefreshAsync().WaitAsync(WaitLimit);
            Assert.NotNull(registry.Find(file));
            Assert.True(contextKeys.TryGet(GitSettingsStore.EXPLORER_DECORATIONS_KEY, out bool shown) && shown);

            await decorations.ToggleAsync();

            Assert.False(decorations.IsEnabled);
            Assert.Null(registry.Find(file));
            Assert.True(contextKeys.TryGet(GitSettingsStore.EXPLORER_DECORATIONS_KEY, out shown));
            Assert.False(shown);
        }

        // A new activation reads the saved switch; disposing removed the layer of the first one.
        using GitExplorerDecorations restarted = new(session, registry, new GitSettingsStore(storage), contextKeys);
        Assert.NotNull(registry.Find(file));
        await restarted.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(restarted.IsEnabled);
        Assert.Null(registry.Find(file));

        await restarted.ToggleAsync();
        Assert.NotNull(registry.Find(file));
    }

    [Fact]
    public async Task A_workspace_outside_a_repository_has_no_decorations()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("a.txt", "changed\n");
        string outside = Path.Combine(Path.GetTempPath(), "xve-no-repo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outside);
        try
        {
            FakeWorkspaceInfo workspace = new() { WorkspacePath = repository.Path };
            ExplorerDecorationRegistry registry = new();
            using GitSession session = new(
                GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)), workspace);
            using GitExplorerDecorations decorations = new(session, registry);
            await session.RefreshAsync().WaitAsync(WaitLimit);
            Assert.NotNull(registry.Find(Path.Combine(repository.Path, "a.txt")));

            workspace.WorkspacePath = outside;
            await session.RefreshAsync().WaitAsync(WaitLimit);

            Assert.Equal(GitSessionState.NotRepository, session.Current.State);
            Assert.Null(registry.Find(Path.Combine(repository.Path, "a.txt")));
            Assert.Null(registry.Find(repository.Path));
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    private static GitSession CreateSession(GitTestRepository repository, out TestExtensionStorage storage)
    {
        storage = new TestExtensionStorage();
        return new GitSession(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new FakeWorkspaceInfo { WorkspacePath = repository.Path },
            new GitSettingsStore(storage));
    }
}
