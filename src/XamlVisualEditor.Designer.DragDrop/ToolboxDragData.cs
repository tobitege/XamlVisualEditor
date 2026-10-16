using System.Text.Json;

namespace XamlVisualEditor.Designer.DragDrop;

/// <summary>Preserves the complete XAML type identity during a toolbox drag.</summary>
public sealed record ToolboxDragData(string TypeName, string XmlNamespace)
{
    /// <summary>Encodes a drag payload.</summary>
    public string Serialize() => JsonSerializer.Serialize(this);

    /// <summary>Reads a complete payload; incomplete or malformed identities are not accepted.</summary>
    public static bool TryParse(string? value, out ToolboxDragData? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            result = JsonSerializer.Deserialize<ToolboxDragData>(value);
            return result is not null && !string.IsNullOrWhiteSpace(result.TypeName)
                && !string.IsNullOrWhiteSpace(result.XmlNamespace);
        }
        catch (JsonException) { return false; }
    }
}

/// <summary>Provides a toolbox entry's identity without depending on a particular panel viewmodel.</summary>
public interface IToolboxDragItem
{
    /// <summary>Gets whether the entry can be dragged as a control.</summary>
    bool CanDrag { get; }
    /// <summary>Gets the XAML type name.</summary>
    string TypeName { get; }
    /// <summary>Gets the complete XML namespace.</summary>
    string XmlNamespace { get; }
}
