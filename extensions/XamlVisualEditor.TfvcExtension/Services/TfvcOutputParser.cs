using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>Parses tf.exe output. XML formats are preferred because they do not depend on the tf.exe display language.</summary>
public static partial class TfvcOutputParser
{
    private static readonly char[] ChangeTypeSeparators = [',', ' '];

    /// <summary>Parses the output of <c>tf workspaces /format:xml</c>.</summary>
    public static IReadOnlyList<TfvcWorkspace> ParseWorkspaces(string xml)
    {
        XDocument document = LoadXml(xml);
        List<TfvcWorkspace> workspaces = new();
        foreach (XElement workspaceElement in document.Descendants("Workspace"))
        {
            string? name = (string?)workspaceElement.Attribute("name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            List<TfvcWorkingFolder> folders = new();
            foreach (XElement folderElement in workspaceElement.Descendants("WorkingFolder"))
            {
                string? serverPath = (string?)folderElement.Attribute("item");
                if (string.IsNullOrWhiteSpace(serverPath))
                {
                    continue;
                }

                string? localPath = (string?)folderElement.Attribute("local");
                string? type = (string?)folderElement.Attribute("type");
                bool isCloaked = string.Equals(type, "Cloak", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(localPath);
                folders.Add(new TfvcWorkingFolder(
                    TfvcWorkspace.NormalizeServerPath(serverPath),
                    isCloaked ? string.Empty : localPath!.Trim(),
                    isCloaked));
            }

            workspaces.Add(new TfvcWorkspace(
                name,
                (string?)workspaceElement.Attribute("ownerdisp"),
                (string?)workspaceElement.Attribute("computer"),
                folders));
        }

        return workspaces;
    }

    /// <summary>Parses the output of <c>tf status /format:xml</c>.</summary>
    /// <param name="xml">tf.exe output.</param>
    /// <param name="workspaceName">Optional workspace filter; null keeps the changes of all pending sets.</param>
    public static IReadOnlyList<TfvcPendingChange> ParsePendingChanges(string xml, string? workspaceName = null)
    {
        XDocument document = LoadXml(xml);
        List<TfvcPendingChange> changes = new();
        foreach (XElement pendingSet in document.Descendants("PendingSet"))
        {
            string? setName = (string?)pendingSet.Attribute("name");
            if (workspaceName is not null && setName is not null
                && !string.Equals(setName, workspaceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (XElement changeElement in pendingSet.Descendants("PendingChange"))
            {
                string? serverPath = (string?)changeElement.Attribute("item");
                if (string.IsNullOrWhiteSpace(serverPath))
                {
                    continue;
                }

                string? localPath = (string?)changeElement.Attribute("local");
                string? lockLevel = (string?)changeElement.Attribute("lock");
                changes.Add(new TfvcPendingChange(
                    serverPath,
                    string.IsNullOrWhiteSpace(localPath) ? null : localPath,
                    SplitChangeTypes((string?)changeElement.Attribute("chg")),
                    string.Equals((string?)changeElement.Attribute("type"), "Folder", StringComparison.OrdinalIgnoreCase),
                    ParseInt((string?)changeElement.Attribute("ver")),
                    string.IsNullOrWhiteSpace(lockLevel) || string.Equals(lockLevel, "none", StringComparison.OrdinalIgnoreCase) ? null : lockLevel,
                    NullIfEmpty((string?)changeElement.Attribute("srcitem"))));
            }
        }

        return changes;
    }

    /// <summary>Extracts the changeset number from <c>tf checkin</c> output.</summary>
    /// <remarks>
    /// tf.exe lists every folder and file before the summary line, and their paths may contain the word "changeset"
    /// followed by digits. Only a line that starts with the changeset message counts, and the last one wins. The message
    /// is recognized in English and German only.
    /// </remarks>
    public static int? TryParseChangesetId(string output)
    {
        string[] lines = (output ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            Match match = ChangesetLineRegex().Match(lines[i]);
            if (match.Success && int.TryParse(match.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>Extracts the first collection URL from <c>tf workfold</c> output.</summary>
    public static string? TryParseCollectionUrl(string output)
    {
        Match match = UrlRegex().Match(output ?? string.Empty);
        return match.Success ? match.Value.TrimEnd('/', '.', ',') : null;
    }

    /// <summary>Splits a tf.exe change type list such as <c>Add, Edit, Encoding</c>.</summary>
    public static IReadOnlyList<string> SplitChangeTypes(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Array.Empty<string>();
        }

        return value.Split(ChangeTypeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static XDocument LoadXml(string xml)
    {
        string text = xml ?? string.Empty;
        int start = text.IndexOf('<');
        if (start < 0)
        {
            return new XDocument();
        }

        try
        {
            return XDocument.Parse(text.Substring(start), LoadOptions.None);
        }
        catch (XmlException)
        {
            return new XDocument();
        }
    }

    private static int ParseInt(string? value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result) ? result : 0;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    // English "Changeset #123 checked in.", German "Changeset Nr. 123 wurde eingecheckt." or "Änderungssatz Nr. 123 eingecheckt.",
    // optionally after one leading word such as "Das". The runner decodes the output with the ANSI code page, so "Ä" arrives
    // as one character. Other display languages are not matched; the caller then asks the server for the changeset.
    [GeneratedRegex(@"^(?:\p{L}+\s+)?(?:changeset|änderungssatz)\s*(?:#|nr\.?)?\s*(?<id>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChangesetLineRegex();

    [GeneratedRegex(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();
}
