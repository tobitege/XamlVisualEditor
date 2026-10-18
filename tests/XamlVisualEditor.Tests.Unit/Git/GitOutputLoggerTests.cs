using System;
using XamlVisualEditor.Core;
using XamlVisualEditor.Extensions.Hosting;
using XamlVisualEditor.GitExtension.Services;
using XamlVisualEditor.Tests.Unit.Tfvc;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitOutputLoggerTests
{
    [Theory]
    [InlineData("https://user:secret@example.com/repo.git", "https://***@example.com/repo.git")]
    [InlineData("fatal: unable to access 'http://token@host/x/': 403", "fatal: unable to access 'http://***@host/x/': 403")]
    [InlineData("ssh://git@host:22/repo", "ssh://***@host:22/repo")]
    [InlineData("https://example.com/repo.git", "https://example.com/repo.git")]
    [InlineData("git@github.com:owner/repo.git", "git@github.com:owner/repo.git")]
    [InlineData("https://example.com/a@b/repo.git", "https://example.com/a@b/repo.git")]
    [InlineData("https://user:p@ss@example.com/repo.git", "https://***@example.com/repo.git")]
    [InlineData("see https://a:b@x.org/r and https://y.org/s?u=v@w", "see https://***@x.org/r and https://y.org/s?u=v@w")]
    [InlineData("../remote.git", "../remote.git")]
    public void Mask_replaces_the_user_information_of_urls(string text, string expected)
    {
        Assert.Equal(expected, GitUrlCredentials.Mask(text));
    }

    [Fact]
    public void LogCommand_writes_the_masked_command_line_duration_exit_code_and_error_output()
    {
        InMemoryOutputChannel channel = Assert.IsType<InMemoryOutputChannel>(new InMemoryWindow().CreateOutputChannel("Git"));
        RecordingExtensionLogger host = new();
        GitOutputLogger logger = new(host, channel);

        logger.LogCommand(new GitCommandLogEntry(
            "repo",
            ["push", "https://user:secret@example.com/r.git", "a b.txt"],
            128,
            TimeSpan.FromMilliseconds(42),
            "fatal: Authentication failed for 'https://user:secret@example.com/r.git'"));

        Assert.Equal(2, channel.Lines.Count);
        // The Output panel shows the time in its own column, so the lines carry no timestamp.
        Assert.Equal("> git push https://***@example.com/r.git \"a b.txt\" [42 ms] (exit code 128)", channel.Lines[0]);
        Assert.Equal("fatal: Authentication failed for 'https://***@example.com/r.git'", channel.Lines[1]);
        Assert.DoesNotContain("secret", string.Join("\n", channel.Lines));
        Assert.Empty(host.Entries);
    }
}
