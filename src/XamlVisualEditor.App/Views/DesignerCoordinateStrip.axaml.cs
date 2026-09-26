using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Shows pixel positions of the pointer, the element under it, the selection and a measured distance
/// below a design surface.
/// </summary>
public partial class DesignerCoordinateStrip : UserControl
{
    public DesignerCoordinateStrip() => AvaloniaXamlLoader.Load(this);
}
