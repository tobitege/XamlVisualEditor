using System.Reactive.Concurrency;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Tests.Unit.Tfvc;
using XamlVisualEditor.Workspace;
using Xunit;
using FakeEditorServices = XamlVisualEditor.Tests.Unit.Tfvc.FakeEditorServices;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitConflictMarkersTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task The_blocks_of_an_open_conflicted_file_are_marked_from_the_buffer_until_it_is_resolved()
    {
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        using Fixture fixture = await Fixture.CreateAsync(repository);
        FakeEditorDocument content = fixture.Open("content.txt");
        FakeEditorDocument local = fixture.Open("local.txt");

        Assert.Equal([new TextEditorLineMark(2, 5, TextEditorLineMarkKind.Conflict)], await fixture.NextMarksAsync(content));
        Assert.Empty(fixture.Registry.Find(local.FilePath, out _));

        content.Edit("x\n" + content.Text);
        Assert.Equal([new TextEditorLineMark(3, 5, TextEditorLineMarkKind.Conflict)], await fixture.NextMarksAsync(content));

        content.Edit("a\nresolved\nc\n");
        Assert.Empty(await fixture.NextMarksAsync(content));
    }

    [Fact]
    public async Task A_file_that_is_no_longer_conflicted_or_closed_loses_its_marks()
    {
        using GitTestRepository repository = GitTestRepository.CreateConflict();
        using Fixture fixture = await Fixture.CreateAsync(repository);
        FakeEditorDocument content = fixture.Open("content.txt");
        Assert.NotEmpty(await fixture.NextMarksAsync(content));
        FakeEditorDocument added = fixture.Open("added.txt");
        Assert.NotEmpty(await fixture.NextMarksAsync(added));

        Task cleared = fixture.NextChangeAsync(content.FilePath);
        repository.RunGit("add", "content.txt");
        await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
        await cleared.WaitAsync(WaitLimit);
        Assert.Empty(fixture.Registry.Find(content.FilePath, out _));

        Task closed = fixture.NextChangeAsync(added.FilePath);
        fixture.Editor.Close(added);
        await closed.WaitAsync(WaitLimit);
        Assert.Empty(fixture.Registry.Find(added.FilePath, out _));
    }

    [Fact]
    public void Blocks_become_conflict_marks_over_all_their_lines()
    {
        IReadOnlyList<GitConflictBlock> blocks = GitConflictParser.Parse("<<<<<<< a\nx\n=======\ny\n>>>>>>> b\n");

        Assert.Equal([new TextEditorLineMark(1, 5, TextEditorLineMarkKind.Conflict)], GitConflictMarkers.ToMarks(blocks));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly GitTestRepository _repository;

        private Fixture(GitTestRepository repository)
        {
            _repository = repository;
            Session = new GitSession(
                GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
                new FakeWorkspaceInfo { WorkspacePath = repository.Path });
            Markers = new GitConflictMarkers(Session, Editor, new LineMarkHost(Registry), scheduler: Scheduler);
        }

        public GitSession Session { get; }

        public HistoricalScheduler Scheduler { get; } = new();

        public FakeEditorServices Editor { get; } = new();

        public TextEditorLineMarkRegistry Registry { get; } = new();

        public GitConflictMarkers Markers { get; }

        public static async Task<Fixture> CreateAsync(GitTestRepository repository)
        {
            Fixture fixture = new(repository);
            await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
            return fixture;
        }

        public FakeEditorDocument Open(string relativePath)
        {
            FakeEditorDocument document = new(Path.Combine(_repository.Path, relativePath), _repository.ReadFile(relativePath)!);
            Editor.Activate(document);
            return document;
        }

        // Ends the quiet time and returns the marks that the read of the buffer sets.
        public async Task<IReadOnlyList<TextEditorLineMark>> NextMarksAsync(FakeEditorDocument document)
        {
            Task set = NextChangeAsync(document.FilePath);
            Scheduler.AdvanceBy(GitFileDiffSource.Delay);
            await set.WaitAsync(WaitLimit);
            return Registry.Find(document.FilePath, out _);
        }

        public Task NextChangeAsync(string path)
        {
            TaskCompletionSource set = new(TaskCreationOptions.RunContinuationsAsynchronously);
            EventHandler<string>? handler = null;
            handler = (_, changed) =>
            {
                if (FileSystemPathComparison.Equals(changed, path))
                {
                    Registry.Changed -= handler;
                    set.TrySetResult();
                }
            };
            Registry.Changed += handler;
            return set.Task;
        }

        public void Dispose()
        {
            Markers.Dispose();
            Session.Dispose();
        }
    }

    private sealed class LineMarkHost(TextEditorLineMarkRegistry registry) : ITextEditorViewHost
    {
        public TextEditorViewOptions Options { get; } = new();

        public bool HasActiveEditor => false;

        public event EventHandler? OptionsChanged
        {
            add { }
            remove { }
        }

        public void SetOptions(TextEditorViewOptions options)
        {
        }

        public Task<bool> ExecuteAsync(TextEditorViewAction action, CancellationToken cancellationToken) => Task.FromResult(false);

        public ITextEditorLineMarkLayer? CreateLineMarkLayer(string? clickCommandId = null) => registry.CreateLayer(clickCommandId);
    }
}
