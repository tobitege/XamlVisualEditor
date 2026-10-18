using XamlVisualEditor.Core;
using XamlVisualEditor.Tests.Shared;
using XamlVisualEditor.Workspace;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

/// <summary>The write gate held across a check and the command it decides on.</summary>
public sealed class GitWriteGateTests
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task Writing_commands_inside_the_held_gate_run_at_once_and_others_wait_until_it_is_released()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using GitTestRepository repository = GitTestRepository.Create("a.txt");
        GitProcessRunner runner = new(repository.ProcessOptions);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task<int> held = runner.RunWithWriteGateAsync(repository.Path, async () =>
        {
            await runner.RunAsync(Tag(repository, "inside"), ct);
            // A nested hold of the same gate runs at once as well.
            await runner.RunWithWriteGateAsync(repository.Path, () => runner.RunAsync(Tag(repository, "nested"), ct), ct);
            entered.SetResult();
            await release.Task;
            return 1;
        }, ct);
        await entered.Task.WaitAsync(WaitLimit, ct);

        Task<GitProcessResult> outside = runner.RunAsync(Tag(repository, "outside"), ct);
        Task ended = runner.WhenWritesEnded();
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => runner.RunWithWriteGateAsync(repository.Path, () => Task.FromResult(0), cancelled.Token));
        await Task.Delay(300, ct);

        Assert.False(outside.IsCompleted);
        Assert.False(ended.IsCompleted);
        Assert.Equal(["inside", "nested"], Tags(repository));

        release.SetResult();
        Assert.Equal(1, await held.WaitAsync(WaitLimit, ct));
        Assert.Equal(0, (await outside.WaitAsync(WaitLimit, ct)).ExitCode);
        await ended.WaitAsync(WaitLimit, ct);
        Assert.Equal(["inside", "nested", "outside"], Tags(repository));
    }

    private static GitProcessRequest Tag(GitTestRepository repository, string name) => new()
    {
        WorkingDirectory = repository.Path,
        Arguments = ["tag", name],
        Access = GitCommandAccess.Write
    };

    private static string[] Tags(GitTestRepository repository) =>
        repository.RunGit("tag", "-l").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
