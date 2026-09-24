using System.Reactive;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using XamlVisualEditor.TextEditingExtension.ViewModels;

namespace XamlVisualEditor.TextEditingExtension.Views;

public sealed partial class TextEditorOptionsDialog : Window
{
    private IDisposable? _closeHandler;

    public TextEditorOptionsDialog()
    {
        AvaloniaXamlLoader.Load(this);
        DataContextChanged += (_, _) => RegisterCloseHandler();
        Closed += (_, _) => _closeHandler?.Dispose();
    }

    private void RegisterCloseHandler()
    {
        _closeHandler?.Dispose();
        if (DataContext is TextEditorOptionsViewModel vm)
        {
            _closeHandler = vm.CloseInteraction.RegisterHandler(context =>
            {
                Close(context.Input);
                context.SetOutput(Unit.Default);
            });
        }
    }
}
