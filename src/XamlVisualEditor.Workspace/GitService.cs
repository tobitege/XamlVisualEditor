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
/// Git CLI-backed service for repository status and diff operations.
/// </summary>
public sealed class GitService : IGitService, IGitRepositoryService, IGitCommandLog
{
    private readonly ILogger<GitService> _logger;
    private readonly GitProcessRunner _runner;
    private readonly ConcurrentDictionary<string, string> _gitDirectories = new(FileSystemPathComparison.Comparer);
    private int _versionChecked;

    /// <summary>Creates the service.</summary>
    /// <param name="logger">Receives warnings about failing git commands and an outdated git version.</param>
    /// <param name="options">The git executable and environment of the git processes; <see cref="GitProcessOptions.Default"/> when null.</param>
    public GitService(ILogger<GitService>? logger = null, GitProcessOptions? options = null)
    {
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GitService>.Instance;
        _runner = new GitProcessRunner(options ?? GitProcessOptions.Default);
    }

    /// <inheritdoc />
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
            isReadOnly: true,
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
        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "--no-optional-locks", "status", "--porcelain=v2", "-z", "--branch", "--untracked-files=normal" },
            isReadOnly: true,
            ct);
        if (result.ExitCode != 0)
        {
            return new GitRepositoryStatus
            {
                RepositoryRoot = repoPath,
                IsRepository = false,
                ErrorMessage = result.StandardError,
                Changes = Array.Empty<GitFileChange>()
            };
        }

        GitRepositoryOperation operation = await GetOperationAsync(repoPath, ct);
        return GitStatusParser.ParsePorcelainV2(repoPath, result.StandardOutput, operation);
    }

    public async Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default)
    {
        string[] args = BuildDiffArguments(request);
        GitProcessResult result = await RunGitAsync(repoPath, args, isReadOnly: true, ct, allowNonZeroExitCode: true);
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

    public async Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        List<string> args = new() { "add", "--" };
        args.AddRange(paths);
        await RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    public async Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        List<string> args = new() { "reset", "-q", "--" };
        args.AddRange(paths);
        await RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    public async Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0)
        {
            return;
        }

        // Restores the working file from the index only: staged content stays, and a file that is
        // staged but not yet committed is never removed from disk.
        List<string> args = new() { "restore", "--worktree", "--" };
        args.AddRange(paths);
        await RunGitCheckedAsync(repoPath, args, standardInput: null, ct);
    }

    public async Task CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Standard input carries the message as UTF-8 bytes and has no command line length limit.
        await using Stream input = GitProcessRequest.Utf8Input(message);
        await RunGitCheckedAsync(repoPath, new[] { "commit", "-F", "-" }, input, ct);
    }

    /// <summary>
    /// Runs a modifying git command and throws <see cref="InvalidOperationException"/> with git's error output
    /// when it exits with a non-zero code.
    /// </summary>
    private async Task RunGitCheckedAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        Stream? standardInput,
        CancellationToken ct)
    {
        GitProcessResult result = await RunGitAsync(workingDirectory, args, isReadOnly: false, ct, standardInput: standardInput);
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
            ? $"git {args[0]} exited with code {result.ExitCode}."
            : error);
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
            isReadOnly: true,
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
        if (_gitDirectories.TryGetValue(repoPath, out string? cached))
        {
            return cached;
        }

        GitProcessResult result = await RunGitAsync(
            repoPath,
            new[] { "rev-parse", "--absolute-git-dir" },
            isReadOnly: true,
            ct,
            allowNonZeroExitCode: true);
        string gitDirectory = result.StandardOutput.Trim();
        if (result.ExitCode != 0 || gitDirectory.Length == 0)
        {
            return null;
        }

        gitDirectory = Path.GetFullPath(gitDirectory);
        _gitDirectories[repoPath] = gitDirectory;
        return gitDirectory;
    }

    internal static GitRepositoryOperation DetectOperation(string gitDirectory)
    {
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

        return File.Exists(Path.Combine(gitDirectory, "REVERT_HEAD"))
            ? GitRepositoryOperation.Reverting
            : GitRepositoryOperation.None;
    }

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

    private static string[] BuildDiffArguments(GitDiffRequest request)
    {
        List<string> args = new();

        if (request.IsUntracked && !string.IsNullOrWhiteSpace(request.Path))
        {
            args.Add("diff");
            args.Add("--no-index");
            args.Add("--no-color");
            args.Add("--unified=3");
            args.Add("--");
            args.Add("/dev/null");
            args.Add(request.Path!);
            return args.ToArray();
        }

        args.Add("diff");
        if (request.Staged)
        {
            args.Add("--cached");
        }

        args.Add("--no-color");
        args.Add("--unified=3");

        if (!string.IsNullOrWhiteSpace(request.Path))
        {
            args.Add("--");
            args.Add(request.Path!);
        }

        return args.ToArray();
    }

    private async Task<GitProcessResult> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> args,
        bool isReadOnly,
        CancellationToken ct,
        bool allowNonZeroExitCode = false,
        Stream? standardInput = null)
    {
        if (Interlocked.Exchange(ref _versionChecked, 1) == 0)
        {
            await WarnIfOutdatedAsync(workingDirectory, ct);
        }

        long started = Stopwatch.GetTimestamp();
        GitProcessResult result = await _runner.RunAsync(
            new GitProcessRequest
            {
                WorkingDirectory = workingDirectory,
                Arguments = args,
                IsReadOnly = isReadOnly,
                StandardInput = standardInput
            },
            ct);
        CommandCompleted?.Invoke(this, new GitCommandLogEntry(
            workingDirectory,
            args,
            result.ExitCode,
            Stopwatch.GetElapsedTime(started),
            result.StandardError.Trim()));

        if (!allowNonZeroExitCode && result.ExitCode != 0)
        {
            _logger.LogWarning("git command failed: {Args} ({ExitCode})", string.Join(" ", args), result.ExitCode);
        }

        return result;
    }

    private async Task WarnIfOutdatedAsync(string workingDirectory, CancellationToken ct)
    {
        try
        {
            GitProcessResult result = await _runner.RunAsync(
                new GitProcessRequest { WorkingDirectory = workingDirectory, Arguments = new[] { "--version" }, IsReadOnly = true },
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
