using Avalonia;
using Avalonia.Controls;

namespace XamlVisualEditor.Designer.Rendering;

/// <summary>Associates a rendered control with its source AST node without changing its name or tag.</summary>
public sealed class DesignNodeIdentity : AvaloniaObject
{
    public static readonly AttachedProperty<string?> NodeIdProperty =
        AvaloniaProperty.RegisterAttached<DesignNodeIdentity, Control, string?>("NodeId");

    public static string? GetNodeId(Control control) => control.GetValue(NodeIdProperty);

    public static void SetNodeId(Control control, string? value) => control.SetValue(NodeIdProperty, value);
}
