using System.Text.Json;
using System.Text.Json.Serialization;

namespace XamlVisualEditor.Core.Lsp;

/// <summary>
/// Writes an absolute URI in its escaped URI form, such as <c>file:///C:/src/View.axaml</c>, as the language server
/// protocol requires; the default converter writes the string the URI was created from, which is a local path for a
/// URI made from a file path.
/// </summary>
public sealed class LspUriJsonConverter : JsonConverter<Uri>
{
    /// <inheritdoc />
    public override Uri Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Uri.TryCreate(reader.GetString(), UriKind.RelativeOrAbsolute, out Uri? uri)
            ? uri
            : throw new JsonException("The value is not a valid URI.");

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, Uri value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.IsAbsoluteUri ? value.AbsoluteUri : value.OriginalString);
}
