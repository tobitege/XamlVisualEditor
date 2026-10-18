using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

public sealed partial class MessageDialog : Window
{
    private IDisposable? _closeHandler;

    public MessageDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    /// <summary>Gets the message text, which can be selected and copied with Ctrl+C or its context menu.</summary>
    internal SelectableTextBlock MessageTextBlock => this.FindControl<SelectableTextBlock>("MessageText")!;

    /// <summary>Copies the selected part of the message, or the whole message when nothing is selected.</summary>
    internal void CopyMessage()
    {
        SelectableTextBlock text = MessageTextBlock;
        if (text.SelectionStart == text.SelectionEnd)
        {
            text.SelectAll();
        }

        text.Copy();
    }

    private void OnCopyClick(object? sender, RoutedEventArgs e) => CopyMessage();

    private void OnSelectAllClick(object? sender, RoutedEventArgs e) => MessageTextBlock.SelectAll();

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is MessageDialogViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(ctx =>
            {
                Close(ctx.Input);
                ctx.SetOutput(RxVoid.Default);
            });
        }
    }
}
