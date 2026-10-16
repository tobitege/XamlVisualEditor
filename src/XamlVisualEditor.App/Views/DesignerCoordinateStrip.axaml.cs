using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace XamlVisualEditor.App.Views;

/// <summary>
/// Shows pixel positions of the pointer, the element under it, the selection and a measured distance
/// below a design surface, together with the state of its preview.
/// </summary>
public partial class DesignerCoordinateStrip : UserControl
{
    public static readonly StyledProperty<string?> StatusTextProperty =
        AvaloniaProperty.Register<DesignerCoordinateStrip, string?>(nameof(StatusText));

    public DesignerCoordinateStrip() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Gets or sets the preview state shown in the strip; <see langword="null"/> when the preview is current.
    /// </summary>
    public string? StatusText
    {
        get => GetValue(StatusTextProperty);
        set => SetValue(StatusTextProperty, value);
    }
}
