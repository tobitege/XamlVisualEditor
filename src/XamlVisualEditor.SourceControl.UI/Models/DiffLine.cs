namespace XamlVisualEditor.SourceControl.UI.Models;

/// <summary>Kind of a line of a unified diff.</summary>
public enum DiffLineKind
{
    /// <summary>An unchanged line.</summary>
    Context,

    /// <summary>A file header line such as <c>diff --git</c>, <c>index</c>, <c>---</c> or <c>+++</c>.</summary>
    FileHeader,

    /// <summary>A hunk header line starting with <c>@@</c>.</summary>
    HunkHeader,

    /// <summary>An added line.</summary>
    Added,

    /// <summary>A removed line.</summary>
    Removed,

    /// <summary>The marker <c>\ No newline at end of file</c>.</summary>
    NoNewline
}

/// <summary>One line of a unified diff.</summary>
/// <param name="Kind">The kind of the line.</param>
/// <param name="Text">The line as the diff writes it, including the <c>+</c>, <c>-</c> or space marker.</param>
/// <param name="OldLineNumber">The 1-based line number in the old file, or null when the line is not in it.</param>
/// <param name="NewLineNumber">The 1-based line number in the new file, or null when the line is not in it.</param>
public sealed record DiffLine(DiffLineKind Kind, string Text, int? OldLineNumber = null, int? NewLineNumber = null);

/// <summary>A range of lines of a diff.</summary>
/// <param name="Start">The 0-based index of the first line.</param>
/// <param name="Count">The number of lines; at least 1.</param>
public readonly record struct DiffLineRange(int Start, int Count)
{
    /// <summary>Gets the 0-based index of the last line.</summary>
    public int End => Start + Count - 1;
}
