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
    /// default installation folders, and the plain name <c>git</c> is used when it finds nothing.
    /// </summary>
    public string? ExecutablePath { get; init; }

    /// <summary>
    /// Gets environment variables applied to every git process after the built-in ones, so they can replace them.
    /// A null value removes the variable from the environment of the process.
    /// </summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
}
