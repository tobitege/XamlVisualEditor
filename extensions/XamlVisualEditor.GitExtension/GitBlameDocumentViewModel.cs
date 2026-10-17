using System.Globalization;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.GitExtension;

/// <summary>A line of a blame; the first line of a hunk also shows its commit.</summary>
public sealed class GitBlameRowViewModel
{
    private const int SHORT_SHA_LENGTH = 8;

    /// <summary>
    /// Gets texts as wide as any commit text in a monospace font: an abbreviated commit id and the note of uncommitted
    /// lines in English, German and the current culture, which size the commit column.
    /// </summary>
    public static IReadOnlyList<string> CommitTextSamples { get; } =
        new[] { new string('0', SHORT_SHA_LENGTH), Uncommitted("en"), Uncommitted("de"), GitStrings.Uncommitted }
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    /// <summary>Creates the row.</summary>
    public GitBlameRowViewModel(int lineNumber, string text, GitBlameCommit commit, bool isHunkStart)
    {
        LineNumber = lineNumber;
        Text = text;
        Commit = commit;
        if (isHunkStart)
        {
            CommitText = commit.IsUncommitted ? GitStrings.Uncommitted : commit.Sha[..Math.Min(SHORT_SHA_LENGTH, commit.Sha.Length)];
            AuthorText = commit.IsUncommitted ? string.Empty : commit.AuthorName;
            DateText = commit.IsUncommitted ? string.Empty : commit.AuthorDate.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Gets the 1-based line number.</summary>
    public int LineNumber { get; }

    /// <summary>Gets the text of the line.</summary>
    public string Text { get; }

    /// <summary>Gets the commit that last changed the line.</summary>
    public GitBlameCommit Commit { get; }

    /// <summary>Gets the abbreviated commit id on the first line of a hunk, otherwise empty.</summary>
    public string CommitText { get; } = string.Empty;

    /// <summary>Gets the author on the first line of a hunk, otherwise empty.</summary>
    public string AuthorText { get; } = string.Empty;

    /// <summary>Gets the formatted author date on the first line of a hunk, otherwise empty.</summary>
    public string DateText { get; } = string.Empty;

    /// <summary>Gets the summary of the commit, shown as tooltip.</summary>
    public string Summary => Commit.Summary;

    private static string Uncommitted(string culture) =>
        GitStrings.ResourceManager.GetString(nameof(GitStrings.Uncommitted), CultureInfo.GetCultureInfo(culture)) ?? string.Empty;
}

/// <summary>
/// ViewModel of a document that shows the commit of every line of a saved file. It reads the blame again when HEAD
/// changes and on <see cref="RefreshCommand"/>; <see cref="OpenCommitCommand"/> shows the commit of the selected line
/// in the Git History panel.
/// </summary>
public sealed partial class GitBlameDocumentViewModel : ReactiveObject, IDisposable
{
    /// <summary>The number of bytes of git's output that are read.</summary>
    public const int MAX_BLAME_BYTES = 16 * 1024 * 1024;

    private readonly CompositeDisposable _disposables = new();
    private readonly Subject<Unit> _refreshes = new();

    /// <summary>Creates the document and starts reading.</summary>
    /// <param name="session">The Git session.</param>
    /// <param name="repositoryPath">The repository-relative path of the file with <c>/</c> as separator.</param>
    /// <param name="executeCommand">Runs a Git command by id with arguments; null opens no commit.</param>
    public GitBlameDocumentViewModel(
        GitSession session,
        string repositoryPath,
        Func<string, IReadOnlyList<object?>?, Task>? executeCommand)
    {
        RepositoryPath = repositoryPath;
        OpenCommitCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => SelectedRow is { } row && executeCommand is not null
                ? executeCommand(GitCommands.SHOW_HISTORY, [row.Commit.Sha])
                : Task.CompletedTask,
            this.WhenAnyValue(x => x.SelectedRow, row => row is { Commit.IsUncommitted: false } && executeCommand is not null)));
        RefreshCommand = AddCommand(ReactiveCommand.Create(() => _refreshes.OnNext(Unit.Default)));

        IObservable<(string? Root, string? Head)> heads = session.Snapshots
            .Select(snapshot => (snapshot.RepositoryRoot, snapshot.Status?.HeadSha))
            .DistinctUntilChanged();
        _disposables.Add(Observable.Switch(heads
                .CombineLatest(_refreshes.StartWith(Unit.Default), (head, _) => head)
                .Select(head => Load(session, head.Root)))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(Apply));
        _disposables.Add(_refreshes);
    }

    /// <summary>Gets the repository-relative path of the file.</summary>
    public string RepositoryPath { get; }

    /// <summary>Gets the lines of the file with their commits.</summary>
    [Reactive]
    public partial IReadOnlyList<GitBlameRowViewModel> Rows { get; private set; } = [];

    /// <summary>Gets or sets the selected line.</summary>
    [Reactive]
    public partial GitBlameRowViewModel? SelectedRow { get; set; }

    /// <summary>Gets a note while reading, after a failure and when lines are missing; null otherwise.</summary>
    [Reactive]
    public partial string? Message { get; private set; } = GitStrings.BlameLoading;

    /// <summary>Shows the commit of the selected line in the Git History panel; not for uncommitted lines.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenCommitCommand { get; }

    /// <summary>Reads the blame again.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposables.Dispose();
    }

    // A rows result, or a message: null rows keep the shown rows while reading.
    private IObservable<(IReadOnlyList<GitBlameRowViewModel>? Rows, string? Message)> Load(GitSession session, string? root)
    {
        if (root is null || session.Services.Content is not { } content)
        {
            return Observable.Return<(IReadOnlyList<GitBlameRowViewModel>?, string?)>(
                ([], root is null ? null : GitStrings.BlameUnavailable));
        }

        return Observable.FromAsync<(IReadOnlyList<GitBlameRowViewModel>?, string?)>(async ct =>
            {
                try
                {
                    GitBlame blame = await content.GetBlameAsync(root, RepositoryPath, MAX_BLAME_BYTES, ct).ConfigureAwait(false);
                    return (ToRows(blame), blame.IsTruncated ? GitStrings.BlameTruncated : null);
                }
                catch (InvalidOperationException exception)
                {
                    return ([], string.Format(CultureInfo.CurrentCulture, GitStrings.LoadBlameFailedFormat, exception.Message));
                }
            })
            .StartWith((null, GitStrings.BlameLoading));
    }

    private static GitBlameRowViewModel[] ToRows(GitBlame blame)
    {
        GitBlameRowViewModel[] rows = new GitBlameRowViewModel[blame.Lines.Count];
        foreach (GitBlameHunk hunk in blame.Hunks)
        {
            for (int offset = 0; offset < hunk.LineCount; offset++)
            {
                int line = hunk.StartLine + offset;
                rows[line - 1] = new GitBlameRowViewModel(line, blame.Lines[line - 1], hunk.Commit, offset == 0);
            }
        }

        return rows;
    }

    private void Apply((IReadOnlyList<GitBlameRowViewModel>? Rows, string? Message) result)
    {
        Message = result.Message;
        if (result.Rows is { } rows)
        {
            Rows = rows;
        }
    }

    private ReactiveCommand<RxVoid, RxVoid> AddCommand(ReactiveCommand<RxVoid, RxVoid> command)
    {
        _disposables.Add(command);
        // Without a subscriber ReactiveUI ends the application when a command fails.
        _disposables.Add(command.ThrownExceptions.Subscribe(error => Message = error.Message));
        return command;
    }
}
