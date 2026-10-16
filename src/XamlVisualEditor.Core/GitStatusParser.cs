using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Core;

/// <summary>
/// Parses git status output into structured status models.
/// </summary>
public static class GitStatusParser
{
    /// <summary>
    /// Parses a porcelain v1 status output into a repository status snapshot.
    /// </summary>
    /// <param name="repoPath">The repository root used for the status call.</param>
    /// <param name="statusOutput">The output of `git status --porcelain=v1 -b`.</param>
    public static GitRepositoryStatus ParseStatus(string repoPath, string statusOutput)
    {
        string normalized = NormalizeLineEndings(statusOutput);
        string[] lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        string branchName = string.Empty;
        string? upstreamName = null;
        int aheadBy = 0;
        int behindBy = 0;

        List<GitFileChange> changes = new();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                ParseBranchLine(line, ref branchName, ref upstreamName, ref aheadBy, ref behindBy);
                continue;
            }

            GitFileChange? change = ParseStatusLine(line);
            if (change is not null)
            {
                changes.Add(change);
            }
        }

        return new GitRepositoryStatus
        {
            RepositoryRoot = repoPath,
            BranchName = branchName,
            UpstreamName = upstreamName,
            AheadBy = aheadBy,
            BehindBy = behindBy,
            Changes = changes
        };
    }

    /// <summary>
    /// Parses a porcelain v2 status output with NUL-terminated entries into a repository status snapshot.
    /// </summary>
    /// <param name="repoPath">The repository root used for the status call.</param>
    /// <param name="statusOutput">The output of `git status --porcelain=v2 -z --branch`.</param>
    /// <param name="operation">The operation in progress, which the status output does not contain.</param>
    public static GitRepositoryStatus ParsePorcelainV2(
        string repoPath,
        string statusOutput,
        GitRepositoryOperation operation = GitRepositoryOperation.None)
    {
        string[] entries = statusOutput.Split('\0');

        string branchName = string.Empty;
        string? upstreamName = null;
        string? headSha = null;
        bool isDetached = false;
        bool isUnborn = false;
        int aheadBy = 0;
        int behindBy = 0;

        List<GitFileChange> changes = new();

        for (int i = 0; i < entries.Length; i++)
        {
            string entry = entries[i];
            if (entry.Length < 2)
            {
                continue;
            }

            switch (entry[0])
            {
                case '#':
                    ParseV2Header(
                        entry,
                        ref branchName,
                        ref upstreamName,
                        ref headSha,
                        ref isDetached,
                        ref isUnborn,
                        ref aheadBy,
                        ref behindBy);
                    break;
                case '1':
                    AddIfNotNull(changes, ParseV2Ordinary(entry));
                    break;
                case '2':
                    // The original path of a rename or copy is the next NUL-terminated entry.
                    string? originalPath = i + 1 < entries.Length ? entries[++i] : null;
                    AddIfNotNull(changes, ParseV2RenamedOrCopied(entry, originalPath));
                    break;
                case 'u':
                    AddIfNotNull(changes, ParseV2Unmerged(entry));
                    break;
                case '?':
                    changes.Add(new GitFileChange
                    {
                        Path = entry.Substring(2),
                        WorkTreeStatus = GitChangeKind.Untracked,
                        IsUntracked = true
                    });
                    break;
                case '!':
                    changes.Add(new GitFileChange
                    {
                        Path = entry.Substring(2),
                        WorkTreeStatus = GitChangeKind.Ignored,
                        IsIgnored = true
                    });
                    break;
            }
        }

        if (isDetached && headSha is not null)
        {
            branchName = headSha.Length > 7 ? headSha.Substring(0, 7) : headSha;
        }

        return new GitRepositoryStatus
        {
            RepositoryRoot = repoPath,
            BranchName = branchName,
            UpstreamName = upstreamName,
            AheadBy = aheadBy,
            BehindBy = behindBy,
            HeadSha = headSha,
            IsDetached = isDetached,
            IsUnborn = isUnborn,
            Operation = operation,
            Changes = changes
        };
    }

    private static void AddIfNotNull(List<GitFileChange> changes, GitFileChange? change)
    {
        if (change is not null)
        {
            changes.Add(change);
        }
    }

    private static void ParseV2Header(
        string entry,
        ref string branchName,
        ref string? upstreamName,
        ref string? headSha,
        ref bool isDetached,
        ref bool isUnborn,
        ref int aheadBy,
        ref int behindBy)
    {
        const string OID = "# branch.oid ";
        const string HEAD = "# branch.head ";
        const string UPSTREAM = "# branch.upstream ";
        const string AHEAD_BEHIND = "# branch.ab ";

        if (entry.StartsWith(OID, StringComparison.Ordinal))
        {
            string oid = entry.Substring(OID.Length);
            isUnborn = oid == "(initial)";
            headSha = isUnborn ? null : oid;
        }
        else if (entry.StartsWith(HEAD, StringComparison.Ordinal))
        {
            string head = entry.Substring(HEAD.Length);
            isDetached = head == "(detached)";
            branchName = isDetached ? string.Empty : head;
        }
        else if (entry.StartsWith(UPSTREAM, StringComparison.Ordinal))
        {
            upstreamName = entry.Substring(UPSTREAM.Length);
        }
        else if (entry.StartsWith(AHEAD_BEHIND, StringComparison.Ordinal))
        {
            string[] counts = entry.Substring(AHEAD_BEHIND.Length).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < counts.Length; i++)
            {
                string count = counts[i];
                if (count.Length > 1 && count[0] == '+' && int.TryParse(count.AsSpan(1), out int ahead))
                {
                    aheadBy = ahead;
                }
                else if (count.Length > 1 && count[0] == '-' && int.TryParse(count.AsSpan(1), out int behind))
                {
                    behindBy = behind;
                }
            }
        }
    }

    // 1 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <path>
    private static GitFileChange? ParseV2Ordinary(string entry)
    {
        string[] fields = entry.Split(' ', 9);
        if (fields.Length < 9 || fields[1].Length != 2)
        {
            return null;
        }

        return CreateV2Change(fields[1], fields[8], oldPath: null);
    }

    // 2 <XY> <sub> <mH> <mI> <mW> <hH> <hI> <X><score> <path>, followed by the original path as its own entry
    private static GitFileChange? ParseV2RenamedOrCopied(string entry, string? originalPath)
    {
        string[] fields = entry.Split(' ', 10);
        if (fields.Length < 10 || fields[1].Length != 2)
        {
            return null;
        }

        return CreateV2Change(fields[1], fields[9], string.IsNullOrEmpty(originalPath) ? null : originalPath);
    }

    // u <XY> <sub> <m1> <m2> <m3> <mW> <h1> <h2> <h3> <path>
    private static GitFileChange? ParseV2Unmerged(string entry)
    {
        string[] fields = entry.Split(' ', 11);
        if (fields.Length < 11)
        {
            return null;
        }

        GitConflictKind conflictKind = fields[1] switch
        {
            "DD" => GitConflictKind.BothDeleted,
            "AU" => GitConflictKind.AddedByUs,
            "UD" => GitConflictKind.DeletedByThem,
            "UA" => GitConflictKind.AddedByThem,
            "DU" => GitConflictKind.DeletedByUs,
            "AA" => GitConflictKind.BothAdded,
            _ => GitConflictKind.BothModified
        };

        return new GitFileChange
        {
            Path = fields[10],
            IndexStatus = GitChangeKind.Unmerged,
            WorkTreeStatus = GitChangeKind.Unmerged,
            ConflictKind = conflictKind
        };
    }

    private static GitFileChange CreateV2Change(string status, string path, string? oldPath)
    {
        // Porcelain v2 writes '.' for an unchanged side.
        GitChangeKind indexStatus = MapStatusChar(status[0] == '.' ? ' ' : status[0]);
        GitChangeKind workTreeStatus = MapStatusChar(status[1] == '.' ? ' ' : status[1]);
        return new GitFileChange
        {
            Path = path,
            OldPath = oldPath,
            IndexStatus = indexStatus,
            WorkTreeStatus = workTreeStatus,
            IsRenamed = indexStatus == GitChangeKind.Renamed || workTreeStatus == GitChangeKind.Renamed,
            IsCopied = indexStatus == GitChangeKind.Copied || workTreeStatus == GitChangeKind.Copied
        };
    }

    private static string NormalizeLineEndings(string input)
    {
        return input.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
    }

    private static void ParseBranchLine(
        string line,
        ref string branchName,
        ref string? upstreamName,
        ref int aheadBy,
        ref int behindBy)
    {
        string content = line.Substring(3);
        int bracketIndex = content.IndexOf('[');
        string branchPart = bracketIndex >= 0 ? content.Substring(0, bracketIndex).Trim() : content.Trim();

        string? upstream = null;
        string branch = branchPart;
        int upstreamIndex = branchPart.IndexOf("...", StringComparison.Ordinal);
        if (upstreamIndex >= 0)
        {
            branch = branchPart.Substring(0, upstreamIndex);
            upstream = branchPart.Substring(upstreamIndex + 3);
        }

        branchName = branch;
        upstreamName = upstream;
        aheadBy = 0;
        behindBy = 0;

        if (bracketIndex >= 0)
        {
            int endIndex = content.IndexOf(']', bracketIndex);
            if (endIndex > bracketIndex)
            {
                string stats = content.Substring(bracketIndex + 1, endIndex - bracketIndex - 1);
                ParseAheadBehind(stats, ref aheadBy, ref behindBy);
            }
        }
    }

    private static void ParseAheadBehind(string stats, ref int aheadBy, ref int behindBy)
    {
        string[] parts = stats.Split(',', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i].Trim();
            if (part.StartsWith("ahead ", StringComparison.Ordinal)
                && int.TryParse(part.Substring(6), out int ahead))
            {
                aheadBy = ahead;
            }
            else if (part.StartsWith("behind ", StringComparison.Ordinal)
                && int.TryParse(part.Substring(7), out int behind))
            {
                behindBy = behind;
            }
        }
    }

    private static GitFileChange? ParseStatusLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 3)
        {
            return null;
        }

        string status = line.Substring(0, 2);
        string pathPart = line.Length > 3 ? line.Substring(3) : string.Empty;
        string? oldPath = null;
        string path = pathPart;

        int arrowIndex = pathPart.IndexOf(" -> ", StringComparison.Ordinal);
        if (arrowIndex >= 0)
        {
            oldPath = pathPart.Substring(0, arrowIndex);
            path = pathPart.Substring(arrowIndex + 4);
        }

        GitChangeKind indexStatus = MapStatusChar(status[0]);
        GitChangeKind workTreeStatus = MapStatusChar(status[1]);
        bool isUntracked = status == "??";
        bool isIgnored = status == "!!";

        return new GitFileChange
        {
            Path = path,
            OldPath = oldPath,
            IndexStatus = indexStatus,
            WorkTreeStatus = workTreeStatus,
            IsUntracked = isUntracked,
            IsIgnored = isIgnored,
            IsRenamed = indexStatus == GitChangeKind.Renamed || workTreeStatus == GitChangeKind.Renamed,
            IsCopied = indexStatus == GitChangeKind.Copied || workTreeStatus == GitChangeKind.Copied
        };
    }

    private static GitChangeKind MapStatusChar(char status)
    {
        return status switch
        {
            ' ' => GitChangeKind.None,
            'M' => GitChangeKind.Modified,
            'A' => GitChangeKind.Added,
            'D' => GitChangeKind.Deleted,
            'R' => GitChangeKind.Renamed,
            'C' => GitChangeKind.Copied,
            'U' => GitChangeKind.Unmerged,
            'T' => GitChangeKind.TypeChanged,
            '?' => GitChangeKind.Untracked,
            '!' => GitChangeKind.Ignored,
            _ => GitChangeKind.Unknown
        };
    }
}
