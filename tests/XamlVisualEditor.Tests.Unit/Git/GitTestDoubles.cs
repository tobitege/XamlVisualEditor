using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Core.Interfaces;
using XamlVisualEditor.Extensions;

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

    /// <summary>Gets or sets whether a commit waits until it is cancelled, like a commit with a hanging hook.</summary>
    public bool BlockCommit { get; set; }

    /// <summary>Gets or sets the error of the status, which is thrown as <see cref="InvalidOperationException"/>; null reads the status.</summary>
    public string? StatusError { get; set; }

    public int StatusCalls => Volatile.Read(ref _statusCalls);

    private int _statusCalls;

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

    public Task<GitRepositoryStatus> GetStatusAsync(string repoPath, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _statusCalls);
        return StatusError is null
            ? Task.FromResult(Status)
            : Task.FromException<GitRepositoryStatus>(new InvalidOperationException(StatusError));
    }

    public Task<GitDiff> GetDiffAsync(string repoPath, GitDiffRequest request, CancellationToken ct = default) =>
        Task.FromResult(new GitDiff { Files = Array.Empty<GitDiffFile>() });

    /// <summary>Gets or sets git's error output of a failing stage, thrown as <see cref="InvalidOperationException"/>; null stages.</summary>
    public string? StageError { get; set; }

    public async Task StageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default)
    {
        await Record("stage", paths);
        if (StageError is not null)
        {
            throw new InvalidOperationException(StageError);
        }
    }

    public Task UnstageAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => Record("unstage", paths);

    public Task DiscardAsync(string repoPath, IReadOnlyList<string> paths, CancellationToken ct = default) => Record("discard", paths);

    public async Task CommitAsync(string repoPath, string message, CancellationToken ct = default)
    {
        await Record("commit", Array.Empty<string>());
        if (BlockCommit)
        {
            await Task.Delay(Timeout.Infinite, ct);
        }

        CommitMessage = message;
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

/// <summary>A repository service without processes whose git directories are fixed paths.</summary>
internal sealed class FixedRepositoryService : IGitRepositoryService
{
    public FixedRepositoryService(string gitDirectory)
    {
        GitDirectory = gitDirectory;
    }

    public string GitDirectory { get; }

    public Task<string?> GetGitDirectoryAsync(string repoPath, CancellationToken ct = default) => Task.FromResult<string?>(GitDirectory);

    public Task<string?> GetCommonDirectoryAsync(string repoPath, CancellationToken ct = default) => Task.FromResult<string?>(GitDirectory);

    public Task<IReadOnlyList<string>> GetRemoteNamesAsync(string repoPath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task InitAsync(string path, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>A remote service without processes: it records the operations and returns scripted results.</summary>
internal sealed class ScriptedRemoteService : IGitRemoteService
{
    private readonly object _gate = new();
    private readonly List<string> _calls = new();

    /// <summary>Gets or sets the result of an operation by its name: fetch, pull or push.</summary>
    public Func<string, GitRemoteResult> Respond { get; set; } = _ => GitRemoteResult.Succeeded(string.Empty);

    /// <summary>Gets or sets whether operations wait until they are cancelled.</summary>
    public bool Block { get; set; }

    /// <summary>
    /// Gets or sets whether a blocked operation returns its result when it is cancelled, like git that ended at the
    /// moment it was to be killed.
    /// </summary>
    public bool FinishWhenCancelled { get; set; }

    public bool WasCancelled { get; private set; }

    public IReadOnlyList<string> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public Task<IReadOnlyList<GitRemote>> GetRemotesAsync(string repoPath, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GitRemote>>([new GitRemote("origin", "remote", "remote")]);

    public Task AddRemoteAsync(string repoPath, string name, string url, CancellationToken ct = default) => throw new NotSupportedException();

    public Task RenameRemoteAsync(string repoPath, string name, string newName, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task RemoveRemoteAsync(string repoPath, string name, CancellationToken ct = default) => throw new NotSupportedException();

    public Task SetRemoteUrlAsync(string repoPath, string name, string url, CancellationToken ct = default) => throw new NotSupportedException();

    public Task<GitRemoteResult> FetchAsync(string repoPath, CancellationToken ct = default) => RunAsync("fetch", ct);

    public Task<GitRemoteResult> PullAsync(string repoPath, CancellationToken ct = default) => RunAsync("pull", ct);

    public Task<GitRemoteResult> PushAsync(string repoPath, string? setUpstreamRemote = null, CancellationToken ct = default) =>
        RunAsync(setUpstreamRemote is null ? "push" : "push -u " + setUpstreamRemote, ct);

    public Task<GitRemoteResult> ForcePushAsync(string repoPath, string branch, string expectedUpstreamSha, CancellationToken ct = default) =>
        RunAsync("push --force-with-lease " + branch + " " + expectedUpstreamSha, ct);

    /// <summary>Gets or sets the lease <see cref="GetForcePushLeaseAsync"/> returns; null offers no force push.</summary>
    public string? ForcePushLease { get; set; }

    public Task<string?> GetForcePushLeaseAsync(string repoPath, string branch, CancellationToken ct = default) =>
        Task.FromResult(ForcePushLease);

    public Task<GitRemoteResult> PushTagAsync(string repoPath, string remote, string name, CancellationToken ct = default) =>
        RunAsync("push " + remote + " tag " + name, ct);

    public Task<GitRemoteResult> DeleteRemoteTagAsync(string repoPath, string remote, string name, CancellationToken ct = default) =>
        RunAsync("push " + remote + " delete tag " + name, ct);

    private async Task<GitRemoteResult> RunAsync(string operation, CancellationToken ct)
    {
        lock (_gate)
        {
            _calls.Add(operation);
        }

        if (Block)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                if (!FinishWhenCancelled)
                {
                    throw;
                }
            }
        }

        return Respond(operation.Split(' ')[0]);
    }
}

/// <summary>How <see cref="GitDialogHost"/> shows a progress dialog; public because test theories take it as data.</summary>
public enum ProgressDialogMode
{
    /// <summary>The dialog stays open until its token is cancelled, then completes with null.</summary>
    StayOpen,

    /// <summary>The user cancels at once: the dialog completes with true.</summary>
    Cancel,

    /// <summary>The dialog completes with null at once, like the editor's host without main window.</summary>
    CompleteAtOnce,

    /// <summary>Showing the dialog fails.</summary>
    Fail
}

/// <summary>
/// Answers confirmations through a callback and shows progress dialogs like the editor, as set by
/// <see cref="ProgressMode"/>.
/// </summary>
internal sealed class GitDialogHost : IDialogHost
{
    private readonly Func<string, object?, object?> _respond;

    public GitDialogHost(Func<string, object?, object?> respond)
    {
        _respond = respond;
    }

    public List<(string DialogId, object? ViewModel)> Requests { get; } = new();

    /// <summary>Gets or sets how a progress dialog behaves.</summary>
    public ProgressDialogMode ProgressMode { get; set; }

    public IEnumerable<ConfirmationDialogRequest> Confirmations => Requests.Select(request => request.ViewModel).OfType<ConfirmationDialogRequest>();

    public IEnumerable<ProgressDialogRequest> Progress => Requests.Select(request => request.ViewModel).OfType<ProgressDialogRequest>();

    public IDisposable RegisterDialog(string dialogId, Func<object?, object> factory) => throw new NotSupportedException();

    /// <summary>
    /// Gets or sets an answer to questions that can wait, such as for the token that closes the question; it replaces
    /// the callback of the constructor when set.
    /// </summary>
    public Func<object?, CancellationToken, Task<object?>>? RespondAsync { get; set; }

    /// <summary>Gets or sets how a progress dialog ends, replacing <see cref="ProgressMode"/> when set.</summary>
    public Func<ProgressDialogRequest, CancellationToken, Task<bool?>>? ProgressAsync { get; set; }

    public async Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add((dialogId, viewModel));
        }

        if (dialogId != ProgressDialogRequest.DialogId)
        {
            object? answer = RespondAsync is { } respondAsync
                ? await respondAsync(viewModel, cancellationToken)
                : _respond(dialogId, viewModel);
            return answer is T result ? result : default;
        }

        if (ProgressAsync is { } progressAsync)
        {
            return (T?)(object?)await progressAsync((ProgressDialogRequest)viewModel!, cancellationToken);
        }

        switch (ProgressMode)
        {
            // As in the editor, the cancel button shows only while the work can be cancelled.
            case ProgressDialogMode.Cancel when ((ProgressDialogRequest)viewModel!).CancelAvailable is { } available:
                try
                {
                    await available.FirstAsync(canCancel => canCancel).ToTask(cancellationToken);
                    return (T?)(object?)true;
                }
                catch (OperationCanceledException)
                {
                    return default;
                }
            case ProgressDialogMode.Cancel:
                return (T?)(object?)true;
            case ProgressDialogMode.CompleteAtOnce:
                return default;
            case ProgressDialogMode.Fail:
                throw new InvalidOperationException("Dialog factory must return a Window instance.");
        }

        TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (cancellationToken.Register(() => closed.TrySetResult()))
        {
            await closed.Task;
        }

        return default;
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
