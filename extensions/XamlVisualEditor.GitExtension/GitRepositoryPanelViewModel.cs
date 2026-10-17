using System.Collections.ObjectModel;
using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text;
using System.Windows.Input;
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
/// A group of the branch tree in the Git Repository panel: the local or the remote-tracking branches.
/// </summary>
public sealed class GitBranchGroupViewModel
{
    /// <summary>Creates an empty group.</summary>
    public GitBranchGroupViewModel(string title, bool isRemote)
    {
        Title = title;
        IsRemote = isRemote;
    }

    /// <summary>Gets the group title.</summary>
    public string Title { get; }

    /// <summary>Gets whether the group holds the remote-tracking branches.</summary>
    public bool IsRemote { get; }

    /// <summary>Gets the branches of the group.</summary>
    public ObservableCollection<GitBranchItemViewModel> Branches { get; } = new();
}

/// <summary>
/// A branch in the Git Repository panel with the commands of its context menu.
/// </summary>
public sealed class GitBranchItemViewModel
{
    /// <summary>Creates the item for <paramref name="branch"/>; the commands are those of <paramref name="panel"/>.</summary>
    public GitBranchItemViewModel(GitBranch branch, GitRepositoryPanelViewModel panel)
    {
        Branch = branch;
        SwitchCommand = panel.SwitchCommand;
        DeleteCommand = panel.DeleteCommand;
        CreateBranchFromCommand = panel.CreateBranchFromCommand;
        TrackingText = FormatTracking(branch);
        ToolTip = FormatToolTip(branch);
    }

    /// <summary>Gets the branch.</summary>
    public GitBranch Branch { get; }

    /// <summary>Gets the short branch name.</summary>
    public string Name => Branch.Name;

    /// <summary>Gets whether HEAD points to the branch.</summary>
    public bool IsCurrent => Branch.IsCurrent;

    /// <summary>Gets whether the branch can be switched to; false for the current branch.</summary>
    public bool CanSwitch => !Branch.IsCurrent;

    /// <summary>Gets whether the branch can be deleted: a local branch that is not the current one.</summary>
    public bool CanDelete => !Branch.IsRemote && !Branch.IsCurrent;

    /// <summary>
    /// Gets the state of the upstream: the commits ahead of and behind it, for example "↑1 ↓2", or the marker of an
    /// upstream that no longer exists; empty otherwise.
    /// </summary>
    public string TrackingText { get; }

    /// <summary>Gets the tooltip: the full name, the current marker, the upstream and the subject of the last commit.</summary>
    public string ToolTip { get; }

    /// <summary>Gets the command that switches to the branch; its parameter is this item.</summary>
    public ICommand SwitchCommand { get; }

    /// <summary>Gets the command that deletes the branch; its parameter is this item.</summary>
    public ICommand DeleteCommand { get; }

    /// <summary>Gets the command that creates a branch starting at this branch; its parameter is this item.</summary>
    public ICommand CreateBranchFromCommand { get; }

    private static string FormatTracking(GitBranch branch)
    {
        if (branch.IsUpstreamGone)
        {
            return GitStrings.UpstreamGone;
        }

        StringBuilder text = new();
        if (branch.AheadBy > 0)
        {
            text.Append('↑').Append(branch.AheadBy.ToString(CultureInfo.CurrentCulture));
        }

        if (branch.BehindBy > 0)
        {
            if (text.Length > 0)
            {
                text.Append(' ');
            }

            text.Append('↓').Append(branch.BehindBy.ToString(CultureInfo.CurrentCulture));
        }

        return text.ToString();
    }

