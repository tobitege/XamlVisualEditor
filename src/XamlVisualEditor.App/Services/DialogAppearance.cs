using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.App.Services;

/// <summary>
/// Gives every owned window, including the dialogs of extensions, the accent frame that EditorTheme styles for
/// <see cref="FrameClass"/>.
/// </summary>
/// <remarks>
/// A window receives its owner during <c>Show</c> before its first frame is rendered, so the class is in place for
/// that frame and the content of the window stays where it is.
/// </remarks>
internal static class DialogAppearance
{
    internal const string FrameClass = "editor-dialog-frame";
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;
        Install();
    }

    /// <summary>Adds the class handler; disposing the result removes it.</summary>
    internal static IDisposable Install() =>
        WindowBase.OwnerProperty.Changed.AddClassHandler<Window>(static (window, _) =>
            window.Classes.Set(FrameClass, window.Owner is not null));
}
