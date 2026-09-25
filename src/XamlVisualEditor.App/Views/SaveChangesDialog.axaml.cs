using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

public sealed partial class SaveChangesDialog : Window
{
    private IDisposable? _closeHandler;

    public SaveChangesDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        // The default button takes the keyboard focus, so Enter saves and Escape cancels without a click first.
        Opened += (_, _) => this.FindControl<Button>("SaveButton")?.Focus();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is SaveChangesDialogViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(ctx =>
            {
                Close(ctx.Input);
                ctx.SetOutput(Unit.Default);
            });
        }
    }
}
