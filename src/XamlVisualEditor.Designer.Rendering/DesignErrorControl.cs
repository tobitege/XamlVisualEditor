using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Preserves a failed control's declared layout while its diagnostics appear in Problems.</summary>
public sealed class DesignErrorControl : Border
{
    public static readonly StyledProperty<string> OriginalTypeProperty =
        AvaloniaProperty.Register<DesignErrorControl, string>(nameof(OriginalType), string.Empty);
    public static readonly StyledProperty<string> ErrorProperty =
        AvaloniaProperty.Register<DesignErrorControl, string>(nameof(Error), string.Empty);

    public DesignErrorControl()
    {
        Background = Brushes.Transparent;
    }

    public string OriginalType { get => GetValue(OriginalTypeProperty); set => SetValue(OriginalTypeProperty, value); }
    public string Error { get => GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
}
