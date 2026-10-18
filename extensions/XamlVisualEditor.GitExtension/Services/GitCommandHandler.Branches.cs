using System.Globalization;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.GitExtension.Localization;

namespace XamlVisualEditor.GitExtension.Services;

internal sealed partial class GitCommandHandler
{
    public Task ToggleRepositoryViewAsync(CommandContext context) =>
        _viewHost.ToggleAsync(GitCommands.REPOSITORY_VIEW_ID, CancellationToken.None);

    public async Task CheckoutAsync(CommandContext context)
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
            target = await PickBranchAsync(branches, GitStrings.CheckoutPickTitle);
            if (target is null)
            {
                return;
            }
        }

        if (target.IsRemote)
        {
            await CheckoutRemoteAsync(branches, target);
        }
        else
        {
            await SwitchToAsync(target);
        }
    }

    public async Task CreateBranchAsync(CommandContext context)
    {
        if (await EnsureBranchServiceAsync())
        {
            await CreateBranchFromAsync(FirstStringArgument(context));
        }
    }

    public async Task DeleteBranchAsync(CommandContext context)
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
            GitBranch[] candidates = branches.Where(branch => !branch.IsRemote && !branch.IsCurrent).ToArray();
            if (candidates.Length == 0)
            {
                await ShowInformationAsync(GitStrings.NoBranchToDelete);
                return;
            }

            target = await PickBranchAsync(candidates, GitStrings.DeleteBranchPickTitle);
            if (target is null)
            {
                return;
            }
        }

        if (target.IsRemote)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.CannotDeleteRemoteFormat, target.Name));
            return;
        }

        if (target.IsCurrent)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.CannotDeleteCurrentFormat, target.Name));
            return;
        }

        string name = target.Name;
        if (!await ConfirmAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.DeleteBranchPromptFormat, name),
                GitStrings.DeleteBranchConfirm))
        {
            return;
        }

        GitBranchDeleteResult? result = await DeleteAsync(name, force: false);
        if (result == GitBranchDeleteResult.NotFullyMerged)
        {
            if (!await ConfirmAsync(
                    string.Format(CultureInfo.CurrentCulture, GitStrings.DeleteUnmergedPromptFormat, name),
                    GitStrings.DeleteUnmergedConfirm))
            {
                return;
            }

            result = await DeleteAsync(name, force: true);
        }

        if (result == GitBranchDeleteResult.Deleted)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.BranchDeletedFormat, name));
        }
    }

    public async Task FetchAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync())
        {
            return;
        }

        GitRemoteResult? result = await RunWithProgressAsync(
            GitStrings.FetchCommand,
            GitStrings.FetchProgress,
            (services, root, ct) => services.Remotes!.FetchAsync(root, ct));
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            await ShowInformationAsync(GitStrings.FetchSucceeded);
            return;
        }

        string failure = FormatRemoteFailure(result);
        await ShowErrorAsync(
            result.FetchedRemotes.Count == 0
                ? failure
                : string.Format(
                    CultureInfo.CurrentCulture,
                    GitStrings.FetchPartlyFailedFormat,
                    string.Join(", ", result.FetchedRemotes),
                    failure),
            HasGitOutput(result));
    }

    public async Task PullAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync())
        {
            return;
        }

        GitSnapshot snapshot = _session.Current;
        if (!await EnsureUpstreamAsync(snapshot))
        {
            return;
        }

        GitRemoteResult? result = await RunWithProgressAsync(
            GitStrings.PullCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.PullProgressFormat, snapshot.BranchName),
            (services, root, ct) => services.Remotes!.PullAsync(root, ct));
        await ReportRemoteResultAsync(result, FormatPullSucceeded(result, snapshot));
    }

    /// <summary>
    /// Tells a fast-forward with the number of pulled commits from a branch that was already up to date; without a
    /// number, only that the pull succeeded.
    /// </summary>
    private static string FormatPullSucceeded(GitRemoteResult? result, GitSnapshot snapshot)
    {
        return result?.PulledCommitCount switch
        {
            null => string.Format(CultureInfo.CurrentCulture, GitStrings.PullSucceededFormat, snapshot.BranchName, snapshot.UpstreamName),
            0 => string.Format(CultureInfo.CurrentCulture, GitStrings.PullUpToDateFormat, snapshot.BranchName, snapshot.UpstreamName),
            1 => string.Format(CultureInfo.CurrentCulture, GitStrings.PullFastForwardOneFormat, snapshot.BranchName, snapshot.UpstreamName),
            int count => string.Format(CultureInfo.CurrentCulture, GitStrings.PullFastForwardManyFormat, snapshot.BranchName, snapshot.UpstreamName, count)
        };
    }

    /// <summary>
    /// Returns whether the current branch has an upstream; otherwise explains why pull and sync cannot run. An
    /// upstream that the status shows as gone is not checked here: the status is only as recent as the last fetch,
    /// and the pull fetches first and reports <see cref="GitRemoteFailureKind.UpstreamGone"/> itself.
    /// </summary>
    private async Task<bool> EnsureUpstreamAsync(GitSnapshot snapshot)
    {
        if (!snapshot.HasUpstream)
        {
            await ShowInformationAsync(GitStrings.NoUpstream);
            return false;
        }

        return true;
    }

    public async Task PushAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync())
        {
            return;
        }

        GitSnapshot snapshot = _session.Current;
        if (snapshot.IsDetached)
        {
            await ShowInformationAsync(GitStrings.DetachedHeadCannotPush);
            return;
        }

        string branch = snapshot.BranchName;
        string? setUpstreamRemote = null;
        string? upstream = snapshot.UpstreamName;
        if (!snapshot.HasUpstream)
        {
            IReadOnlyList<GitRemote> remotes = await _session.Services.Remotes!.GetRemotesAsync(
                snapshot.RepositoryRoot!,
                CancellationToken.None);
            setUpstreamRemote = GitRemote.SelectDefault(remotes)?.Name;
            if (setUpstreamRemote is null)
            {
                await ShowInformationAsync(GitStrings.NoRemote);
                return;
            }

            if (!await ConfirmAsync(
                    string.Format(CultureInfo.CurrentCulture, GitStrings.PushSetUpstreamPromptFormat, branch, setUpstreamRemote),
                    GitStrings.PushSetUpstreamConfirm,
                    GitStrings.PushCommand,
                    isDestructive: false))
            {
                return;
            }

            upstream = setUpstreamRemote + "/" + branch;
        }

        GitRemoteResult? result = await RunWithProgressAsync(
            GitStrings.PushCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.PushProgressFormat, branch),
            (services, root, ct) => services.Remotes!.PushAsync(root, setUpstreamRemote, ct));
        await ReportRemoteResultAsync(
            result,
            string.Format(CultureInfo.CurrentCulture, GitStrings.PushSucceededFormat, branch, upstream));
    }

    public async Task SyncAsync(CommandContext context)
    {
        if (!await EnsureRemoteServiceAsync())
        {
            return;
        }

        GitSnapshot snapshot = _session.Current;
        if (!await EnsureUpstreamAsync(snapshot))
        {
            return;
        }

        SyncResult? result = await RunWithProgressAsync(
            GitStrings.SyncCommand,
            string.Format(CultureInfo.CurrentCulture, GitStrings.SyncProgressFormat, snapshot.BranchName, snapshot.UpstreamName),
            async (services, root, ct) =>
            {
                IGitRemoteService remotes = services.Remotes!;
                GitRemoteResult pull = await remotes.PullAsync(root, ct).ConfigureAwait(false);
                return new SyncResult(pull, pull.Success ? await remotes.PushAsync(root, ct: ct).ConfigureAwait(false) : null);
            });
        if (result?.Push is not { Success: false } push)
        {
            await ReportRemoteResultAsync(
                result?.Pull,
                string.Format(CultureInfo.CurrentCulture, GitStrings.SyncSucceededFormat, snapshot.BranchName, snapshot.UpstreamName));
            return;
        }

        // The pull already changed the branch; the message says so instead of reporting only the push.
        await ShowErrorAsync(
            string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.SyncPushFailedFormat,
                snapshot.BranchName,
                snapshot.UpstreamName,
                FormatRemoteFailure(push)),
            HasGitOutput(push));
    }

    /// <summary>
    /// Runs an operation through the session while the progress dialog is shown and returns its result. Only the
    /// cancel result of the dialog cancels the operation; a dialog that closes otherwise or cannot be shown leaves it
    /// running. The dialog closes when the operation ends. An operation that finished although it was cancelled
    /// returns its result. Returns null when the operation was cancelled or failed with an exception, both of which
    /// are reported.
    /// </summary>
    private async Task<T?> RunWithProgressAsync<T>(
        string title,
        string message,
        Func<GitServiceSet, string, CancellationToken, Task<T>> operation)
        where T : class
    {
        using CancellationTokenSource work = new();
        using CancellationTokenSource closeDialog = new();
        Task<T> run = _session.RunWithServicesAsync(operation, work.Token);
        Task<bool?> dialog = ShowProgressAsync(title, message, closeDialog.Token);
        if (await Task.WhenAny(run, dialog) == dialog && await dialog == true)
        {
            work.Cancel();
        }

        closeDialog.Cancel();
        await dialog;
        try
        {
            return await run;
        }
        catch (OperationCanceledException) when (work.IsCancellationRequested)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.OperationCancelledFormat, title));
            return null;
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }
    }

    /// <summary>
    /// Runs an operation without result like <see cref="RunWithProgressAsync{T}"/>; returns whether it completed.
    /// </summary>
    private async Task<bool> RunWithProgressAsync(
        string title,
        string message,
        Func<GitServiceSet, string, CancellationToken, Task> operation)
    {
        object? completed = await RunWithProgressAsync<object>(
            title,
            message,
            async (services, root, ct) =>
            {
                await operation(services, root, ct).ConfigureAwait(false);
                return true;
            });
        return completed is not null;
    }

    /// <summary>
    /// Shows the progress dialog until <paramref name="closeToken"/> is cancelled. Returns true when the user
    /// cancelled, otherwise null; a dialog that fails is logged and counts as not cancelled.
    /// </summary>
    private async Task<bool?> ShowProgressAsync(string title, string message, CancellationToken closeToken)
    {
        try
        {
            return await _dialogHost.ShowDialogAsync<bool?>(
                ProgressDialogRequest.DialogId,
                new ProgressDialogRequest(title, message),
                closeToken);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            _logger?.Error(GitStrings.LogProgressDialogFailed, exception);
            return null;
        }
    }

    private async Task ReportRemoteResultAsync(GitRemoteResult? result, string successMessage)
    {
        if (result is null)
        {
            return;
        }

        if (result.Success)
        {
            await ShowInformationAsync(successMessage);
            return;
        }

        await ShowErrorAsync(FormatRemoteFailure(result), HasGitOutput(result));
    }

    /// <summary>
    /// Logs and shows an error; <paramref name="hasGitOutput"/> tells whether git wrote output that the output
    /// channel holds, which the message then points to.
    /// </summary>
    private async Task ShowErrorAsync(string message, bool hasGitOutput)
    {
        _logger?.Error(message);
        await _window.ShowErrorMessageAsync(hasGitOutput ? AppendOutputHint(message) : message, CancellationToken.None);
    }

    /// <summary>
    /// Returns whether a failed remote command left git output in the output channel; a timeout kills git and keeps
    /// none of its output.
    /// </summary>
    private static bool HasGitOutput(GitRemoteResult result) =>
        result.FailureKind != GitRemoteFailureKind.Timeout && result.Message.Length > 0;

    /// <summary>
    /// Returns the hint for the failure kind followed by the decisive line of git's output, with masked credentials.
    /// </summary>
    private static string FormatRemoteFailure(GitRemoteResult result)
    {
        string gitMessage = GitOutputLogger.MaskCredentials(GitRemoteErrorClassifier.SelectDecisiveLine(result.Message));
        string? hint = result.FailureKind switch
        {
            GitRemoteFailureKind.Authentication => GitStrings.AuthenticationFailedHint,
            GitRemoteFailureKind.Forbidden => GitStrings.ForbiddenHint,
            GitRemoteFailureKind.Network => GitStrings.NetworkHint,
            GitRemoteFailureKind.HostKeyVerification => GitStrings.HostKeyHint,
            GitRemoteFailureKind.Rejected => GitStrings.PushRejectedHint,
            GitRemoteFailureKind.RemoteRejected => GitStrings.RemoteRejectedHint,
            GitRemoteFailureKind.Diverged => GitStrings.PullDivergedHint,
            GitRemoteFailureKind.NoUpstream => GitStrings.NoUpstream,
            GitRemoteFailureKind.UpstreamGone => GitStrings.UpstreamGoneHint,
            GitRemoteFailureKind.Timeout => GitStrings.TimeoutHint,
            _ => null
        };
        return hint is null
            ? string.Format(CultureInfo.CurrentCulture, UiStrings.GitCommandFailed, gitMessage)
            : string.Format(CultureInfo.CurrentCulture, GitStrings.RemoteFailureFormat, hint, gitMessage);
    }

    /// <summary>
    /// Checks out a remote-tracking branch through the local branch that tracks it, or creates that branch. A local
    /// branch of the same name that tracks something else is never switched to without asking, because it does not
    /// have the commits of the remote branch.
    /// </summary>
    private async Task CheckoutRemoteAsync(IReadOnlyList<GitBranch> branches, GitBranch remote)
    {
        GitBranch? tracking = branches.FirstOrDefault(branch =>
            !branch.IsRemote && string.Equals(branch.UpstreamName, remote.Name, StringComparison.Ordinal));
        if (tracking is not null)
        {
            await SwitchToAsync(tracking);
            return;
        }

        GitBranch? sameName = FindLocal(branches, remote.LocalName);
        if (sameName is null)
        {
            await SwitchToAsync(remote);
            return;
        }

        QuickPickItem switchLocal = new(
            string.Format(CultureInfo.CurrentCulture, GitStrings.SwitchToLocalBranchFormat, sameName.Name),
            sameName.UpstreamName is { } upstream
                ? string.Format(CultureInfo.CurrentCulture, GitStrings.TracksUpstreamFormat, upstream)
                : GitStrings.NoUpstreamDescription,
            null);
        QuickPickItem createBranch = new(
            string.Format(CultureInfo.CurrentCulture, GitStrings.CreateBranchFromRemoteFormat, remote.Name),
            null,
            null);
        QuickPickItem detach = new(
            string.Format(CultureInfo.CurrentCulture, GitStrings.CheckoutDetachedFormat, remote.Name),
            null,
            null);
        QuickPickItem? choice = await _window.ShowQuickPickAsync(
            [switchLocal, createBranch, detach],
            new QuickPickOptions(
                string.Format(CultureInfo.CurrentCulture, GitStrings.LocalBranchExistsTitleFormat, sameName.Name, remote.Name),
                false),
            CancellationToken.None);
        if (choice == switchLocal)
        {
            await SwitchToAsync(sameName);
        }
        else if (choice == createBranch)
        {
            await CreateBranchFromAsync(remote.FullName);
        }
        else if (choice == detach)
        {
            string commitish = remote.FullName;
            await TryRunAsync<bool>(async (branches, root, ct) =>
            {
                await branches.DetachAsync(root, commitish, ct);
                return true;
            });
        }
    }

    /// <summary>
    /// Switches to a local branch, or to the local branch git creates for a remote-tracking branch, which then
    /// tracks it.
    /// </summary>
    private async Task SwitchToAsync(GitBranch branch)
    {
        if (branch.IsCurrent)
        {
            await ShowInformationAsync(string.Format(CultureInfo.CurrentCulture, GitStrings.AlreadyOnBranchFormat, branch.Name));
            return;
        }

        string name = branch.FullName;
        await TryRunAsync<bool>(async (branches, root, ct) =>
        {
            await branches.SwitchAsync(root, name, ct);
            return true;
        });
    }

    /// <summary>Asks for the name and whether to switch, and creates a branch at <paramref name="startPoint"/> or HEAD.</summary>
    private async Task CreateBranchFromAsync(string? startPoint)
    {
        string prompt = startPoint is null
            ? GitStrings.CreateBranchPrompt
            : string.Format(
                CultureInfo.CurrentCulture,
                GitStrings.CreateBranchFromPromptFormat,
                GitRefParser.ShortenBranchReference(startPoint));
        string? name = (await _window.ShowInputBoxAsync(
            new InputBoxOptions(GitStrings.CreateBranchTitle, prompt, null),
            CancellationToken.None))?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            return;
        }

        // Checked before git runs, so an invalid name gets a plain message instead of git's error output.
        bool? valid = await TryRunAsync<bool>(async (branches, root, ct) =>
            await branches.IsValidBranchNameAsync(root, name, ct));
        if (valid is null)
        {
            return;
        }

        if (valid is false)
        {
            await ShowErrorAsync(
                string.Format(CultureInfo.CurrentCulture, GitStrings.InvalidBranchNameFormat, name),
                hasGitOutput: false);
            return;
        }

        QuickPickItem createAndSwitch = new(GitStrings.CreateAndSwitch, null, null);
        QuickPickItem createOnly = new(GitStrings.CreateOnly, null, null);
        QuickPickItem? mode = await _window.ShowQuickPickAsync(
            [createAndSwitch, createOnly],
            new QuickPickOptions(string.Format(CultureInfo.CurrentCulture, GitStrings.CreateBranchModeTitleFormat, name), false),
            CancellationToken.None);
        if (mode is null)
        {
            return;
        }

        bool switchTo = mode == createAndSwitch;
        await TryRunAsync<bool>(async (branches, root, ct) =>
        {
            await branches.CreateAsync(root, name, startPoint, switchTo, ct);
            return true;
        });
    }

    private Task<GitBranchDeleteResult?> DeleteAsync(string name, bool force) =>
        TryRunAsync<GitBranchDeleteResult>(async (branches, root, ct) =>
            await branches.DeleteAsync(root, name, force, ct));

    /// <summary>
    /// Runs a branch operation through the session; returns null and reports git's error when it failed, or that the
    /// branch commands are unavailable when the services of the session have no branch service.
    /// </summary>
    private async Task<T?> TryRunAsync<T>(Func<IGitBranchService, string, CancellationToken, Task<T?>> operation)
        where T : struct
    {
        bool unavailable = false;
        T? result;
        try
        {
            result = await _session.RunWithServicesAsync<T?>(async (services, root, ct) =>
            {
                if (services.Branches is not { } branches)
                {
                    unavailable = true;
                    return null;
                }

                return await operation(branches, root, ct);
            });
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }

        if (unavailable)
        {
            await ShowInformationAsync(GitStrings.BranchesUnavailable);
        }

        return result;
    }

    /// <summary>Returns the branches of the repository, or null after reporting why there are none.</summary>
    private async Task<IReadOnlyList<GitBranch>?> LoadBranchesAsync()
    {
        if (!await EnsureBranchServiceAsync() || _session.Services.Branches is not { } branches)
        {
            return null;
        }

        try
        {
            return await branches.GetBranchesAsync(_session.Current.RepositoryRoot!, CancellationToken.None);
        }
        catch (InvalidOperationException exception)
        {
            await ReportFailureAsync(exception);
            return null;
        }
    }

    private async Task<bool> EnsureBranchServiceAsync()
    {
        if (!await EnsureRepositoryAsync())
        {
            return false;
        }

        if (_session.Services.Branches is null)
        {
            await ShowInformationAsync(GitStrings.BranchesUnavailable);
            return false;
        }

        return true;
    }

    private async Task<bool> EnsureRemoteServiceAsync()
    {
        if (!await EnsureRepositoryAsync())
        {
            return false;
        }

        if (_session.Services.Remotes is null)
        {
            await ShowInformationAsync(GitStrings.RemotesUnavailable);
            return false;
        }

        return true;
    }

    private async Task<bool> EnsureRepositoryAsync()
    {
        GitSnapshot snapshot = _session.Current;
        if (snapshot.IsRepository && snapshot.RepositoryRoot is not null)
        {
            return true;
        }

        await ShowInformationAsync(snapshot.ErrorMessage ?? UiStrings.WorkspaceNotGitRepository);
        return false;
    }

    private async Task<GitBranch?> PickBranchAsync(IReadOnlyList<GitBranch> branches, string title)
    {
        // The items are value-equal records with distinct values, so the index of the picked item is its branch.
        QuickPickItem[] items = branches
            .Select(branch => new QuickPickItem(
                branch.Name,
                branch.IsCurrent
                    ? GitStrings.CurrentBranchDescription
                    : branch.IsRemote ? GitStrings.RemoteBranchDescription : null,
                branch.Subject.Length == 0 ? null : branch.Subject))
            .ToArray();
        QuickPickItem? picked = await _window.ShowQuickPickAsync(items, new QuickPickOptions(title, false), CancellationToken.None);
        int index = picked is null ? -1 : Array.IndexOf(items, picked);
        return index < 0 ? null : branches[index];
    }

    private Task<bool> ConfirmAsync(string message, string confirmText) =>
        ConfirmAsync(message, confirmText, GitStrings.DeleteBranchConfirmTitle, isDestructive: true);

    private async Task<bool> ConfirmAsync(string message, string confirmText, string title, bool isDestructive)
    {
        bool? confirmed = await _dialogHost.ShowDialogAsync<bool>(
            ConfirmationDialogRequest.DialogId,
            new ConfirmationDialogRequest(title, message, confirmText, isDestructive),
            CancellationToken.None);
        return confirmed == true;
    }

    private static GitBranch? FindBranch(IReadOnlyList<GitBranch> branches, string name)
    {
        return branches.FirstOrDefault(branch => string.Equals(branch.FullName, name, StringComparison.Ordinal))
            ?? branches.FirstOrDefault(branch => !branch.IsRemote && string.Equals(branch.Name, name, StringComparison.Ordinal))
            ?? branches.FirstOrDefault(branch => branch.IsRemote && string.Equals(branch.Name, name, StringComparison.Ordinal));
    }

    private static GitBranch? FindLocal(IReadOnlyList<GitBranch> branches, string name) =>
        branches.FirstOrDefault(branch => !branch.IsRemote && string.Equals(branch.Name, name, StringComparison.Ordinal));

    private static string? FirstStringArgument(CommandContext context) =>
        context.Arguments is { Count: > 0 } arguments && arguments[0] is string { Length: > 0 } argument
            ? argument
            : null;

    /// <summary>The pull of a sync and the push that follows a successful pull.</summary>
    private sealed record SyncResult(GitRemoteResult Pull, GitRemoteResult? Push);
}
