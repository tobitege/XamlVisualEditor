using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>A git service without processes: a fixed repository root and status, and a record of the commands.</summary>
internal sealed class FakeGitService : IGitService
{
    private readonly object _gate = new();
    private readonly List<(string Command, string[] Paths)> _calls = new();

    public FakeGitService(string repositoryRoot, params GitFileChange[] changes)
    {
        RepositoryRoot = repositoryRoot;
        Status = new GitRepositoryStatus { RepositoryRoot = repositoryRoot, BranchName = "main", Changes = changes };
    }

    public string RepositoryRoot { get; }

    public GitRepositoryStatus Status { get; set; }

    public string? CommitMessage { get; private set; }

    public IReadOnlyList<(string Command, string[] Paths)> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default) => Task.FromResult<string?>(RepositoryRoot);

    public Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default) => Task.FromResult(Status);

    public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
        Task.FromResult(new GitDiff { Files = Array.Empty<GitDiffFile>() });

    public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => Record("stage", paths);

    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => Record("unstage", paths);

    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => Record("discard", paths);

    public Task CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        CommitMessage = message;
        return Record("commit", Array.Empty<string>());
    }

    private Task Record(string command, IReadOnlyList<string> paths)
    {
        lock (_gate)
        {
            _calls.Add((command, paths.ToArray()));
        }

        return Task.CompletedTask;
    }
}

/// <summary>Passes every call to a real git service and holds the first status call until it is released.</summary>
internal sealed class GatedGitService : IGitService
{
    private readonly IGitService _inner;
    private int _statusCalls;
    private int _rootCalls;
    private int _activeStatusCalls;
    private int _maxConcurrentStatusCalls;

    public GatedGitService(IGitService inner)
    {
        _inner = inner;
    }

    public TaskCompletionSource FirstStatusStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource ReleaseFirstStatus { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int StatusCalls => Volatile.Read(ref _statusCalls);

    public int RootCalls => Volatile.Read(ref _rootCalls);

    public int MaxConcurrentStatusCalls => Volatile.Read(ref _maxConcurrentStatusCalls);

    public Task<string?> GetRepositoryRootAsync(string path, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _rootCalls);
        return _inner.GetRepositoryRootAsync(path, ct);
    }

    public async Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        int call = Interlocked.Increment(ref _statusCalls);
        int active = Interlocked.Increment(ref _activeStatusCalls);
        InterlockedMax(ref _maxConcurrentStatusCalls, active);
        try
        {
            if (call == 1)
            {
                FirstStatusStarted.TrySetResult();
                await ReleaseFirstStatus.Task.WaitAsync(ct);
            }

            return await _inner.GetStatusAsync(repoPath, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeStatusCalls);
        }
    }

    public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
        _inner.GetDiffAsync(repoPath, request, ct);

    public Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        _inner.StageAsync(repoPath, paths, ct);

    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        _inner.UnstageAsync(repoPath, paths, ct);

    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) =>
        _inner.DiscardAsync(repoPath, paths, ct);

    public Task CommitAsync(string repoPath, string message, CancellationToken ct = default) =>
        _inner.CommitAsync(repoPath, message, ct);

    private static void InterlockedMax(ref int target, int value)
    {
        int current = Volatile.Read(ref target);
        while (value > current)
        {
            int previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
