using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace XamlVisualEditor.TestControls;

public sealed class LibraryTheme : Styles
{
    public LibraryTheme() => AvaloniaXamlLoader.Load(this);
}

public class LibraryInput : TemplatedControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<LibraryInput, string>(nameof(Text), string.Empty);

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }
}

public sealed class DerivedLibraryInput : LibraryInput
{
    protected override System.Type StyleKeyOverride => typeof(LibraryInput);
}
