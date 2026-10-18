using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService : IGitMergeService
{
    /// <inheritdoc />
    public async Task<GitMergeResult> MergeAsync(string repoPath, string revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        (GitProcessResult result, bool stopped) = await RunStoppingCommandAsync(
            repoPath, ["merge", "--no-edit", "--", revision], GitRepositoryOperation.Merging, ct);
        if (stopped)
        {
            return GitMergeResult.Stopped;
        }

        return result.StandardOutput.Contains("Already up to date", StringComparison.Ordinal)
            ? GitMergeResult.AlreadyUpToDate
            : GitMergeResult.Merged;
    }

    /// <inheritdoc />
    public Task<bool> AbortAsync(string repoPath, GitRepositoryOperation operation, CancellationToken ct = default) =>
        RunConfirmedOperationCommandAsync(repoPath, operation, "--abort", ct);

    /// <inheritdoc />
    public async Task ContinueAsync(string repoPath, CancellationToken ct = default) =>
        await RunOperationCommandAsync(repoPath, await GetOperationAsync(repoPath, ct), "--continue", ct);

    /// <inheritdoc />
    /// <remarks>A merge has no steps; git merge has no --skip.</remarks>
    public async Task<bool> SkipAsync(string repoPath, GitRepositoryOperation operation, string? step, CancellationToken ct = default)
    {
        if (operation == GitRepositoryOperation.Merging
            || await GetGitDirectoryAsync(repoPath, ct) is not { } gitDirectory
            || ReadOperationStep(gitDirectory, operation) != step)
        {
            return false;
        }

        return await RunConfirmedOperationCommandAsync(repoPath, operation, "--skip", ct);
    }

    /// <inheritdoc />
    public Task<GitMergeResult> CherryPickAsync(string repoPath, string commit, CancellationToken ct = default) =>
        ApplyCommitAsync(repoPath, "cherry-pick", commit, GitRepositoryOperation.CherryPicking, ct);

    /// <inheritdoc />
    public Task<GitMergeResult> RevertAsync(string repoPath, string commit, CancellationToken ct = default) =>
        ApplyCommitAsync(repoPath, "revert", commit, GitRepositoryOperation.Reverting, ct);

    /// <inheritdoc />
    public async Task<GitMergeResult> RebaseAsync(string repoPath, string upstream, string? branch, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(upstream);
        string[] args = branch is null
            ? ["rebase", "--end-of-options", upstream]
            : ["rebase", "--end-of-options", upstream, branch];
        (GitProcessResult result, bool stopped) = await RunStoppingCommandAsync(repoPath, args, GitRepositoryOperation.Rebasing, ct);
        if (stopped)
        {
            return GitMergeResult.Stopped;
        }

        // git writes "Current branch main is up to date."
        return result.StandardOutput.Contains("is up to date", StringComparison.Ordinal)
            ? GitMergeResult.AlreadyUpToDate
            : GitMergeResult.Merged;
    }

    /// <summary>Runs <c>git cherry-pick</c> or <c>git revert</c> of one commit.</summary>
    private async Task<GitMergeResult> ApplyCommitAsync(
        string repoPath,
        string command,
        string commit,
        GitRepositoryOperation stoppedOperation,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commit);
        (_, bool stopped) = await RunStoppingCommandAsync(
            repoPath, [command, "--no-edit", "--end-of-options", commit], stoppedOperation, ct);
        return stopped ? GitMergeResult.Stopped : GitMergeResult.Merged;
    }

    /// <summary>
    /// Runs a merge, rebase, cherry-pick or revert and tells whether it stopped with <paramref name="stoppedOperation"/>
    /// in progress; throws with git's output when git refused. git exits with code 1 both when it stopped and when it
    /// refused, so the operation decides, and only one that this command started: an operation in progress before
    /// makes git refuse.
    /// </summary>
    private async Task<(GitProcessResult Result, bool Stopped)> RunStoppingCommandAsync(
        string repoPath,
        string[] args,
        GitRepositoryOperation stoppedOperation,
        CancellationToken ct)
    {
        bool wasInProgress = await GetOperationAsync(repoPath, ct) != GitRepositoryOperation.None;
        GitProcessResult result = await RunWorkingTreeCommandAsync(repoPath, args, ct, UntranslatedOutput);
        if (result.ExitCode == 0)
        {
            return (result, false);
        }

        if (!wasInProgress && await GetOperationAsync(repoPath, CancellationToken.None) == stoppedOperation)
        {
            return (result, true);
        }

        ThrowIfFailed(result, args[0]);
        return (result, false);
    }

    /// <summary>
    /// Runs <c>git &lt;operation&gt; &lt;option&gt;</c> when <paramref name="operation"/>, the one the user confirmed, is
    /// in progress; returns false and runs nothing otherwise.
    /// </summary>
    private async Task<bool> RunConfirmedOperationCommandAsync(
        string repoPath,
        GitRepositoryOperation operation,
        string option,
        CancellationToken ct)
    {
        GitRepositoryOperation current = await GetOperationAsync(repoPath, ct);
        if (current != operation || current == GitRepositoryOperation.None)
        {
            return false;
        }

        await RunOperationCommandAsync(repoPath, current, option, ct);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// <c>git restore --ours</c> or <c>--theirs</c> writes the side's version and deletes the file when the side has
    /// none; <c>git add -A</c> then stages the file or its deletion. On a path that is no longer unmerged, restore would
    /// write the index version over the working file, so the unmerged paths are read first.
    /// </remarks>
    public async Task<bool> TakeSideAsync(
        string repoPath,
        IReadOnlyList<string> paths,
        GitConflictSide side,
        CancellationToken ct = default)
    {
        string[] args = { "ls-files", "-u", "-z" };
        GitProcessResult unmerged = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct);
        ThrowIfFailed(unmerged, args[0]);
        // Each entry reads "<mode> <object> <stage>\t<path>", once per stage.
        HashSet<string> unmergedPaths = unmerged.StandardOutput
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry[(entry.IndexOf('\t') + 1)..])
            .ToHashSet(StringComparer.Ordinal);
        if (!paths.All(unmergedPaths.Contains))
        {
            return false;
        }

        await RunPathCommandAsync(repoPath, new[] { "restore", side == GitConflictSide.Ours ? "--ours" : "--theirs" }, paths, ct);
        await StageAsync(repoPath, paths, ct);
        return true;
    }

    /// <inheritdoc />
    /// <remarks>
    /// git writes MERGE_MSG for a stopped merge, cherry-pick, revert and rebase. <c>git stripspace --strip-comments</c>
    /// removes the comment lines, such as the list of conflicts, with the comment character git is configured with.
    /// </remarks>
    public async Task<string?> GetPreparedMessageAsync(string repoPath, CancellationToken ct = default)
    {
        if (await GetGitDirectoryAsync(repoPath, ct) is not { } gitDirectory)
        {
            return null;
        }

        byte[] message;
        try
        {
            message = await File.ReadAllBytesAsync(Path.Combine(gitDirectory, "MERGE_MSG"), ct);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        string[] args = { "stripspace", "--strip-comments" };
        await using Stream input = new MemoryStream(message, writable: false);
        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct, standardInput: input);
        ThrowIfFailed(result, args[0]);
        string stripped = result.StandardOutput.TrimEnd();
        return stripped.Length == 0 ? null : stripped;
    }

    /// <summary>Runs <c>git &lt;operation&gt; &lt;option&gt;</c> for <paramref name="operation"/>, the operation in progress.</summary>
    private async Task RunOperationCommandAsync(string repoPath, GitRepositoryOperation operation, string option, CancellationToken ct)
    {
        string? command = operation switch
        {
            GitRepositoryOperation.Merging => "merge",
            GitRepositoryOperation.Rebasing => "rebase",
            GitRepositoryOperation.CherryPicking => "cherry-pick",
            GitRepositoryOperation.Reverting => "revert",
            GitRepositoryOperation.ApplyingPatches => "am",
            _ => null
        };
        if (command is null)
        {
            throw new InvalidOperationException("No merge, rebase, cherry-pick, revert or patch application is in progress.");
        }

        GitProcessResult result = await RunWorkingTreeCommandAsync(repoPath, new[] { command, option }, ct);
        ThrowIfFailed(result, command);
    }

    /// <summary>
    /// Runs a command that rewrites the working tree and returns also a failed result. As every writing command,
    /// <paramref name="ct"/> cancels only the wait for the write gate.
    /// </summary>
    private Task<GitProcessResult> RunWorkingTreeCommandAsync(
        string repoPath,
        string[] args,
        CancellationToken ct,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        RunGitAsync(repoPath, args, GitCommandAccess.Write, ct, allowNonZeroExitCode: true, environment: environment);
}
