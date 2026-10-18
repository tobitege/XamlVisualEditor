using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// Git CLI-backed service for repository status, diff, branch, remote and history operations.
/// </summary>
public sealed partial class GitService : IGitService, IGitRepositoryService, IGitCommandLog, IGitBranchService, IGitRemoteService, IGitHistoryService
{
    private readonly ILogger<GitService> _logger;
    private readonly GitProcessOptions _options;
    private readonly GitProcessRunner _runner;
    private readonly ConcurrentDictionary<string, GitDirectories> _gitDirectories = new(FileSystemPathComparison.Comparer);
    private int _versionChecked;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Receives warnings about failing git commands and an outdated git version.</param>
    /// <param name="options">The git executable and environment of the git processes; <see cref="GitProcessOptions.Default"/> when null.</param>
    public GitService(ILogger<GitService>? logger = null, GitProcessOptions? options = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitService>.Instance;
        _options = options ?? GitProcessOptions.Default;
        _runner = new GitProcessRunner(_options, logger: _logger);
    }

    /// <inheritdoc />
    /// <remarks>Raised on the thread that ran git; an exception of a subscriber is logged and does not fail the command.</remarks>
    public event EventHandler<GitCommandLogEntry>? CommandCompleted;

    public async Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        string? workingDirectory = ResolveWorkingDirectory(path);
        if (workingDirectory is null)
        {
            return null;
        }

        GitProcessResult result = await RunGitAsync(
            workingDirectory,
            new[] { "rev-parse", "--show-toplevel" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        if (result.ExitCode != 0)
        {
            return null;
        }

        string output = result.StandardOutput.Trim();
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        string[] args = { "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=normal" };
        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct);
        ThrowIfFailed(result, args[0]);
        string? gitDirectory = await GetGitDirectoryAsync(repoPath, ct);
        GitRepositoryOperation operation = gitDirectory is null ? GitRepositoryOperation.None : DetectOperation(gitDirectory);
        return GitStatusParser.ParsePorcelainV2(repoPath, result.StandardOutput, operation) with
        {
            IsStoppedForEdit = operation == GitRepositoryOperation.Rebasing && IsStoppedForEdit(gitDirectory!)
        };
    }

