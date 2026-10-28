using System;
using System.IO;
using System.Text.Json;
using XamlVisualEditor.Core.Lsp;
using XamlVisualEditor.Lsp;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class LspUriSerializationTests
{
    [Fact]
    public void A_Document_Uri_Made_From_A_Path_Is_Sent_As_A_File_Uri()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve uri", "View.axaml");
        LspTextDocumentIdentifier document = new() { Uri = new Uri(path) };

        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(document, LspMessageFraming.SerializerOptions));
        string? uri = json.RootElement.GetProperty("uri").GetString();

        Assert.Equal(new Uri(path).AbsoluteUri, uri);
        Assert.StartsWith("file://", uri, StringComparison.Ordinal);
        Assert.Equal(path, JsonSerializer.Deserialize<LspTextDocumentIdentifier>(json.RootElement.GetRawText(),
            LspMessageFraming.SerializerOptions)!.Uri.LocalPath);
    }

    [Fact]
    public void A_Relative_Uri_Keeps_Its_Text_And_An_Invalid_Uri_Is_A_Json_Error()
    {
        LspTextDocumentIdentifier relative = new() { Uri = new Uri("views/View.axaml", UriKind.Relative) };

        string json = JsonSerializer.Serialize(relative, LspMessageFraming.SerializerOptions);

        Assert.Equal("{\"uri\":\"views/View.axaml\"}", json);
        Assert.Equal(relative.Uri, JsonSerializer.Deserialize<LspTextDocumentIdentifier>(json, LspMessageFraming.SerializerOptions)!.Uri);
        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<LspTextDocumentIdentifier>("{\"uri\":\"file://[\"}", LspMessageFraming.SerializerOptions));
    }
}
