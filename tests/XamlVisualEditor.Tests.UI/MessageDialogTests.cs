using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XamlVisualEditor.App.Views;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.UI;

public sealed class MessageDialogTests
{
    private const string MESSAGE = "Git command failed: fatal: Not possible to fast-forward, aborting.";

    [AvaloniaFact]
    public async Task The_message_can_be_selected_and_copied_with_ctrl_c_and_the_context_menu()
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window owner = new();
        MessageDialog dialog = new() { DataContext = new MessageDialogViewModel("Error", MESSAGE) };
        try
        {
            owner.Show();
            dialog.Show(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            SelectableTextBlock text = dialog.MessageTextBlock;
            IClipboard clipboard = Assert.IsAssignableFrom<IClipboard>(dialog.Clipboard);
            Assert.Equal(MESSAGE, text.Text);

            // A selected part is copied with Ctrl+C.
            text.Focus();
            text.SelectionStart = MESSAGE.IndexOf("fatal:", StringComparison.Ordinal);
            text.SelectionEnd = MESSAGE.Length;
            Assert.Equal("fatal: Not possible to fast-forward, aborting.", text.SelectedText);
            dialog.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal("fatal: Not possible to fast-forward, aborting.", await clipboard.TryGetTextAsync());

            // Without a selection, Copy of the context menu copies the whole message.
            text.ClearSelection();
            MenuFlyout flyout = Assert.IsType<MenuFlyout>(text.ContextFlyout);
            MenuItem copy = Assert.IsType<MenuItem>(flyout.Items[0]);
            Assert.Equal(UiStrings.Copy, copy.Header);
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.Equal(MESSAGE, await clipboard.TryGetTextAsync());
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Application.Current.Styles.Remove(theme);
        }
    }

    [AvaloniaTheory]
    [InlineData(Key.Enter, PhysicalKey.Enter, "\r", true)]
    [InlineData(Key.Escape, PhysicalKey.Escape, null, false)]
    public async Task Clicking_the_message_focuses_it_and_enter_and_escape_still_close_the_dialog_with_a_selection(
        Key key,
        PhysicalKey physicalKey,
        string? keySymbol,
        bool expectedResult)
    {
        Avalonia.Themes.Fluent.FluentTheme theme = new();
        Application.Current!.Styles.Add(theme);
        Window owner = new();
        MessageDialog dialog = new() { DataContext = new MessageDialogViewModel("Error", MESSAGE) };
        try
        {
            owner.Show();
            Task<bool> closed = dialog.ShowDialog<bool>(owner);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            SelectableTextBlock text = dialog.MessageTextBlock;
            Assert.False(text.IsFocused);

            // With headless drawing, a drag through dialog.MouseDown and MouseMove selects nothing, so the press and the
            // release of the left button are raised on the message text, as a click into it routes them.
            Point position = text.TranslatePoint(new Point(1, 5), dialog) ?? throw new InvalidOperationException("The message is not in the dialog.");
            Pointer mouse = new(Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true);
            text.RaiseEvent(new PointerPressedEventArgs(
                text,
                mouse,
                dialog,
                position,
                0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None,
                1));
            text.RaiseEvent(new PointerReleasedEventArgs(
                text,
                mouse,
                dialog,
                position,
                0,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
                KeyModifiers.None,
                MouseButton.Left));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            Assert.True(
                text.IsFocused,
                "Focused: " + dialog.FocusManager?.GetFocusedElement()?.GetType().Name + ", focusable: " + text.Focusable);

            // The selection a drag would make.
            text.SelectionStart = 0;
            text.SelectionEnd = "Git command failed".Length;
            Assert.Equal("Git command failed", text.SelectedText);
            Assert.True(text.IsFocused);
            Assert.False(closed.IsCompleted);

            dialog.KeyPress(key, RawInputModifiers.None, physicalKey, keySymbol);

            Assert.Equal(expectedResult, await closed.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            dialog.Close();
            owner.Close();
            Application.Current.Styles.Remove(theme);
        }
    }
}
