using System;
using System.Collections.Generic;
using System.IO;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Parses the output of <c>tf reconcile /promote /preview</c>.</summary>
public static class TfvcReconcileOutputParser
{
    /// <summary>
    /// Returns the absolute paths of the listed items. The output groups the items by folder: a header line ends with
    /// ':' and holds an absolute path or a path relative to <paramref name="scopeLocalPath"/>, and each item line reads
    /// "&lt;change type&gt;: &lt;name&gt;". Items before the first header belong to the scope folder. The change type
    /// is localized, so only the separator is evaluated; Windows file names cannot contain ':'.
    /// </summary>
    public static IReadOnlyCollection<string> ParseCandidatePaths(string output, string scopeLocalPath)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string currentFolder = scopeLocalPath;
        foreach (string rawLine in (output ?? string.Empty).Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.EndsWith(':'))
            {
                string header = line[..^1].Trim();
                if (header.Length > 0)
                {
                    currentFolder = Path.IsPathRooted(header) ? header : Path.Combine(scopeLocalPath, header);
                }

                continue;
            }

            int separator = line.LastIndexOf(':');
            if (separator < 0 || separator + 1 >= line.Length)
            {
                continue;
            }

            string name = line[(separator + 1)..].Trim();
            if (name.Length == 0)
            {
                continue;
            }

            try
            {
                paths.Add(Path.GetFullPath(Path.Combine(currentFolder, name)));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
            }
        }

        return paths;
    }
}
