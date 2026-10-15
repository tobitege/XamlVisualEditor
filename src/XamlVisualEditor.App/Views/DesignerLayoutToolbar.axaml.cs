using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace XamlVisualEditor.App.Views;

public partial class DesignerLayoutToolbar : UserControl
{
    public DesignerLayoutToolbar() => AvaloniaXamlLoader.Load(this);
}
