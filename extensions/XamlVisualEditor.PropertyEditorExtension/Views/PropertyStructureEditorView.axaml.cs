using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace XamlVisualEditor.PropertyEditorExtension.Views;

public sealed partial class PropertyStructureEditorView : UserControl
{
    public PropertyStructureEditorView() => AvaloniaXamlLoader.Load(this);
}
