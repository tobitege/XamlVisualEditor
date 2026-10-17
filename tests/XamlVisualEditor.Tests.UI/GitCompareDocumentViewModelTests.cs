using System.ComponentModel;
using System.Globalization;
using System.Reactive.Concurrency;
using System.Reactive.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.SourceControl.UI.Models;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitCompareDocumentViewModelTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);
    private const string COMMITTED = "1\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n";
    private const string STAGED = "1\nzwei\n3\n4\n5\n6\n7\n8\n9\n10\n11\n12\n";

    [AvaloniaFact]
    public async Task Head_and_index_versions_give_one_hunk_per_change_and_the_whole_file_keeps_them()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        TestDocument document = fixture.Open("1\nzwei\n3\n4\n5\n6\n7\n8\n9\n10\nelf\n12\n");

        using GitCompareDocumentViewModel head = fixture.Compare(GitFileRevision.Head);
        using GitCompareDocumentViewModel index = fixture.Compare(GitFileRevision.Index);
        await fixture.NextDiffAsync(head, index);

        Assert.Equal(2, head.HunkCount);
        Assert.Equal(1, index.HunkCount);
        Assert.Null(head.Message);
        Assert.Equal(["-11", "+elf"], index.DiffLines.Where(line => line.Kind is DiffLineKind.Added or DiffLineKind.Removed).Select(line => line.Text));

        head.IsWholeFile = true;
        Assert.Equal(2, head.HunkCount);
        Assert.Equal(2, head.DiffLines.Count(line => line.Kind == DiffLineKind.HunkHeader));
        Assert.Equal(12, head.DiffLines.Count(line => line.NewLineNumber is not null));

        head.ShowLine(11);
        Assert.Equal(1, head.CurrentHunkIndex);
        head.ShowLine(1);
        Assert.Equal(0, head.CurrentHunkIndex);
        await head.NextChangeCommand.Execute().ToTask();
        Assert.Equal(1, head.CurrentHunkIndex);

        // A change of the buffer arrives after the quiet time and keeps the shown hunk.
        document.Edit("1\nzwei\n3\n4\n5\n6\n7\n8\n9\n10\nelf\n12\n13\n");
        await fixture.NextDiffAsync(head);
        Assert.Equal(3, head.HunkCount);
        Assert.Equal(1, head.CurrentHunkIndex);
        Assert.Contains(head.DiffLines, line => line.Text == "+13");
    }

    [AvaloniaFact]
    public async Task A_line_asked_for_before_the_first_diff_is_shown_when_it_arrives()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Open("one\n2\n3\n4\n5\n6\n7\n8\n9\n10\n11\ntwelve\n");
        using GitCompareDocumentViewModel compare = fixture.Compare(GitFileRevision.Head);

        compare.ShowLine(12);
        await fixture.NextDiffAsync(compare);

        Assert.Equal(1, compare.CurrentHunkIndex);
    }

    [AvaloniaFact]
    public async Task A_file_without_version_and_a_file_without_changes_show_a_note()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        string untracked = fixture.Repository.WriteFile("new.txt", "new\n");
        using GitCompareDocumentViewModel noVersion = fixture.Compare(GitFileRevision.Head, untracked);
        // A closed file is read from disk.
        using GitCompareDocumentViewModel unchanged = fixture.Compare(GitFileRevision.Index);

        await fixture.NextDiffAsync(noVersion, unchanged);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareNoVersionFormat, "HEAD"), noVersion.Message);
        Assert.Empty(noVersion.DiffLines);
        Assert.Equal(GitStrings.CompareNoChanges, unchanged.Message);
        Assert.Equal(0, unchanged.HunkCount);
    }

    [AvaloniaFact]
    public async Task A_change_of_only_the_final_newline_or_the_line_endings_is_named_instead_of_no_changes()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        fixture.Repository.CommitFileIn(fixture.Repository.Path, "b.txt", "one\ntwo\n", "b");
        fixture.Repository.CommitFileIn(fixture.Repository.Path, "c.txt", "one\ntwo\n", "c");
        string newline = fixture.Repository.WriteFile("b.txt", "one\ntwo");
        string endings = fixture.Repository.WriteFile("c.txt", "one\r\ntwo\r\n");
        await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
        using GitCompareDocumentViewModel newlineIndex = fixture.Compare(GitFileRevision.Index, newline);
        using GitCompareDocumentViewModel endingsHead = fixture.Compare(GitFileRevision.Head, endings);

        await fixture.NextDiffAsync(newlineIndex, endingsHead);

        Assert.Equal(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareOnlyIgnoredBytesFormat, GitStrings.IndexName), newlineIndex.Message);
        Assert.Equal(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareOnlyIgnoredBytesFormat, "HEAD"), endingsHead.Message);
        Assert.Equal(0, endingsHead.HunkCount);
    }

    [AvaloniaFact]
    public async Task Every_button_of_the_compare_and_blame_views_shows_an_icon_and_a_tooltip()
    {
        using Fixture fixture = await Fixture.CreateAsync();
        using GitCompareDocumentViewModel compare = fixture.Compare(GitFileRevision.Head);
        using GitBlameDocumentViewModel blame = new(fixture.Session, "a.txt", null);
        Window window = new() { Content = new StackPanel { Children = { new GitCompareDocumentView { DataContext = compare }, new GitBlameDocumentView { DataContext = blame } } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Button[] buttons = window.GetLogicalDescendants().OfType<Button>().ToArray();
        // Previous, next, whole file and compare again; read again of the blame.
        Assert.Equal(5, buttons.Length);
        Assert.All(buttons, button =>
        {
            Assert.NotEmpty(button.GetLogicalDescendants().OfType<PathIcon>());
            Assert.False(string.IsNullOrEmpty(ToolTip.GetTip(button) as string));
        });
        window.Close();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly HistoricalScheduler _scheduler = new();

        private Fixture(GitTestRepository repository)
        {
            Repository = repository;
            Session = new GitSession(
                GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
                new TestWorkspaceInfo(repository.Path));
        }

        public GitTestRepository Repository { get; }

        public GitSession Session { get; }

        public TestEditorServices Editor { get; } = new();

        public string FilePath => Path.Combine(Repository.Path, "a.txt");

        public static async Task<Fixture> CreateAsync()
        {
            GitTestRepository repository = GitTestRepository.Create();
            repository.CommitFileIn(repository.Path, "a.txt", COMMITTED, "init");
            repository.WriteFile("a.txt", STAGED);
            repository.RunGit("add", "--", "a.txt");
            Fixture fixture = new(repository);
            await fixture.Session.RefreshAsync().WaitAsync(WaitLimit);
            return fixture;
        }

        public TestDocument Open(string text)
        {
            TestDocument document = new(FilePath, text);
            Editor.Open(document);
            return document;
        }

        public GitCompareDocumentViewModel Compare(GitFileRevision revision, string? path = null) =>
            new(Session, Editor, path ?? FilePath, revision, _scheduler);

        // Ends the quiet time and waits until every view model shows its new diff.
        public async Task NextDiffAsync(params GitCompareDocumentViewModel[] compares)
        {
            Task[] diffs = compares.Select(NextDiffLinesAsync).ToArray();
            _scheduler.AdvanceBy(GitFileDiffSource.Delay);
            await Task.WhenAll(diffs).WaitAsync(WaitLimit);
        }

        public void Dispose()
        {
            Session.Dispose();
            Repository.Dispose();
        }

        // A diff sets the note or the lines, or both; the waiting test continues after the whole diff was applied.
        private static Task NextDiffLinesAsync(GitCompareDocumentViewModel compare)
        {
            TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            PropertyChangedEventHandler? handler = null;
            handler = (_, e) =>
            {
                if (e.PropertyName is nameof(GitCompareDocumentViewModel.DiffLines) or nameof(GitCompareDocumentViewModel.Message))
                {
                    compare.PropertyChanged -= handler;
                    changed.TrySetResult();
                }
            };
            compare.PropertyChanged += handler;
            return changed.Task;
        }
    }
}

internal sealed class TestWorkspaceInfo(string path) : IWorkspaceInfo
{
    public string? WorkspacePath => path;

    public event EventHandler<WorkspaceChangedEventArgs>? WorkspaceChanged
    {
        add { }
        remove { }
    }
}

internal sealed class TestEditorServices : IEditorServices
{
    private readonly List<IEditorDocument> _documents = new();

    public IEditorDocument? ActiveDocument { get; private set; }

    public event EventHandler<EditorActiveDocumentChangedEventArgs>? ActiveDocumentChanged;

    public void Open(IEditorDocument document)
    {
        _documents.Add(document);
        ActiveDocument = document;
        ActiveDocumentChanged?.Invoke(this, new EditorActiveDocumentChangedEventArgs { Document = document });
    }

    public IReadOnlyList<IEditorDocument> GetOpenDocuments() => _documents.ToArray();

    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, CancellationToken ct) => Task.FromResult<IEditorDocument?>(null);

    public Task<IEditorDocument?> OpenDocumentAsync(string filePath, EditorDocumentOpenBehavior behavior, CancellationToken ct) =>
        Task.FromResult<IEditorDocument?>(null);

    public Task<bool> OpenLocationAsync(LanguageLocation location, CancellationToken ct) => Task.FromResult(false);
}

internal sealed class TestDocument(string filePath, string text) : IEditorDocument
{
    public string FilePath { get; } = filePath;

    public string? LanguageId => null;

    public string Text { get; private set; } = text;

    public int CaretOffset { get; set; }

    public int SelectionStart { get; set; }

    public int SelectionLength { get; set; }

    public event EventHandler<EditorDocumentChangedEventArgs>? Changed;

#pragma warning disable CS0067
    public event EventHandler<EditorSelectionChangedEventArgs>? SelectionChanged;
#pragma warning restore CS0067

    public Task<string> GetTextAsync(CancellationToken ct) => Task.FromResult(Text);

    public Task ApplyEditsAsync(IReadOnlyList<TextEdit> edits, CancellationToken ct) => Task.CompletedTask;

    public void Edit(string text)
    {
        Text = text;
        Changed?.Invoke(this, new EditorDocumentChangedEventArgs { FilePath = FilePath });
    }
}
