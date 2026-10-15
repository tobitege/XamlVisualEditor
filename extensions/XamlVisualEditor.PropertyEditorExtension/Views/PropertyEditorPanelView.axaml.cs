using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using RxVoid = ReactiveUI.Primitives.RxVoid;

namespace XamlVisualEditor.PropertyEditorExtension.Views;

public sealed partial class PropertyEditorPanelView : UserControl
{
    public PropertyEditorPanelView()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
