using System;
using System.Reactive;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using ReactiveUI;
using XamlVisualEditor.Shell.ViewModels;

namespace XamlVisualEditor.App.Views;

public sealed partial class ExtensionManagerDialog : Window
{
    private IDisposable? _closeHandler;

    public ExtensionManagerDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is ExtensionManagerViewModel viewModel)
        {
            _closeHandler = viewModel.CloseInteraction.RegisterHandler(interaction =>
            {
                Close();
                interaction.SetOutput(Unit.Default);
            });
        }
    }
}
