using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    // Argument 0 is the commit the History panel passes; without it, the tag is created at HEAD.
    public async Task CreateTagAsync(CommandContext context)
    {
        if (!await EnsureBranchServiceAsync())
        {
            return;
        }

        string revision = FirstStringArgument(context) ?? "HEAD";
        string? name = await AskAsync(GitStrings.CreateTagTitle, GitStrings.CreateTagPrompt, null);
        if (name is null)
        {
            return;
        }

        // An empty message makes a lightweight tag; only a cancelled question stops.
        string? message = await _window.ShowInputBoxAsync(
            new InputBoxOptions(GitStrings.CreateTagTitle, string.Format(CultureInfo.CurrentCulture, GitStrings.TagMessagePromptFormat, name), null),
            CancellationToken.None);
        if (message is null)
        {
            return;
        }

        await TryRunAsync<bool>(async (branches, root, ct) =>
        {
            await branches.CreateTagAsync(root, name, revision, message.Trim(), ct);
            return true;
        });
    }

    public async Task DeleteTagAsync(CommandContext context)
    {
        if (!await EnsureBranchServiceAsync()
            || await PickTagAsync(context, GitStrings.DeleteTagCommand) is not { } tag
            || !await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.DeleteTagPromptFormat, tag.Name),
                GitStrings.DeleteTagConfirm,
                GitStrings.DeleteTagCommand,
                isDestructive: true))
        {
            return;
        }

        string name = tag.Name;
        await TryRunAsync<bool>(async (branches, root, ct) =>
        {
            await branches.DeleteTagAsync(root, name, ct);
            return true;
        });
    }

    public async Task PushTagAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync()
            || await PickTagAsync(context, GitStrings.PushTagCommand) is not { } tag
            || await PickRemoteAsync(name: null, GitStrings.PushTagCommand) is not { } remote)
        {
            return;
        }

        string name = tag.Name;
        GitRemoteResult? result = await RunWithProgressAsync(
            GitStrings.PushTagCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.PushTagProgressFormat, name, remote.Name),
            (services, root, ct) => services.Remotes!.PushTagAsync(root, remote.Name, name, ct));
        if (result is { Success: false, FailureKind: GitRemoteFailureKind.Rejected })
        {
            // The hint of a rejected branch push, to pull first, does not fit a tag.
            await ShowErrorAsync(
                string.Format(
                    CultureInfo.CurrentCulture,
                    GitStrings.RemoteFailureFormat,
                    string.Format(CultureInfo.CurrentCulture, GitStrings.TagRejectedHintFormat, name, remote.Name),
                    GitUrlCredentials.Mask(GitRemoteErrorClassifier.SelectDecisiveLine(result.Message))),
                HasGitOutput(result));
            return;
        }

        await ReportRemoteResultAsync(result, string.Format(CultureInfo.CurrentCulture, GitStrings.TagPushedFormat, name, remote.Name));
    }

    public async Task DeleteRemoteTagAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync()
            || await PickTagAsync(context, GitStrings.DeleteRemoteTagCommand) is not { } tag
            || await PickRemoteAsync(name: null, GitStrings.DeleteRemoteTagCommand) is not { } remote
            || !await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.DeleteRemoteTagPromptFormat, tag.Name, remote.Name),
                GitStrings.DeleteTagConfirm,
                GitStrings.DeleteRemoteTagCommand,
                isDestructive: true))
        {
            return;
        }

        string name = tag.Name;
        GitRemoteResult? result = await RunWithProgressAsync(
            GitStrings.DeleteRemoteTagCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.DeleteRemoteTagProgressFormat, name, remote.Name),
            (services, root, ct) => services.Remotes!.DeleteRemoteTagAsync(root, remote.Name, name, ct));
        await ReportRemoteResultAsync(result, string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteTagDeletedFormat, name, remote.Name));
    }

    /// <summary>
    /// Returns the tag named in argument 0 (short or full name), or one picked from a list; null after reporting why
    /// there is none.
    /// </summary>
    private async Task<GitTag?> PickTagAsync(CommandContext context, string title)
    {
        if (_session.Services.History is not { } history)
        {
            await ShowInformationAsync(GitStrings.TagsUnavailable);
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

        if (FirstStringArgument(context) is { } argument)
        {
            GitTag? match = tags.FirstOrDefault(tag =>
                string.Equals(tag.Name, argument, StringComparison.Ordinal) || string.Equals(tag.FullName, argument, StringComparison.Ordinal));
            if (match is null)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.TagNotFoundFormat, argument));
            }

            return match;
        }

        if (tags.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NoTags);
            return null;
        }

        // The tag names are distinct, so the index of the picked item is its tag.
        QuickPickItem[] items = tags
            .Select(tag => new QuickPickItem(tag.Name, tag.TargetShortSha, tag.Subject.Length == 0 ? null : tag.Subject))
            .ToArray();
        QuickPickItem? picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions(DialogTitle(title), false), CancellationToken.None);
        int index = picked is null ? -1 : Array.IndexOf(items, picked);
        return index < 0 ? null : tags[index];
    }
}
