using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Applies preview-only settings without changing the target application's resources.</summary>
public sealed class DesignPreviewSettings : AvaloniaObject
{
    /// <summary>Serialized host-only provider settings; local rendering does not execute providers.</summary>
    public static readonly AttachedProperty<string?> ThemeRequestProperty =
        AvaloniaProperty.RegisterAttached<DesignPreviewSettings, Control, string?>("ThemeRequest");

    public static string? GetThemeRequest(Control control) => control.GetValue(ThemeRequestProperty);

    public static void SetThemeRequest(Control control, string? value) => control.SetValue(ThemeRequestProperty, value);

    public static readonly AttachedProperty<ThemeVariant?> ThemeVariantProperty =
        AvaloniaProperty.RegisterAttached<DesignPreviewSettings, Control, ThemeVariant?>("ThemeVariant");

    static DesignPreviewSettings()
    {
        ThemeVariantProperty.Changed.AddClassHandler<Control>((control, _) =>
            Dispatcher.UIThread.Post(() =>
            {
                // The remote loader attaches windowless roots after completing their XAML load.
                if (GetThemeVariant(control) is { } theme && theme != ThemeVariant.Default
                    && TopLevel.GetTopLevel(control) is { } window)
                    window.RequestedThemeVariant = theme;
            }));
    }

    public static ThemeVariant? GetThemeVariant(Control control) => control.GetValue(ThemeVariantProperty);

    public static void SetThemeVariant(Control control, ThemeVariant? value) => control.SetValue(ThemeVariantProperty, value);
}
