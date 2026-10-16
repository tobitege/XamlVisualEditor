using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>A local file or folder that can be added to version control.</summary>
/// <param name="LocalPath">Local path.</param>
/// <param name="IsFolder">True for a folder.</param>
/// <param name="Modified">Last write time.</param>
/// <param name="Size">File size in bytes; null for a folder.</param>
public sealed record TfvcAddEntry(string LocalPath, bool IsFolder, DateTime Modified, long? Size);

/// <summary>
/// Finds the items below a mapped folder that are not in version control, for adding them to a folder.
/// </summary>
/// <remarks>
/// One <c>tf reconcile /promote /preview</c> of the start folder lists every unversioned item that version control
/// does not ignore. A changed versioned file appears like an added one, so one recursive server query of the start
/// folder removes the versioned items. The folders are then browsed from that result.
/// </remarks>
public sealed class TfvcAddItemsScanner
{
    private readonly TfvcSession _session;
    private readonly TfvcWorkspace _workspace;
    private readonly HashSet<string> _candidates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the scanner for a folder of a workspace.</summary>
    public TfvcAddItemsScanner(TfvcSession session, TfvcWorkspace workspace, string rootFolder)
    {
        _session = session;
        _workspace = workspace;
        RootFolder = Path.TrimEndingDirectorySeparator(rootFolder);
    }

    /// <summary>Gets the folder the scan starts from.</summary>
    public string RootFolder { get; }

    /// <summary>Lists the unversioned items below the start folder.</summary>
    public async Task ScanAsync(CancellationToken cancellationToken)
    {
        _candidates.Clear();
        IReadOnlyCollection<string> listed = await _session.Client.PreviewLocalChangesAsync(RootFolder, cancellationToken);
        HashSet<string> versioned = await GetVersionedServerPathsAsync(cancellationToken);
        foreach (string path in listed)
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && _session.FindPendingChangeByLocalPath(path) is null
                && !(_workspace.TryGetServerPath(path) is { } serverPath && versioned.Contains(serverPath)))
            {
                _candidates.Add(Path.TrimEndingDirectorySeparator(path));
            }
        }
    }

    /// <summary>Gets the server path a local folder maps to.</summary>
    public string? GetServerPath(string localFolder) => _workspace.TryGetServerPath(localFolder);

    /// <summary>Gets whether a path is an item to add itself, not only a folder that holds items to add.</summary>
    public bool IsCandidate(string localPath) => _candidates.Contains(Path.TrimEndingDirectorySeparator(localPath));

    /// <summary>
    /// Lists the entries directly in a folder: unversioned files, and folders that are unversioned or hold unversioned
    /// items.
    /// </summary>
    public IReadOnlyList<TfvcAddEntry> List(string folder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);
        List<string> files = new();
        foreach (string candidate in _candidates)
        {
            if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string relative = candidate[prefix.Length..];
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator >= 0)
            {
                folders.Add(prefix + relative[..separator]);
            }
            else if (Directory.Exists(candidate))
            {
                folders.Add(candidate);
            }
            else
            {
                files.Add(candidate);
            }
        }

        List<TfvcAddEntry> entries = new();
        foreach (string path in folders)
        {
            entries.Add(CreateEntry(path));
        }

        foreach (string path in files)
        {
            entries.Add(CreateEntry(path));
        }

        return entries;
    }

    /// <summary>Lists the items to add below a folder at any depth, the folder itself excluded.</summary>
    public IReadOnlyList<TfvcAddEntry> ListCandidatesBelow(string folder)
    {
        string prefix = Path.TrimEndingDirectorySeparator(folder) + Path.DirectorySeparatorChar;
        List<TfvcAddEntry> entries = new();
        foreach (string candidate in _candidates)
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                entries.Add(CreateEntry(candidate));
            }
        }

        return entries;
    }

    /// <summary>Describes a local file or folder.</summary>
    public static TfvcAddEntry CreateEntry(string localPath)
    {
        if (Directory.Exists(localPath))
        {
            return new TfvcAddEntry(localPath, true, Directory.GetLastWriteTime(localPath), null);
        }

        FileInfo info = new(localPath);
        return new TfvcAddEntry(localPath, false, info.LastWriteTime, info.Length);
    }

    private async Task<HashSet<string>> GetVersionedServerPathsAsync(CancellationToken cancellationToken)
    {
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        string? collectionUrl = _session.Settings.CollectionUrl;
        string? rootServerPath = _workspace.TryGetServerPath(RootFolder);
        if (collectionUrl is null || rootServerPath is null)
        {
            return paths;
        }

        foreach (TfvcServerItem item in await _session.Server.GetItemsRecursiveAsync(collectionUrl, rootServerPath, cancellationToken))
        {
            paths.Add(item.ServerPath);
        }

        return paths;
    }
}
