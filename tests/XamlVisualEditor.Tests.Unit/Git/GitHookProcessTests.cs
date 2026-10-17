using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>
/// Git commands whose hook starts a process that reports through a <see cref="LoopbackSignal"/> over its own
/// connection, waits until the test closes the connection and then writes a marker file.
/// </summary>
public sealed class GitHookProcessTests
{
    private const string MARKER_FILE = "hook-marker.txt";
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task A_commit_returns_although_its_hook_left_a_process_that_holds_the_output_open()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateRepositoryWithStagedFile();
        using LoopbackSignal signal = new();
        WriteBackgroundHook(repository, signal.Port);
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);

        // The process the hook left behind keeps the error output of git open until the test releases it.
        await service.CommitAsync(repository.Path, "with hook", ct).WaitAsync(WaitLimit, ct);

        Assert.NotEqual(head, repository.RunGit("rev-parse", "HEAD").Trim());
        Assert.Equal("started", await signal.WaitForProcessAsync(new TaskCompletionSource().Task));
        signal.Release();
    }

    [Fact]
    public async Task Cancelling_a_commit_ends_the_processes_its_hook_started()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateRepositoryWithStagedFile();
        using LoopbackSignal signal = new();
        WriteForegroundHook(repository, signal.Port);
        string head = repository.RunGit("rev-parse", "HEAD").Trim();
        GitService service = new(options: repository.ProcessOptions);
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);

        Task commit = service.CommitAsync(repository.Path, "cancelled", cancellation.Token);
        Assert.Equal("started", await signal.WaitForProcessAsync(commit));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commit.WaitAsync(WaitLimit, ct));
        // The process holds its own connection until it ends, and it writes the marker before it ends unless it is
        // killed; the test never closes the connection first. A closed connection without marker therefore means
        // that the process was killed. On Windows, killing only the process tree of git leaves this process running.
        Assert.True(await signal.WaitForCloseAsync(), "A process started by the hook survived the cancellation.");
        Assert.False(
            File.Exists(Path.Combine(repository.Path, MARKER_FILE)),
            "A process started by the hook survived the cancellation and wrote the marker.");
        Assert.Equal(head, repository.RunGit("rev-parse", "HEAD").Trim());
    }

    [Fact]
    public async Task A_pull_waiting_for_a_running_commit_can_be_cancelled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateRepositoryWithStagedFile();
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        using LoopbackSignal signal = new();
        WriteForegroundHook(repository, signal.Port);
        TaskCompletionSource mergeWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            WriteGateWaitStarted = arguments =>
            {
                if (arguments[0] == "merge")
                {
                    mergeWaiting.TrySetResult();
                }
            }
        });

        // The commit holds the write gate until the test releases the process of its hook.
        Task commit = service.CommitAsync(repository.Path, "held", ct);
        Assert.Equal("started", await signal.WaitForProcessAsync(commit));
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<GitRemoteResult> pull = service.PullAsync(repository.Path, cancellation.Token);
        await mergeWaiting.Task.WaitAsync(WaitLimit, ct);

        // Only the cancellation of the wait for the write gate can end the pull now.
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pull.WaitAsync(WaitLimit, ct));
        Assert.False(commit.IsCompleted);
        signal.Release();
        await commit.WaitAsync(WaitLimit, ct);
    }

    [Fact]
    public async Task A_commit_that_lands_while_the_pull_waits_for_it_is_not_counted_as_pulled()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = CreateRepositoryWithStagedFile();
        repository.AddBareRemote();
        repository.RunGit("push", "-q", "-u", "origin", "main");
        using LoopbackSignal signal = new();
        WriteForegroundHook(repository, signal.Port);
        string upstream = repository.RunGit("rev-parse", "origin/main").Trim();
        TaskCompletionSource<string[]> mergeWaiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        GitService service = new(options: new GitProcessOptions
        {
            Environment = repository.Environment,
            WriteGateWaitStarted = arguments =>
            {
                if (arguments[0] == "merge")
                {
                    mergeWaiting.TrySetResult(arguments.ToArray());
                }
            }
        });

        // The commit holds the write gate until the test releases the process of its hook; the upstream has no new
        // commits, so the pull brings none.
        Task commit = service.CommitAsync(repository.Path, "held", ct);
        Assert.Equal("started", await signal.WaitForProcessAsync(commit));
        Task<GitRemoteResult> pull = service.PullAsync(repository.Path, ct);
        // The merge that waits names the upstream commit that the pull resolved.
        Assert.Equal(new[] { "merge", "--ff-only", upstream }, await mergeWaiting.Task.WaitAsync(WaitLimit, ct));
        signal.Release();
        await commit.WaitAsync(WaitLimit, ct);

        GitRemoteResult pulled = await pull.WaitAsync(WaitLimit, ct);

        Assert.True(pulled.Success, pulled.Message);
        Assert.Equal(0, pulled.PulledCommitCount);
        Assert.Equal("held", repository.RunGit("log", "-1", "--format=%s").Trim());
    }

    private static GitTestRepository CreateRepositoryWithStagedFile()
    {
        GitTestRepository repository = GitTestRepository.Create("a.txt");
        repository.WriteFile("b.txt", "b\n");
        repository.RunGit("add", "--", "b.txt");
        return repository;
    }

    /// <summary>
    /// Writes a pre-commit hook that starts a process in the background and ends: the process connects to the signal,
    /// writes "started", waits in an external program until the connection closes and then writes the marker file in
    /// the working tree.
    /// </summary>
    private static void WriteBackgroundHook(GitTestRepository repository, int port)
    {
        WriteHook(
            repository,
            "#!/usr/bin/env bash\n"
            + "( " + SignalCommand(port) + " ) &\n"
            + "exit 0\n");
    }

    /// <summary>
    /// Writes a pre-commit hook whose last command runs in the foreground until the test closes the connection: a
    /// bash below the hook shell that opens its own connection to the signal, writes "started", waits until the
    /// connection closes and then writes the marker file. Nothing it holds was inherited from the hook shell, so the
    /// connection closes only when this bash and its child have ended. On Windows, killing the process tree of git leaves
    /// this bash running; only the job of git ends it.
    /// </summary>
    private static void WriteForegroundHook(GitTestRepository repository, int port)
    {
        WriteHook(repository, "#!/bin/sh\nbash -c '" + SignalCommand(port) + "'\n");
    }

    private static string SignalCommand(int port) =>
        "exec 3<>/dev/tcp/127.0.0.1/" + port.ToString(CultureInfo.InvariantCulture)
        + " && echo started >&3 && cat <&3 >/dev/null; echo done > " + MARKER_FILE;

    private static void WriteHook(GitTestRepository repository, string text)
    {
        string hook = Path.Combine(repository.Path, ".git", "hooks", "pre-commit");
        File.WriteAllText(hook, text);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
