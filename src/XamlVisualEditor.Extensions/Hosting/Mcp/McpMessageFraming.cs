using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace XamlVisualEditor.Extensions.Hosting.Mcp;

/// <summary>Liest und schreibt die zeilenweise UTF-8-Kodierung des MCP-stdio-Transports.</summary>
public static class McpMessageFraming
{
    /// <summary>Serializer options for JSON-RPC payloads.</summary>
    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Writes a framed JSON-RPC message.</summary>
    public static async Task WriteMessageAsync(Stream output, object payload, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);
        await output.WriteAsync(body, ct).ConfigureAwait(false);
        await output.WriteAsync(new byte[] { (byte)'\n' }, ct).ConfigureAwait(false);
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads a framed JSON-RPC message.</summary>
    public static async Task<JsonDocument> ReadMessageAsync(StreamReader input, CancellationToken ct)
    {
        string? line;
        while ((line = await input.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line)) return JsonDocument.Parse(line);
        }
        throw new EndOfStreamException("MCP input closed.");
    }
}
