using System;
using System.Text;

namespace XamlVisualEditor.Core;

/// <summary>
/// Finds out from git's error output why a fetch, pull or push failed. The texts are those git and ssh write with
/// <c>LC_ALL=C</c>.
/// </summary>
public static class GitRemoteErrorClassifier
{
    private const string CONTINUATION_INDENT = "    ";

    // The horizontal ellipsis, U+2026, that ends a shortened list of indented lines.
    private const char ELLIPSIS = (char)0x2026;

    private static readonly string[] AuthenticationMarkers =
    [
        "Authentication failed",
        "could not read Username",
        "could not read Password",
        "Permission denied (publickey",
        "Permission denied, please try again",
        "terminal prompts disabled",
        "Invalid username or password",
        "HTTP Basic: Access denied",
        "The requested URL returned error: 401"
    ];

    private static readonly string[] ForbiddenMarkers =
    [
        "The requested URL returned error: 403"
    ];

    private static readonly string[] HostKeyMarkers =
    [
        "Host key verification failed",
        "No ED25519 host key is known",
        "No RSA host key is known",
        "No ECDSA host key is known"
    ];

    private static readonly string[] NetworkMarkers =
    [
        "Could not resolve host",
        "Could not resolve proxy",
        "Connection refused",
        "Connection timed out",
        "Connection reset",
        "Operation timed out",
        "Failed to connect to",
        "Couldn't connect to server",
        "Network is unreachable",
        "No route to host"
    ];

    // The remote itself declined the update, for example "(pre-receive hook declined)" or
    // "(protected branch hook declined)"; pulling does not help.
    private static readonly string[] RemoteRejectedMarkers =
    [
        "[remote rejected]"
    ];

    private static readonly string[] RejectedMarkers =
    [
        "[rejected]",
        "Updates were rejected"
    ];

    private static readonly string[] DivergedMarkers =
    [
        "Not possible to fast-forward",
        "Diverging branches can't be fast-forwarded"
    ];

    private static readonly string[] NoUpstreamMarkers =
    [
        "There is no tracking information for the current branch",
        "has no upstream branch",
        "no upstream configured for branch"
    ];

    // "... but no such ref was fetched.": the message of git pull, and of the editor's pull, when the fetch did not
    // bring the upstream branch. "merge: @{upstream} - not something we can merge": the upstream is configured, but
    // its remote-tracking branch no longer exists.
    private static readonly string[] UpstreamGoneMarkers =
    [
        "no such ref was fetched",
        "@{upstream} - not something we can merge"
    ];

    // How many indented lines after the decisive line SelectDecisiveLine keeps.
    private const int MAX_CONTINUATION_LINES = 10;

    /// <summary>
    /// Returns the kind of failure described by <paramref name="errorOutput"/>, or
    /// <see cref="GitRemoteFailureKind.Other"/> when no known text occurs. Authentication is checked first, because
    /// a failed login also ends with the general message that the remote could not be read, and a declined push
    /// before a refused one, because both name the refused reference.
    /// </summary>
    public static GitRemoteFailureKind Classify(string? errorOutput)
    {
        if (string.IsNullOrWhiteSpace(errorOutput))
        {
            return GitRemoteFailureKind.Other;
        }

        if (ContainsAny(errorOutput, AuthenticationMarkers))
        {
            return GitRemoteFailureKind.Authentication;
        }

        if (ContainsAny(errorOutput, ForbiddenMarkers))
        {
            return GitRemoteFailureKind.Forbidden;
        }

        if (ContainsAny(errorOutput, HostKeyMarkers))
        {
            return GitRemoteFailureKind.HostKeyVerification;
        }

        if (ContainsAny(errorOutput, NetworkMarkers))
        {
            return GitRemoteFailureKind.Network;
        }

        if (ContainsAny(errorOutput, RemoteRejectedMarkers))
        {
            return GitRemoteFailureKind.RemoteRejected;
        }

        if (ContainsAny(errorOutput, RejectedMarkers))
        {
            return GitRemoteFailureKind.Rejected;
        }

        if (ContainsAny(errorOutput, DivergedMarkers))
        {
            return GitRemoteFailureKind.Diverged;
        }

        if (ContainsAny(errorOutput, UpstreamGoneMarkers))
        {
            return GitRemoteFailureKind.UpstreamGone;
        }

        return ContainsAny(errorOutput, NoUpstreamMarkers)
            ? GitRemoteFailureKind.NoUpstream
            : GitRemoteFailureKind.Other;
    }

    /// <summary>
    /// Returns the line of git's output that says what went wrong, without the advice lines starting with
    /// <c>hint:</c>: a line of a refused reference such as <c>! [rejected]</c>, otherwise the first line with
    /// <c>fatal:</c>, otherwise the first line with <c>error:</c>, otherwise the first other line. The lines right
    /// after it that are indented deeper belong to it, such as the files listed after
    /// <c>error: Your local changes to the following files would be overwritten by merge:</c>; at most
    /// ten of them follow on their own lines, indented, then an ellipsis (U+2026). Returns an empty string when the
    /// output has no such line.
    /// </summary>
    public static string SelectDecisiveLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        string[] lines = output.Split('\n');
        int refused = -1;
        int fatal = -1;
        int error = -1;
        int first = -1;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || line.StartsWith("hint:", StringComparison.Ordinal))
            {
                continue;
            }

            if (refused < 0 && line.StartsWith("! [", StringComparison.Ordinal))
            {
                refused = index;
            }
            else if (fatal < 0 && line.Contains("fatal:", StringComparison.Ordinal))
            {
                fatal = index;
            }
            else if (error < 0 && line.Contains("error:", StringComparison.Ordinal))
            {
                error = index;
            }

            if (first < 0)
            {
                first = index;
            }
        }

        int decisive = refused >= 0 ? refused : fatal >= 0 ? fatal : error >= 0 ? error : first;
        if (decisive < 0)
        {
            return string.Empty;
        }

        StringBuilder text = new(lines[decisive].Trim());
        int indentation = CountIndentation(lines[decisive]);
        int added = 0;
        for (int index = decisive + 1; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            if (line.Length == 0 || CountIndentation(lines[index]) <= indentation)
            {
                break;
            }

            text.Append(Environment.NewLine).Append(CONTINUATION_INDENT);
            if (added == MAX_CONTINUATION_LINES)
            {
                text.Append(ELLIPSIS);
                break;
            }

            text.Append(line);
            added++;
        }

        return text.ToString();
    }

    private static int CountIndentation(string line)
    {
        int count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t'))
        {
            count++;
        }

        return count;
    }

    private static bool ContainsAny(string text, string[] markers)
    {
        foreach (string marker in markers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
