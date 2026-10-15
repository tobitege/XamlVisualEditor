using System.Collections;
using System.Globalization;
using System.Text;
using XamlVisualEditor.Core.Localization;
using XamlVisualEditor.Shell.ViewModels;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TerminalOptionsTests
{
    [Theory]
    [InlineData(TerminalShell.CommandPrompt, "custom-cmd.exe")]
    [InlineData(TerminalShell.WindowsPowerShell, "custom-powershell.exe")]
    [InlineData(TerminalShell.PowerShell7, "custom-pwsh.exe")]
    public void ShellChoiceUsesItsConfiguredExecutable(TerminalShell shell, string expected)
    {
        EditorOptions options = new()
        {
            TerminalShell = shell,
            CommandPromptPath = "custom-cmd.exe",
            WindowsPowerShellPath = "custom-powershell.exe",
            PowerShell7Path = "custom-pwsh.exe"
        };
        Assert.Equal(expected, options.ShellPath);
    }

    [Fact]
    public void TerminalCaptionDropsExecutableDirectoriesAndKeepsCustomNames()
    {
        ITerminalSession session = new TerminalService(new WindowsPtyProvider(), new ManagedTerminalEmulatorFactory())
            .CreateSession(new TerminalSessionOptions());
        using TerminalViewModel vm = new(session, @"C:\Apps\PowerShell\7\pwsh.exe");
        Assert.Equal("pwsh.exe", vm.Title);
        session.Emulator.ProcessInput(Encoding.UTF8.GetBytes("\u001b]0;C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\u0007"));
        Assert.Equal("powershell.exe", vm.Title);
        session.Emulator.ProcessInput(Encoding.UTF8.GetBytes("\u001b]0;Build shell\u0007"));
        Assert.Equal("Build shell", vm.Title);
    }

    [Fact]
    public void GermanAndEnglishResourceCatalogsHaveTheSameKeys()
    {
        var english = UiStrings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!;
        var german = UiStrings.ResourceManager.GetResourceSet(CultureInfo.GetCultureInfo("de"), true, false)!;
        Assert.NotNull(english);
        Assert.NotNull(german);
        string[] enKeys = english.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).Order().ToArray();
        string[] deKeys = german.Cast<DictionaryEntry>().Select(entry => (string)entry.Key).Order().ToArray();
        Assert.Equal(enKeys, deKeys);
        foreach (string key in enKeys)
        {
            string enValue = english.GetString(key)!;
            string deValue = german.GetString(key)!;
            Assert.False(string.IsNullOrWhiteSpace(enValue), key);
            Assert.False(string.IsNullOrWhiteSpace(deValue), key);
            if (System.Text.RegularExpressions.Regex.IsMatch(enValue, @"(?<!\{)\{\d"))
            {
                Assert.Equal(CompositeFormat.Parse(enValue).MinimumArgumentCount,
                    CompositeFormat.Parse(deValue).MinimumArgumentCount);
            }
        }
        Assert.Equal("Options", english.GetString("OptionsTitle"));
        Assert.Equal("Optionen", german.GetString("OptionsTitle"));
    }
}