    private static string FormatToolTip(GitBranch branch)
    {
        // The name first: the row cuts a long name with an ellipsis.
        List<string> lines = new() { branch.Name };
        if (branch.IsCurrent)
        {
            lines.Add(GitStrings.CurrentBranch);
        }

        if (branch.UpstreamName is { } upstream)
        {
            lines.Add(branch.IsUpstreamGone
                ? string.Format(CultureInfo.CurrentCulture, GitStrings.UpstreamGoneFormat, upstream)
                : string.Format(CultureInfo.CurrentCulture, GitStrings.StatusBarUpstreamFormat, upstream, branch.AheadBy, branch.BehindBy));
        }

        if (branch.Subject.Length > 0)
        {
            lines.Add(branch.Subject);
        }

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// A tag in the Tags tab of the Git Repository panel.
/// </summary>
public sealed class GitTagItemViewModel
{
    /// <summary>Creates the item for <paramref name="tag"/>.</summary>
    public GitTagItemViewModel(GitTag tag)
    {
        Tag = tag;
        DateText = tag.Date?.ToLocalTime().ToString("G", CultureInfo.CurrentCulture) ?? string.Empty;
        TargetText = tag.IsCommit
            ? tag.TargetShortSha
            : string.Format(CultureInfo.CurrentCulture, GitStrings.TagTargetObjectFormat, tag.TargetType);
        string kind = tag.IsAnnotated
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.AnnotatedTagFormat, tag.TaggerName)
            : GitStrings.LightweightTag;
        ToolTip = tag.IsCommit
            ? kind
            : kind + Environment.NewLine + string.Format(CultureInfo.CurrentCulture, GitStrings.TagNotCommitFormat, tag.TargetType);
    }

    /// <summary>Gets the tag.</summary>
    public GitTag Tag { get; }

    /// <summary>Gets the short tag name.</summary>
    public string Name => Tag.Name;

    /// <summary>
    /// Gets the abbreviated id of the commit the tag points to, or the type of the object for a tag of a tree, a blob
    /// or a tag object.
    /// </summary>
    public string TargetText { get; }

    /// <summary>Gets whether the tag points to a commit, so its history can be shown and a branch can start at it.</summary>
    public bool IsCommit => Tag.IsCommit;

    /// <summary>Gets the date of the tag; the sort key of the date column.</summary>
    public DateTimeOffset? Date => Tag.Date;

    /// <summary>Gets the formatted date of the tag.</summary>
    public string DateText { get; }

    /// <summary>Gets the subject of the tag message, or of the commit of a lightweight tag.</summary>
    public string Subject => Tag.Subject;

    /// <summary>Gets the tooltip: whether the tag is annotated and by whom.</summary>
    public string ToolTip { get; }
}

/// <summary>
/// ViewModel of the Git Repository panel: the local and remote-tracking branches and the tags of the session's
/// repository and buttons for fetch, pull, push, sync and a new branch. The actions run the Git commands, which ask,
/// show progress and report results.
/// </summary>
/// <remarks>
/// The branches and tags are read only while <see cref="IsShown"/> is true, and again only when a snapshot changes
/// HEAD, the branch, its upstream state or <see cref="GitSnapshot.RefsVersion"/>; a newer read cancels an older one.
/// Items of unchanged branches are kept.
/// </remarks>
public sealed partial class GitRepositoryPanelViewModel : ReactiveObject, IDisposable
{
    private readonly GitSession _session;
    private readonly Func<string, IReadOnlyList<object?>?, Task> _executeCommand;
    private readonly CompositeDisposable _disposables = new();
    private bool _branchesLoading;
    private bool _tagsLoading;

