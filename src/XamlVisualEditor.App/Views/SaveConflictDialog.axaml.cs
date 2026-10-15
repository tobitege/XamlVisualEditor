using System;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.App.Views;

public sealed partial class SaveConflictDialog : Window
{
    private IDisposable? _closeHandler;

    public SaveConflictDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        // Both other answers replace one of the two versions, so the keyboard focus starts on the harmless one.
        Opened += (_, _) => this.FindControl<Button>("CancelButton")?.Focus();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is SaveConflictDialogViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(ctx =>
            {
                Close(ctx.Input);
                ctx.SetOutput(RxVoid.Default);
            });
        }
    }
}
