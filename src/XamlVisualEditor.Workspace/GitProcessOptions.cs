using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Workspace;

/// <summary>
/// Settings for the git processes that <see cref="GitService"/> starts.
/// </summary>
public sealed class GitProcessOptions
{
    /// <summary>Gets the options that locate git through <see cref="GitExecutableLocator"/> and add no environment variables.</summary>
    public static GitProcessOptions Default { get; } = new();

    /// <summary>
    /// Gets the path of the git executable. When null, <see cref="GitExecutableLocator"/> searches PATH and the
    /// default installation folders, and the plain name <c>git</c> is used when it finds nothing. A Git for Windows
    /// launcher, such as <c>&lt;root&gt;\cmd\git.exe</c>, or a scoop shim of it is replaced by the git it starts in
    /// both cases.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets environment variables applied to every git process after the built-in ones, so they can replace them.
    /// A null value removes the variable from the environment of the process. For git of a Git for Windows launcher,
    /// <c>MSYSTEM</c>, a missing <c>HOME</c> and the front of PATH are then set as that launcher sets them.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// Gets the time after which fetch, pull and push are stopped; null lets them run until they end or are
    /// cancelled. Ten minutes by default.
    /// </summary>
    public TimeSpan? NetworkTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets a callback for tests that receives the arguments and process id of every git process right after it started.</summary>
    internal Action<IReadOnlyList<string>, int>? ProcessStarted { get; init; }

    /// <summary>
    /// Gets a callback for tests that receives the arguments of a writing command when it starts to wait for another
    /// writing command of its working directory. The callback must not throw.
    /// </summary>
    internal Action<IReadOnlyList<string>>? WriteGateWaitStarted { get; init; }
}
