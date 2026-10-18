using System.Globalization;
using System.Text;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

/// <summary>
/// Shows the branch, the commits ahead of and behind the upstream, the number of changed files, the operation in
/// progress and the number of conflicts in the status bar, for example "main ↑1 ↓2 · 5 · Merging ⚠2". A click opens
/// the Git Changes panel.
/// </summary>
internal static class GitStatusBar
{
    /// <summary>Gets the status bar priority; the TFVC item uses 40.</summary>
    public const int PRIORITY = 30;

    /// <summary>Shows the snapshot in the item, or hides the item when the workspace is not inside a repository.</summary>
    public static void Update(IStatusBarItem item, GitSnapshot snapshot)
    {
        if (!snapshot.IsRepository)
        {
            item.Hide();
            return;
        }

        if (snapshot.State == GitSessionState.StatusFailed)
        {
            item.Text = GitStrings.StatusBarStatusFailed;
            item.Tooltip = snapshot.ErrorMessage + Environment.NewLine + GitStrings.StatusBarClickHint;
        }
        else
        {
            item.Text = FormatText(snapshot);
            item.Tooltip = FormatTooltip(snapshot);
        }

        item.CommandId = GitCommands.SHOW_CHANGES;
        item.Show();
    }

    /// <summary>Formats the status bar text.</summary>
    public static string FormatText(GitSnapshot snapshot)
    {
        StringBuilder text = new(BranchOrHead(snapshot));
        if (snapshot.AheadBy > 0)
        {
            text.Append(" ↑").Append(snapshot.AheadBy.ToString(CultureInfo.CurrentCulture));
        }

        if (snapshot.BehindBy > 0)
        {
            text.Append(" ↓").Append(snapshot.BehindBy.ToString(CultureInfo.CurrentCulture));
        }

        if (snapshot.ChangeCount > 0)
        {
            text.Append(" · ").Append(snapshot.ChangeCount.ToString(CultureInfo.CurrentCulture));
        }

        if (GitOperationNames.Get(snapshot.Operation) is { } operation)
        {
            text.Append(" · ").Append(operation);
        }

        if (snapshot.ConflictCount > 0)
        {
            text.Append(" ⚠").Append(snapshot.ConflictCount.ToString(CultureInfo.CurrentCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Formats the tooltip: branch, upstream with ahead and behind, changed files, the operation in progress with its
    /// conflicts and the click hint.
    /// </summary>
    public static string FormatTooltip(GitSnapshot snapshot)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        string branch = string.Format(
            culture,
            snapshot.IsDetached ? GitStrings.StatusBarDetachedFormat : GitStrings.StatusBarBranchFormat,
            BranchOrHead(snapshot));
        string upstream = !snapshot.HasUpstream
            ? GitStrings.StatusBarNoUpstream
            : snapshot.IsUpstreamGone
                ? string.Format(culture, GitStrings.UpstreamGoneFormat, snapshot.UpstreamName)
                : string.Format(culture, GitStrings.StatusBarUpstreamFormat, snapshot.UpstreamName, snapshot.AheadBy, snapshot.BehindBy);
        string changes = string.Format(culture, GitStrings.StatusBarChangesFormat, snapshot.ChangeCount);
        StringBuilder tooltip = new(branch);
        tooltip.Append(Environment.NewLine).Append(upstream).Append(Environment.NewLine).Append(changes);
        if (GitOperationNames.Get(snapshot.Operation) is { } operation)
        {
            tooltip.Append(Environment.NewLine).Append(operation);
        }

        if (snapshot.ConflictCount > 0)
        {
            tooltip.Append(Environment.NewLine)
                .Append(string.Format(culture, GitStrings.ConflictCountFormat, snapshot.ConflictCount));
        }

        return tooltip.Append(Environment.NewLine).Append(GitStrings.StatusBarClickHint).ToString();
    }

    private static string BranchOrHead(GitSnapshot snapshot)
    {
        return string.IsNullOrEmpty(snapshot.BranchName) ? "HEAD" : snapshot.BranchName;
    }
}
