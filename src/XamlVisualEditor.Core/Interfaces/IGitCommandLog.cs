using System;

namespace XamlVisualEditor.Core.Interfaces;

/// <summary>
/// Reports every git command a git service ran, for example to show it in an output channel.
/// </summary>
public interface IGitCommandLog
{
    /// <summary>
    /// Raised on the thread that ran the command after git exited. Not raised when git could not be started.
    /// </summary>
    event EventHandler<GitCommandLogEntry>? CommandCompleted;
}
