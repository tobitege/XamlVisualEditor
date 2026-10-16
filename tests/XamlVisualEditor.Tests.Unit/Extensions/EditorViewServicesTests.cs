using XamlVisualEditor.Extensions;
using XamlVisualEditor.Extensions.Hosting;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Extensions;

public sealed class EditorViewServicesTests
{
    [Fact]
    public void NormalizeClampsValuesAndDropsBlankColors()
    {
        TextEditorViewOptions options = new TextEditorViewOptions
        {
            FontFamily = "  ",
            FontSize = 200,
            ColumnRulerPosition = 5,
            IndentationSize = 0,
            WordWrapIndentation = double.NaN,
            BackgroundColor = " ",
            LineNumbersColor = " #808080 "
        }.Normalize();

        Assert.Equal(new TextEditorViewOptions().FontFamily, options.FontFamily);
        Assert.Equal(TextEditorViewOptions.MAX_FONT_SIZE, options.FontSize);
        Assert.Equal(20, options.ColumnRulerPosition);
        Assert.Equal(1, options.IndentationSize);
        Assert.Equal(0d, options.WordWrapIndentation);
        Assert.Null(options.BackgroundColor);
        Assert.Equal("#808080", options.LineNumbersColor);
    }

    [Fact]
    public void SeveralContextKeysChangeWithOneNotification()
    {
        ContextKeyRegistry keys = new();
        int changes = 0;
        keys.Changed += (_, _) => changes++;

        keys.Set([new("a", true), new("b", true)]);
        keys.Set([new("a", true), new("b", true)]);
        keys.Set("A", false);

        Assert.Equal(2, changes);
        Assert.True(keys.TryGet(" a ", out bool a) && !a);
        Assert.False(keys.TryGet("missing", out _));
    }
}
