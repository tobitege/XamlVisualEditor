using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService
{
    private const string LOCAL_BRANCH_PREFIX = "refs/heads/";
    private const string REMOTE_BRANCH_PREFIX = "refs/remotes/";

    /// <summary>
    /// Environment of commands whose output or error text is parsed: git writes its messages untranslated, so the
    /// parsers do not depend on the language of the git installation.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string?> UntranslatedOutput = new Dictionary<string, string?>
    {
        ["LC_ALL"] = "C"
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitBranch>> GetBranchesAsync(string repoPath, CancellationToken ct = default)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "for-each-ref", "--sort=refname", "--format=" + GitRefParser.BRANCH_FORMAT, "refs/heads", "refs/remotes" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true,
            environment: UntranslatedOutput);
        ThrowIfFailed(result, "for-each-ref");
        IReadOnlyList<string> remoteNames = result.StandardOutput.Contains(REMOTE_BRANCH_PREFIX, StringComparison.Ordinal)
            ? await GetRemoteNamesAsync(repoPath, ct)
            : Array.Empty<string>();
        return GitRefParser.ParseBranches(result.StandardOutput, remoteNames);
    }

    /// <inheritdoc />
    public async Task SwitchAsync(string repoPath, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string[] args;
        if (name.StartsWith(LOCAL_BRANCH_PREFIX, StringComparison.Ordinal))
        {
            args = new[] { "switch", name[LOCAL_BRANCH_PREFIX.Length..] };
        }
        else if (name.StartsWith(REMOTE_BRANCH_PREFIX, StringComparison.Ordinal))
        {
            args = new[] { "switch", "--track", name[REMOTE_BRANCH_PREFIX.Length..] };
        }
        else if (!await ReferenceExistsAsync(repoPath, LOCAL_BRANCH_PREFIX + name, ct)
                 && await ReferenceExistsAsync(repoPath, REMOTE_BRANCH_PREFIX + name, ct))
        {
            args = new[] { "switch", "--track", name };
        }
        else
        {
            args = new[] { "switch", name };
        }

        await RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// A remote-tracking start point whose branch has the name of the new branch gets no tracking option, so
    /// <c>branch.autoSetupMerge</c> decides whether it becomes the upstream, as on the command line; by default it
    /// does. Any other start point gets <c>--no-track</c>, because a later <c>git push</c> to an upstream of another
    /// name fails under <c>push.default=simple</c>. <c>--</c> keeps a name or start point that starts with <c>-</c>
    /// from being read as an option; git then rejects it as an invalid branch name.
    /// </remarks>
    public async Task CreateAsync(string repoPath, string name, string? startPoint, bool switchTo, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        List<string> args = new() { switchTo ? "switch" : "branch" };
        if (!await IsRemoteBranchNamedAsync(repoPath, startPoint, name, ct))
        {
            args.Add("--no-track");
        }

        args.AddRange(switchTo ? new[] { "-c", name, "--" } : new[] { "--", name });
        if (!string.IsNullOrWhiteSpace(startPoint))
        {
            args.Add(startPoint);
        }

        await RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    /// <inheritdoc />
    public async Task<bool> IsValidBranchNameAsync(string repoPath, string name, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "check-ref-format", "--branch", name },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);

        // --branch expands @{-1} and similar to the branch they name, which is no name for a new branch.
        return result.ExitCode == 0 && string.Equals(result.StandardOutput.Trim(), name, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns whether <paramref name="startPoint"/> is a remote-tracking branch of a configured remote whose branch
    /// is called <paramref name="name"/>, such as <c>origin/feature</c> or <c>refs/remotes/origin/feature</c> for
    /// <c>feature</c>. A short start point that also names a local branch is that local branch, as for git.
    /// </summary>
    internal async Task<bool> IsRemoteBranchNamedAsync(string repoPath, string? startPoint, string name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(startPoint))
        {
            return false;
        }

        string remoteBranch;
        if (startPoint.StartsWith(REMOTE_BRANCH_PREFIX, StringComparison.Ordinal))
        {
            remoteBranch = startPoint[REMOTE_BRANCH_PREFIX.Length..];
        }
        else if (!startPoint.StartsWith("refs/", StringComparison.Ordinal)
                 && !await ReferenceExistsAsync(repoPath, LOCAL_BRANCH_PREFIX + startPoint, ct)
                 && await ReferenceExistsAsync(repoPath, REMOTE_BRANCH_PREFIX + startPoint, ct))
        {
            remoteBranch = startPoint;
        }
        else
        {
            return false;
        }

        string? remote = null;
        foreach (string candidate in await GetRemoteNamesAsync(repoPath, ct))
        {
            // Remote names may contain slashes; the longest matching remote owns the branch.
            if (remoteBranch.Length > candidate.Length + 1
                && remoteBranch[candidate.Length] == '/'
                && remoteBranch.StartsWith(candidate, StringComparison.Ordinal)
                && candidate.Length > (remote?.Length ?? 0))
            {
                remote = candidate;
            }
        }

        return remote is not null && string.Equals(remoteBranch[(remote.Length + 1)..], name, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public Task DetachAsync(string repoPath, string commitish, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commitish);
        return RunGitCheckedAsync(repoPath, new[] { "switch", "--detach", "--", commitish }, standardInput: null, ct);
    }

    /// <inheritdoc />
    public async Task<GitBranchDeleteResult> DeleteAsync(string repoPath, string name, bool force, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string branch = name.StartsWith(LOCAL_BRANCH_PREFIX, StringComparison.Ordinal)
            ? name[LOCAL_BRANCH_PREFIX.Length..]
            : name;
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "branch", force ? "-D" : "-d", "--", branch },
            GitCommandAccess.Write,
            ct,
            allowNonZeroExitCode: true,
            environment: UntranslatedOutput);
        if (result.ExitCode != 0
            && !force
            && result.StandardError.Contains("not fully merged", StringComparison.OrdinalIgnoreCase))
        {
            return GitBranchDeleteResult.NotFullyMerged;
        }

        ThrowIfFailed(result, "branch");
        return GitBranchDeleteResult.Deleted;
    }

    /// <inheritdoc />
    public async Task<GitMergeResult> MergeAsync(string repoPath, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "merge", "--no-edit", "--", name },
            GitCommandAccess.Write,
            ct,
            allowNonZeroExitCode: true,
            environment: UntranslatedOutput);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput.Contains("Already up to date", StringComparison.Ordinal)
                ? GitMergeResult.AlreadyUpToDate
                : GitMergeResult.Merged;
        }

        // Exit code 1 also means a refused merge; only a MERGE_HEAD tells that the merge stopped with conflicts.
        if (await GetOperationAsync(repoPath, ct) == GitRepositoryOperation.Merging)
        {
            return GitMergeResult.Conflicts;
        }

        ThrowIfFailed(result, "merge");
        return GitMergeResult.Merged;
    }

    /// <inheritdoc />
    public Task AbortMergeAsync(string repoPath, CancellationToken ct = default) =>
        RunGitCheckedAsync(repoPath, new[] { "merge", "--abort" }, standardInput: null, ct);

    private async Task<bool> ReferenceExistsAsync(string repoPath, string reference, CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "show-ref", "--verify", "--quiet", reference },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        return result.ExitCode == 0;
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> with git's error output when git exited with a non-zero code.</summary>
    private static void ThrowIfFailed(GitProcessResult result, string command)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        string error = result.StandardError.Trim();
        if (error.Length == 0)
        {
            error = result.StandardOutput.Trim();
        }

        throw new InvalidOperationException(error.Length == 0
            ? $"git {command} exited with code {result.ExitCode}."
            : error);
    }
}
