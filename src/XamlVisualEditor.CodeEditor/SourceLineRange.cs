namespace XamlVisualEditor.CodeEditor;

/// <summary>An inclusive range of 1-based document lines.</summary>
public readonly record struct SourceLineRange(int StartLine, int EndLine)
{
    /// <summary>Returns whether a line lies in the range.</summary>
    public bool Contains(int line) => line >= StartLine && line <= EndLine;
}
