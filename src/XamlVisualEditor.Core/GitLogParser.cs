using System;
using System.Collections.Generic;
using System.Globalization;

namespace XamlVisualEditor.Core;

/// <summary>
/// Parses the commit list of <c>git log -z</c> in <see cref="LOG_FORMAT"/> and the changed files of
/// <c>git diff-tree -r -z --name-status</c>.
/// </summary>
public static class GitLogParser
{
    /// <summary>
    /// The <c>--format</c> of <c>git log -z</c> that <see cref="ParseLog"/> reads: every commit starts with the record
    /// separator 0x1E and has twelve fields separated by NUL, the message last; <c>-z</c> ends every
    /// commit with NUL. A message cannot contain NUL, so messages with any text stay intact.
    /// </summary>
    public const string LOG_FORMAT = "%x1e%H%x00%h%x00%P%x00%p%x00%an%x00%ae%x00%aI%x00%cn%x00%cI%x00%D%x00%s%x00%B";

    private const int FIELD_COUNT = 12;
    private const char RECORD_SEPARATOR = '\u001e';

    /// <summary>Parses the output of <c>git log -z --format=LOG_FORMAT</c>; malformed records are skipped.</summary>
    public static IReadOnlyList<GitCommitInfo> ParseLog(string output)
    {
        List<GitCommitInfo> commits = new();
        if (string.IsNullOrEmpty(output))
        {
            return commits;
        }

        string[] tokens = output.Split('\0');
        int index = 0;
        while (index < tokens.Length)
        {
            string first = tokens[index];
            if (first.Length == 0 || first[0] != RECORD_SEPARATOR)
            {
                index++;
                continue;
            }

            if (index + FIELD_COUNT > tokens.Length)
            {
                break;
            }

            commits.Add(CreateCommit(first[1..], tokens.AsSpan(index + 1, FIELD_COUNT - 1)));
            index += FIELD_COUNT;
        }

        return commits;
    }

    /// <summary>
    /// Parses the output of <c>git diff-tree -r -z --name-status</c>: a status such as <c>M</c>, <c>A</c>, <c>D</c>,
    /// <c>T</c> followed by the path, or <c>R</c>/<c>C</c> with the similarity followed by the old and the new path.
    /// </summary>
    public static IReadOnlyList<GitCommitFileChange> ParseNameStatus(string output)
    {
        List<GitCommitFileChange> files = new();
        if (string.IsNullOrEmpty(output))
        {
            return files;
        }

        string[] tokens = output.Split('\0');
        int index = 0;
        while (index < tokens.Length)
        {
            string status = tokens[index].Trim();
            if (status.Length == 0)
            {
                index++;
                continue;
            }

            GitChangeKind kind = ToKind(status[0]);
            bool hasOldPath = kind is GitChangeKind.Renamed or GitChangeKind.Copied;
            int pathCount = hasOldPath ? 2 : 1;
            if (index + pathCount >= tokens.Length)
            {
                break;
            }

            int? similarity = status.Length > 1
                && int.TryParse(status.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                    ? value
                    : null;
            files.Add(new GitCommitFileChange
            {
                Path = tokens[index + pathCount],
                OldPath = hasOldPath ? tokens[index + 1] : null,
                Kind = kind,
                Similarity = similarity
            });
            index += pathCount + 1;
        }

        return files;
    }

    /// <summary>
    /// Splits the decoration <c>%D</c>, for example <c>HEAD -&gt; main, tag: v1.0, origin/main</c>, into its references.
    /// </summary>
    public static IReadOnlyList<string> ParseRefs(string decoration)
    {
        return string.IsNullOrWhiteSpace(decoration)
            ? Array.Empty<string>()
            : decoration.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static GitCommitInfo CreateCommit(string sha, ReadOnlySpan<string> fields)
    {
        return new GitCommitInfo
        {
            Sha = sha,
            ShortSha = fields[0],
            ParentShas = fields[1].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            ParentShortShas = fields[2].Split(' ', StringSplitOptions.RemoveEmptyEntries),
            AuthorName = fields[3],
            AuthorEmail = fields[4],
            AuthorDate = ParseDate(fields[5]),
            CommitterName = fields[6],
            CommitterDate = ParseDate(fields[7]),
            Refs = ParseRefs(fields[8]),
            Subject = fields[9],
            Message = fields[10].TrimEnd('\r', '\n')
        };
    }

    private static DateTimeOffset ParseDate(string text)
    {
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset date)
            ? date
            : default;
    }

    private static GitChangeKind ToKind(char status)
    {
        return status switch
        {
            'A' => GitChangeKind.Added,
            'M' => GitChangeKind.Modified,
            'D' => GitChangeKind.Deleted,
            'R' => GitChangeKind.Renamed,
            'C' => GitChangeKind.Copied,
            'T' => GitChangeKind.TypeChanged,
            'U' => GitChangeKind.Unmerged,
            _ => GitChangeKind.Unknown
        };
    }
}
