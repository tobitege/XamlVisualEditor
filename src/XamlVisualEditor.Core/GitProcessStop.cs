using System;
using System.Threading;

namespace XamlVisualEditor.Core;

/// <summary>
/// A scope in which the user can stop the writing git commands. A writing command otherwise is not stopped once it
/// started, neither by its cancellation token nor by the editor ending, because a command stopped half way can leave
/// <c>index.lock</c> behind and a half updated working tree; a stop accepts that risk for a command that hangs, for
/// example in a hook. The scope counts the writing commands that wait for or run git, so a caller offers the stop only
/// while one of them runs and learns when the last one ended.
/// </summary>
public sealed class GitProcessStop
{
    private static readonly AsyncLocal<GitProcessStop?> CurrentScope = new();

    // Without a timer the source holds nothing that needs disposing, so a scope outlives any use of its token.
    private readonly CancellationTokenSource _stop = new();
    private int _writing;

    public GitProcessStop()
    {
        Token = _stop.Token;
    }

    /// <summary>Gets the scope of the current asynchronous flow; null outside a scope.</summary>
    public static GitProcessStop? Current => CurrentScope.Value;

    /// <summary>Gets the token that a stop cancels.</summary>
    public CancellationToken Token { get; }

    /// <summary>Gets whether a writing git command of the scope waits for or runs git.</summary>
    public bool IsWriting => Volatile.Read(ref _writing) > 0;

    /// <summary>Gets whether the scope was stopped.</summary>
    public bool IsStopped => _stop.IsCancellationRequested;

    /// <summary>Raised on any thread when <see cref="IsWriting"/> changes.</summary>
    public event EventHandler? WritingChanged;

    /// <summary>
    /// Makes this the scope of the current flow until the result is disposed, including the tasks started meanwhile.
    /// </summary>
    public IDisposable Enter()
    {
        GitProcessStop? previous = CurrentScope.Value;
        CurrentScope.Value = this;
        return new Restore(previous);
    }

    /// <summary>Stops the writing git commands of the scope that wait for or run git, and those that start later.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Counts a writing git command until the result is disposed.</summary>
    public IDisposable BeginWrite()
    {
        if (Interlocked.Increment(ref _writing) == 1)
        {
            WritingChanged?.Invoke(this, EventArgs.Empty);
        }

        return new WriteEnd(this);
    }

    private void EndWrite()
    {
        if (Interlocked.Decrement(ref _writing) == 0)
        {
            WritingChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private sealed class Restore(GitProcessStop? previous) : IDisposable
    {
        public void Dispose() => CurrentScope.Value = previous;
    }

    private sealed class WriteEnd(GitProcessStop scope) : IDisposable
    {
        private int _ended;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _ended, 1) == 0)
            {
                scope.EndWrite();
            }
        }
    }
}
