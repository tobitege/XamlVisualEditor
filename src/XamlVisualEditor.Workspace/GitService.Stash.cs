using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService : IGitStashService
{
    /// <summary>Removes <c>GIT_LITERAL_PATHSPECS</c>, which every git process of the service gets otherwise.</summary>
    private static readonly IReadOnlyDictionary<string, string?> PatternPathspecs =
        new Dictionary<string, string?> { ["GIT_LITERAL_PATHSPECS"] = null };

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitStashEntry>> ListAsync(string repoPath, CancellationToken ct = default)
    {
        string[] args = { "stash", "list", "-z", "--format=%gd%x00%H%x00%ct%x00%gs" };
        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct);
        ThrowIfFailed(result, args[0]);
        return GitStashParser.Parse(result.StandardOutput);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Without changes git creates no stash and exits with code 0. The push runs without literal pathspecs: with them,
    /// <c>git stash push --include-untracked</c> stores the untracked files but leaves them in the working tree. It
    /// passes no pathspec, so nothing else changes.
    /// </remarks>
    public Task PushAsync(string repoPath, string? message, bool includeUntracked, CancellationToken ct = default) =>
        PushStashAsync(repoPath, message, includeUntracked ? ["--include-untracked"] : [], [], ct);

    /// <inheritdoc />
    /// <remarks>
    /// Each path gets the <c>literal</c> pathspec magic instead of <c>GIT_LITERAL_PATHSPECS</c>, which the push runs
    /// without (see <see cref="PushAsync"/>), so a name with <c>*</c> or <c>?</c> stashes only that file.
    /// </remarks>
    public Task PushPathsAsync(string repoPath, string? message, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);
        return PushStashAsync(repoPath, message, ["--include-untracked"], paths.Select(path => ":(literal)" + path).ToArray(), ct);
    }

    /// <inheritdoc />
    public Task PushStagedAsync(string repoPath, string? message, CancellationToken ct = default) =>
        PushStashAsync(repoPath, message, ["--staged"], [], ct);

    private async Task PushStashAsync(
        string repoPath,
        string? message,
        string[] options,
        string[] pathspecs,
        CancellationToken ct)
    {
        List<string> args = new() { "stash", "push" };
        args.AddRange(options);
        if (!string.IsNullOrWhiteSpace(message))
        {
            args.AddRange(["-m", message]);
        }

        if (pathspecs.Length > 0)
        {
            args.Add("--");
            args.AddRange(pathspecs);
        }

        string? previousStash = await ReadNewestStashAsync(repoPath, ct);
        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Write, ct, environment: PatternPathspecs);
        // git can save the stash and then fail to remove its changes from the working tree, for example with
        // --staged over overlapping unstaged changes; the message must not suggest that nothing was saved.
        if (result.ExitCode != 0 && await ReadNewestStashAsync(repoPath, CancellationToken.None) is { } newest && newest != previousStash)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.CurrentCulture,
                UiStrings.GitStashSavedChangesKeptFormat,
                result.StandardError.Trim()));
        }

        ThrowIfFailed(result, args[0]);
    }

    private async Task<string?> ReadNewestStashAsync(string repoPath, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath, ["rev-parse", "-q", "--verify", "refs/stash"], GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        return result.ExitCode == 0 ? result.StandardOutput.Trim() : null;
    }

    /// <inheritdoc />
    /// <remarks>The stash is named by its commit id, so a change of the stash list in between does not matter.</remarks>
    public Task ApplyAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(stash);
        return RunGitCheckedAsync(repoPath, new[] { "stash", "apply", stash.ObjectId }, standardInput: null, ct);
    }

    /// <inheritdoc />
    /// <remarks>git pops and drops only by position; the position is checked against the commit id first.</remarks>
    public Task<bool> PopAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default) =>
        RunOnVerifiedStashAsync(repoPath, ["pop"], stash, ct);

    /// <inheritdoc />
    /// <remarks>git pops and drops only by position; the position is checked against the commit id first.</remarks>
    public Task<bool> DropAsync(string repoPath, GitStashEntry stash, CancellationToken ct = default) =>
        RunOnVerifiedStashAsync(repoPath, ["drop"], stash, ct);

    /// <inheritdoc />
    /// <remarks>
    /// git removes the stash after the branch only when it is named by its position; the position is checked against
    /// the commit id first.
    /// </remarks>
    public Task<bool> BranchAsync(string repoPath, GitStashEntry stash, string branchName, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
        return RunOnVerifiedStashAsync(repoPath, ["branch", "--", branchName], stash, ct);
    }

    private async Task<bool> RunOnVerifiedStashAsync(string repoPath, string[] command, GitStashEntry stash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stash);
        GitProcessResult current = await RunGitAsync(
            repoPath,
            new[] { "rev-parse", "--verify", "--quiet", stash.Name },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        if (current.ExitCode != 0 || !string.Equals(current.StandardOutput.Trim(), stash.ObjectId, StringComparison.Ordinal))
        {
            return false;
        }

        await RunGitCheckedAsync(repoPath, ["stash", .. command, stash.Name], standardInput: null, ct);
        return true;
    }
}
