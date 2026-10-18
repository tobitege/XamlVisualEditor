namespace XamlVisualEditor.Extensions;

/// <summary>Hosts modal dialogs for extensions.</summary>
public interface IDialogHost
{
    /// <summary>Registers a dialog factory.</summary>
    IDisposable RegisterDialog(string dialogId, Func<object?, object> factory);

    /// <summary>Shows a dialog by id.</summary>
    Task<T?> ShowDialogAsync<T>(string dialogId, object? viewModel, CancellationToken cancellationToken);
}

/// <summary>Requests a modal confirmation with explicit action text.</summary>
/// <remarks><see cref="CancelText"/> replaces the default "Cancel" label, for example when declining is an action of its own.</remarks>
public sealed record ConfirmationDialogRequest(string Title, string Message, string ConfirmText, bool IsDestructive = false,
    string? CancelText = null)
{
    /// <summary>Identifier of the shared confirmation dialog.</summary>
    public const string DialogId = "xve.confirm";
}

/// <summary>Requests a modal, indeterminate progress dialog that blocks the editor while work runs.</summary>
/// <remarks>
/// The dialog closes when the cancellation token passed to <see cref="IDialogHost.ShowDialogAsync{T}"/> is cancelled,
/// which the caller does when its work ends; the dialog task then completes with null. The user can close it earlier
/// with the cancel button or the window button; the dialog task then completes with true, and the caller cancels its
/// work. Any other result, such as the immediate default of a host without main window, is no cancel. With a
/// <c>CancelDelay</c> the cancel button shows and the window button cancels only once the delay elapsed, for work whose
/// cancel has a risk that a moment of patience avoids.
/// </remarks>
public sealed record ProgressDialogRequest(string Title, string Message, string? CancelText = null)
{
    /// <summary>
    /// Gets how long the shown dialog waits before its cancel button shows and its window button cancels; zero, the
    /// default, allows the cancel at once.
    /// </summary>
    public TimeSpan CancelDelay { get; init; }

    /// <summary>Identifier of the shared progress dialog.</summary>
    public const string DialogId = "xve.progress";
}

/// <summary>Controls workspace opening behavior.</summary>
public interface IWorkspaceHost
{
    /// <summary>Opens a workspace path.</summary>
    Task OpenWorkspaceAsync(string workspacePath, WorkspaceOpenMode mode, CancellationToken cancellationToken);
}

/// <summary>Workspace open modes.</summary>
public enum WorkspaceOpenMode
{
    /// <summary>Open in the current window.</summary>
    CurrentWindow,

    /// <summary>Open in a new window.</summary>
    NewWindow
}
