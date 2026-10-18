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
    /// <remarks>
    /// git exits with code 1 both when the merge stopped and when the revision cannot be merged, so MERGE_HEAD decides.
    /// Every other non-zero exit code means git refused before it changed anything.
    /// </remarks>
    public async Task<GitMergeResult> MergeAsync(string repoPath, string revision, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(revision);
        GitProcessResult result = await RunWorkingTreeCommandAsync(
            repoPath, new[] { "merge", "--no-edit", "--", revision }, ct, UntranslatedOutput);
        if (result.ExitCode == 0)
        {
            return result.StandardOutput.Contains("Already up to date", StringComparison.Ordinal)
                ? GitMergeResult.AlreadyUpToDate
                : GitMergeResult.Merged;
        }

        if (await GetOperationAsync(repoPath, CancellationToken.None) == GitRepositoryOperation.Merging)
        {
            return GitMergeResult.Stopped;
        }

        ThrowIfFailed(result, "merge");
        return GitMergeResult.Merged;
    }

    /// <inheritdoc />
    public async Task<bool> AbortAsync(string repoPath, GitRepositoryOperation operation, CancellationToken ct = default)
    {
        GitRepositoryOperation current = await GetOperationAsync(repoPath, ct);
        if (current != operation || current == GitRepositoryOperation.None)
        {
            return false;
        }

        await RunOperationCommandAsync(repoPath, current, "--abort", ct);
        return true;
    }

    /// <inheritdoc />
    public async Task ContinueAsync(string repoPath, CancellationToken ct = default) =>
        await RunOperationCommandAsync(repoPath, await GetOperationAsync(repoPath, ct), "--continue", ct);

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
