using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.SourceControl.UI.Controls;
using XamlVisualEditor.SourceControl.UI.Models;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.GitExtension;

/// <summary>A file of the commit shown in the commit details.</summary>
public sealed class GitCommitFileRowViewModel
{
    /// <summary>Creates the row.</summary>
    public GitCommitFileRowViewModel(GitCommitFileChange change)
    {
        Change = change;
        ChangeText = FormatKind(change.Kind);
        DisplayPath = change.OldPath is { } oldPath && !string.Equals(oldPath, change.Path, StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.RenamedFromFormat, change.Path, oldPath)
            : change.Path;
    }

    /// <summary>Gets the changed file.</summary>
    public GitCommitFileChange Change { get; }

    /// <summary>Gets the repository-relative path after the commit.</summary>
    public string Path => Change.Path;

    /// <summary>Gets the path, with the old path of a renamed or copied file.</summary>
    public string DisplayPath { get; }

    /// <summary>Gets the kind of the change.</summary>
    public string ChangeText { get; }

    private static string FormatKind(GitChangeKind kind) => kind switch
    {
        GitChangeKind.Added => GitStrings.ChangeAdded,
        GitChangeKind.Modified => GitStrings.ChangeModified,
        GitChangeKind.Deleted => GitStrings.ChangeDeleted,
        GitChangeKind.Renamed => GitStrings.ChangeRenamed,
        GitChangeKind.Copied => GitStrings.ChangeCopied,
        GitChangeKind.TypeChanged => GitStrings.ChangeTypeChanged,
        GitChangeKind.Unmerged => GitStrings.ChangeUnmerged,
        _ => GitStrings.ChangeOther
    };
}

/// <summary>
/// Details of the commit selected in the Git History panel: message, author, committer, parents, the changed files
/// and the diff of the selected file. Merge commits are compared with their first parent.
/// </summary>
/// <remarks>
/// A newer commit or file cancels the reading of the previous one. The first file is selected when the files of a
/// commit arrive, so its diff shows at once.
/// </remarks>
public sealed partial class GitCommitDetailsViewModel : ReactiveObject, IDisposable
{
    /// <summary>
    /// The number of diff lines that are read and shown; reading stops after them, so a huge diff is never read
    /// completely.
    /// </summary>
    public const int MAX_DIFF_LINE_COUNT = UnifiedDiffEditor.DEFAULT_MAX_LINE_COUNT;

    /// <summary>
    /// The number of diff bytes that are read, so a diff of few but very long lines, such as a minified file, is never
    /// read completely either.
    /// </summary>
    public const int MAX_DIFF_BYTE_COUNT = 4 * 1024 * 1024;

    private readonly GitSession _session;
    private readonly Func<string, IReadOnlyList<object?>?, Task> _executeCommand;
    private readonly CompositeDisposable _disposables = new();
    private DetailsContext? _context;

