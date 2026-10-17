using System;
using System.Collections.Generic;
using System.Globalization;

namespace XamlVisualEditor.Core;

/// <summary>Parses the output of <c>git stash list -z --format=%gd%x00%H%x00%ct%x00%gs</c>.</summary>
public static class GitStashParser
{
    /// <summary>
    /// Parses four NUL-terminated fields per stash: name, commit id, commit date and message. Entries whose name is not
    /// <c>stash@{n}</c> are skipped.
    /// </summary>
    public static IReadOnlyList<GitStashEntry> Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string[] fields = output.Split('\0');
        List<GitStashEntry> entries = new();
        for (int i = 0; i + 3 < fields.Length; i += 4)
        {
            string name = fields[i];
            if (name.StartsWith("stash@{", StringComparison.Ordinal)
                && name.EndsWith('}')
                && int.TryParse(name.AsSpan(7, name.Length - 8), NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                && long.TryParse(fields[i + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long seconds))
            {
                entries.Add(new GitStashEntry(index, fields[i + 1], fields[i + 3], DateTimeOffset.FromUnixTimeSeconds(seconds)));
            }
        }

        return entries;
    }
}
