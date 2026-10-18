using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.GitExtension.Localization;
using XamlVisualEditor.GitExtension.Services;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.GitExtension;

/// <summary>A conflicted file in the Conflicts list of the Git Changes panel.</summary>
public sealed class GitConflictEntryViewModel
{
    internal GitConflictEntryViewModel(GitFileChange change)
    {
        Path = change.Path;
        KindLabel = FormatKind(change.ConflictKind);
    }

    /// <summary>Gets the repository-relative path of the file.</summary>
    public string Path { get; }

    /// <summary>Gets how the two sides changed the file, such as "both modified".</summary>
    public string KindLabel { get; }

    /// <summary>Gets every text <see cref="KindLabel"/> can have, which sizes the status column.</summary>
    public static IReadOnlyList<string> KindLabels =>
        Enum.GetValues<GitConflictKind>().Where(kind => kind != GitConflictKind.None).Select(FormatKind).ToArray();

    private static string FormatKind(GitConflictKind kind) => kind switch
    {
        GitConflictKind.BothDeleted => GitStrings.ConflictBothDeleted,
        GitConflictKind.AddedByUs => GitStrings.ConflictAddedByUs,
        GitConflictKind.DeletedByThem => GitStrings.ConflictDeletedByThem,
        GitConflictKind.AddedByThem => GitStrings.ConflictAddedByThem,
        GitConflictKind.DeletedByUs => GitStrings.ConflictDeletedByUs,
        GitConflictKind.BothAdded => GitStrings.ConflictBothAdded,
        _ => GitStrings.ConflictBothModified
    };
}

/// <summary>The operation banner, the Conflicts list and the prepared commit message of the Git Changes panel.</summary>
public sealed partial class GitPanelViewModel
{
    private readonly ObservableCollection<GitConflictEntryViewModel> _conflictItems = new();

    // The message git prepared for the merge in progress that was filled into the empty message box.
    private string? _preparedMessage;

    private bool _fillingMessage;

    private bool _merging;

    /// <summary>Gets the conflicted files.</summary>
    public IReadOnlyList<GitConflictEntryViewModel> ConflictItems => _conflictItems;

    /// <summary>Gets or sets the selected conflicted file; selecting one clears the selection of the other lists.</summary>
    [Reactive]
    public partial GitConflictEntryViewModel? SelectedConflict { get; set; }

    /// <summary>Gets whether a merge, rebase, cherry-pick, revert or patch application is in progress.</summary>
    [Reactive]
    public partial bool IsOperationInProgress { get; private set; }

    /// <summary>Gets whether a merge is in progress, which a commit completes.</summary>
    [Reactive]
    public partial bool IsMerging { get; private set; }

    /// <summary>Gets whether a commit fits the operation in progress, see <see cref="GitSnapshot.AllowsCommit"/>.</summary>
    [Reactive]
    public partial bool IsCommitAllowed { get; private set; } = true;

    /// <summary>Gets the number of conflicted files.</summary>
    [Reactive]
    public partial int ConflictCount { get; private set; }

    /// <summary>Gets whether a file has an unresolved conflict; the Conflicts list is shown only then.</summary>
    [Reactive]
    public partial bool HasConflicts { get; private set; }

    /// <summary>Gets the text of the operation banner: the operation and what is left to do.</summary>
    [Reactive]
    public partial string OperationSummary { get; private set; } = string.Empty;

    /// <summary>Runs <see cref="GitCommands.ABORT"/>.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AbortCommand { get; private set; }

    /// <summary>Runs <see cref="GitCommands.CONTINUE"/>; not for a merge, which <see cref="CommitCommand"/> completes.</summary>
    public ReactiveCommand<RxVoid, RxVoid> ContinueCommand { get; private set; }

    /// <summary>Opens the selected conflicted file in the editor.</summary>
    public ReactiveCommand<RxVoid, RxVoid> OpenConflictCommand { get; private set; }

    /// <summary>Runs <see cref="GitCommands.ACCEPT_OURS"/> for the selected conflicted file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AcceptOursCommand { get; private set; }

    /// <summary>Runs <see cref="GitCommands.ACCEPT_THEIRS"/> for the selected conflicted file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> AcceptTheirsCommand { get; private set; }

    /// <summary>Runs <see cref="GitCommands.MARK_RESOLVED"/> for the selected conflicted file.</summary>
    public ReactiveCommand<RxVoid, RxVoid> MarkResolvedCommand { get; private set; }

