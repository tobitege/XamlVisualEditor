using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using ReactiveUI;
using ReactiveUI.Primitives.Signals;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.GitExtension;

/// <summary>
/// What the Git History panel lists: the commits of the whole repository, of a file or of a folder, starting at HEAD
/// or at a revision.
/// </summary>
/// <param name="Path">The repository-relative file or folder with <c>/</c> as separator; null for the whole repository.</param>
/// <param name="IsDirectory">Whether <paramref name="Path"/> is a folder; the history of a file follows its renames.</param>
/// <param name="Revision">The commit, branch or tag the history starts at; null starts at HEAD.</param>
public sealed record GitHistoryScope(string? Path = null, bool IsDirectory = false, string? Revision = null)
{
    /// <summary>Gets the history of the whole repository, starting at HEAD.</summary>
    public static GitHistoryScope Repository { get; } = new();

    /// <summary>Gets whether the scope is the whole repository.</summary>
    public bool IsWholeRepository => string.IsNullOrEmpty(Path);
}

/// <summary>A commit row of the Git History panel.</summary>
public sealed class GitCommitRowViewModel
{
    /// <summary>Creates the row.</summary>
    public GitCommitRowViewModel(GitCommitInfo commit)
    {
        Commit = commit;
        DateText = commit.AuthorDate.ToLocalTime().ToString("G", CultureInfo.CurrentCulture);
        RefsText = string.Join(", ", commit.Refs);
    }

    /// <summary>Gets the commit.</summary>
    public GitCommitInfo Commit { get; }

    /// <summary>Gets the full commit id.</summary>
    public string Sha => Commit.Sha;

    /// <summary>Gets the abbreviated commit id.</summary>
    public string ShortSha => Commit.ShortSha;

    /// <summary>Gets the subject of the message.</summary>
    public string Subject => Commit.Subject;

    /// <summary>Gets the complete message, shown as tooltip.</summary>
    public string Message => Commit.Message;

    /// <summary>Gets the branches and tags that point to the commit, separated by commas.</summary>
    public string RefsText { get; }

    /// <summary>Gets whether a branch or tag points to the commit.</summary>
    public bool HasRefs => RefsText.Length > 0;

    /// <summary>Gets the author name.</summary>
    public string AuthorName => Commit.AuthorName;

    /// <summary>Gets the formatted author date.</summary>
    public string DateText { get; }
}

/// <summary>Sample dates of the Git panels, which size their date columns.</summary>
public static class GitDateSamples
{
    // Two digits in every field and a time before and after noon, so a sample has the most digits of its form and
    // both day period designators.
    private static readonly DateTime[] Dates =
    [
        new(2088, 12, 28, 10, 58, 58),
        new(2088, 12, 28, 22, 58, 58)
    ];

