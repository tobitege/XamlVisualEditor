using System;
using System.Collections.Generic;

namespace XamlVisualEditor.Core;

/// <summary>The version of a file that <see cref="Interfaces.IGitContentService.GetFileContentAsync"/> reads.</summary>
public enum GitFileRevision
{
    /// <summary>The staged version, stage 0 of the index.</summary>
    Index,

    /// <summary>The version of the commit HEAD points to.</summary>
    Head
}

/// <summary>The bytes of a file version.</summary>
/// <param name="Bytes">The bytes, at most the requested number.</param>
/// <param name="IsTruncated">Whether the file is longer than the requested number of bytes.</param>
public sealed record GitFileContent(byte[] Bytes, bool IsTruncated);

/// <summary>A commit that lines of a blame belong to.</summary>
/// <param name="Sha">The full commit id; zeros for lines changed since the last commit.</param>
/// <param name="AuthorName">The author name.</param>
/// <param name="AuthorDate">The author date with the author's time zone.</param>
/// <param name="Summary">The first line of the commit message.</param>
public sealed record GitBlameCommit(string Sha, string AuthorName, DateTimeOffset AuthorDate, string Summary)
{
    /// <summary>Gets whether the lines are not committed yet.</summary>
    public bool IsUncommitted => Sha.AsSpan().IndexOfAnyExcept('0') < 0;
}

/// <summary>Consecutive lines that belong to the same commit.</summary>
/// <param name="StartLine">The first line, 1-based.</param>
/// <param name="LineCount">The number of lines.</param>
/// <param name="Commit">The commit that last changed the lines.</param>
public sealed record GitBlameHunk(int StartLine, int LineCount, GitBlameCommit Commit);

/// <summary>The line authorship of a file.</summary>
/// <param name="Hunks">The hunks in line order; together they cover <paramref name="Lines"/>.</param>
/// <param name="Lines">The text of the lines without line terminator.</param>
/// <param name="IsTruncated">Whether git's output was longer than the read limit, so the last lines are missing.</param>
public sealed record GitBlame(IReadOnlyList<GitBlameHunk> Hunks, IReadOnlyList<string> Lines, bool IsTruncated);
