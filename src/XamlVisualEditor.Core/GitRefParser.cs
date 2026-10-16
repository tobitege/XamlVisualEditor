using System;
using System.Collections.Generic;
using System.Globalization;

namespace XamlVisualEditor.Core;

/// <summary>
/// Parses the branch list of <c>git for-each-ref</c> in <see cref="BRANCH_FORMAT"/> and the remote list of
/// <c>git remote -v</c>.
/// </summary>
public static class GitRefParser
{
    /// <summary>
    /// The <c>--format</c> of <c>git for-each-ref</c> that <see cref="ParseBranches"/> reads: the fields of a
    /// reference are separated by NUL and every reference ends with the record separator 0x1E, so subjects with any
    /// text stay intact. The tracking field is in English only when git runs with <c>LC_ALL=C</c>.
    /// </summary>
    public const string BRANCH_FORMAT =
        "%(HEAD)%00%(refname)%00%(upstream)%00%(upstream:track,nobracket)%00%(symref)%00%(contents:subject)%1e";

    private const string LOCAL_PREFIX = "refs/heads/";
    private const string REMOTE_PREFIX = "refs/remotes/";
    private const int FIELD_COUNT = 6;
    private const int SUBJECT_FIELD = 5;

    /// <summary>
    /// Parses the output of <c>git for-each-ref --format=BRANCH_FORMAT refs/heads refs/remotes</c>. Returns the
    /// local branches followed by the remote-tracking branches in the order of the output; symbolic references
    /// such as <c>refs/remotes/origin/HEAD</c> and malformed records are skipped.
    /// </summary>
    /// <param name="output">The output of git.</param>
    /// <param name="remoteNames">
    /// The configured remotes. Remote names may contain slashes, so the remote of a remote-tracking branch is the
    /// longest remote name its name starts with; without a match, the part before the first slash.
    /// </param>
    public static IReadOnlyList<GitBranch> ParseBranches(string output, IReadOnlyList<string> remoteNames)
    {
        List<GitBranch> local = new();
        List<GitBranch> remote = new();
        if (string.IsNullOrEmpty(output))
        {
            return local;
        }

        foreach (string rawRecord in output.Split('\u001e'))
        {
            // for-each-ref ends every record with a line feed, which then starts the next record.
            string record = rawRecord.TrimStart('\r', '\n');
            if (record.Length == 0)
            {
                continue;
            }

            string[] fields = record.Split('\0');
            if (fields.Length < FIELD_COUNT || fields[4].Length > 0)
            {
                continue;
            }

            GitBranch? branch = CreateBranch(fields, remoteNames);
            if (branch is null)
            {
                continue;
            }

            (branch.IsRemote ? remote : local).Add(branch);
        }

        local.AddRange(remote);
        return local;
    }

    /// <summary>
    /// Parses the tracking field <c>%(upstream:track,nobracket)</c> as git writes it with <c>LC_ALL=C</c>:
    /// empty, <c>gone</c>, <c>ahead 2</c>, <c>behind 1</c> or <c>ahead 2, behind 1</c>.
    /// </summary>
    public static (int AheadBy, int BehindBy, bool IsGone) ParseTrack(string track)
    {
        int ahead = 0;
        int behind = 0;
        if (string.IsNullOrWhiteSpace(track))
        {
            return (0, 0, false);
        }

        if (string.Equals(track.Trim(), "gone", StringComparison.Ordinal))
        {
            return (0, 0, true);
        }

        foreach (string part in track.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int space = part.IndexOf(' ', StringComparison.Ordinal);
            if (space <= 0
                || !int.TryParse(part.AsSpan(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            {
                continue;
            }

            string word = part[..space];
            if (word == "ahead")
            {
                ahead = count;
            }
            else if (word == "behind")
            {
                behind = count;
            }
        }

        return (ahead, behind, false);
    }

    /// <summary>
    /// Parses the output of <c>git remote -v</c>, which lists a fetch and a push line per remote, into one entry per
    /// remote in the order of the output.
    /// </summary>
    public static IReadOnlyList<GitRemote> ParseRemotes(string output)
    {
        List<string> names = new();
        Dictionary<string, (string Fetch, string Push)> urls = new(StringComparer.Ordinal);
        foreach (string rawLine in output.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int tab = line.IndexOf('\t', StringComparison.Ordinal);
            int kindStart = line.LastIndexOf(" (", StringComparison.Ordinal);
            if (tab <= 0 || kindStart <= tab || !line.EndsWith(')'))
            {
                continue;
            }

            string name = line[..tab];
            string url = line[(tab + 1)..kindStart];
            string kind = line[(kindStart + 2)..^1];
            if (!urls.TryGetValue(name, out (string Fetch, string Push) entry))
            {
                names.Add(name);
                entry = (string.Empty, string.Empty);
            }

            urls[name] = kind switch
            {
                "fetch" => (url, entry.Push),
                "push" => (entry.Fetch, url),
                _ => entry
            };
        }

        List<GitRemote> remotes = new(names.Count);
        foreach (string name in names)
        {
            (string fetch, string push) = urls[name];
            remotes.Add(new GitRemote(name, fetch, push.Length == 0 ? fetch : push));
        }

        return remotes;
    }

    /// <summary>Returns the name of a branch reference without <c>refs/heads/</c> or <c>refs/remotes/</c>.</summary>
    public static string ShortenBranchReference(string fullName)
    {
        if (fullName.StartsWith(LOCAL_PREFIX, StringComparison.Ordinal))
        {
            return fullName[LOCAL_PREFIX.Length..];
        }

        return fullName.StartsWith(REMOTE_PREFIX, StringComparison.Ordinal)
            ? fullName[REMOTE_PREFIX.Length..]
            : fullName;
    }

    private static GitBranch? CreateBranch(string[] fields, IReadOnlyList<string> remoteNames)
    {
        string fullName = fields[1];
        bool isRemote = fullName.StartsWith(REMOTE_PREFIX, StringComparison.Ordinal);
        if (!isRemote && !fullName.StartsWith(LOCAL_PREFIX, StringComparison.Ordinal))
        {
            return null;
        }

        string name = ShortenBranchReference(fullName);
        string upstream = fields[2];
        (int ahead, int behind, bool gone) = upstream.Length == 0 ? (0, 0, false) : ParseTrack(fields[3]);
        return new GitBranch
        {
            Name = name,
            FullName = fullName,
            IsRemote = isRemote,
            RemoteName = isRemote ? FindRemoteName(name, remoteNames) : null,
            IsCurrent = fields[0] == "*",
            UpstreamName = upstream.Length == 0 ? null : ShortenBranchReference(upstream),
            AheadBy = ahead,
            BehindBy = behind,
            IsUpstreamGone = gone,
            Subject = string.Join('\0', fields, SUBJECT_FIELD, fields.Length - SUBJECT_FIELD)
        };
    }

    /// <summary>
    /// Returns the longest remote name that <paramref name="name"/> starts with, followed by a slash; without a
    /// match, the part before the first slash, or null when there is none.
    /// </summary>
    private static string? FindRemoteName(string name, IReadOnlyList<string> remoteNames)
    {
        string? found = null;
        foreach (string remote in remoteNames)
        {
            if (name.Length > remote.Length + 1
                && name[remote.Length] == '/'
                && name.StartsWith(remote, StringComparison.Ordinal)
                && remote.Length > (found?.Length ?? 0))
            {
                found = remote;
            }
        }

        if (found is not null)
        {
            return found;
        }

        int slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash > 0 ? name[..slash] : null;
    }
}
