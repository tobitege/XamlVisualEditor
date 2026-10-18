using System.ComponentModel;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using XamlVisualEditor.GitExtension.Views;
using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class GitBlameDocumentViewModelTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [AvaloniaFact]
    public async Task Rows_show_the_commit_on_the_first_line_of_a_hunk_and_open_committed_lines_in_the_history()
    {
        using GitTestRepository repository = GitTestRepository.Create();
        repository.CommitFileIn(repository.Path, "a.txt", "one\ntwo\n", "First");
        repository.WriteFile("a.txt", "one\ntwo\nthree\nfour\n");
        repository.RunGit("-c", "user.name=Ben", "commit", "-q", "-am", "Second");
        repository.WriteFile("a.txt", "one\ntwo\nthree\nvier\n");
        string first = repository.RunGit("rev-parse", "HEAD~1").Trim();
        string second = repository.RunGit("rev-parse", "HEAD").Trim();
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new TestWorkspaceInfo(repository.Path));
        List<(string CommandId, IReadOnlyList<object?>? Arguments)> executed = new();
        await session.RefreshAsync().WaitAsync(WaitLimit);
        using GitBlameDocumentViewModel blame = new(session, "a.txt", (commandId, arguments) =>
        {
            executed.Add((commandId, arguments));
            return Task.CompletedTask;
        });
        await NextRowsAsync(blame).WaitAsync(WaitLimit);

        Assert.Null(blame.Message);
        Assert.Equal(["one", "two", "three", "vier"], blame.Rows.Select(row => row.Text));
        Assert.Equal([1, 2, 3, 4], blame.Rows.Select(row => row.LineNumber));
        Assert.Equal([first[..8], "", second[..8], GitStrings.Uncommitted], blame.Rows.Select(row => row.CommitText));
        Assert.Equal(["Test", "", "Ben", ""], blame.Rows.Select(row => row.AuthorText));
        Assert.Equal("Second", blame.Rows[2].Summary);

        blame.SelectedRow = blame.Rows[1];
        await blame.OpenCommitCommand.Execute().ToTask();
        Assert.Equal([(GitCommands.SHOW_HISTORY, (object?)first)], executed.Select(entry => (entry.CommandId, entry.Arguments![0])));

        blame.SelectedRow = blame.Rows[3];
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.False(((System.Windows.Input.ICommand)blame.OpenCommitCommand).CanExecute(null));
    }

    [AvaloniaFact]
    public void The_commit_column_fits_an_id_and_the_note_of_uncommitted_lines_in_english_and_german()
    {
        GitBlameCommit uncommitted = new(new string('0', 40), "Not Committed Yet", DateTimeOffset.UnixEpoch, string.Empty);
        GitBlameRowViewModel row = new(1, "text", uncommitted, isHunkStart: true);
        GitBlameDocumentView view = new();
        using ThemedWindow window = ThemedWindow.Show(view, 800, 200);
        ListBox lines = view.FindControl<ListBox>("Lines")!;
        lines.ItemsSource = new[] { row };
        window.Pump();

        TextBlock commit = lines.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == row.CommitText);
        Assert.Contains(GitBlameRowViewModel.CommitTextSamples, sample => sample == "Not committed");
        Assert.Contains(GitBlameRowViewModel.CommitTextSamples, sample => sample == "Nicht committet");
        foreach (string sample in GitBlameRowViewModel.CommitTextSamples)
        {
            TextBlock probe = new() { Text = sample, FontFamily = commit.FontFamily, FontSize = commit.FontSize };
            probe.Measure(Size.Infinity);
            TestContext.Current.TestOutputHelper?.WriteLine($"commit column {commit.Bounds.Width:F1}, \"{sample}\" {probe.DesiredSize.Width:F1}");
            Assert.True(commit.Bounds.Width >= probe.DesiredSize.Width - 0.01, $"{sample}: {probe.DesiredSize.Width} > {commit.Bounds.Width}");
        }
    }

    [AvaloniaFact]
    public async Task A_file_git_cannot_blame_shows_the_failure()
    {
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("new.txt", "new\n");
        using GitSession session = new(
            GitServiceSet.FromService(new GitService(options: repository.ProcessOptions)),
            new TestWorkspaceInfo(repository.Path));
        await session.RefreshAsync().WaitAsync(WaitLimit);
        using GitBlameDocumentViewModel blame = new(session, "new.txt", null);

        await NextRowsAsync(blame).WaitAsync(WaitLimit);

        Assert.Empty(blame.Rows);
        Assert.StartsWith(string.Format(System.Globalization.CultureInfo.CurrentCulture, GitStrings.LoadBlameFailedFormat, string.Empty), blame.Message);
    }

    // Waits for the read that follows the loading note.
    private static Task NextRowsAsync(GitBlameDocumentViewModel blame)
    {
        TaskCompletionSource read = new(TaskCreationOptions.RunContinuationsAsynchronously);
        PropertyChangedEventHandler? handler = null;
        handler = (_, e) =>
        {
            if (e.PropertyName == nameof(GitBlameDocumentViewModel.Message) && blame.Message != GitStrings.BlameLoading)
            {
                blame.PropertyChanged -= handler;
                read.TrySetResult();
            }
        };
        blame.PropertyChanged += handler;
        return read.Task;
    }
}
