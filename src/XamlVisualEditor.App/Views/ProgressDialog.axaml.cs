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

    public ProgressDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
        // The indeterminate animation takes its travel distance from the bar width when it starts; started before
        // the dialog is laid out, it runs over a width of zero and the bar stands still.
        Opened += (_, _) => Dispatcher.UIThread.Post(
            () => this.FindControl<ProgressBar>("Progress")!.IsIndeterminate = true,
            DispatcherPriority.Loaded);
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
