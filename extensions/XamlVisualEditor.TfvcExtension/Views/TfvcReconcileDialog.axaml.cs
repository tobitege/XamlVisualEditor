using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Reactive;
using Avalonia.Controls;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>Dialog that lists local changes made outside version control.</summary>
public sealed partial class TfvcReconcileDialog : Window
{
    private IDisposable? _closeHandler;

    /// <summary>Creates the dialog.</summary>
    public TfvcReconcileDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is TfvcReconcileDialogViewModel viewModel)
        {
            _closeHandler = viewModel.CloseInteraction.RegisterHandler(context =>
            {
                Close(context.Input);
                context.SetOutput(RxVoid.Default);
            });
        }
    }
}
