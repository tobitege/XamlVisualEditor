using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public async Task MergeAsync(CommandContext context)
    {
        if (!await EnsureMergeServiceAsync() || await RefuseDuringOperationAsync() || await LoadBranchesAsync() is not { } branches)
        {
            return;
        }

        GitBranch? target;
        if (FirstStringArgument(context) is { } argument)
        {
            target = FindBranch(branches, argument);
            if (target is null)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.BranchNotFoundFormat, argument));
                return;
            }
        }
        else
        {
            GitBranch[] candidates = branches.Where(branch => !branch.IsCurrent).ToArray();
            if (candidates.Length == 0)
            {
                await ShowInformationAsync(GitStrings.NoBranchToMerge);
                return;
            }

            // The list starts on its first branch, so a plain Enter picks one; the merge, which the editor cannot undo,
            // asks first, and Enter there cancels.
            target = await PickBranchAsync(candidates, GitStrings.MergePickTitle);
            if (target is null
                || !await ConfirmAsync(
                    string.Format(CultureInfo.CurrentCulture, GitStrings.MergePromptFormat, target.Name, CurrentBranchName()),
                    GitStrings.MergeConfirm,
                    GitStrings.MergeCommand,
                    isDestructive: true))
            {
                return;
            }
        }

        // The short name keeps the message that git writes: "Merge branch 'feature/x'".
        string name = target.Name;
        string current = CurrentBranchName();
        GitMergeResult? result = await TryRunMergeAsync<GitMergeResult>(async (merge, root, ct) =>
            await merge.MergeAsync(root, name, ct));
        switch (result)
        {
            case GitMergeResult.Merged:
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.MergedFormat, target.Name, current));
                break;
            case GitMergeResult.AlreadyUpToDate:
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.MergeUpToDateFormat, target.Name, current));
                break;
            case GitMergeResult.Stopped:
                await _viewHost.ShowAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.MergeStoppedFormat, target.Name));
                break;
        }
    }

    public async Task AbortAsync(CommandContext context)
    {
        GitSnapshot snapshot = _session.Current;
        if (!await EnsureMergeServiceAsync())
        {
            return;
        }

        if (GitOperationNames.Get(snapshot.Operation) is not { } operation)
        {
            await ShowInformationAsync(GitStrings.NoOperationInProgress);
            return;
        }

        string prompt = string.Format(CultureInfo.CurrentCulture, GitStrings.AbortPromptFormat, operation);
        if (!await ConfirmAsync(prompt, GitStrings.AbortConfirm, GitStrings.AbortTitle, isDestructive: true))
        {
            return;
        }

        // The operation is checked again: another program may have ended or changed it while the question was open.
        GitRepositoryOperation confirmed = snapshot.Operation;
        bool? aborted = await TryRunMergeAsync<bool>(async (merge, root, ct) => await merge.AbortAsync(root, confirmed, ct));
        if (aborted is not null)
        {
            await ShowInformationAsync(aborted.Value ? GitStrings.OperationAborted : GitStrings.OperationChanged);
        }
    }

    // A step that became empty after its conflicts were resolved cannot be continued, only skipped.
    public async Task SkipAsync(CommandContext context)
    {
        GitSnapshot snapshot = _session.Current;
        if (!await EnsureMergeServiceAsync())
        {
            return;
        }

        if (snapshot.Operation is GitRepositoryOperation.None or GitRepositoryOperation.Merging
            || GitOperationNames.Get(snapshot.Operation) is not { } operation)
        {
            await ShowInformationAsync(GitStrings.NoStepToSkip);
            return;
        }

        // git rebase --skip resets the whole working tree, the others only the files of the step.
        string promptFormat = snapshot.Operation == GitRepositoryOperation.Rebasing
            ? GitStrings.SkipRebasePromptFormat
            : GitStrings.SkipPromptFormat;
        if (!await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, promptFormat, operation),
                GitStrings.SkipConfirm,
                GitStrings.SkipTitle,
                isDestructive: true))
        {
            return;
        }

        // The step is checked again: the operation may have gone on elsewhere while the question was open.
        GitRepositoryOperation confirmed = snapshot.Operation;
        string? step = snapshot.Status?.OperationStep;
        bool? skipped = await TryRunMergeAsync<bool>(async (merge, root, ct) => await merge.SkipAsync(root, confirmed, step, ct));
        if (skipped == false)
        {
            await ShowInformationAsync(GitStrings.OperationChangedNothingSkipped);
        }
    }

    public Task CherryPickAsync(CommandContext context) =>
        ApplyCommitAsync(context, GitStrings.CherryPickStopped, (merge, root, commit, ct) => merge.CherryPickAsync(root, commit, ct));

    public Task RevertAsync(CommandContext context) =>
        ApplyCommitAsync(context, GitStrings.RevertStopped, (merge, root, commit, ct) => merge.RevertAsync(root, commit, ct));

    /// <summary>
    /// Cherry-picks or reverts the commit in argument 0, which the History panel passes; without it, shows the panel to
    /// pick one there.
    /// </summary>
    private async Task ApplyCommitAsync(
        CommandContext context,
        string stoppedMessage,
        Func<IGitMergeService, string, string, CancellationToken, Task<GitMergeResult>> apply)
    {
        if (!await EnsureMergeServiceAsync() || await RefuseDuringOperationAsync())
        {
            return;
        }

        if (FirstStringArgument(context) is not { } commit)
        {
            await _viewHost.ShowAsync(GitCommands.HISTORY_VIEW_ID, CancellationToken.None);
            await ShowInformationAsync(GitStrings.PickCommitInHistory);
            return;
        }

        GitMergeResult? result = await TryRunMergeAsync<GitMergeResult>(async (merge, root, ct) => await apply(merge, root, commit, ct));
        if (result == GitMergeResult.Stopped)
        {
            await _viewHost.ShowAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);
            await ShowInformationAsync(stoppedMessage);
        }
    }

    // Rewriting the commits of the current branch cannot be undone in the editor, so it always asks.
    public async Task RebaseAsync(CommandContext context)
    {
        if (!await EnsureMergeServiceAsync() || await RefuseDuringOperationAsync() || await LoadBranchesAsync() is not { } branches)
        {
            return;
        }

        GitBranch? target;
        if (FirstStringArgument(context) is { } argument)
        {
            target = FindBranch(branches, argument);
            if (target is null)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.BranchNotFoundFormat, argument));
                return;
            }
        }
        else
        {
            GitBranch[] candidates = branches.Where(branch => !branch.IsCurrent).ToArray();
            if (candidates.Length == 0)
            {
                await ShowInformationAsync(GitStrings.NoBranchToRebase);
                return;
            }

            target = await PickBranchAsync(candidates, GitStrings.RebasePickTitle);
            if (target is null)
            {
                return;
            }
        }

        string current = CurrentBranchName();
        string? branch = _session.Current.IsDetached ? null : current;
        if (!await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.RebasePromptFormat, current, target.Name),
                GitStrings.RebaseConfirm,
                GitStrings.RebaseCommand,
                isDestructive: true))
        {
            return;
        }

        // The confirmed branch is rebased, also when another one was checked out while the question was open.
        string upstream = target.FullName;
        GitMergeResult? result = await TryRunMergeAsync<GitMergeResult>(async (merge, root, ct) =>
            await merge.RebaseAsync(root, upstream, branch, ct));
        switch (result)
        {
            case GitMergeResult.Merged:
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.RebasedFormat, current, target.Name));
                break;
            case GitMergeResult.AlreadyUpToDate:
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.RebaseUpToDateFormat, current, target.Name));
                break;
            case GitMergeResult.Stopped:
                await _viewHost.ShowAsync(GitCommands.CHANGES_VIEW_ID, CancellationToken.None);
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.RebaseStoppedFormat, current, target.Name));
                break;
        }
    }

    private string CurrentBranchName() => _session.Current.BranchName is { Length: > 0 } branch ? branch : "HEAD";

    // A merge is completed by a commit; git's own merge --continue is that commit.
    public async Task ContinueAsync(CommandContext context)
    {
        GitSnapshot snapshot = _session.Current;
        if (!await EnsureMergeServiceAsync())
        {
            return;
        }

        string? message = snapshot.Operation switch
        {
            GitRepositoryOperation.None => GitStrings.NoOperationInProgress,
            GitRepositoryOperation.Merging => GitStrings.ContinueMergeWithCommit,
            _ when snapshot.HasConflicts => GitStrings.ContinueWithConflicts,
            _ => null
        };
        if (message is not null)
        {
            await ShowInformationAsync(message);
            return;
        }

        // git continues to the end once it started; a hook that hangs can be stopped after a wait and a confirmation.
        await RunWithProgressAsync(
            GitStrings.ContinueCommand,
            GitStrings.ContinueProgress,
            (services, root, ct) => services.Merge!.ContinueAsync(root, ct),
            writes: true);
    }

    public Task AcceptOursAsync(CommandContext context) =>
        TakeSideAsync(context, GitConflictSide.Ours, GitStrings.AcceptOursPromptFormat, GitStrings.ResolveOursLabel);

    public Task AcceptTheirsAsync(CommandContext context) =>
        TakeSideAsync(context, GitConflictSide.Theirs, GitStrings.AcceptTheirsPromptFormat, GitStrings.ResolveTheirsLabel);

    // Staging marks a conflict resolved. The saved files are what git stages, so a file whose editor buffer differs
    // from it has to be saved first, and the saved files are checked for markers.
    public async Task MarkResolvedAsync(CommandContext context)
    {
        if (await ResolveConflictsAsync(context) is not { } conflicts || _session.Current.RepositoryRoot is not { } root)
        {
            return;
        }

        IReadOnlyList<string> paths = GitCommandTarget.PathsOf(conflicts);
        string[] unsaved = await FindUnsavedAsync(root, paths);
        if (unsaved.Length > 0)
        {
            await ShowInformationAsync(string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.MarkResolvedUnsavedFormat,
                string.Join(Environment.NewLine, unsaved)));
            return;
        }

        string[] withMarkers = await Task.Run(() => paths.Where(path => HasConflictMarkers(Path.Combine(root, path))).ToArray());
        if (withMarkers.Length > 0
            && !await ConfirmAsync(
                string.Format(
                    CultureInfo.CurrentCulture,
                    GitStrings.MarkResolvedWithMarkersPromptFormat,
                    string.Join(Environment.NewLine, withMarkers)),
                GitStrings.MarkResolvedCommand,
                GitStrings.MarkResolvedCommand,
                isDestructive: true))
        {
            return;
        }

        await RunGitAsync((git, repositoryRoot, ct) => git.StageAsync(repositoryRoot, paths, ct));
    }

    // Argument 0 is the file and argument 1 the 1-based line, as a click on a conflict mark passes them; without them
    // the block at the caret of the active document is resolved. Only the editor buffer changes, in one undo step.
    public async Task ResolveConflictAsync(CommandContext context)
    {
        if (_editor is null)
        {
            return;
        }

        IEditorDocument? document;
        int line;
        if (context.Arguments is [string path, int clickedLine, ..])
        {
            document = await _editor.OpenDocumentAsync(path, CancellationToken.None);
            line = clickedLine;
        }
        else
        {
            document = _editor.ActiveDocument;
            line = document is null ? 0 : LineAt(await document.GetTextAsync(CancellationToken.None), document.CaretOffset);
        }

        if (document is null)
        {
            await ShowInformationAsync(GitStrings.NoTargetPath);
            return;
        }

        GitConflictBlock? block = GitConflictParser.FindAt(
            GitConflictParser.Parse(await document.GetTextAsync(CancellationToken.None)),
            line);
        if (block is null)
        {
            await ShowInformationAsync(GitStrings.NoConflictAtLine);
            return;
        }

        (QuickPickItem Item, GitConflictResolution Resolution)[] choices =
        [
            (new(GitStrings.ResolveOursLabel, block.OursLabel, null), GitConflictResolution.Ours),
            (new(GitStrings.ResolveTheirsLabel, block.TheirsLabel, null), GitConflictResolution.Theirs),
            (new(GitStrings.ResolveBothLabel, null, null), GitConflictResolution.Both)
        ];
        QuickPickItem? picked = await _window.ShowQuickPickAsync(
            choices.Select(choice => choice.Item).ToArray(),
            new QuickPickOptions(DialogTitle(GitStrings.ResolveConflictCommand), false),
            CancellationToken.None);
        int index = Array.FindIndex(choices, choice => ReferenceEquals(choice.Item, picked));
        if (index < 0)
        {
            return;
        }

        // The buffer may have changed while the list was open; the block is replaced only when it is still the same.
        GitConflictBlock? current = GitConflictParser.FindAt(
            GitConflictParser.Parse(await document.GetTextAsync(CancellationToken.None)),
            line);
        if (current != block)
        {
            await ShowInformationAsync(GitStrings.ConflictBlockChanged);
            return;
        }

        GitConflictResolution resolution = choices[index].Resolution;
        await document.ApplyEditsAsync(
            [new TextEdit { Offset = block.Start, Length = block.End - block.Start, NewText = GitConflictParser.Resolve(block, resolution) }],
            CancellationToken.None);
    }

    /// <summary>Returns the commit message git prepared for the operation in progress, or null.</summary>
    private async Task<string?> ReadPreparedMessageAsync()
    {
        try
        {
            return await _session.RunWithServicesAsync<string?>(
                (services, root, ct) => services.Merge?.GetPreparedMessageAsync(root, ct) ?? Task.FromResult<string?>(null));
        }
        catch (InvalidOperationException exception)
        {
            _logger?.Error(exception.Message, exception);
            return null;
        }
    }

    private async Task TakeSideAsync(CommandContext context, GitConflictSide side, string promptFormat, string title)
    {
        if (!await EnsureMergeServiceAsync() || await ResolveConflictsAsync(context) is not { } conflicts)
        {
            return;
        }

        IReadOnlyList<string> paths = GitCommandTarget.PathsOf(conflicts);
        // Taking a side replaces the files, which an editor with unsaved changes would then hold in another version.
        if (_session.Current.RepositoryRoot is { } repositoryRoot && await FindUnsavedAsync(repositoryRoot, paths) is { Length: > 0 } unsaved)
        {
            await ShowInformationAsync(string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.TakeSideUnsavedFormat,
                string.Join(Environment.NewLine, unsaved)));
            return;
        }

        string files = conflicts.Count == 1
            ? conflicts[0].Path
            : string.Format(CultureInfo.CurrentCulture, GitStrings.FilesCountFormat, conflicts.Count);
        if (!await ConfirmAsync(string.Format(CultureInfo.CurrentCulture, promptFormat, files), title, title, isDestructive: true))
        {
            return;
        }

        if (await TryRunMergeAsync<bool>(async (merge, root, ct) => await merge.TakeSideAsync(root, paths, side, ct)) == false)
        {
            await ShowInformationAsync(GitStrings.ConflictsChanged);
        }
    }

    /// <summary>Returns the conflicted files of the target, or null after reporting that there are none.</summary>
    private async Task<IReadOnlyList<GitFileChange>?> ResolveConflictsAsync(CommandContext context)
    {
        if (await ResolveTargetAsync(context) is not { } target)
        {
            return null;
        }

        IReadOnlyList<GitFileChange> conflicts = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            target.RepositoryPath,
            change => change.IsConflicted);
        if (conflicts.Count == 0)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.NoConflictsFormat, target.DisplayPath));
            return null;
        }

        return conflicts;
    }

    /// <summary>Returns the paths whose open editor document has text other than the saved file.</summary>
    private async Task<string[]> FindUnsavedAsync(string root, IReadOnlyList<string> paths)
    {
        if (_editor is null)
        {
            return [];
        }

        Dictionary<string, string> byFullPath = new(FileSystemPathComparison.Comparer);
        foreach (string path in paths)
        {
            byFullPath[Path.GetFullPath(Path.Combine(root, path))] = path;
        }

        List<string> unsaved = new();
        foreach (IEditorDocument document in _editor.GetOpenDocuments())
        {
            string fullPath = Path.GetFullPath(document.FilePath);
            if (byFullPath.TryGetValue(fullPath, out string? path)
                && await Task.Run(() => ReadSavedText(fullPath)) is { } saved
                && !SameText(await document.GetTextAsync(CancellationToken.None), saved))
            {
                unsaved.Add(path);
            }
        }

        return unsaved.ToArray();
    }

    // The editor may keep other line endings than the file, which is no unsaved change.
    private static bool SameText(string buffer, string saved) =>
        string.Equals(buffer.ReplaceLineEndings("\n"), saved.ReplaceLineEndings("\n"), StringComparison.Ordinal);

    // Null when the file is missing or cannot be read; the marker check below then decides.
    private static string? ReadSavedText(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) ? SharedFileReader.Decode(File.ReadAllBytes(fullPath)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // A deleted file has no markers; a file that cannot be read counts as one with markers, so the user decides.
    private static bool HasConflictMarkers(string fullPath)
    {
        try
        {
            return File.Exists(fullPath) && GitConflictParser.HasConflict(SharedFileReader.Decode(File.ReadAllBytes(fullPath)));
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static int LineAt(string text, int offset)
    {
        int line = 1;
        int end = Math.Clamp(offset, 0, text.Length);
        for (int i = 0; i < end; i++)
        {
            if (text[i] == '\n')
            {
                line++;
            }
        }

        return line;
    }

    /// <summary>
    /// Runs a merge operation through the session; returns null and reports git's error when it failed.
    /// <see cref="EnsureMergeServiceAsync"/> checked that the services have a merge service.
    /// </summary>
    private async Task<T?> TryRunMergeAsync<T>(Func<IGitMergeService, string, CancellationToken, Task<T>> operation)
        where T : struct
    {
        try
        {
            return await _session.RunWithServicesAsync<T?>(async (services, root, ct) => await operation(services.Merge!, root, ct));
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }
    }

    private async Task<bool> EnsureMergeServiceAsync()
    {
        if (!await EnsureRepositoryAsync())
        {
            return false;
        }

        if (_session.Services.Merge is null)
        {
            await ShowInformationAsync(GitStrings.MergeUnavailable);
            return false;
        }

        return true;
    }
}
