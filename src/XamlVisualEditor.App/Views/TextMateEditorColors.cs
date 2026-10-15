using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.TextMate;

namespace XamlVisualEditor.App.Views;

/// <summary>Takes the editor colors from the installed TextMate theme, so they match its syntax colors.</summary>
internal static class TextMateEditorColors
{
    // The bundled TextMate themes define no link color; these are the Visual Studio Code defaults for textLink.foreground.
    private static readonly Color DarkLinkColor = Color.Parse("#3794FF");
    private static readonly Color LightLinkColor = Color.Parse("#006AB1");

    public static void Apply(TextEditor editor, TextMate.Installation installation)
    {
        if (installation.TryGetThemeColor("editor.background", out string? background) &&
            Color.TryParse(background, out Color backgroundColor))
        {
            editor.Background = new SolidColorBrush(backgroundColor);
            bool isDark = 0.2126 * backgroundColor.R + 0.7152 * backgroundColor.G + 0.0722 * backgroundColor.B < 128;
            editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(isDark ? DarkLinkColor : LightLinkColor);
        }

        if (installation.TryGetThemeColor("editor.foreground", out string? foreground) &&
            Color.TryParse(foreground, out Color foregroundColor))
        {
            editor.Foreground = new SolidColorBrush(foregroundColor);
        }
    }
}
