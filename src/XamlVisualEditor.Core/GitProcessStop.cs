using System;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
    private readonly object _gate = new();
    private readonly BehaviorSubject<bool> _writing = new(false);
    private int _writers;

    public GitProcessStop()
    {
        Token = _stop.Token;
    }

    /// <summary>Gets the scope of the current asynchronous flow; null outside a scope.</summary>
    public static GitProcessStop? Current => CurrentScope.Value;

    /// <summary>Gets the token that a stop cancels.</summary>
    public CancellationToken Token { get; }

    /// <summary>Gets whether a writing git command of the scope waits for or runs git.</summary>
    public bool IsWriting => _writing.Value;

    /// <summary>
    /// Gets <see cref="IsWriting"/>: the current value on subscribing, then every change, in order. Changes arrive on
    /// the thread of the git command under a lock of the scope, so an observer only records or posts them.
    /// </summary>
    public IObservable<bool> Writing => Observable.Create<bool>(observer =>
    {
        // Under the lock the current value reaches a new observer before any later change.
        lock (_gate)
        {
            return _writing.Subscribe(observer);
        }
    });

    /// <summary>Gets whether the scope was stopped.</summary>
    public bool IsStopped => _stop.IsCancellationRequested;

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
        CountWriter(1);
        return new WriteEnd(this);
    }

    // The lock keeps the changes in the order of the counts, so the last value an observer gets is the current one.
    private void CountWriter(int change)
    {
        lock (_gate)
        {
            _writers += change;
            if (_writers > 0 != _writing.Value)
            {
                _writing.OnNext(_writers > 0);
            }
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
                scope.CountWriter(-1);
            }
        }
    }
}
