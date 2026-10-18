using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public async Task StashAsync(CommandContext context)
    {
        if (!await RefuseDuringOperationAsync())
        {
            await PushStashAsync(includeUntracked: false);
        }
    }

    public async Task StashIncludeUntrackedAsync(CommandContext context)
    {
        if (!await RefuseDuringOperationAsync())
        {
            await PushStashAsync(includeUntracked: true);
        }
    }

    // Argument 0 is a file or folder, as the context menus pass it; without it, the active document.
    public async Task StashPathsAsync(CommandContext context)
    {
        if (await RefuseDuringOperationAsync() || !await EnsureStashServiceAsync() || await ResolveTargetAsync(context) is not { } target)
        {
            return;
        }

        IReadOnlyList<GitFileChange> changes = GitCommandTarget.FindChanges(
            _session.Current.Changes,
            target.RepositoryPath,
            change => !change.IsConflicted);
        if (changes.Count == 0)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.NoChangesToStashFormat, target.DisplayPath));
            return;
        }

        if (await AskStashMessageAsync(GitStrings.StashPathsCommand) is not { } message)
        {
            return;
        }

        // Exactly the listed files are stashed, never a file that changed after the last refresh; a rename with its old
        // path, so the stash takes both halves of it.
        string[] paths = changes
            .SelectMany(change => change.OldPath is { } oldPath ? new[] { change.Path, oldPath } : [change.Path])
            .ToArray();
        await TryRunStashAsync(async (stashes, root, ct) =>
        {
            await stashes.PushPathsAsync(root, message, paths, ct);
            return true;
        });
    }

    public async Task StashStagedAsync(CommandContext context)
    {
        if (await RefuseDuringOperationAsync() || !await EnsureStashServiceAsync())
        {
            return;
        }

        if (!_session.Current.Changes.Any(GitCommandTarget.HasStagedChange))
        {
            await ShowInformationAsync(GitStrings.NothingStagedToStash);
            return;
        }

        if (await AskStashMessageAsync(GitStrings.StashStagedCommand) is { } message)
        {
            await TryRunStashAsync(async (stashes, root, ct) =>
            {
                await stashes.PushStagedAsync(root, message, ct);
                return true;
            });
        }
    }

    public async Task StashApplyAsync(CommandContext context)
    {
        if (!await RefuseDuringOperationAsync() && await PickStashAsync(context, GitStrings.StashApplyCommand) is { } stash)
        {
            await ApplyStashAsync(stash, pop: false);
        }
    }

    public async Task StashPopAsync(CommandContext context)
    {
        if (!await RefuseDuringOperationAsync() && await PickStashAsync(context, GitStrings.StashPopCommand) is { } stash)
        {
            await ApplyStashAsync(stash, pop: true);
        }
    }

    /// <summary>
    /// Reports and returns true while an operation is in progress. The When clause hides the commands then, but the
    /// panels and command arguments run them directly; a stash would end a resolved merge without its merge commit.
    /// </summary>
    private async Task<bool> RefuseDuringOperationAsync()
    {
        if (GitOperationNames.Get(_session.Current.Operation) is not { } operation)
        {
            return false;
        }

        await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.RefusedDuringOperationFormat, operation));
        return true;
    }

    public async Task StashDropAsync(CommandContext context)
    {
        if (await PickStashAsync(context, GitStrings.StashDropCommand) is not { } stash
            || !await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.StashDropPromptFormat, stash.Name, stash.Message),
                GitStrings.StashDropConfirm,
                GitStrings.StashDropCommand,
                isDestructive: true))
        {
            return;
        }

        if (await TryRunStashAsync((stashes, root, ct) => stashes.DropAsync(root, stash, ct)) == false)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashMovedFormat, stash.Name));
        }
    }

    public async Task StashShowAsync(CommandContext context)
    {
        if (await PickStashAsync(context, GitStrings.StashShowCommand) is not { } stash)
        {
            return;
        }

        if (_session.Services.History is not { } history)
        {
            await ShowInformationAsync(GitStrings.HistoryUnavailable);
            return;
        }

        GitCommitInfo commit;
        try
        {
            commit = await history.ResolveCommitAsync(_session.Current.RepositoryRoot!, stash.ObjectId, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return;
        }

        // Named by the commit id, in the id and the title: the name stash@{n} moves when a stash is added or removed.
        await _viewHost.ShowDocumentAsync(
            "git.stash:" + stash.ObjectId,
            string.Format(CultureInfo.CurrentCulture, GitStrings.StashTitleFormat, commit.ShortSha),
            stash.Message,
            () => GitCommitDetailsViewModel.ForStash(_session, _executeCommand, commit),
            CancellationToken.None);
    }

    public async Task StashBranchAsync(CommandContext context)
    {
        if (await RefuseDuringOperationAsync() || await PickStashAsync(context, GitStrings.StashBranchCommand) is not { } stash)
        {
            return;
        }

        string? name = (await _window.ShowInputBoxAsync(
            new InputBoxOptions(
                GitStrings.StashBranchCommand,
                string.Format(CultureInfo.CurrentCulture, GitStrings.StashBranchPromptFormat, stash.Name),
                null),
            CancellationToken.None))?.Trim();
        if (string.IsNullOrEmpty(name) || !await IsValidBranchNameAsync(name))
        {
            return;
        }

        // git creates and checks out the branch before it applies the stash; when applying fails, the user is on the
        // new branch and the stash stays.
        try
        {
            if (!await _session.RunWithServicesAsync((services, root, ct) => services.Stash!.BranchAsync(root, stash, name, ct)))
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashMovedFormat, stash.Name));
            }
        }
        catch (InvalidOperationException exception) when (exception is not GitStoppedException
            && string.Equals(_session.Current.BranchName, name, StringComparison.Ordinal))
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashBranchStoppedFormat, name, stash.Name));
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }

    public async Task StashClearAsync(CommandContext context)
    {
        if (!await EnsureStashServiceAsync())
        {
            return;
        }

        IReadOnlyList<GitStashEntry> stashes;
        try
        {
            stashes = await _session.Services.Stash!.ListAsync(_session.Current.RepositoryRoot!, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return;
        }

        if (stashes.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NoStashes);
            return;
        }

        if (!await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.StashClearPromptFormat, stashes.Count),
                GitStrings.StashClearConfirm,
                GitStrings.StashClearCommand,
                isDestructive: true))
        {
            return;
        }

        // Each confirmed stash is dropped at the top of the list after its commit id is checked there, so a stash made
        // while the question was open, or meanwhile, is never dropped.
        int dropped = 0;
        bool? cleared = await TryRunStashAsync(async (service, root, ct) =>
        {
            foreach (GitStashEntry stash in stashes)
            {
                if (!await service.DropAsync(root, stash with { Index = 0 }, ct))
                {
                    return false;
                }

                dropped++;
            }

            return true;
        });
        if (cleared == false)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashesChangedFormat, dropped, stashes.Count));
        }
    }

    // git creates no stash and reports success when nothing can be stashed, so the status is checked first.
    private async Task PushStashAsync(bool includeUntracked)
    {
        if (!await EnsureStashServiceAsync())
        {
            return;
        }

        if (!_session.Current.Changes.Any(change => includeUntracked || !GitCommandTarget.IsUntracked(change)))
        {
            await ShowInformationAsync(GitStrings.NothingToStash);
            return;
        }

        if (await AskStashMessageAsync(includeUntracked ? GitStrings.StashIncludeUntrackedCommand : GitStrings.StashCommand) is not { } message)
        {
            return;
        }

        await TryRunStashAsync(async (stashes, root, ct) =>
        {
            await stashes.PushAsync(root, message, includeUntracked, ct);
            return true;
        });
    }

    /// <summary>Returns the entered stash message without surrounding spaces, empty for git's message, or null when cancelled.</summary>
    private async Task<string?> AskStashMessageAsync(string title) =>
        (await _window.ShowInputBoxAsync(new InputBoxOptions(title, GitStrings.StashMessagePrompt, null), CancellationToken.None))?.Trim();

    /// <summary>
    /// Applies or pops a stash. A stash that applied with conflicts stays in the list; that is reported instead of
    /// git's output when the conflicts are new.
    /// </summary>
    private async Task ApplyStashAsync(GitStashEntry stash, bool pop)
    {
        bool hadConflicts = _session.Current.HasConflicts;
        try
        {
            bool applied = await _session.RunWithServicesAsync(async (services, root, ct) =>
            {
                if (pop)
                {
                    return await services.Stash!.PopAsync(root, stash, ct);
                }

                await services.Stash!.ApplyAsync(root, stash, ct);
                return true;
            });
            if (!applied)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashMovedFormat, stash.Name));
            }
        }
        catch (InvalidOperationException exception) when (exception is not GitStoppedException && !hadConflicts && _session.Current.HasConflicts)
        {
            await ShowInformationAsync(GitStrings.StashConflicts);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }

    /// <summary>
    /// Returns the stash whose commit id is argument 0, or one picked from the list; null after reporting why there is
    /// none.
    /// </summary>
    private async Task<GitStashEntry?> PickStashAsync(CommandContext context, string title)
    {
        if (!await EnsureStashServiceAsync())
        {
            return null;
        }

        IReadOnlyList<GitStashEntry> stashes;
        try
        {
            stashes = await _session.Services.Stash!.ListAsync(_session.Current.RepositoryRoot!, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }

        if (FirstStringArgument(context) is { } objectId)
        {
            GitStashEntry? match = stashes.FirstOrDefault(stash => string.Equals(stash.ObjectId, objectId, StringComparison.Ordinal));
            if (match is null)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.StashNotFoundFormat, objectId));
            }

            return match;
        }

        if (stashes.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NoStashes);
            return null;
        }

        // The names stash@{n} are distinct, so the index of the picked item is its stash.
        QuickPickItem[] items = stashes
            .Select(stash => new QuickPickItem(stash.Name, stash.Message, GitStashItemViewModel.FormatDate(stash)))
            .ToArray();
        QuickPickItem? picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions(title, false), CancellationToken.None);
        int index = picked is null ? -1 : Array.IndexOf(items, picked);
        return index < 0 ? null : stashes[index];
    }

    /// <summary>Runs a stash operation through the session; returns null and reports git's error when it failed.</summary>
    private async Task<bool?> TryRunStashAsync(Func<IGitStashService, string, CancellationToken, Task<bool>> operation)
    {
        try
        {
            return await _session.RunWithServicesAsync((services, root, ct) => operation(services.Stash!, root, ct));
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }
    }

    private async Task<bool> EnsureStashServiceAsync()
    {
        if (!await EnsureRepositoryAsync())
        {
            return false;
        }

        if (_session.Services.Stash is null)
        {
            await ShowInformationAsync(GitStrings.StashesUnavailable);
            return false;
        }

        return true;
    }
}
