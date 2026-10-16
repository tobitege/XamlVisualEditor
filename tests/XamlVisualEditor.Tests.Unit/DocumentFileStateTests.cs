using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class DocumentFileStateTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XamlVisualEditor-DocumentFileState-" + Guid.NewGuid().ToString("N"));

    public DocumentFileStateTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public async Task Reading_a_file_that_is_not_utf8_fails_instead_of_replacing_characters()
    {
        string path = Path.Combine(_root, "Legacy.cs");
        // "Größe" in Windows-1252: the umlaut bytes are invalid UTF-8.
        await File.WriteAllBytesAsync(path, new byte[] { 0x47, 0x72, 0xF6, 0xDF, 0x65 }, TestContext.Current.CancellationToken);
        DocumentFileState state = new();

        await Assert.ThrowsAsync<DecoderFallbackException>(() => state.ReadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Reading_invalid_bytes_behind_a_utf8_byte_order_mark_fails_as_well()
    {
        string path = Path.Combine(_root, "Marked.cs");
        await File.WriteAllBytesAsync(path, new byte[] { 0xEF, 0xBB, 0xBF, 0x47, 0x72, 0xF6, 0xDF, 0x65 },
            TestContext.Current.CancellationToken);
        DocumentFileState state = new();

        await Assert.ThrowsAsync<DecoderFallbackException>(() => state.ReadAsync(path, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saving_keeps_the_byte_order_mark_of_the_file(bool withBom)
    {
        string path = Path.Combine(_root, "View.axaml");
        UTF8Encoding encoding = new(withBom);
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("<UserControl />")];
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        DocumentFileState state = new();

        string text = await state.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("<UserControl />", text);
        Assert.False(state.IsModified(text));
        Assert.True(state.IsModified("<UserControl Width=\"10\" />"));
        await state.SaveAsync(path, "<UserControl Width=\"10\" />");

        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("<UserControl Width=\"10\" />")];
        byte[] saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(expected, saved);
        Assert.False(state.IsModified("<UserControl Width=\"10\" />"));
    }

    [Fact]
    public async Task Saving_keeps_a_utf16_encoding()
    {
        string path = Path.Combine(_root, "Wide.xaml");
        UnicodeEncoding encoding = new(bigEndian: false, byteOrderMark: true);
        byte[] original = [.. encoding.GetPreamble(), .. encoding.GetBytes("<Grid />")];
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        DocumentFileState state = new();

        Assert.Equal("<Grid />", await state.ReadAsync(path, TestContext.Current.CancellationToken));
        await state.SaveAsync(path, "<Grid Margin=\"1\" />");

        byte[] expected = [.. encoding.GetPreamble(), .. encoding.GetBytes("<Grid Margin=\"1\" />")];
        byte[] saved = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(expected, saved);
    }

    [Fact]
    public async Task Saving_over_a_file_changed_outside_the_editor_fails_and_keeps_the_outside_change()
    {
        string path = Path.Combine(_root, "Shared.cs");
        await File.WriteAllTextAsync(path, "class First { }", TestContext.Current.CancellationToken);
        DocumentFileState state = new();
        await state.ReadAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "class Outside { }", TestContext.Current.CancellationToken);

        IOException error = await Assert.ThrowsAsync<FileChangedOutsideEditorException>(() => state.SaveAsync(path, "class Editor { }"));

        Assert.Equal(string.Format(UiStrings.FileChangedOutsideEditor, "Shared.cs"), error.Message);
        Assert.Equal("class Outside { }", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Overwriting_a_file_changed_outside_the_editor_writes_and_takes_the_written_bytes_as_baseline()
    {
        string path = Path.Combine(_root, "Overwrite.cs");
        await File.WriteAllTextAsync(path, "class First { }", TestContext.Current.CancellationToken);
        DocumentFileState state = new();
        await state.ReadAsync(path, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(path, "class Outside, longer than the first { }", TestContext.Current.CancellationToken);

        await state.SaveAsync(path, "class Editor { }", overwrite: true);

        Assert.Equal("class Editor { }", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.False(state.IsModified("class Editor { }"));
        await state.SaveAsync(path, "class Next { }");
        Assert.Equal("class Next { }", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Saving_a_new_document_does_not_overwrite_an_existing_file()
    {
        string path = Path.Combine(_root, "Existing.cs");
        await File.WriteAllTextAsync(path, "class Existing { }", TestContext.Current.CancellationToken);
        DocumentFileState state = new();

        await Assert.ThrowsAsync<IOException>(() => state.SaveAsync(path, "class New { }"));

        Assert.Equal("class Existing { }", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