    [MemberNotNull(nameof(AbortCommand), nameof(ContinueCommand), nameof(OpenConflictCommand), nameof(AcceptOursCommand),
        nameof(AcceptTheirsCommand), nameof(MarkResolvedCommand))]
    private void InitializeConflicts()
    {
        AbortCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => _executeCommand(GitCommands.ABORT, null),
            this.WhenAnyValue(x => x.IsOperationInProgress)));
        ContinueCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => _executeCommand(GitCommands.CONTINUE, null),
            this.WhenAnyValue(x => x.IsOperationInProgress, x => x.IsMerging, x => x.ConflictCount,
                (inProgress, merging, conflicts) => inProgress && !merging && conflicts == 0)));
        IObservable<bool> hasSelection = this.WhenAnyValue(x => x.SelectedConflict).Select(entry => entry is not null);
        OpenConflictCommand = AddCommand(ReactiveCommand.CreateFromTask(OpenConflictAsync, hasSelection));
        AcceptOursCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForConflictAsync(GitCommands.ACCEPT_OURS), hasSelection));
        AcceptTheirsCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForConflictAsync(GitCommands.ACCEPT_THEIRS), hasSelection));
        MarkResolvedCommand = AddCommand(ReactiveCommand.CreateFromTask(
            () => ExecuteForConflictAsync(GitCommands.MARK_RESOLVED), hasSelection));

        // An unmerged file has no diff the panel can show; the view shows a hint instead.
        _disposables.Add(this.WhenAnyValue(x => x.SelectedConflict)
            .Where(entry => entry is not null)
            .Subscribe(_ =>
            {
                SelectedStagedChange = null;
                SelectedUnstagedChange = null;
            }));
    }

    private void ApplyOperation(GitSnapshot snapshot)
    {
        GitRepositoryOperation operation = snapshot.Operation;
        // An unchanged list keeps its items, so the selection does not flicker on every refresh.
        GitConflictEntryViewModel[] entries = snapshot.Changes
            .Where(change => change.IsConflicted)
            .Select(change => new GitConflictEntryViewModel(change))
            .ToArray();
        if (!entries.Select(entry => (entry.Path, entry.KindLabel))
                .SequenceEqual(_conflictItems.Select(entry => (entry.Path, entry.KindLabel))))
        {
            string? selectedPath = SelectedConflict?.Path;
            _conflictItems.Clear();
            foreach (GitConflictEntryViewModel entry in entries)
            {
                _conflictItems.Add(entry);
            }

            SelectedConflict = selectedPath is null
                ? null
                : _conflictItems.FirstOrDefault(entry => string.Equals(entry.Path, selectedPath, StringComparison.Ordinal));
        }

        ConflictCount = _conflictItems.Count;
        HasConflicts = ConflictCount > 0;
        IsOperationInProgress = operation != GitRepositoryOperation.None;
        IsMerging = operation == GitRepositoryOperation.Merging;
        IsCommitAllowed = snapshot.AllowsCommit;
        string? name = GitOperationNames.Get(operation);
        OperationSummary = name is null
            ? string.Empty
            : name + " · " + (ConflictCount > 0
                ? string.Format(CultureInfo.CurrentCulture, GitStrings.ConflictCountFormat, ConflictCount)
                : IsMerging ? GitStrings.MergeReadyToCommit : GitStrings.OperationReadyToContinue);
        if (!snapshot.AllowsAmend)
        {
            IsAmend = false;
        }

        // Only a merge is completed by a commit, the other operations commit each step with git's message when they
        // continue. git writes MERGE_HEAD before MERGE_MSG, so every snapshot of the merge tries again until the
        // message was read; a message the user changed or cleared afterwards stays.
        _merging = IsMerging;
        if (!IsMerging)
        {
            if (_preparedMessage is not null && string.Equals(CommitMessage, _preparedMessage, StringComparison.Ordinal))
            {
                CommitMessage = string.Empty;
            }

            _preparedMessage = null;
        }
        else if (_preparedMessage is null && !_fillingMessage)
        {
            _ = FillPreparedMessageAsync();
        }
    }

    // Started on the UI thread, so the code after the await runs there again.
    private async System.Threading.Tasks.Task FillPreparedMessageAsync()
    {
        if (!string.IsNullOrWhiteSpace(CommitMessage) || RepositoryRoot is not { } root || _session.Services.Merge is not { } merge)
        {
            return;
        }

        _fillingMessage = true;
        try
        {
            string? message = await merge.GetPreparedMessageAsync(root, _lifetime.Token);
            if (message is not null && _merging && _preparedMessage is null && string.IsNullOrWhiteSpace(CommitMessage))
            {
                _preparedMessage = message;
                CommitMessage = message;
            }
        }
        catch (InvalidOperationException exception)
        {
            ErrorMessage = string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, exception.Message);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _fillingMessage = false;
        }
    }

    private System.Threading.Tasks.Task ExecuteForConflictAsync(string commandId) =>
        SelectedConflict is { } entry ? _executeCommand(commandId, [entry.Path]) : System.Threading.Tasks.Task.CompletedTask;

    private System.Threading.Tasks.Task OpenConflictAsync() =>
        SelectedConflict is { } entry && RepositoryRoot is { } root && _openFile is not null
            ? _openFile(System.IO.Path.GetFullPath(System.IO.Path.Combine(root, entry.Path)))
            : System.Threading.Tasks.Task.CompletedTask;
}
