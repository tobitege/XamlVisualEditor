using System;
using System.Threading;
using System.Threading.Tasks;

namespace XamlVisualEditor.Extensions;

/// <summary>Allows extensions to show, toggle, or query view visibility.</summary>
public interface IExtensionViewHost
{
    /// <summary>Raised when extension view visibility changes.</summary>
    event EventHandler<ExtensionViewVisibilityChangedEventArgs>? VisibilityChanged;

    /// <summary>Raised when extension view focus changes.</summary>
    event EventHandler<ExtensionViewFocusChangedEventArgs>? FocusChanged;

    /// <summary>Shows a view by id.</summary>
    Task ShowAsync(string viewId, CancellationToken cancellationToken);

    /// <summary>Toggles a view by id.</summary>
    Task ToggleAsync(string viewId, CancellationToken cancellationToken);

    /// <summary>Checks if a view is visible.</summary>
    Task<bool> IsVisibleAsync(string viewId, CancellationToken cancellationToken);

    /// <summary>Activates a view by id without changing visibility.</summary>
    Task ActivateAsync(string viewId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a document tab with the content from <paramref name="createViewModel"/>, or activates the open tab with
    /// the same <paramref name="documentId"/> without calling <paramref name="createViewModel"/>. The content is shown
    /// as the content of a content control: an Avalonia control as it is, any other object through the data templates
    /// of the host, which know only the view models of the built-in extensions, so other objects show as text.
    /// Returns the shown content, new or existing, or null when the host has no document tabs. The tab is not saved in
    /// the layout; closing it disposes content that implements <see cref="IDisposable"/>. Callers prefix their ids,
    /// for example with their extension id.
    /// </summary>
    Task<object?> ShowDocumentAsync(
        string documentId,
        string title,
        string? toolTip,
        Func<object> createViewModel,
        CancellationToken cancellationToken) => Task.FromResult<object?>(null);
}

/// <summary>Describes extension view visibility state.</summary>
public sealed class ExtensionViewVisibilityChangedEventArgs : EventArgs
{
    public ExtensionViewVisibilityChangedEventArgs(string viewId, bool isVisible)
    {
        ViewId = viewId;
        IsVisible = isVisible;
    }

    public string ViewId { get; }

    public bool IsVisible { get; }
}

/// <summary>Describes extension view focus state.</summary>
public sealed class ExtensionViewFocusChangedEventArgs : EventArgs
{
    public ExtensionViewFocusChangedEventArgs(string viewId, bool isFocused)
    {
        ViewId = viewId;
        IsFocused = isFocused;
    }

    public string ViewId { get; }

    public bool IsFocused { get; }
}
