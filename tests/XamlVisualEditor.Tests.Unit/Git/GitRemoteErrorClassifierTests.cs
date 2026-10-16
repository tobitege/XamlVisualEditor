using XamlVisualEditor.Core;
using Xunit;

namespace XamlVisualEditor.Tests.Unit.Git;

public sealed class GitRemoteErrorClassifierTests
{
    [Theory]
    [InlineData("remote: Invalid credentials\nfatal: Authentication failed for 'https://example.com/r.git/'")]
    [InlineData("fatal: could not read Username for 'https://example.com': terminal prompts disabled")]
    [InlineData("fatal: could not read Password for 'https://user@example.com': terminal prompts disabled")]
    [InlineData("git@example.com: Permission denied (publickey).\r\nfatal: Could not read from remote repository.\r\n\r\nPlease make sure you have the correct access rights")]
    [InlineData("error: terminal prompts disabled")]
    public void Authentication_failures_are_recognized(string error)
    {
        Assert.Equal(GitRemoteFailureKind.Authentication, GitRemoteErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("To ../remote.git\n ! [rejected]        main -> main (non-fast-forward)\nerror: failed to push some refs to '../remote.git'\nhint: Updates were rejected because the tip of your current branch is behind")]
    [InlineData("To ../remote.git\n ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to '../remote.git'")]
    public void Pushes_behind_the_remote_are_rejected(string error)
    {
        Assert.Equal(GitRemoteFailureKind.Rejected, GitRemoteErrorClassifier.Classify(error));
    }

    // The first text is the output of git 2.55 for a pre-receive hook that exits with 1.
    [Theory]
    [InlineData("remote: GH006: Protected branch update failed        \nTo ../remote.git\n ! [remote rejected] main -> main (pre-receive hook declined)\nerror: failed to push some refs to '../remote.git'")]
    [InlineData(" ! [remote rejected] main -> main (protected branch hook declined)")]
    public void Pushes_declined_by_the_remote_are_not_taken_for_pushes_behind_it(string error)
    {
        Assert.Equal(GitRemoteFailureKind.RemoteRejected, GitRemoteErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("fatal: unable to access 'https://example.invalid/r.git/': Could not resolve host: example.invalid", GitRemoteFailureKind.Network)]
    [InlineData("ssh: connect to host 127.0.0.1 port 1: Connection refused\r\nfatal: Could not read from remote repository.", GitRemoteFailureKind.Network)]
    [InlineData("ssh: Could not resolve hostname nonexistent.invalid: No such host is known.\nfatal: Could not read from remote repository.", GitRemoteFailureKind.Network)]
    [InlineData("fatal: unable to access 'https://example.com/r.git/': Failed to connect to example.com port 443 after 21 ms: Couldn't connect to server", GitRemoteFailureKind.Network)]
    [InlineData("remote: Permission to owner/r.git denied to user.\nfatal: unable to access 'https://example.com/owner/r.git/': The requested URL returned error: 403", GitRemoteFailureKind.Forbidden)]
    [InlineData("fatal: unable to access 'https://example.com/r.git/': The requested URL returned error: 401", GitRemoteFailureKind.Authentication)]
    public void Network_failures_and_missing_rights_are_told_apart_from_sign_in_failures(string error, GitRemoteFailureKind expected)
    {
        Assert.Equal(expected, GitRemoteErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("Host key verification failed.\nfatal: Could not read from remote repository.", GitRemoteFailureKind.HostKeyVerification)]
    [InlineData("fatal: no upstream configured for branch 'topic'", GitRemoteFailureKind.NoUpstream)]
    [InlineData("hint: Diverging branches can't be fast-forwarded, you need to either:\nfatal: Not possible to fast-forward, aborting.", GitRemoteFailureKind.Diverged)]
    [InlineData("fatal: Not possible to fast-forward, aborting.", GitRemoteFailureKind.Diverged)]
    [InlineData("There is no tracking information for the current branch.\nPlease specify which branch you want to merge with.", GitRemoteFailureKind.NoUpstream)]
    [InlineData("fatal: The current branch main has no upstream branch.", GitRemoteFailureKind.NoUpstream)]
    [InlineData("merge: @{upstream} - not something we can merge", GitRemoteFailureKind.UpstreamGone)]
    [InlineData("Your configuration specifies to merge with the ref 'refs/heads/feature'\nfrom the remote, but no such ref was fetched.", GitRemoteFailureKind.UpstreamGone)]
    [InlineData("fatal: '../missing.git' does not appear to be a git repository", GitRemoteFailureKind.Other)]
    [InlineData("", GitRemoteFailureKind.Other)]
    [InlineData(null, GitRemoteFailureKind.Other)]
    public void Other_failures_get_their_kind(string? error, GitRemoteFailureKind expected)
    {
        Assert.Equal(expected, GitRemoteErrorClassifier.Classify(error));
    }

    [Theory]
    [InlineData("To ../remote.git\n ! [rejected]        main -> main (fetch first)\nerror: failed to push some refs to '../remote.git'\nhint: Updates were rejected", "! [rejected]        main -> main (fetch first)")]
    [InlineData("hint: Diverging branches can't be fast-forwarded, you need to either:\nhint:\nfatal: Not possible to fast-forward, aborting.", "fatal: Not possible to fast-forward, aborting.")]
    [InlineData("warning: x\r\nerror: pathspec 'a' did not match any file(s) known to git\r\nhint: y", "error: pathspec 'a' did not match any file(s) known to git")]
    [InlineData("broken: fatal: '../missing.git' does not appear to be a git repository\nfatal: Could not read from remote repository.", "broken: fatal: '../missing.git' does not appear to be a git repository")]
    [InlineData("Everything up-to-date", "Everything up-to-date")]
    [InlineData("hint: only advice", "")]
    [InlineData(null, "")]
    public void The_decisive_line_is_the_refused_reference_then_fatal_then_error_without_hints(string? output, string expected)
    {
        Assert.Equal(expected, GitRemoteErrorClassifier.SelectDecisiveLine(output));
    }

    [Fact]
    public void The_indented_lines_after_the_decisive_line_belong_to_it()
    {
        string output = "Updating 1a2b3c4..5d6e7f8\n"
            + "error: Your local changes to the following files would be overwritten by merge:\r\n"
            + "\ta.txt\r\n"
            + "  dir/b.txt\r\n"
            + "Please commit your changes or stash them before you merge.\n"
            + "Aborting\n";

        Assert.Equal(
            "error: Your local changes to the following files would be overwritten by merge:"
                + Environment.NewLine + "    a.txt"
                + Environment.NewLine + "    dir/b.txt",
            GitRemoteErrorClassifier.SelectDecisiveLine(output));
    }

    [Fact]
    public void At_most_ten_indented_lines_follow_and_then_an_ellipsis()
    {
        string output = "error: The following untracked working tree files would be overwritten by merge:\n"
            + string.Concat(Enumerable.Range(1, 12).Select(number => "\tfile" + number + ".txt\n"))
            + "Aborting\n";

        string[] lines = GitRemoteErrorClassifier.SelectDecisiveLine(output).Split(Environment.NewLine);

        Assert.Equal(12, lines.Length);
        Assert.Equal("    file10.txt", lines[^2]);
        Assert.Equal("    " + (char)0x2026, lines[^1]);
    }

    [Fact]
    public void A_refused_reference_does_not_take_the_next_refused_reference_of_the_same_indentation()
    {
        string output = "To ../remote.git\n"
            + " ! [rejected]        main -> main (fetch first)\n"
            + " ! [rejected]        dev -> dev (fetch first)\n"
            + "error: failed to push some refs to '../remote.git'\n";

        Assert.Equal("! [rejected]        main -> main (fetch first)", GitRemoteErrorClassifier.SelectDecisiveLine(output));
    }

    [Fact]
    public void Authentication_wins_over_the_general_read_failure()
    {
        const string error = "git@example.com: Permission denied (publickey).\nfatal: Could not read from remote repository.\n ! [rejected]";

        Assert.Equal(GitRemoteFailureKind.Authentication, GitRemoteErrorClassifier.Classify(error));
    }
}
