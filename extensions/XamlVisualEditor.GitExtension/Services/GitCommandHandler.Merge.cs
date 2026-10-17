using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public async Task MergeAsync(CommandContext context)
    {
        if (await LoadBranchesAsync() is not { } branches)
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

            target = await PickBranchAsync(candidates, GitStrings.MergePickTitle);
            if (target is null)
            {
                return;
            }
        }

        // The short name keeps the message that git writes: "Merge branch 'feature/x'".
        string name = target.Name;
        string current = _session.Current.BranchName ?? "HEAD";
        GitMergeResult? result = await TryRunAsync<GitMergeResult>(async (branches, root, ct) =>
            await branches.MergeAsync(root, name, ct));
        string? message = result switch
        {
            GitMergeResult.Merged => string.Format(CultureInfo.CurrentCulture, GitStrings.MergedFormat, target.Name, current),
            GitMergeResult.AlreadyUpToDate => string.Format(CultureInfo.CurrentCulture, GitStrings.MergeUpToDateFormat, target.Name, current),
            GitMergeResult.Conflicts => string.Format(CultureInfo.CurrentCulture, GitStrings.MergeConflictsFormat, target.Name),
            _ => null
        };
        if (message is not null)
        {
            await ShowInformationAsync(message);
        }
    }

    public async Task AbortMergeAsync(CommandContext context)
    {
        if (!await ConfirmAsync(GitStrings.AbortMergePrompt, GitStrings.AbortMergeConfirm, GitStrings.AbortMergeTitle, isDestructive: true))
        {
            return;
        }

        bool? aborted = await TryRunAsync<bool>(async (branches, root, ct) =>
        {
            await branches.AbortMergeAsync(root, ct);
            return true;
        });
        if (aborted == true)
        {
            await ShowInformationAsync(GitStrings.MergeAborted);
        }
    }
}
