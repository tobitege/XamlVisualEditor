using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App.Views;

public sealed partial class ConfirmDialog : Window
{
    private IDisposable? _closeHandler;

    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
        // A destructive question starts on the cancel button: Enter or Space cancels, and the first Tab does not reach
        // the confirm button, which it would from a dialog without focus.
        Opened += (_, _) =>
        {
            if (DataContext is ConfirmDialogViewModel { IsDestructive: true })
            {
                Dispatcher.UIThread.Post(() => this.FindControl<Button>("CancelButton")!.Focus(), DispatcherPriority.Loaded);
            }
        };
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is ConfirmDialogViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(ctx =>
            {
                Close(ctx.Input);
                ctx.SetOutput(RxVoid.Default);
            });
        }
    }
}
