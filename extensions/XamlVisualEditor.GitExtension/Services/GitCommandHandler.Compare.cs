using System.Globalization;
using System.Text;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    /// <summary>The number of commits listed per side of a comparison.</summary>
    internal const int COMPARE_COMMIT_COUNT = 50;

    private const string TAG_PREFIX = "refs/tags/";

    // Argument 0 is a branch or tag (full or short name), as the Git Repository panel passes it; without it, one is
    // picked from the branches and tags. The current branch is compared with it in a document tab.
    public async Task CompareBranchesAsync(CommandContext context)
    {
        if (!await EnsureRepositoryAsync())
        {
            return;
        }

        if (_session.Services.History is not { } history)
        {
            await ShowInformationAsync(GitStrings.HistoryUnavailable);
            return;
        }

        string? other = FirstStringArgument(context) ?? await PickRefToCompareAsync(history);
        if (other is null)
        {
            return;
        }

        string root = _session.Current.RepositoryRoot!;
        string current = CurrentBranchName();
        string otherName = other.StartsWith(TAG_PREFIX, StringComparison.Ordinal)
            ? other[TAG_PREFIX.Length..]
            : GitRefParser.ShortenBranchReference(other);
        try
        {
            GitCommitInfo tip = await history.ResolveCommitAsync(root, "HEAD", CancellationToken.None);
            GitCommitInfo theirs = await history.ResolveCommitAsync(root, other, CancellationToken.None);
            if (await history.GetMergeBaseAsync(root, theirs.Sha, tip.Sha, CancellationToken.None) is not { } baseSha)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareNoCommonCommitFormat, current, otherName));
                return;
            }

            GitCommitInfo mergeBase = await history.ResolveCommitAsync(root, baseSha, CancellationToken.None);
            string message = await FormatComparisonCommitsAsync(history, root, (current, tip.Sha), (otherName, theirs.Sha));
            string title = string.Format(CultureInfo.CurrentCulture, GitStrings.BranchCompareTitleFormat, current, otherName);
            string summary = string.Format(CultureInfo.CurrentCulture, GitStrings.CompareSummaryFormat, current, otherName, mergeBase.ShortSha);
            await _viewHost.ShowDocumentAsync(
                "git.compare:" + theirs.Sha + "..." + tip.Sha,
                title,
                summary,
                () => GitCommitDetailsViewModel.ForComparison(_session, _executeCommand, tip, mergeBase, title, summary, message),
                CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }

    /// <summary>Lists the commits that only one side has, the newest <see cref="COMPARE_COMMIT_COUNT"/> per side.</summary>
    private static async Task<string> FormatComparisonCommitsAsync(
        IGitHistoryService history,
        string root,
        (string Name, string Sha) current,
        (string Name, string Sha) other)
    {
        StringBuilder text = new();
        foreach (var (side, without) in new[] { (current, other), (other, current) })
        {
            // One more than listed tells whether there are more.
            IReadOnlyList<GitCommitInfo> commits = await history.GetLogAsync(
                root,
                new GitLogQuery { Revision = without.Sha + ".." + side.Sha, MaxCount = COMPARE_COMMIT_COUNT + 1 },
                CancellationToken.None);
            if (text.Length > 0)
            {
                text.AppendLine().AppendLine();
            }

            if (commits.Count == 0)
            {
                text.Append(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareNoCommitsOnlyOnFormat, side.Name, without.Name));
                continue;
            }

            text.Append(string.Format(CultureInfo.CurrentCulture, GitStrings.CompareCommitsOnlyOnFormat, side.Name, without.Name));
            foreach (GitCommitInfo commit in commits.Take(COMPARE_COMMIT_COUNT))
            {
                text.AppendLine().Append(commit.ShortSha).Append(' ').Append(commit.Subject);
            }

            if (commits.Count > COMPARE_COMMIT_COUNT)
            {
                text.AppendLine().Append(GitStrings.CompareMoreCommits);
            }
        }

        return text.ToString();
    }

    /// <summary>Returns the full name of a branch or tag picked from a list, or null when none was picked.</summary>
    private async Task<string?> PickRefToCompareAsync(IGitHistoryService history)
    {
        if (await LoadBranchesAsync() is not { } branches)
        {
            return null;
        }

        IReadOnlyList<GitTag> tags;
        try
        {
            tags = await history.GetTagsAsync(_session.Current.RepositoryRoot!, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }

        (QuickPickItem Item, string FullName)[] choices = branches
            .Where(branch => !branch.IsCurrent)
            .Select(branch => (new QuickPickItem(
                    branch.Name,
                    branch.IsRemote ? GitStrings.RemoteBranchDescription : null,
                    branch.Subject.Length == 0 ? null : branch.Subject),
                branch.FullName))
            .Concat(tags.Where(tag => tag.IsCommit).Select(tag => (new QuickPickItem(
                    tag.Name,
                    GitStrings.TagDescription,
                    tag.Subject.Length == 0 ? null : tag.Subject),
                tag.FullName)))
            .ToArray();
        if (choices.Length == 0)
        {
            await ShowInformationAsync(GitStrings.NothingToCompare);
            return null;
        }

        QuickPickItem? picked = await _window.ShowQuickPickAsync(
            choices.Select(choice => choice.Item).ToArray(),
            new QuickPickOptions(GitStrings.ComparePickTitle, false),
            CancellationToken.None);
        int index = Array.FindIndex(choices, choice => ReferenceEquals(choice.Item, picked));
        return index < 0 ? null : choices[index].FullName;
    }
}
