using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

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
    public async Task PushAsync(string repoPath, string? message, bool includeUntracked, CancellationToken ct = default)
    {
        List<string> args = new() { "stash", "push" };
        if (includeUntracked)
        {
            args.Add("--include-untracked");
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            args.AddRange(["-m", message]);
        }

        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Write, ct, environment: PatternPathspecs);
        ThrowIfFailed(result, args[0]);
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