    /// <summary>
    /// Gets a date in the morning and one in the evening with their time as the panels format them ("G"), in English
    /// (United States), in German and in the current culture. Measured with every digit as the widest digit of a font
    /// without kerning between digits, such as Inter, they are at least as wide as any date and time of these cultures.
    /// </summary>
    public static IReadOnlyList<string> Texts =>
        new[] { CultureInfo.GetCultureInfo("en-US"), CultureInfo.GetCultureInfo("de-DE"), CultureInfo.CurrentCulture }
            .SelectMany(culture => Dates.Select(date => date.ToString("G", culture)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// ViewModel of the Git History panel: the commits of the scope, filtered by message and author, 100 at a time, and
/// the details of the selected commit.
/// </summary>
/// <remarks>
/// The commits are read only while <see cref="IsShown"/> is true. A new scope or filter reads the first page and
/// cancels a running read. A refresh, a snapshot that changes HEAD or <see cref="GitSnapshot.RefsVersion"/>, and
/// showing the panel again read as many commits as are listed; the selected commit stays selected when it is still
/// listed. Every page starts at the commit the first page started at: the HEAD commit of the snapshot, or the commit
/// the revision of the scope named then. A commit made between two pages therefore neither shifts nor repeats rows;
/// the new snapshot then reads the listed range again. A Load More made while a new state is read does nothing, and
/// only the results of the last request are applied.
/// </remarks>
public sealed partial class GitHistoryViewModel : ReactiveObject, IDisposable
{
    /// <summary>The number of commits read at a time.</summary>
    public const int PAGE_SIZE = 100;

    private readonly GitSession _session;
    private readonly Func<string, IReadOnlyList<object?>?, Task> _executeCommand;
    private readonly CompositeDisposable _disposables = new();
    private readonly BehaviorSubject<HistoryRequest> _requests;
    private readonly Subject<PageRequest?> _loadRequests = new();
    private readonly HashSet<string> _knownAuthors = new(StringComparer.CurrentCultureIgnoreCase);
    private PageRequest? _nextPage;
    private PageRequest? _requestedPage;
    private HistoryRequest? _listedRequest;
    private string? _listedRoot;
    private string? _authorsRoot;
    private bool _isApplyingLoad;

    /// <summary>Creates the panel for a shared session.</summary>
    /// <param name="session">The Git session whose repository is shown.</param>
    /// <param name="executeCommand">Runs a Git command by id with arguments.</param>
    public GitHistoryViewModel(GitSession session, Func<string, IReadOnlyList<object?>?, Task> executeCommand)
    {
        _session = session;
        _executeCommand = executeCommand;
        _requests = new BehaviorSubject<HistoryRequest>(new HistoryRequest(GitHistoryScope.Repository, null, null, 0));
        ScopeText = FormatScope(Scope);
        Details = new GitCommitDetailsViewModel(session, executeCommand);

        IObservable<bool> isRepository = this.WhenAnyValue(x => x.IsRepository);
        SearchCommand = AddCommand(ReactiveCommand.Create(ApplyFilter, isRepository));
        ClearFilterCommand = AddCommand(ReactiveCommand.Create(ClearFilter, isRepository));
        RefreshCommand = AddCommand(ReactiveCommand.Create(Reload, isRepository));
        LoadMoreCommand = AddCommand(ReactiveCommand.Create(
            LoadMore,
            this.WhenAnyValue(x => x.HasMore, x => x.IsLoading, (more, loading) => more && !loading)));
        ShowWholeRepositoryCommand = AddCommand(ReactiveCommand.Create(
            () => Show(GitHistoryScope.Repository),
            this.WhenAnyValue(x => x.Scope, x => x.IsRepository, (scope, repository) => repository && scope != GitHistoryScope.Repository)));
        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedCommit).Select(row => row is not null);
        CopyCommitIdCommand = AddCommand(ReactiveCommand.CreateFromTask(CopyCommitIdAsync, hasSelection));
        CreateBranchFromCommitCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => SelectedCommit is { } row ? _executeCommand(GitCommands.CREATE_BRANCH, [row.Sha]) : Task.CompletedTask,
            hasSelection));

        _disposables.Add(_session.Snapshots
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(snapshot => IsRepository = snapshot.IsRepository));
        // While a page replaces the rows, a bound grid clears the selection; the details follow only the final one.
        _disposables.Add(this.WhenAnyValue(x => x.SelectedCommit)
            .Where(_ => !_isApplyingLoad)
            .Subscribe(row => Details.Commit = row?.Commit));

        _disposables.Add(Observable.Switch(_loadRequests.Select(page => page is null ? Observable.Return(HistoryLoad.Idle) : LoadPage(page)))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyLoad));

        // Which commits to read depends on the listed ones, so the sources are handled on the UI thread; null stops
        // reading while the panel is hidden.
        IObservable<HistorySource?> sources = _session.Snapshots
            .Select(HistoryKey.Of)
            .DistinctUntilChanged()
            .CombineLatest(_requests, (key, request) => (HistorySource?)new HistorySource(key, request));
        _disposables.Add(Observable.Switch(this.WhenAnyValue(x => x.IsShown)
                .Select(isShown => isShown ? sources : Observable.Return<HistorySource?>(null)))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(OnSourceChanged));

