using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Xaml.LanguageServer;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class XamlLanguageServerRenameTests
{
    // Built from a path of the running system, so the server's way back from the path gives the same URI.
    private static readonly string DocumentUri = new Uri(Path.Combine(Path.GetTempPath(), "views", "View.axaml")).AbsoluteUri;

    private const string Xaml = """
        <UserControl xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
          <StackPanel>
            <TextBlock x:Name="Title" />
            <TextBlock x:Name="Other" />
          </StackPanel>
        </UserControl>
        """;

    [Fact]
    public async Task A_Refused_Rename_Answers_With_RequestFailed_And_Its_Reason()
    {
        int line = Xaml.Split('\n').TakeWhile(text => !text.Contains("\"Title\"", StringComparison.Ordinal)).Count();
        MemoryStream input = new();
        Write(input, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        Write(input, new { jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri = DocumentUri, languageId = "xml", version = 1, text = Xaml } } });
        Write(input, new { jsonrpc = "2.0", id = 2, method = "textDocument/rename",
            @params = new { textDocument = new { uri = DocumentUri }, position = new { line, character = 26 }, newName = "Other" } });
        Write(input, new { jsonrpc = "2.0", id = 3, method = "shutdown" });
        Write(input, new { jsonrpc = "2.0", method = "exit" });
        input.Position = 0;
        MemoryStream output = new();

        await new XamlLanguageServer().RunAsync(input, output, TestContext.Current.CancellationToken);

        JsonElement rename = Read(output).Single(message => message.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 2);
        Assert.False(rename.TryGetProperty("result", out _));
        JsonElement error = rename.GetProperty("error");
        Assert.Equal(-32803, error.GetProperty("code").GetInt32());
        Assert.Equal(string.Format(UiStrings.DesignerNameInUse, "Other"), error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_Rename_Answers_With_The_File_Uri_Of_The_Document()
    {
        int line = Xaml.Split('\n').TakeWhile(text => !text.Contains("\"Title\"", StringComparison.Ordinal)).Count();
        MemoryStream input = new();
        Write(input, new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { capabilities = new { } } });
        Write(input, new { jsonrpc = "2.0", method = "textDocument/didOpen",
            @params = new { textDocument = new { uri = DocumentUri, languageId = "xml", version = 1, text = Xaml } } });
        Write(input, new { jsonrpc = "2.0", id = 2, method = "textDocument/rename",
            @params = new { textDocument = new { uri = DocumentUri }, position = new { line, character = 26 }, newName = "Header" } });
        Write(input, new { jsonrpc = "2.0", id = 3, method = "shutdown" });
        Write(input, new { jsonrpc = "2.0", method = "exit" });
        input.Position = 0;
        MemoryStream output = new();

        await new XamlLanguageServer().RunAsync(input, output, TestContext.Current.CancellationToken);

        JsonElement rename = Read(output).Single(message => message.TryGetProperty("id", out JsonElement id) && id.GetInt32() == 2);
        JsonElement change = Assert.Single(rename.GetProperty("result").GetProperty("documentChanges").EnumerateArray());
        Assert.Equal(DocumentUri, change.GetProperty("textDocument").GetProperty("uri").GetString());
    }

    private static void Write(Stream stream, object message)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        stream.Write(header);
        stream.Write(body);
    }

    private static IEnumerable<JsonElement> Read(MemoryStream stream)
    {
        string text = Encoding.UTF8.GetString(stream.ToArray());
        List<JsonElement> messages = new();
        for (int start = text.IndexOf("\r\n\r\n", StringComparison.Ordinal); start >= 0;
             start = text.IndexOf("\r\n\r\n", start, StringComparison.Ordinal))
        {
            int next = text.IndexOf("Content-Length:", start, StringComparison.Ordinal);
            string body = next < 0 ? text[(start + 4)..] : text[(start + 4)..next];
            messages.Add(JsonDocument.Parse(body).RootElement.Clone());
            start = next < 0 ? text.Length : next;
        }

        return messages;
    }
}
