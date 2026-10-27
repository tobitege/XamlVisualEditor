using System;
using System.Collections.Generic;
using XamlVisualEditor.Terminal;
using Xunit;

namespace XamlVisualEditor.Tests.Unit;

public sealed class TerminalEnvironmentTests
{
    [Fact]
    public void ComposeAppliesDefaultsThenOverridesAndRemovesEmptyValues()
    {
        Dictionary<string, string> environment = TerminalEnvironment.Compose(
            new Dictionary<string, string> { ["A"] = "1", ["B"] = "2" },
            defaults => defaults["TERM"] = "xterm-256color",
            new Dictionary<string, string> { ["B"] = string.Empty, ["C"] = "3", ["TERM"] = string.Empty });

        Assert.Equal(new Dictionary<string, string> { ["A"] = "1", ["C"] = "3" }, environment);
    }

    [Fact]
    public void CaptureStartupKeepsTheEnvironmentOfTheFirstCapture()
    {
        string added = "XVE_ENV_TEST_LATER_" + Guid.NewGuid().ToString("N");
        TerminalEnvironment.CaptureStartup();
        Environment.SetEnvironmentVariable(added, "later");
        try
        {
            TerminalEnvironment.CaptureStartup();

            Assert.False(TerminalEnvironment.Base.ContainsKey(added));
        }
        finally
        {
            Environment.SetEnvironmentVariable(added, null);
        }
    }

    [Fact]
    public void UnixEntriesRemoveHostVariablesAndLeaveMultiplexerVariablesToPortaPty()
    {
        Dictionary<string, string> startup = new() { ["PATH"] = "/bin", ["TMUX"] = "/tmp/tmux", ["KEEP"] = "1" };
        Dictionary<string, string> current = new(startup) { ["MSBUILD_EXE_PATH"] = "/sdk/MSBuild.dll" };

        Dictionary<string, string> entries = UnixPtyProvider.CreateEnvironment(
            startup, current, new Dictionary<string, string> { ["ADDED"] = "added", ["KEEP"] = string.Empty });

        Assert.Equal(new Dictionary<string, string>
        {
            ["MSBUILD_EXE_PATH"] = string.Empty,
            ["TERM"] = "xterm-256color",
            ["COLORTERM"] = "truecolor",
            ["ADDED"] = "added",
            ["KEEP"] = string.Empty
        }, entries);
    }

    [Fact]
    public void UnixEntriesKeepTheTerminalTypeOfTheCallerWhenItMatchesTheCurrentOne()
    {
        Dictionary<string, string> current = new() { ["TERM"] = "vt100" };

        Dictionary<string, string> entries = UnixPtyProvider.CreateEnvironment(
            current, current, new Dictionary<string, string> { ["TERM"] = "vt100" });

        Assert.Equal("vt100", entries["TERM"]);
    }

    [Fact]
    public void WindowsEnvironmentAddsMissingTerminalVariablesBeforeTheOverrides()
    {
        Dictionary<string, string> withTerm = WindowsNative.CreateEnvironment(
            new Dictionary<string, string> { ["Path"] = "C:\\Windows", ["TERM"] = "xterm" },
            new Dictionary<string, string> { ["COLORTERM"] = string.Empty });
        Dictionary<string, string> withoutTerm = WindowsNative.CreateEnvironment(
            new Dictionary<string, string> { ["Path"] = "C:\\Windows" },
            new Dictionary<string, string>());

        Assert.Equal(new Dictionary<string, string> { ["Path"] = "C:\\Windows", ["TERM"] = "xterm" }, withTerm);
        Assert.Equal("xterm-256color", withoutTerm["TERM"]);
        Assert.Equal("truecolor", withoutTerm["COLORTERM"]);
    }
}