    public async Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default)
    {
        string[] args = BuildDiffArguments(request);
        GitProcessResult result = await RunGitAsync(repoPath, args, GitCommandAccess.Read, ct, allowNonZeroExitCode: true);
        if (result.ExitCode > 1)
        {
            _logger.LogWarning("git diff failed: {Error}", result.StandardError);
            return new GitDiff
            {
                Files = Array.Empty<GitDiffFile>(),
                RawText = result.StandardOutput
            };
        }

        return GitDiffParser.ParseUnifiedDiff(result.StandardOutput);
    }

    /// <inheritdoc />
    /// <remarks>Also stages deletions; <c>.</c> stages everything like <c>git add -A</c>.</remarks>
    public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        return RunPathCommandAsync(repoPath, new[] { "add", "-A" }, paths, ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Runs <c>git reset</c> with paths, which also works before the first commit and, unlike <c>git reset</c>
    /// without paths, keeps a merge in progress.
    /// </remarks>
    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        return RunPathCommandAsync(repoPath, new[] { "reset", "-q" }, paths, ct);
    }

    /// <inheritdoc />
    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        // Restores the working file from the index only: staged content stays, and a file that is
        // staged but not yet committed is never removed from disk.
        return RunPathCommandAsync(repoPath, new[] { "restore", "--worktree" }, paths, ct);
    }

    /// <summary>
    /// Runs a modifying command on <paramref name="paths"/>. The paths are written NUL-separated to the standard
    /// input of git, which has no command line length limit.
    /// </summary>
    private async Task RunPathCommandAsync(
        string repoPath,
        string[] command,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        if (paths.Count == 0)
        {
            return;
        }

        List<string> args = new(command) { "--pathspec-from-file=-", "--pathspec-file-nul" };
        await using Stream input = GitProcessRequest.Utf8Input(string.Join('\0', paths));
        await RunGitCheckedAsync(repoPath, args, input, ct);
    }

    public async Task CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Standard input carries the message as UTF-8 bytes and has no command line length limit.
        await using Stream input = GitProcessRequest.Utf8Input(message);
        await RunGitCheckedAsync(repoPath, new[] { "commit", "-F", "-" }, input, ct, cancellable: true);
    }

    /// <summary>
    /// Runs a modifying git command and throws <see cref="InvalidOperationException"/> with git's error output
    /// when it exits with a non-zero code.
    /// </summary>
    private async Task RunGitCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        Stream? standardInput,
        CancellationToken ct,
        bool cancellable = false)
    {
        GitProcessResult result = await RunGitAsync(
            workingDirectory,
            args,
            GitCommandAccess.Write,
            ct,
            standardInput: standardInput,
            cancellable: cancellable);
        ThrowIfFailed(result, args[0]);
    }

    private async Task<GitRepositoryOperation> GetOperationAsync(string repoPath, CancellationToken ct)
    {
        string? gitDirectory = await GetGitDirectoryAsync(repoPath, ct);
        return gitDirectory is null ? GitRepositoryOperation.None : DetectOperation(gitDirectory);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetRemoteNamesAsync(string repoPath, CancellationToken ct = default)
    {
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "remote" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        if (result.ExitCode != 0)
        {
            return Array.Empty<string>();
        }

        return result.StandardOutput.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <inheritdoc />
    public Task InitAsync(string path, CancellationToken ct = default)
    {
        return RunGitCheckedAsync(path, new[] { "init" }, standardInput: null, ct);
    }

    /// <inheritdoc />
    public async Task<string?> GetGitDirectoryAsync(string repoPath, CancellationToken ct = default)
    {
        return (await GetDirectoriesAsync(repoPath, ct))?.GitDirectory;
    }

    /// <inheritdoc />
    public async Task<string?> GetCommonDirectoryAsync(string repoPath, CancellationToken ct = default)
    {
        return (await GetDirectoriesAsync(repoPath, ct))?.CommonDirectory;
    }

    private async Task<GitDirectories?> GetDirectoriesAsync(string repoPath, CancellationToken ct)
    {
        if (_gitDirectories.TryGetValue(repoPath, out GitDirectories? cached))
        {
            return cached;
        }

        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "rev-parse", "--absolute-git-dir", "--git-common-dir" },
            GitCommandAccess.Read,
            ct,
            allowNonZeroExitCode: true);
        string[] lines = result.StandardOutput.Split(
            new[] { '\r', '\n' },
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (result.ExitCode != 0 || lines.Length < 2)
        {
            return null;
        }

        // A relative common directory is relative to the folder git ran in.
        GitDirectories directories = new(
            Path.GetFullPath(lines[0]),
            Path.GetFullPath(lines[1], Path.GetFullPath(repoPath)));
        _gitDirectories[repoPath] = directories;
        return directories;
    }

    internal static GitRepositoryOperation DetectOperation(string gitDirectory)
    {
        // git am keeps its state in rebase-apply as the apply backend of rebase does, and marks it with this file.
        if (File.Exists(Path.Combine(gitDirectory, "rebase-apply", "applying")))
        {
            return GitRepositoryOperation.ApplyingPatches;
        }

        if (Directory.Exists(Path.Combine(gitDirectory, "rebase-merge"))
            || Directory.Exists(Path.Combine(gitDirectory, "rebase-apply")))
        {
            return GitRepositoryOperation.Rebasing;
        }

        if (File.Exists(Path.Combine(gitDirectory, "MERGE_HEAD")))
        {
            return GitRepositoryOperation.Merging;
        }

        if (File.Exists(Path.Combine(gitDirectory, "CHERRY_PICK_HEAD")))
        {
            return GitRepositoryOperation.CherryPicking;
        }

        if (File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD")))
        {
            return GitRepositoryOperation.Reverting;
        }

        // A sequence of picks or reverts that stopped and whose step was committed has no CHERRY_PICK_HEAD or
        // REVERT_HEAD; its remaining steps wait in the sequencer, the first of them names the kind.
        string todo = Path.Combine(gitDirectory, "sequencer", "todo");
        string? step = File.Exists(todo)
            ? File.ReadLines(todo).Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0 && line[0] != '#')
            : null;
        return step switch
        {
            null => GitRepositoryOperation.None,
            _ when step.StartsWith("revert ", StringComparison.Ordinal) => GitRepositoryOperation.Reverting,
            _ => GitRepositoryOperation.CherryPicking
        };
    }

    // git writes this file when a rebase stops at an edit step, and not when it stops for a conflict.
    internal static bool IsStoppedForEdit(string gitDirectory) =>
        File.Exists(Path.Combine(gitDirectory, "rebase-merge", "amend"));

    private static string? ResolveWorkingDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        if (Directory.Exists(path))
        {
            return path;
        }

        if (File.Exists(path))
        {
            return Path.GetDirectoryName(path);
        }

        return null;
    }

    /// <summary>
    /// Returns the arguments of the diff that <see cref="GetDiffAsync"/> shows and that
    /// <see cref="ApplySelectionAsync"/> turns into a patch: without external diff tool and text conversion and with
    /// the prefixes <c>a/</c> and <c>b/</c>, whatever <c>diff.external</c>, textconv attributes,
    /// <c>diff.noprefix</c> or <c>diff.mnemonicPrefix</c> configure.
    /// </summary>
    private static string[] BuildDiffArguments(GitDiffRequest request)
    {
        bool isUntracked = request.IsUntracked && !string.IsNullOrWhiteSpace(request.Path);
        List<string> args = new() { "diff" };
        if (isUntracked)
        {
            args.Add("--no-index");
        }
        else if (request.Staged)
        {
            args.Add("--cached");
        }

        args.AddRange(["--no-color", "--unified=3", "--no-ext-diff", "--no-textconv", "--src-prefix=a/", "--dst-prefix=b/"]);
        if (isUntracked)
        {
            args.AddRange(["--", "/dev/null", request.Path!]);
        }
        else if (!string.IsNullOrWhiteSpace(request.Path))
        {
            args.AddRange(["--", request.Path!]);
        }

        return args.ToArray();
    }

    private async Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        GitCommandAccess access,
        CancellationToken ct,
        bool allowNonZeroExitCode = false,
        Stream? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        TimeSpan? timeout = null,
        CancellationToken? waitCancellation = null,
        int? maxOutputLines = null,
        int? maxOutputBytes = null,
        bool cancellable = false)
    {
        if (Interlocked.Exchange(ref _versionChecked, 1) == 0)
        {
            await WarnIfOutdatedAsync(workingDirectory, ct);
        }

        // A writing command stopped half way can leave index.lock behind and a half updated working tree, so once it
        // runs, it is finished: cancelling stops only the wait for the other writing commands. A commit stays
        // cancellable, because its hooks can hang and stopping git before it writes the commit changes nothing.
        if (access == GitCommandAccess.Write && !cancellable)
        {
            waitCancellation ??= ct;
            ct = CancellationToken.None;
        }

        Action<IReadOnlyList<string>, int>? processStarted = _options.ProcessStarted;
        long started = Stopwatch.GetTimestamp();
        GitProcessResult result = await _runner.RunAsync(
            new GitProcessRequest
            {
                WorkingDirectory = workingDirectory,
                Arguments = args,
                Access = access,
                StandardInput = standardInput,
                Environment = environment,
                Timeout = timeout,
                MaxOutputLines = maxOutputLines,
                MaxOutputBytes = maxOutputBytes,
                ProcessStarted = processStarted is null ? null : id => processStarted(args, id)
            },
            ct,
            waitCancellation);
        try
        {
            CommandCompleted?.Invoke(this, new GitCommandLogEntry(
                workingDirectory,
                args,
                result.ExitCode,
                Stopwatch.GetElapsedTime(started),
                result.StandardError.Trim()));
        }
        catch (Exception exception)
        {
            // A failing subscriber must not change the result of the git command.
            _logger.LogWarning(exception, "A subscriber of CommandCompleted failed.");
        }

        if (!allowNonZeroExitCode && result.ExitCode != 0)
        {
            // An argument can be a URL with a token, such as the URL of git remote add.
            _logger.LogWarning("git command failed: {Args} ({ExitCode})", GitUrlCredentials.Mask(string.Join(" ", args)), result.ExitCode);
        }

        return result;
    }

    /// <summary>The git directory and the common git directory of a working tree.</summary>
    private sealed record GitDirectories(string GitDirectory, string CommonDirectory);

    private async Task WarnIfOutdatedAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            GitProcessResult result = await _runner.RunAsync(
                new GitProcessRequest { WorkingDirectory = workingDirectory, Arguments = new[] { "--version" } },
                ct);
            if (GitExecutableLocator.TryParseVersion(result.StandardOutput, out Version? version)
                && version < GitExecutableLocator.MinimumVersion)
            {
                _logger.LogWarning(
                    "git {Version} at {Path} is older than {MinimumVersion}; some git commands may fail.",
                    version,
                    _runner.ExecutablePath,
                    GitExecutableLocator.MinimumVersion);
            }
        }
        catch (Win32Exception)
        {
            // Git is missing; the command itself reports that.
        }
    }
}