    /// <summary>Creates the panel for a shared session.</summary>
    /// <param name="session">The Git session whose repository is shown.</param>
    /// <param name="executeCommand">Runs a Git command by id with arguments.</param>
    public GitRepositoryPanelViewModel(GitSession session, Func<string, IReadOnlyList<object?>?, Task> executeCommand)
    {
        _session = session;
        _executeCommand = executeCommand;
        LocalGroup = new GitBranchGroupViewModel(GitStrings.LocalBranchesGroup, isRemote: false);
        RemoteGroup = new GitBranchGroupViewModel(GitStrings.RemoteBranchesGroup, isRemote: true);
        Groups = [LocalGroup, RemoteGroup];

        IObservable<bool> isRepository = this.WhenAnyValue(x => x.IsRepository);
        IObservable<bool> hasRemote = this.WhenAnyValue(x => x.IsRepository, x => x.HasRemote, (repository, remote) => repository && remote);
        IObservable<bool> hasUpstream = this.WhenAnyValue(x => x.IsRepository, x => x.HasUpstream, (repository, upstream) => repository && upstream);

        RefreshCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _session.RefreshAllAsync()));
        FetchCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.FETCH, null), hasRemote));
        PullCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.PULL, null), hasUpstream));
        PushCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.PUSH, null), hasRemote));
        SyncCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.SYNC, null), hasUpstream));
        CreateBranchCommand = AddCommand(ReactiveCommand.CreateFromTask(() => _executeCommand(GitCommands.CREATE_BRANCH, null), isRepository));
        SwitchCommand = AddCommand(ReactiveCommand.CreateFromTask<GitBranchItemViewModel>(
            item => _executeCommand(GitCommands.CHECKOUT, [item.Branch.FullName])));
        DeleteCommand = AddCommand(ReactiveCommand.CreateFromTask<GitBranchItemViewModel>(
            item => _executeCommand(GitCommands.DELETE_BRANCH, [item.Branch.FullName])));
        CreateBranchFromCommand = AddCommand(ReactiveCommand.CreateFromTask<GitBranchItemViewModel>(
            item => _executeCommand(GitCommands.CREATE_BRANCH, [item.Branch.FullName])));
        // A tag of a tree or a blob has no history, and no branch can start at it.
        IObservable<bool> hasCommitTag = this.WhenAnyValue(x => x.SelectedTag).Select(tag => tag is { IsCommit: true });
        ShowTagHistoryCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => SelectedTag is { IsCommit: true } tag ? _executeCommand(GitCommands.SHOW_HISTORY, [tag.Tag.FullName]) : Task.CompletedTask,
            hasCommitTag));
        CreateBranchFromTagCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => SelectedTag is { IsCommit: true } tag ? _executeCommand(GitCommands.CREATE_BRANCH, [tag.Tag.FullName]) : Task.CompletedTask,
            hasCommitTag));

        _disposables.Add(_session.Snapshots.WitnessOn(RxSchedulers.MainThreadScheduler).Subscribe(OnSnapshot));

        IObservable<BranchLoad> loads = Observable.Switch(_session.Snapshots
            .DistinctUntilChanged(RefsKey.Of)
            .Select(LoadBranches));
        _disposables.Add(Observable.Switch(this.WhenAnyValue(x => x.IsShown)
                .Select(isShown => isShown ? loads : Observable.Return(BranchLoad.Idle)))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyLoad));

        IObservable<TagLoad> tagLoads = Observable.Switch(_session.Snapshots
            .DistinctUntilChanged(RefsKey.Of)
            .Select(LoadTags));
        _disposables.Add(Observable.Switch(this.WhenAnyValue(x => x.IsShown)
                .Select(isShown => isShown ? tagLoads : Observable.Return(TagLoad.Idle)))
            .WitnessOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(ApplyTagLoad));
    }

    /// <summary>
    /// Gets or sets whether the panel's view is shown. The view sets it while it is attached; branches are read only
    /// while it is true.
    /// </summary>
    [Reactive]
    public partial bool IsShown { get; set; }

    /// <summary>Gets whether the workspace is inside a repository.</summary>
    [Reactive]
    public partial bool IsRepository { get; private set; }

    /// <summary>Gets whether the repository has a remote.</summary>
    [Reactive]
    public partial bool HasRemote { get; private set; }

    /// <summary>Gets whether the current branch has an upstream branch.</summary>
    [Reactive]
    public partial bool HasUpstream { get; private set; }

    /// <summary>Gets the line that tells HEAD is detached and at which commit; null while a branch is checked out.</summary>
    [Reactive]
    public partial string? DetachedHeadText { get; private set; }

    /// <summary>Gets why no branches are listed, or a reading error; null while branches are listed.</summary>
    [Reactive]
    public partial string? StatusMessage { get; private set; }

    /// <summary>Gets whether the branches or the tags are being read.</summary>
    [Reactive]
    public partial bool IsLoading { get; private set; }

    /// <summary>Gets the tags, newest first.</summary>
    public ObservableCollection<GitTagItemViewModel> Tags { get; } = new();

    /// <summary>Gets or sets the selected tag.</summary>
    [Reactive]
    public partial GitTagItemViewModel? SelectedTag { get; set; }

    /// <summary>Gets why no tags are listed, or a reading error; null while tags are listed.</summary>
    [Reactive]
    public partial string? TagsMessage { get; private set; }

    /// <summary>Runs <see cref="GitCommands.SHOW_HISTORY"/> starting at the selected tag, when it points to a commit.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ShowTagHistoryCommand { get; }

    /// <summary>Runs <see cref="GitCommands.CREATE_BRANCH"/> starting at the selected tag, when it points to a commit.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateBranchFromTagCommand { get; }

    /// <summary>Gets or sets the selected tree item: a group or a branch.</summary>
    [Reactive]
    public partial object? SelectedItem { get; set; }

    /// <summary>Gets the local branches.</summary>
    public GitBranchGroupViewModel LocalGroup { get; }

    /// <summary>Gets the remote-tracking branches.</summary>
    public GitBranchGroupViewModel RemoteGroup { get; }

    /// <summary>Gets the two groups of the branch tree.</summary>
    public IReadOnlyList<GitBranchGroupViewModel> Groups { get; }

    /// <summary>Refreshes the Git status and reads the branches again.</summary>
    public ReactiveCommand<RxVoid, RxVoid> RefreshCommand { get; }

    /// <summary>Runs <see cref="GitCommands.FETCH"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> FetchCommand { get; }

    /// <summary>Runs <see cref="GitCommands.PULL"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PullCommand { get; }

    /// <summary>Runs <see cref="GitCommands.PUSH"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> PushCommand { get; }

    /// <summary>Runs <see cref="GitCommands.SYNC"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> SyncCommand { get; }

    /// <summary>Runs <see cref="GitCommands.CREATE_BRANCH"/> at HEAD.</summary>
    public ReactiveCommand<RxVoid, RxVoid> CreateBranchCommand { get; }

    /// <summary>Runs <see cref="GitCommands.CHECKOUT"/> for a branch item.</summary>
    public ReactiveCommand<GitBranchItemViewModel, RxVoid> SwitchCommand { get; }

    /// <summary>Runs <see cref="GitCommands.DELETE_BRANCH"/> for a branch item.</summary>
    public ReactiveCommand<GitBranchItemViewModel, RxVoid> DeleteCommand { get; }

    /// <summary>Runs <see cref="GitCommands.CREATE_BRANCH"/> starting at a branch item.</summary>
    public ReactiveCommand<GitBranchItemViewModel, RxVoid> CreateBranchFromCommand { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        _disposables.Dispose();
    }

    private ReactiveCommand<TParameter, RxVoid> AddCommand<TParameter>(ReactiveCommand<TParameter, RxVoid> command)
    {
        _disposables.Add(command);
        // Without a subscriber ReactiveUI ends the application when a command fails.
        _disposables.Add(command.ThrownExceptions.Subscribe(error =>
            StatusMessage = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, error.Message)));
        return command;
    }

    private void OnSnapshot(GitSnapshot snapshot)
    {
        IsRepository = snapshot.IsRepository;
        HasRemote = snapshot.IsRepository && snapshot.HasRemote;
        HasUpstream = snapshot.HasUpstream;
        DetachedHeadText = snapshot.IsDetached
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.StatusBarDetachedFormat, snapshot.BranchName)
            : null;
    }

    /// <summary>Returns the branch list of <paramref name="snapshot"/>, read in the background after a loading state.</summary>
    private IObservable<BranchLoad> LoadBranches(GitSnapshot snapshot)
    {
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null)
        {
            return Observable.Return(BranchLoad.Loaded(
                Array.Empty<GitBranch>(),
                snapshot.State == GitSessionState.Unknown ? null : snapshot.ErrorMessage ?? UiStrings.WorkspaceNotGitRepository));
        }

        IGitBranchService? branches = _session.Services.Branches;
        if (branches is null)
        {
            return Observable.Return(BranchLoad.Loaded(Array.Empty<GitBranch>(), GitStrings.BranchesUnavailable));
        }

        string repositoryRoot = snapshot.RepositoryRoot;
        return Observable.FromAsync(async ct =>
            {
                try
                {
                    IReadOnlyList<GitBranch> list = await branches.GetBranchesAsync(repositoryRoot, ct).ConfigureAwait(false);
                    return BranchLoad.Loaded(list, list.Count == 0 ? GitStrings.NoBranches : null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return BranchLoad.Loaded(
                        Array.Empty<GitBranch>(),
                        string.Format(CultureInfo.CurrentCulture, GitStrings.LoadBranchesFailedFormat, exception.Message));
                }
            })
            .StartWith(BranchLoad.Loading);
    }

    /// <summary>Returns the tag list of <paramref name="snapshot"/>, read in the background after a loading state.</summary>
    private IObservable<TagLoad> LoadTags(GitSnapshot snapshot)
    {
        if (!snapshot.IsRepository || snapshot.RepositoryRoot is null)
        {
            return Observable.Return(TagLoad.Loaded(Array.Empty<GitTag>(), null));
        }

        IGitHistoryService? history = _session.Services.History;
        if (history is null)
        {
            return Observable.Return(TagLoad.Loaded(Array.Empty<GitTag>(), GitStrings.TagsUnavailable));
        }

        string repositoryRoot = snapshot.RepositoryRoot;
        return Observable.FromAsync(async ct =>
            {
                try
                {
                    IReadOnlyList<GitTag> tags = await history.GetTagsAsync(repositoryRoot, ct).ConfigureAwait(false);
                    return TagLoad.Loaded(tags, tags.Count == 0 ? GitStrings.NoTags : null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    return TagLoad.Loaded(
                        Array.Empty<GitTag>(),
                        string.Format(CultureInfo.CurrentCulture, GitStrings.LoadTagsFailedFormat, exception.Message));
                }
            })
            .StartWith(TagLoad.Loading);
    }

    private void ApplyTagLoad(TagLoad load)
    {
        _tagsLoading = load.IsLoading;
        IsLoading = _branchesLoading || _tagsLoading;
        if (load.Tags is null)
        {
            return;
        }

        string? selected = SelectedTag?.Tag.FullName;
        Tags.Clear();
        foreach (GitTag tag in load.Tags)
        {
            Tags.Add(new GitTagItemViewModel(tag));
        }

        SelectedTag = Tags.FirstOrDefault(item => string.Equals(item.Tag.FullName, selected, StringComparison.Ordinal));
        TagsMessage = load.Message;
    }

    private void ApplyLoad(BranchLoad load)
    {
        _branchesLoading = load.IsLoading;
        IsLoading = _branchesLoading || _tagsLoading;
        if (load.Branches is null)
        {
            return;
        }

        Reconcile(LocalGroup.Branches, load.Branches.Where(branch => !branch.IsRemote));
        Reconcile(RemoteGroup.Branches, load.Branches.Where(branch => branch.IsRemote));
        StatusMessage = load.Message;
    }

    /// <summary>
    /// Makes <paramref name="items"/> show <paramref name="branches"/> in their order. Items are matched by full
    /// name; an item whose branch is unchanged is kept, a changed one is replaced and keeps the selection.
    /// </summary>
    private void Reconcile(ObservableCollection<GitBranchItemViewModel> items, IEnumerable<GitBranch> branches)
    {
        GitBranch[] next = branches.ToArray();
        HashSet<string> names = new(next.Select(branch => branch.FullName), StringComparer.Ordinal);
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (!names.Contains(items[i].Branch.FullName))
            {
                items.RemoveAt(i);
            }
        }

        for (int i = 0; i < next.Length; i++)
        {
            GitBranch branch = next[i];
            int existing = IndexOf(items, branch.FullName, i);
            if (existing < 0)
            {
                items.Insert(i, new GitBranchItemViewModel(branch, this));
                continue;
            }

            if (existing != i)
            {
                items.Move(existing, i);
            }

            if (!items[i].Branch.Equals(branch))
            {
                bool selected = ReferenceEquals(SelectedItem, items[i]);
                items[i] = new GitBranchItemViewModel(branch, this);
                if (selected)
                {
                    SelectedItem = items[i];
                }
            }
        }
    }

    private static int IndexOf(ObservableCollection<GitBranchItemViewModel> items, string fullName, int start)
    {
        for (int i = start; i < items.Count; i++)
        {
            if (string.Equals(items[i].Branch.FullName, fullName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The values of a snapshot that change the branch list or the state shown instead of it.</summary>
    private readonly record struct RefsKey(
        GitSessionState State,
        string? RepositoryRoot,
        string? ErrorMessage,
        string? HeadSha,
        string BranchName,
        string? UpstreamName,
        int AheadBy,
        int BehindBy,
        int RefsVersion)
    {
        public static RefsKey Of(GitSnapshot snapshot) => new(
            snapshot.State,
            snapshot.RepositoryRoot,
            snapshot.ErrorMessage,
            snapshot.Status?.HeadSha,
            snapshot.BranchName,
            snapshot.UpstreamName,
            snapshot.AheadBy,
            snapshot.BehindBy,
            snapshot.RefsVersion);
    }

    /// <summary>
    /// A state of the branch list: loading, idle, or loaded with branches and the message shown instead of or above
    /// them.
    /// </summary>
    private sealed record BranchLoad(bool IsLoading, IReadOnlyList<GitBranch>? Branches, string? Message)
    {
        public static BranchLoad Loading { get; } = new(true, null, null);

        public static BranchLoad Idle { get; } = new(false, null, null);

        public static BranchLoad Loaded(IReadOnlyList<GitBranch> branches, string? message) => new(false, branches, message);
    }

    /// <summary>A state of the tag list: loading, idle, or loaded with tags and the message shown instead of them.</summary>
    private sealed record TagLoad(bool IsLoading, IReadOnlyList<GitTag>? Tags, string? Message)
    {
        public static TagLoad Loading { get; } = new(true, null, null);

        public static TagLoad Idle { get; } = new(false, null, null);

        public static TagLoad Loaded(IReadOnlyList<GitTag> tags, string? message) => new(false, tags, message);
    }
}