        // Disposed after the subscriptions above.
        _disposables.Add(_requests);
        _disposables.Add(_loadRequests);
        _disposables.Add(Details);
    }

    /// <summary>
    /// Gets or sets whether the panel's view is shown. The view sets it while it is attached; commits are read only
    /// while it is true.
    /// </summary>
    [Reactive]
    public partial bool IsShown { get; set; }

    /// <summary>Gets whether the workspace is inside a repository.</summary>
    [Reactive]
    public partial bool IsRepository { get; private set; }

    /// <summary>Gets what the panel lists.</summary>
    [Reactive]
    public partial GitHistoryScope Scope { get; private set; } = GitHistoryScope.Repository;

    /// <summary>Gets the description of <see cref="Scope"/>.</summary>
    [Reactive]
    public partial string ScopeText { get; private set; } = string.Empty;

    /// <summary>Gets or sets the text the commit messages must contain; applied by <see cref="SearchCommand"/>.</summary>
    [Reactive]
    public partial string? SearchText { get; set; }

    /// <summary>Gets or sets the text the author name or email must contain; applied by <see cref="SearchCommand"/>.</summary>
    [Reactive]
    public partial string? AuthorText { get; set; }

    /// <summary>Gets or sets the selected commit, whose details <see cref="Details"/> shows.</summary>
    [Reactive]
    public partial GitCommitRowViewModel? SelectedCommit { get; set; }

    /// <summary>Gets whether more commits can be loaded.</summary>
    [Reactive]
    public partial bool HasMore { get; private set; }

    /// <summary>Gets whether commits are being read.</summary>
    [Reactive]
    public partial bool IsLoading { get; private set; }

    /// <summary>Gets why no commits are listed, or a reading error; null while commits are listed.</summary>
    [Reactive]
    public partial string? Message { get; private set; }

    /// <summary>Gets the listed commits, newest first.</summary>
    public ObservableCollection<GitCommitRowViewModel> Commits { get; } = new();

    /// <summary>Gets the authors of the listed commits, sorted, offered as filter suggestions.</summary>
    public ObservableCollection<string> KnownAuthors { get; } = new();

    /// <summary>Gets the details of the selected commit.</summary>
    public GitCommitDetailsViewModel Details { get; }

    /// <summary>Gets the interaction that places a text on the clipboard; the view handles it.</summary>
    public Interaction<string, RxVoid> CopyToClipboard { get; } = new();

    /// <summary>Applies <see cref="SearchText"/> and <see cref="AuthorText"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SearchCommand { get; }

    /// <summary>Clears both filters and lists all commits of the scope.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ClearFilterCommand { get; }

    /// <summary>Reads the commits again.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Reads the next <see cref="PAGE_SIZE"/> commits.</summary>
    public ReactiveCommand<RxVoid, RxVoid> LoadMoreCommand { get; }

    /// <summary>Lists the commits of the whole repository, starting at HEAD; disabled while they are listed.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowWholeRepositoryCommand { get; }

    /// <summary>Copies the full id of the selected commit to the clipboard.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CopyCommitIdCommand { get; }

    /// <summary>Runs <see cref="GitCommands.CREATE_BRANCH"/> starting at the selected commit.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateBranchFromCommitCommand { get; }

    /// <summary>Lists the commits of <paramref name="scope"/>; the message filter is cleared, the author filter stays.</summary>
    public void Show(GitHistoryScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        Scope = scope;
        ScopeText = FormatScope(scope);
        SearchText = null;
        Publish(_requests.Value with { Scope = scope, Author = Normalize(AuthorText), MessageText = null });
    }

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
            Message = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, error.Message)));
        return command;
    }

    private void ApplyFilter() =>
        Publish(_requests.Value with { Author = Normalize(AuthorText), MessageText = Normalize(SearchText) });

    private void ClearFilter()
    {
        SearchText = null;
        AuthorText = null;
        Publish(_requests.Value with { Author = null, MessageText = null });
    }

    private void Reload() => Publish(_requests.Value);

    /// <summary>Publishes <paramref name="request"/> as a new request, so it is read also when it equals the last one.</summary>
    private void Publish(HistoryRequest request) => _requests.OnNext(request with { Version = _requests.Value.Version + 1 });

    private void LoadMore()
    {
        // Null while a new source is read, so a page of the previous state is never appended.
        if (_nextPage is { } page)
        {
            Request(page);
        }
    }

    /// <summary>Reads <paramref name="page"/>, or nothing for null; only the results of the last request are applied.</summary>
    private void Request(PageRequest? page)
    {
        _requestedPage = page;
        _nextPage = null;
        if (page is not null)
        {
            IsLoading = true;
        }

        _loadRequests.OnNext(page);
    }

    /// <summary>
    /// Reads the first page for a new scope, filter or repository, and otherwise the listed range again, so a
    /// refresh, a change of HEAD or the branches and showing the panel again keep the loaded pages.
    /// </summary>
    private void OnSourceChanged(HistorySource? source)
    {
        if (source is not { } current)
        {
            Request(null);
            return;
        }

        (HistoryKey key, HistoryRequest request) = current;
        if (!string.Equals(key.RepositoryRoot, _authorsRoot, StringComparison.Ordinal))
        {
            _authorsRoot = key.RepositoryRoot;
            _knownAuthors.Clear();
            KnownAuthors.Clear();
        }

        bool rereadsListed = Commits.Count > 0
                             && _listedRequest is { } listed
                             && listed.HasSameFilters(request)
                             && string.Equals(_listedRoot, key.RepositoryRoot, StringComparison.Ordinal);
        Request(new PageRequest(key, request, 0, rereadsListed ? Math.Max(Commits.Count, PAGE_SIZE) : PAGE_SIZE));
    }

    private async Task CopyCommitIdAsync()
    {
        if (SelectedCommit is { } row)
        {
            await CopyToClipboard.Handle(row.Sha);
        }
    }

    /// <summary>
    /// Returns the commits of one page or range, read in the background after a loading state. A read without
    /// <see cref="PageRequest.Start"/> first fixes the commit the history starts at: the HEAD commit of the snapshot,
    /// or the commit the revision of the scope names now. Further pages start at the same commit, so a commit made in
    /// between, also on a branch the scope names, neither repeats nor shifts rows.
    /// </summary>
    private IObservable<HistoryLoad> LoadPage(PageRequest page)
    {
        HistoryKey key = page.Key;
        if (!key.IsRepository || key.RepositoryRoot is null)
        {
            return Observable.Return(HistoryLoad.Loaded(
                page,
                Array.Empty<GitCommitInfo>(),
                hasMore: false,
                key.State == GitSessionState.Unknown ? null : key.ErrorMessage ?? UiStrings.WorkspaceNotGitRepository));
        }

        IGitHistoryService? history = _session.Services.History;
        if (history is null)
        {
            return Observable.Return(HistoryLoad.Loaded(page, Array.Empty<GitCommitInfo>(), hasMore: false, GitStrings.HistoryUnavailable));
        }

        string repositoryRoot = key.RepositoryRoot;
        HistoryRequest request = page.Request;
        return Observable.FromAsync(async ct =>
            {
                try
                {
                    PageStart start = page.Start ?? (request.Scope.Revision is { } revision
                        ? PageStart.Of(await history.ResolveCommitAsync(repositoryRoot, revision, ct).ConfigureAwait(false))
                        : new PageStart(key.HeadSha, null));
                    GitLogQuery query = new()
                    {
                        Path = request.Scope.Path,
                        Follow = !request.Scope.IsWholeRepository && !request.Scope.IsDirectory,
                        Revision = start.Sha,
                        Author = request.Author,
                        MessageText = request.MessageText,
                        Skip = page.Skip,
                        // One commit more than the page tells whether there is a next page.
                        MaxCount = page.Count + 1
                    };
                    IReadOnlyList<GitCommitInfo> commits = await history.GetLogAsync(repositoryRoot, query, ct).ConfigureAwait(false);
                    bool hasMore = commits.Count > page.Count;
                    IReadOnlyList<GitCommitInfo> pageCommits = hasMore ? commits.Take(page.Count).ToArray() : commits;
                    string? message = page.Skip == 0 && pageCommits.Count == 0 ? FormatEmptyMessage(request) : null;
                    return HistoryLoad.Loaded(page, pageCommits, hasMore, message, start);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return HistoryLoad.Loaded(
                        page,
                        Array.Empty<GitCommitInfo>(),
                        hasMore: false,
                        string.Format(CultureInfo.CurrentCulture, GitStrings.LoadHistoryFailedFormat, exception.Message));
                }
            })
            .StartWith(HistoryLoad.Loading(page));
    }

    private void ApplyLoad(HistoryLoad load)
    {
        // A result that passed the switch before a newer request was made belongs to an older state.
        if (!ReferenceEquals(load.Request, _requestedPage))
        {
            return;
        }

        IsLoading = load.IsLoading;
        if (load.Request is not { } page || load.Commits is null)
        {
            return;
        }

        // A read from the first commit replaces the listed commits; a further page is appended.
        bool replaces = page.Skip == 0;
        string? selectedSha = SelectedCommit?.Sha;
        _isApplyingLoad = true;
        try
        {
            if (replaces)
            {
                Commits.Clear();
                _listedRequest = page.Request;
                _listedRoot = page.Key.RepositoryRoot;
            }

            GitCommitRowViewModel? reselected = null;
            foreach (GitCommitInfo commit in load.Commits)
            {
                GitCommitRowViewModel row = new(commit);
                Commits.Add(row);
                AddKnownAuthor(commit.AuthorName);
                if (replaces && selectedSha is not null && string.Equals(row.Sha, selectedSha, StringComparison.Ordinal))
                {
                    reselected = row;
                }
            }

            if (replaces)
            {
                SelectedCommit = reselected;
            }
        }
        finally
        {
            _isApplyingLoad = false;
        }

        Details.Commit = SelectedCommit?.Commit;
        if (replaces && page.Request.Scope == Scope)
        {
            ScopeText = FormatScope(Scope, load.Start?.NameOf(Scope.Revision));
        }

        // A failed further page keeps the listed commits and offers the same page again.
        bool failed = !replaces && load.Message is not null;
        HasMore = load.HasMore || failed;
        _nextPage = load.HasMore ? page with { Skip = Commits.Count, Count = PAGE_SIZE, Start = load.Start } : failed ? page : null;
        Message = load.Message;
    }

    private static string FormatEmptyMessage(HistoryRequest request)
    {
        if (request.Author is not null || request.MessageText is not null)
        {
            return GitStrings.NoMatchingCommits;
        }

        return request.Scope == GitHistoryScope.Repository ? GitStrings.NoCommits : GitStrings.NoCommitsFound;
    }

    private void AddKnownAuthor(string author)
    {
        if (string.IsNullOrWhiteSpace(author) || !_knownAuthors.Add(author))
        {
            return;
        }

        int index = 0;
        while (index < KnownAuthors.Count && string.Compare(KnownAuthors[index], author, StringComparison.CurrentCultureIgnoreCase) < 0)
        {
            index++;
        }

        KnownAuthors.Insert(index, author);
    }

    private static string? Normalize(string? text)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <param name="scope">The scope.</param>
    /// <param name="revisionText">The text shown for the revision of <paramref name="scope"/>; null shows the revision itself.</param>
    private static string FormatScope(GitHistoryScope scope, string? revisionText = null)
    {
        string text = scope.IsWholeRepository
            ? GitStrings.HistoryScopeRepository
            : string.Format(
                CultureInfo.CurrentCulture,
                scope.IsDirectory ? GitStrings.HistoryScopeFolderFormat : GitStrings.HistoryScopeFileFormat,
                scope.Path);
        return string.IsNullOrWhiteSpace(scope.Revision)
            ? text
            : string.Format(CultureInfo.CurrentCulture, GitStrings.HistoryScopeRevisionFormat, text, revisionText ?? ShortenReference(scope.Revision));
    }

    private static string ShortenReference(string revision)
    {
        foreach (string prefix in new[] { "refs/tags/", "refs/heads/", "refs/remotes/" })
        {
            if (revision.StartsWith(prefix, StringComparison.Ordinal))
            {
                return revision[prefix.Length..];
            }
        }

        return revision;
    }

    /// <summary>A scope with its filters; <see cref="Version"/> makes every published request a new one.</summary>
    private sealed record HistoryRequest(GitHistoryScope Scope, string? Author, string? MessageText, int Version)
    {
        /// <summary>Returns whether <paramref name="other"/> lists the same commits, apart from its version.</summary>
        public bool HasSameFilters(HistoryRequest other) =>
            Scope == other.Scope
            && string.Equals(Author, other.Author, StringComparison.Ordinal)
            && string.Equals(MessageText, other.MessageText, StringComparison.Ordinal);
    }

    /// <summary>The repository state and the request the listed commits follow.</summary>
    private readonly record struct HistorySource(HistoryKey Key, HistoryRequest Request);

    /// <summary>
    /// <see cref="Count"/> commits of a request after the first <see cref="Skip"/>, in the repository state of
    /// <see cref="Key"/>, starting at <see cref="Start"/>; a request without start fixes it first.
    /// </summary>
    private sealed record PageRequest(HistoryKey Key, HistoryRequest Request, int Skip, int Count, PageStart? Start = null);

    /// <summary>The commit a history starts at, with its abbreviated id when it was resolved from a revision.</summary>
    /// <param name="Sha">The full commit id; null for the HEAD of a repository without commits.</param>
    /// <param name="ShortSha">The id as git abbreviates it; null for HEAD.</param>
    private sealed record PageStart(string? Sha, string? ShortSha)
    {
        public static PageStart Of(GitCommitInfo commit) => new(commit.Sha, commit.ShortSha);

        /// <summary>
        /// Returns the abbreviated id when <paramref name="revision"/> is the full id of the commit, so the scope names
        /// it as git does also when the commit is not listed; null for a name or an abbreviated id, which the scope
        /// shows as given.
        /// </summary>
        public string? NameOf(string? revision) =>
            ShortSha is not null && string.Equals(Sha, revision, StringComparison.OrdinalIgnoreCase)
                ? ShortSha
                : null;
    }

    /// <summary>The values of a snapshot that change the listed commits or the state shown instead of them.</summary>
    private readonly record struct HistoryKey(
        GitSessionState State,
        string? RepositoryRoot,
        string? ErrorMessage,
        string? HeadSha,
        int RefsVersion)
    {
        public bool IsRepository => State is GitSessionState.Repository or GitSessionState.StatusFailed;

        public static HistoryKey Of(GitSnapshot snapshot) => new(
            snapshot.State,
            snapshot.RepositoryRoot,
            snapshot.ErrorMessage,
            snapshot.Status?.HeadSha,
            snapshot.RefsVersion);
    }

    /// <summary>
    /// A state of the commit list for the request that started it: loading, idle, or a loaded page with the message
    /// shown instead of or above it and the commit the history starts at.
    /// </summary>
    private sealed record HistoryLoad(
        bool IsLoading,
        PageRequest? Request,
        IReadOnlyList<GitCommitInfo>? Commits,
        bool HasMore,
        string? Message,
        PageStart? Start)
    {
        public static HistoryLoad Idle { get; } = new(false, null, null, false, null, null);

        public static HistoryLoad Loading(PageRequest page) => new(true, page, null, false, null, null);

        public static HistoryLoad Loaded(
            PageRequest page,
            IReadOnlyList<GitCommitInfo> commits,
            bool hasMore,
            string? message,
            PageStart? start = null) =>
            new(false, page, commits, hasMore, message, start);
    }
}
