using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>A selectable diagnostic, explicitly not a successful rendering of its source control.</summary>
public sealed class DesignErrorControl : Border
{
    public static readonly StyledProperty<string> OriginalTypeProperty =
        AvaloniaProperty.Register<DesignErrorControl, string>(nameof(OriginalType), string.Empty);
    public static readonly StyledProperty<string> ErrorProperty =
        AvaloniaProperty.Register<DesignErrorControl, string>(nameof(Error), string.Empty);

    public DesignErrorControl()
    {
        BorderBrush = Brushes.Firebrick;
        BorderThickness = new Thickness(1);
        Background = Brushes.MistyRose;
        Padding = new Thickness(6);
        MinWidth = 100;
        MinHeight = 40;
        TextBlock heading = new() { Foreground = Brushes.Firebrick, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap };
        heading.Bind(TextBlock.TextProperty, new Binding(nameof(OriginalType)) { Source = this, StringFormat = "Render error: {0}" });
        TextBlock detail = new() { Foreground = Brushes.Black, TextWrapping = TextWrapping.Wrap };
        detail.Bind(TextBlock.TextProperty, new Binding(nameof(Error)) { Source = this });
        Child = new StackPanel { Spacing = 4, Children = { heading, detail } };
    }

    public string OriginalType { get => GetValue(OriginalTypeProperty); set => SetValue(OriginalTypeProperty, value); }
    public string Error { get => GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }
}