    /// <summary>Creates the details for a shared session.</summary>
    /// <param name="session">The Git session whose repository the commits belong to.</param>
    /// <param name="executeCommand">Runs a Git command by id with arguments.</param>
    public GitCommitDetailsViewModel(GitSession session, Func<string, IReadOnlyList<object?>?, Task> executeCommand)
    {
        _session = session;
        _executeCommand = executeCommand;

        IObservable<bool> hasFile = this.WhenAnyValue(x => x.SelectedFile).Select(file => file is not null);
        // -1 means no hunk yet: the next change is the first hunk, and there is no previous one.
        PreviousHunkCommand = AddCommand(ReactiveCommand.Create(
            () => { CurrentHunkIndex = Math.Min(HunkCount - 1, CurrentHunkIndex - 1); },
            this.WhenAnyValue(x => x.CurrentHunkIndex, x => x.HunkCount, (index, count) => count > 0 && index > 0)));
        NextHunkCommand = AddCommand(ReactiveCommand.Create(
            () => { CurrentHunkIndex = Math.Max(0, CurrentHunkIndex + 1); },
            this.WhenAnyValue(x => x.CurrentHunkIndex, x => x.HunkCount, (index, count) => index < count - 1)));
        ToggleDiffCommand = AddCommand(ReactiveCommand.Create(() => { IsDiffVisible = !IsDiffVisible; }));
        CopyPathCommand = AddCommand(ReactiveCommand.CreateFromTask(CopyPathAsync, hasFile));
        ShowFileHistoryCommand = AddCommand(ReactiveCommand.CreateFromTask(ShowFileHistoryAsync, hasFile));

        _disposables.Add(Observable.Switch(this.WhenAnyValue(x => x.Commit)
                .DistinctUntilChanged(commit => commit?.Sha)
                .Select(LoadDetails))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyDetails));
        _disposables.Add(Observable.Switch(this.WhenAnyValue(x => x.SelectedFile).Select(LoadDiff))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyDiff));
    }

    /// <summary>Gets or sets the shown commit; null shows none.</summary>
    [Reactive]
    public partial GitCommitInfo? Commit { get; set; }

    /// <summary>Gets whether a commit is shown.</summary>
    [Reactive]
    public partial bool HasCommit { get; private set; }

    /// <summary>Gets the subject of the commit.</summary>
    [Reactive]
    public partial string? Title { get; private set; }

    /// <summary>Gets the line with the abbreviated id, the author and the author date.</summary>
    [Reactive]
    public partial string? Summary { get; private set; }

    /// <summary>Gets the committer and commit date when they differ from the author; otherwise null.</summary>
    [Reactive]
    public partial string? CommitterText { get; private set; }

    /// <summary>Gets the abbreviated ids of the parents; null for a root commit.</summary>
    [Reactive]
    public partial string? ParentsText { get; private set; }

    /// <summary>Gets the note that a merge commit is compared with its first parent; null for other commits.</summary>
    [Reactive]
    public partial string? MergeText { get; private set; }

    /// <summary>Gets the complete message.</summary>
    [Reactive]
    public partial string? MessageText { get; private set; }

    /// <summary>Gets the header of the file list with the number of files.</summary>
    [Reactive]
    public partial string? FilesHeader { get; private set; }

    /// <summary>Gets a reading error; null otherwise.</summary>
    [Reactive]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets whether the files of the commit are being read.</summary>
    [Reactive]
    public partial bool IsLoading { get; private set; }

    /// <summary>Gets or sets the file whose diff is shown.</summary>
    [Reactive]
    public partial GitCommitFileRowViewModel? SelectedFile { get; set; }

    /// <summary>Gets the lines of the diff of <see cref="SelectedFile"/>.</summary>
    [Reactive]
    public partial IReadOnlyList<DiffLine> DiffLines { get; private set; } = Array.Empty<DiffLine>();

    /// <summary>
    /// Gets whether git wrote more diff lines than <see cref="MAX_DIFF_LINE_COUNT"/> or more bytes than
    /// <see cref="MAX_DIFF_BYTE_COUNT"/>, which were not read.
    /// </summary>
    [Reactive]
    public partial bool IsDiffTruncated { get; private set; }

    /// <summary>Gets the notice below a diff that was cut at one of the limits; null for a complete diff.</summary>
    [Reactive]
    public partial string? DiffTruncationNotice { get; private set; }

    /// <summary>
    /// Gets or sets the number of hunks the diff editor shows; the view sets it from the editor's
    /// <see cref="UnifiedDiffEditor.HunkCount"/>, which counts only the shown lines.
    /// </summary>
    [Reactive]
    public partial int HunkCount { get; set; }

    /// <summary>
    /// Gets or sets the 0-based index of the hunk at the caret of the diff, or -1 when no hunk is selected yet, before
    /// the first hunk.
    /// </summary>
    [Reactive]
    public partial int CurrentHunkIndex { get; set; } = -1;

    /// <summary>Gets a diff reading error; null otherwise.</summary>
    [Reactive]
    public partial string? DiffMessage { get; private set; }

    /// <summary>Gets whether the diff pane is open.</summary>
    [Reactive]
    public partial bool IsDiffVisible { get; private set; } = true;

    /// <summary>Gets the files the commit changed.</summary>
    public ObservableCollection<GitCommitFileRowViewModel> Files { get; } = new();

    /// <summary>Gets the interaction that places a text on the clipboard; the view handles it.</summary>
    public Interaction<string, RxVoid> CopyToClipboard { get; } = new();

    /// <summary>Moves the caret of the diff to the previous hunk; enabled only when a hunk before the current one exists.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PreviousHunkCommand { get; }

    /// <summary>
    /// Moves the caret of the diff to the next hunk; enabled only when a hunk after the current one exists, which before
    /// the first hunk means any hunk.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> NextHunkCommand { get; }

    /// <summary>Opens or closes the diff pane.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ToggleDiffCommand { get; }

    /// <summary>Copies the repository-relative path of the selected file to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyPathCommand { get; }

    /// <summary>
    /// Runs <see cref="GitCommands.SHOW_FILE_HISTORY"/> for the selected file, starting at the shown commit, so the
    /// history follows that file even when a later commit put another file at its path.
    /// </summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowFileHistoryCommand { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposables.Dispose();
    }

    private ReactiveCommand<RxVoid, RxVoid> AddCommand(ReactiveCommand<RxVoid, RxVoid> command)
    {
        _disposables.Add(command);
        // Without a subscriber ReactiveUI ends the application when a command fails.
        _disposables.Add(command.ThrownExceptions.Subscribe(error =>
            StatusMessage = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, error.Message)));
        return command;
    }

    private async Task CopyPathAsync()
    {
        if (SelectedFile is { } file)
        {
            await CopyToClipboard.Handle(file.Path);
        }
    }

    private Task ShowFileHistoryAsync()
    {
        if (SelectedFile is not { } file || _context is not { } context)
        {
            return Task.CompletedTask;
        }

        string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(context.RepositoryRoot, file.Path));
        return _executeCommand(GitCommands.SHOW_FILE_HISTORY, [fullPath, context.Commit.Sha]);
    }

    /// <summary>Returns the details of <paramref name="commit"/>, read in the background after its header.</summary>
    private IObservable<DetailsLoad> LoadDetails(GitCommitInfo? commit)
    {
        if (commit is null)
        {
            return Observable.Return(DetailsLoad.None);
        }

        string? repositoryRoot = _session.Current.RepositoryRoot;
        IGitHistoryService? history = _session.Services.History;
        DetailsLoad header = new(commit, repositoryRoot, null, true, null);
        if (repositoryRoot is null || history is null)
        {
            return Observable.Return(DetailsLoad.Failed(
                    commit,
                    repositoryRoot,
                    history is null ? GitStrings.HistoryUnavailable : UiStrings.WorkspaceNotGitRepository))
                .StartWith(header);
        }

        return Observable.FromAsync(async ct =>
            {
                try
                {
                    GitCommitDetails details = await history.GetCommitAsync(repositoryRoot, commit, ct).ConfigureAwait(false);
                    return new DetailsLoad(commit, repositoryRoot, details.Files, false, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return DetailsLoad.Failed(
                        commit,
                        repositoryRoot,
                        string.Format(CultureInfo.CurrentCulture, GitStrings.LoadCommitFailedFormat, exception.Message));
                }
            })
            .StartWith(header);
    }

    private void ApplyDetails(DetailsLoad load)
    {
        IsLoading = load.IsLoading;
        if (load.Files is null && load.IsLoading)
        {
            ShowHeader(load.Commit);
            _context = load.Commit is null || load.RepositoryRoot is null ? null : new DetailsContext(load.Commit, load.RepositoryRoot);
            SelectedFile = null;
            Files.Clear();
            FilesHeader = string.Format(CultureInfo.CurrentCulture, GitStrings.CommitFilesFormat, 0);
            return;
        }

        if (load.Commit is null)
        {
            ShowHeader(null);
            _context = null;
            SelectedFile = null;
            Files.Clear();
            return;
        }

        StatusMessage = load.Message;
        foreach (GitCommitFileChange file in load.Files ?? Array.Empty<GitCommitFileChange>())
        {
            Files.Add(new GitCommitFileRowViewModel(file));
        }

        FilesHeader = string.Format(CultureInfo.CurrentCulture, GitStrings.CommitFilesFormat, Files.Count);
        SelectedFile = Files.Count > 0 ? Files[0] : null;
    }

    private void ShowHeader(GitCommitInfo? commit)
    {
        HasCommit = commit is not null;
        StatusMessage = null;
        if (commit is null)
        {
            Title = Summary = CommitterText = ParentsText = MergeText = MessageText = FilesHeader = null;
            return;
        }

        string authorDate = commit.AuthorDate.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        Title = commit.Subject;
        Summary = string.Format(
            CultureInfo.CurrentCulture,
            GitStrings.CommitSummaryFormat,
            commit.ShortSha,
            commit.AuthorName,
            commit.AuthorEmail,
            authorDate);
        CommitterText = string.Equals(commit.CommitterName, commit.AuthorName, StringComparison.Ordinal)
                        && commit.CommitterDate == commit.AuthorDate
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.CommitterFormat,
                commit.CommitterName,
                commit.CommitterDate.ToLocalTime().ToString("G", CultureInfo.CurrentCulture));
        ParentsText = commit.IsRoot
            ? null
            : string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.ParentsFormat,
                string.Join(", ", Enumerable.Range(0, commit.ParentShas.Count).Select(index => ShortParent(commit, index))));
        MergeText = commit.IsMerge
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.MergeComparisonFormat, ShortParent(commit, 0))
            : null;
        MessageText = commit.Message;
    }

    /// <summary>Returns the parent id as git abbreviated it, or the full id when the commit has no abbreviated ids.</summary>
    private static string ShortParent(GitCommitInfo commit, int index) =>
        index < commit.ParentShortShas.Count ? commit.ParentShortShas[index] : commit.ParentShas[index];

    /// <summary>Returns the diff lines of <paramref name="file"/>, read in the background.</summary>
    private IObservable<DiffLoad> LoadDiff(GitCommitFileRowViewModel? file)
    {
        if (file is null || _context is not { } context)
        {
            return Observable.Return(DiffLoad.Empty(null));
        }

        IGitHistoryService? history = _session.Services.History;
        if (history is null)
        {
            return Observable.Return(DiffLoad.Empty(GitStrings.HistoryUnavailable));
        }

        return Observable.FromAsync(async ct =>
        {
            try
            {
                GitDiff diff = await history
                    .GetCommitFileDiffAsync(context.RepositoryRoot, context.Commit, file.Change, MAX_DIFF_LINE_COUNT, MAX_DIFF_BYTE_COUNT, ct)
                    .ConfigureAwait(false);
                return new DiffLoad(DiffLineBuilder.FromGitDiff(diff), diff.IsTruncated, null);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return DiffLoad.Empty(string.Format(CultureInfo.CurrentCulture, GitStrings.LoadDiffFailedFormat, exception.Message));
            }
        });
    }

    private void ApplyDiff(DiffLoad load)
    {
        // Each of the three properties rebuilds the diff editor; the new lines come last, so it builds them once.
        IsDiffTruncated = load.IsTruncated;
        // The diff editor shows at most MAX_DIFF_LINE_COUNT lines as well.
        DiffTruncationNotice = load.IsTruncated
            ? string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.DiffTooLargeFormat,
                Math.Min(load.Lines.Count, MAX_DIFF_LINE_COUNT),
                MAX_DIFF_LINE_COUNT,
                MAX_DIFF_BYTE_COUNT / (1024 * 1024))
            : null;
        DiffLines = load.Lines;
        CurrentHunkIndex = -1;
        DiffMessage = load.Message;
    }

    /// <summary>The commit whose files are listed and the repository it belongs to.</summary>
    private sealed record DetailsContext(GitCommitInfo Commit, string RepositoryRoot);

    /// <summary>
    /// A state of the details: the header of a commit while its files are read, the read files, or a failure.
    /// </summary>
    private sealed record DetailsLoad(
        GitCommitInfo? Commit,
        string? RepositoryRoot,
        IReadOnlyList<GitCommitFileChange>? Files,
        bool IsLoading,
        string? Message)
    {
        public static DetailsLoad None { get; } = new(null, null, null, false, null);

        public static DetailsLoad Failed(GitCommitInfo commit, string? repositoryRoot, string message) =>
            new(commit, repositoryRoot, Array.Empty<GitCommitFileChange>(), false, message);
    }

    /// <summary>The lines of a diff and whether git had more output, or why it could not be read.</summary>
    private sealed record DiffLoad(IReadOnlyList<DiffLine> Lines, bool IsTruncated, string? Message)
    {
        public static DiffLoad Empty(string? message) => new(Array.Empty<DiffLine>(), false, message);
    }
}
