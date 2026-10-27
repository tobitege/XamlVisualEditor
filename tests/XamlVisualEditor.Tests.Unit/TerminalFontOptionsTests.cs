using System;
using System.IO;
using System.Reactive.Linq;
using System.Threading.Tasks;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TerminalFontOptionsTests
{
    [Fact]
    public void StoredFontSettingsOutsideTheAllowedValuesFallBackToUsableOnes()
    {
        string path = Path.Combine(Path.GetTempPath(), "xve-terminal-font-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, "{\"TerminalFontFamilies\": \"  \", \"TerminalFontSize\": 500}");
        try
        {
            EditorOptionsStore store = new(path);

            Assert.Null(store.LoadError);
            Assert.Equal(TerminalFont.DefaultFamilies, store.Current.TerminalFontFamilies);
            Assert.Equal(TerminalFont.MaxSize, store.Current.TerminalFontSize);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task OptionsDialogSavesTheTerminalFont()
    {
        EditorOptionsStore store = new();
        using OptionsDialogViewModel dialog = new(store)
        {
            TerminalFontFamilies = "  Fira Code, monospace ",
            TerminalFontSize = 15
        };

        await dialog.SaveCommand.Execute().FirstAsync();

        Assert.Equal("Fira Code, monospace", store.Current.TerminalFontFamilies);
        Assert.Equal(15, store.Current.TerminalFontSize);
    }
}
