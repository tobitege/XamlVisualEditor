using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public Task StashAsync(CommandContext context) => PushStashAsync(includeUntracked: false);

    public Task StashIncludeUntrackedAsync(CommandContext context) => PushStashAsync(includeUntracked: true);

    public async Task StashApplyAsync(CommandContext context)
    {
        if (await PickStashAsync(context, GitStrings.StashApplyCommand) is { } stash)
        {
            await ApplyStashAsync(stash, pop: false);
        }
    }

    public async Task StashPopAsync(CommandContext context)
    {
        if (await PickStashAsync(context, GitStrings.StashPopCommand) is { } stash)
        {
            await ApplyStashAsync(stash, pop: true);
        }
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

        string? message = await _window.ShowInputBoxAsync(
            new InputBoxOptions(
                includeUntracked ? GitStrings.StashIncludeUntrackedCommand : GitStrings.StashCommand,
                GitStrings.StashMessagePrompt,
                null),
            CancellationToken.None);
        if (message is null)
        {
            return;
        }

        await TryRunStashAsync(async (stashes, root, ct) =>
        {
            await stashes.PushAsync(root, message.Trim(), includeUntracked, ct);
            return true;
        });
    }

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
        catch (InvalidOperationException) when (!hadConflicts && _session.Current.HasConflicts)
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
