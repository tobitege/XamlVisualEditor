using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Threading;

namespace XamlVisualEditor.App.Services;

internal static class DialogAppearance
{
    private const string FrameClass = "editor-dialog-frame";
    private static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) != 0) return;

        Window.IsVisibleProperty.Changed.AddClassHandler<Window>(static (window, _) =>
        {
            if (!window.IsVisible) return;
            window.Opened -= OnDialogOpened;
            window.Opened += OnDialogOpened;
        });
    }

    private static void OnDialogOpened(object? sender, EventArgs args)
    {
        if (sender is not Window window) return;
        window.Opened -= OnDialogOpened;
        if (window.Owner is null) return;
        if (window.Content is Border existing && existing.Classes.Contains(FrameClass)) return;

        var focused = window.FocusManager?.GetFocusedElement();
        // Detach the existing root before adding it to the focus-independent frame.
        object? content = window.Content;
        window.Content = null;
        var frame = new Border
        {
            BorderThickness = new Thickness(2),
            Child = content as Control ?? new ContentControl { Content = content }
        };
        frame.Classes.Add(FrameClass);
        frame.Bind(Border.BorderBrushProperty,
            new DynamicResourceExtension("SystemControlHighlightAccentBrush"));
        window.Content = frame;

        if (focused is not null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (window.IsVisible) focused.Focus();
            }, DispatcherPriority.Loaded);
        }
    }
}
