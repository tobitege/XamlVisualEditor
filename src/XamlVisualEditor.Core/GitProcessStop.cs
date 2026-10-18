using System;
using System.Threading;

namespace XamlVisualEditor.Core;

/// <summary>
/// Lets the user stop the writing git commands started in a scope. A writing command otherwise runs to its end once it
/// started, also when its cancellation token is cancelled or the editor closes, because a command stopped half way can
/// leave <c>index.lock</c> behind and a half updated working tree; a stop accepts that risk for a command that hangs,
/// for example in a hook.
/// </summary>
public static class GitProcessStop
{
    private static readonly AsyncLocal<CancellationToken> CurrentStop = new();

    /// <summary>Gets the token that stops the writing git commands of the current scope; none outside a scope.</summary>
    public static CancellationToken Current => CurrentStop.Value;

    /// <summary>
    /// Lets <paramref name="stop"/> stop the writing git commands that are started until the result is disposed,
    /// including those of the tasks started meanwhile.
    /// </summary>
    public static IDisposable Allow(CancellationToken stop)
    {
        CancellationToken previous = CurrentStop.Value;
        CurrentStop.Value = stop;
        return new Scope(previous);
    }

    private sealed class Scope(CancellationToken previous) : IDisposable
    {
        public void Dispose() => CurrentStop.Value = previous;
    }
}
