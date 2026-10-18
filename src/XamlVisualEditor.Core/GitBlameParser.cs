using System;
using System.Collections.Generic;
using System.Globalization;

namespace XamlVisualEditor.Core;

/// <summary>Parses the output of <c>git blame --porcelain</c>.</summary>
public static class GitBlameParser
{
    /// <summary>
    /// Parses the output into hunks and lines. Git writes the author, date and summary of a commit only where the
    /// commit appears first; every line follows its header with a tab. Malformed lines are skipped.
    /// </summary>
    /// <param name="output">The output of <c>git blame --porcelain</c>.</param>
    /// <param name="isTruncated">Whether the output was cut off; passed on to the result.</param>
    public static GitBlame Parse(string output, bool isTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        Dictionary<string, CommitFields> commits = new(StringComparer.Ordinal);
        List<string> lines = new();
        List<string> lineShas = new();
        CommitFields? current = null;
        foreach (string line in output.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                if (current is not null)
                {
                    lines.Add(line.AsSpan(1).TrimEnd('\r').ToString());
                    lineShas.Add(current.Sha);
                    current = null;
                }

                continue;
            }

            if (TryReadHeader(line, out string? sha))
            {
                if (!commits.TryGetValue(sha, out current))
                {
                    current = new CommitFields(sha);
                    commits.Add(sha, current);
                }

                continue;
            }

            current?.Read(line);
        }

        Dictionary<string, GitBlameCommit> built = new(StringComparer.Ordinal);
        List<GitBlameHunk> hunks = new();
        for (int index = 0; index < lineShas.Count;)
        {
            string sha = lineShas[index];
            int start = index;
            while (index < lineShas.Count && lineShas[index] == sha)
            {
                index++;
            }

            if (!built.TryGetValue(sha, out GitBlameCommit? commit))
            {
                commit = commits[sha].Build();
                built.Add(sha, commit);
            }

            hunks.Add(new GitBlameHunk(start + 1, index - start, commit));
        }

        return new GitBlame(hunks, lines, isTruncated);
    }

    // "<sha> <original line> <final line>[ <line count>]"; the sha has 40 hex digits, or 64 in SHA-256 repositories.
    private static bool TryReadHeader(string line, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? sha)
    {
        int space = line.IndexOf(' ');
        sha = null;
        if (space is not (40 or 64) || line.AsSpan(0, space).ContainsAnyExcept("0123456789abcdef"))
        {
            return false;
        }

        sha = line[..space];
        return true;
    }

    private sealed class CommitFields(string sha)
    {
        private string _author = string.Empty;
        private long _time;
        private TimeSpan _offset;
        private string _summary = string.Empty;

        public string Sha { get; } = sha;

        public void Read(string line)
        {
            int space = line.IndexOf(' ');
            if (space < 0)
            {
                return;
            }

            string value = line[(space + 1)..].TrimEnd('\r');
            switch (line[..space])
            {
                case "author":
                    _author = value;
                    break;
                case "author-time":
                    _ = long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _time);
                    break;
                case "author-tz":
                    _offset = ParseOffset(value);
                    break;
                case "summary":
                    _summary = value;
                    break;
            }
        }

        public GitBlameCommit Build() =>
            new(Sha, _author, DateTimeOffset.FromUnixTimeSeconds(_time).ToOffset(_offset), _summary);

        // "+0130" or "-0800".
        private static TimeSpan ParseOffset(string value)
        {
            if (value.Length != 5 || value[0] is not ('+' or '-')
                || !int.TryParse(value.AsSpan(1, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hours)
                || !int.TryParse(value.AsSpan(3, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int minutes)
                || hours > 14 || minutes > 59)
            {
                return TimeSpan.Zero;
            }

            TimeSpan offset = new(hours, minutes, 0);
            return value[0] == '-' ? offset.Negate() : offset;
        }
    }
}
