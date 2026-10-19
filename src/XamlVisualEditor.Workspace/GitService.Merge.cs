using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Core.Localization;

namespace XamlVisualEditor.Workspace;

public sealed partial class GitService : IGitMergeService
{
    /// <inheritdoc />
    public async Task<GitMergeResult> MergeAsync(string repoPath, string revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        (GitProcessResult result, bool stopped) = await RunStoppingCommandAsync(
            repoPath, ["merge", "--no-edit", "--", revision], revision, GitRepositoryOperation.Merging, UntranslatedOutput, ct);
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
        RunConfirmedOperationCommandAsync(repoPath, operation, step => true, "--abort", ct);

    /// <inheritdoc />
    public async Task ContinueAsync(string repoPath, CancellationToken ct = default) =>
        await RunOperationCommandAsync(repoPath, await GetOperationAsync(repoPath, ct), "--continue", ct);

    /// <inheritdoc />
    /// <remarks>A merge has no steps; git merge has no --skip.</remarks>
    public Task<bool> SkipAsync(string repoPath, GitRepositoryOperation operation, string? step, CancellationToken ct = default) =>
        operation == GitRepositoryOperation.Merging
            ? Task.FromResult(false)
            : RunConfirmedOperationCommandAsync(repoPath, operation, current => current == step, "--skip", ct);

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
        (GitProcessResult result, bool stopped) = await RunStoppingCommandAsync(
            repoPath, args, upstream, GitRepositoryOperation.Rebasing, UntranslatedOutput, ct);
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
            repoPath, [command, "--no-edit", "--end-of-options", commit], commit, stoppedOperation, null, ct);
        return stopped ? GitMergeResult.Stopped : GitMergeResult.Merged;
    }

    /// <summary>
    /// Runs a merge, rebase, cherry-pick or revert of <paramref name="target"/> and tells whether it stopped; throws
    /// with git's output when git refused. git exits with code 1 when it stopped, and when it refused because of a
    /// conflicting operation with 128; code 1 alone does not tell a stop, so it stopped only when
    /// <paramref name="stoppedOperation"/> is in progress for the commit of <paramref name="target"/>.
    /// </summary>
    private Task<(GitProcessResult Result, bool Stopped)> RunStoppingCommandAsync(
        string repoPath,
        string[] args,
        string target,
        GitRepositoryOperation stoppedOperation,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken ct) =>
        RunUnderWriteGateAsync(repoPath, async () =>
        {
            string[] targetIds = await ResolveTargetIdsAsync(repoPath, target, ct);
            GitProcessResult result = await RunWorkingTreeCommandAsync(repoPath, args, ct, environment);
            if (result.ExitCode == 0)
            {
                return (result, false);
            }

            if (result.ExitCode == 1
                && await GetGitDirectoryAsync(repoPath, CancellationToken.None) is { } gitDirectory
                && DetectOperation(gitDirectory) == stoppedOperation
                && ReadOperationTarget(gitDirectory, stoppedOperation) is { } operationTarget
                && targetIds.Contains(operationTarget))
            {
                return (result, true);
            }

            ThrowIfFailed(result, args[0]);
            return (result, false);
        }, ct);

    /// <summary>
    /// Returns the ids git can write for <paramref name="revision"/> into the state of an operation: the object it names,
    /// which MERGE_HEAD holds for an annotated tag, and the commit it peels to; empty when it names none.
    /// </summary>
    private async Task<string[]> ResolveTargetIdsAsync(string repoPath, string revision, CancellationToken ct)
    {
        if (revision.StartsWith('-'))
        {
            return [];
        }

        string? objectId = await ReadRevisionAsync(repoPath, ["--verify", "--quiet", revision], ct);
        string? commitId = await ReadRevisionAsync(repoPath, ["--verify", "--quiet", revision + "^{commit}"], ct);
        return new[] { objectId, commitId }.OfType<string>().ToArray();
    }

    /// <summary>
    /// Returns the commit <paramref name="operation"/> works with: the merged, picked or reverted commit, or the commit a
    /// rebase replays onto; null when git names none.
    /// </summary>
    private static string? ReadOperationTarget(string gitDirectory, GitRepositoryOperation operation)
    {
        string[] files = operation switch
        {
            GitRepositoryOperation.Merging => ["MERGE_HEAD"],
            GitRepositoryOperation.CherryPicking => ["CHERRY_PICK_HEAD"],
            GitRepositoryOperation.Reverting => ["REVERT_HEAD"],
            GitRepositoryOperation.Rebasing => [Path.Combine("rebase-merge", "onto"), Path.Combine("rebase-apply", "onto")],
            _ => []
        };
        string? file = files.Select(name => Path.Combine(gitDirectory, name)).FirstOrDefault(File.Exists);
        try
        {
            // MERGE_HEAD lists each merged commit on a line of its own.
            return file is null ? null : File.ReadLines(file).FirstOrDefault()?.Trim();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Runs <c>git &lt;operation&gt; &lt;option&gt;</c> when <paramref name="operation"/>, the one the user confirmed, is
    /// in progress and its step passes <paramref name="isConfirmedStep"/> (see
    /// <see cref="GitRepositoryStatus.OperationStep"/>); returns false and runs nothing otherwise. The check and the
    /// command hold the write gate together, so no writing command of the editor moves the operation between them.
    /// </summary>
    private Task<bool> RunConfirmedOperationCommandAsync(
        string repoPath,
        GitRepositoryOperation operation,
        Func<string?, bool> isConfirmedStep,
        string option,
        CancellationToken ct) =>
        RunUnderWriteGateAsync(repoPath, async () =>
        {
            if (await GetGitDirectoryAsync(repoPath, ct) is not { } gitDirectory)
            {
                return false;
            }

            GitRepositoryOperation current = DetectOperation(gitDirectory);
            if (current != operation
                || current == GitRepositoryOperation.None
                || !TryReadOperationStep(gitDirectory, current, out string? step)
                || !isConfirmedStep(step))
            {
                return false;
            }

            await RunOperationCommandAsync(repoPath, current, option, ct);
            return true;
        }, ct);

    /// <summary>
    /// Runs <paramref name="action"/> with the write gate of the repository held, see
    /// <see cref="GitProcessRunner.RunWithWriteGateAsync"/>. <paramref name="ct"/> and a stop of the
    /// <see cref="GitProcessStop"/> scope cancel the wait for the gate; the action decides how its own commands take
    /// <paramref name="ct"/>, and its writing commands, as any, run to their end once started.
    /// </summary>
    private async Task<T> RunUnderWriteGateAsync<T>(string repoPath, Func<Task<T>> action, CancellationToken ct)
    {
        GitProcessStop? scope = GitProcessStop.Current;
        using IDisposable? writing = scope?.BeginWrite();
        using CancellationTokenSource? wait = scope is null ? null : CancellationTokenSource.CreateLinkedTokenSource(ct, scope.Token);
        return await _runner.RunWithWriteGateAsync(repoPath, action, wait?.Token ?? ct);
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
            throw new InvalidOperationException(UiStrings.GitNoOperationInProgress);
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
