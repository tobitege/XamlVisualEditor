using RxVoid = ReactiveUI.Primitives.RxVoid;
using System;
using System.Reactive;
using Avalonia.Controls;
using XamlVisualEditor.TfvcExtension.ViewModels;

namespace XamlVisualEditor.TfvcExtension.Views;

/// <summary>Dialog that adds unversioned items of a mapped folder to version control.</summary>
public sealed partial class TfvcAddItemsDialog : Window
{
    private IDisposable? _closeHandler;

    /// <summary>Creates the dialog.</summary>
    public TfvcAddItemsDialog()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is TfvcAddItemsDialogViewModel viewModel)
        {
            _closeHandler = viewModel.CloseInteraction.RegisterHandler(context =>
            {
                Close(context.Input);
                context.SetOutput(RxVoid.Default);
            });
        }
    }
}
