using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public async Task AddRemoteAsync(CommandContext context)
    {
        if (await LoadRemotesAsync() is not { } remotes)
        {
            return;
        }

        // The first remote of a repository is usually origin, as git names the remote of a clone.
        string? name = await AskAsync(GitStrings.AddRemoteCommand, GitStrings.RemoteNamePrompt, remotes.Count == 0 ? GitRemote.DEFAULT_NAME : null);
        if (name is null || await RefuseOptionLikeNameAsync(name))
        {
            return;
        }

        string? url = await AskAsync(
            GitStrings.AddRemoteCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteUrlPromptFormat, name),
            null);
        if (url is not null)
        {
            await TryRunRemoteAsync((service, root, ct) => service.AddRemoteAsync(root, name, url, ct));
        }
    }

    public async Task RenameRemoteAsync(CommandContext context)
    {
        if (await PickRemoteAsync(context, GitStrings.RenameRemoteCommand) is not { } remote)
        {
            return;
        }

        string? newName = await AskAsync(
            GitStrings.RenameRemoteCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.RenameRemotePromptFormat, remote.Name),
            remote.Name);
        if (newName is not null
            && !string.Equals(newName, remote.Name, StringComparison.Ordinal)
            && !await RefuseOptionLikeNameAsync(newName))
        {
            await TryRunRemoteAsync((service, root, ct) => service.RenameRemoteAsync(root, remote.Name, newName, ct));
        }
    }

    public async Task SetRemoteUrlAsync(CommandContext context)
    {
        if (await PickRemoteAsync(context, GitStrings.SetRemoteUrlCommand) is not { } remote)
        {
            return;
        }

        // git remote set-url changes the URL only; a push URL of its own stays, which the prompt says.
        string prompt = string.Equals(remote.PushUrl, remote.FetchUrl, StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteUrlPromptFormat, remote.Name)
            : string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteUrlKeepsPushUrlPromptFormat, remote.Name, GitUrlCredentials.Mask(remote.PushUrl));
        // The field shows the URL as it is, to be edited; the prompt says when that shows credentials.
        if (!string.Equals(GitUrlCredentials.Mask(remote.FetchUrl), remote.FetchUrl, StringComparison.Ordinal))
        {
            prompt += " " + GitStrings.RemoteUrlShowsCredentials;
        }

        string? url = await AskAsync(GitStrings.SetRemoteUrlCommand, prompt, remote.FetchUrl);
        if (url is not null && !string.Equals(url, remote.FetchUrl, StringComparison.Ordinal))
        {
            await TryRunRemoteAsync((service, root, ct) => service.SetRemoteUrlAsync(root, remote.Name, url, ct));
        }
    }

    public async Task RemoveRemoteAsync(CommandContext context)
    {
        if (await PickRemoteAsync(context, GitStrings.RemoveRemoteCommand) is not { } remote
            || !await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.RemoveRemotePromptFormat, remote.Name),
                GitStrings.RemoveRemoteConfirm,
                GitStrings.RemoveRemoteCommand,
                isDestructive: true))
        {
            return;
        }

        await TryRunRemoteAsync((service, root, ct) => service.RemoveRemoteAsync(root, remote.Name, ct));
    }

    /// <summary>
    /// Reports and returns true for a name that starts with "-": git accepts it, but a command that passes it without
    /// "--" reads it as an option.
    /// </summary>
    private async Task<bool> RefuseOptionLikeNameAsync(string name)
    {
        if (!name.StartsWith('-'))
        {
            return false;
        }

        await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.InvalidRemoteNameFormat, name));
        return true;
    }

    /// <summary>Returns the remote named in argument 0, or one picked from a list; null after reporting why there is none.</summary>
    private Task<GitRemote?> PickRemoteAsync(CommandContext context, string title) =>
        PickRemoteAsync(FirstStringArgument(context), title);

    /// <summary>Returns the remote <paramref name="name"/>, or without a name one picked from a list; null after reporting why there is none.</summary>
    private async Task<GitRemote?> PickRemoteAsync(string? name, string title)
    {
        if (await LoadRemotesAsync() is not { } remotes)
        {
            return null;
        }

        if (name is not null)
        {
            GitRemote? match = remotes.FirstOrDefault(remote => string.Equals(remote.Name, name, StringComparison.Ordinal));
            if (match is null)
            {
                await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteNotFoundFormat, name));
            }

            return match;
        }

        if (remotes.Count == 0)
        {
            await ShowInformationAsync(GitStrings.NoRemote);
            return null;
        }

        // The remote names are distinct, so the index of the picked item is its remote.
        QuickPickItem[] items = remotes
            .Select(remote => new QuickPickItem(remote.Name, GitUrlCredentials.Mask(remote.FetchUrl), null))
            .ToArray();
        QuickPickItem? picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions(title, false), CancellationToken.None);
        int index = picked is null ? -1 : Array.IndexOf(items, picked);
        return index < 0 ? null : remotes[index];
    }

    /// <summary>Returns the remotes of the repository, or null after reporting why there are none.</summary>
    private async Task<IReadOnlyList<GitRemote>?> LoadRemotesAsync()
    {
        if (!await EnsureRemoteServiceAsync())
        {
            return null;
        }

        try
        {
            return await _session.Services.Remotes!.GetRemotesAsync(_session.Current.RepositoryRoot!, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }
    }

    /// <summary>Returns the entered text without surrounding spaces, or null when it was cancelled or is empty.</summary>
    private async Task<string?> AskAsync(string title, string prompt, string? value)
    {
        string? text = (await _window.ShowInputBoxAsync(new InputBoxOptions(title, prompt, value), CancellationToken.None))?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Runs a remote operation through the session and reports git's error when it failed.</summary>
    private async Task TryRunRemoteAsync(Func<IGitRemoteService, string, CancellationToken, Task> operation)
    {
        try
        {
            await _session.RunWithServicesAsync(async (services, root, ct) =>
            {
                await operation(services.Remotes!, root, ct);
                return true;
            });
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
        }
    }
}
