using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App.Views;

public sealed partial class ProgressDialog : Window
{
    private IDisposable? _closeHandler;
    private IDisposable? _cancelTimer;

    public ProgressDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
        // The window button cancels like the cancel button: the dialog closes with the result true. Before the cancel
        // delay elapsed, it does nothing, as the hidden cancel button.
        Closing += (_, args) =>
        {
            if (args.CloseReason == WindowCloseReason.WindowClosing && !args.IsProgrammatic)
            {
                args.Cancel = true;
                if (DataContext is ProgressDialogViewModel { CanCancel: true })
                {
                    Dispatcher.UIThread.Post(() => Close(true));
                }
            }
        };
        // The indeterminate animation takes its travel distance from the bar width when it starts; started before
        // the dialog is laid out, it runs over a width of zero and the bar stands still.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<ProgressBar>("Progress")!.IsIndeterminate = true,
            DispatcherPriority.Loaded);
        // The cancel delay counts from the moment the dialog shows.
        Opened += (_, _) =>
        {
            if (DataContext is ProgressDialogViewModel { CanCancel: false } vm)
            {
                _cancelTimer = DispatcherTimer.RunOnce(vm.AllowCancel, vm.CancelDelay);
            }
        };
        Closed += (_, _) => _cancelTimer?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is ProgressDialogViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(ctx =>
            {
                Close(ctx.Input);
                ctx.SetOutput(RxVoid.Default);
            });
        }
    }
}
