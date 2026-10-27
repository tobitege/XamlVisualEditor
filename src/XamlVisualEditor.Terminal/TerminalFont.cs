using System;

namespace XamlVisualEditor.Terminal;

/// <summary>Default and allowed font settings of the terminal view.</summary>
public static class TerminalFont
{
    /// <summary>Comma-separated families; the view uses the first installed one.</summary>
    public const string DefaultFamilies = "Cascadia Mono, Menlo, Consolas, monospace";
    public const double DefaultSize = 13;
    public const double MinSize = 6;
    public const double MaxSize = 72;

    /// <summary>Returns the trimmed families, or the default when the value is empty.</summary>
    public static string NormalizeFamilies(string? families) =>
        string.IsNullOrWhiteSpace(families) ? DefaultFamilies : families.Trim();

    /// <summary>Returns the size clamped to the allowed range, or the default when it is not a number.</summary>
    public static double NormalizeSize(double size) =>
        double.IsFinite(size) ? Math.Clamp(size, MinSize, MaxSize) : DefaultSize;
}
