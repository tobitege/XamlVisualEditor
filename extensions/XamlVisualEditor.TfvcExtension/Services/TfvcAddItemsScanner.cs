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
/// does not ignore; the folders are then browsed from that result. A changed versioned file appears like an added one,
/// so listed files are checked against the server.
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
        foreach (string path in await _session.Client.PreviewLocalChangesAsync(_workspace, RootFolder, cancellationToken))
        {
            if ((File.Exists(path) || Directory.Exists(path)) && _session.FindPendingChangeByLocalPath(path) is null)
            {
                _candidates.Add(Path.TrimEndingDirectorySeparator(path));
            }
        }
    }

    /// <summary>Gets the server path a local folder maps to.</summary>
    public string? GetServerPath(string localFolder) => _workspace.TryGetServerPath(localFolder);

    /// <summary>
    /// Lists the entries directly in a folder: unversioned files, and folders that are unversioned or hold unversioned
    /// items.
    /// </summary>
    public async Task<IReadOnlyList<TfvcAddEntry>> ListAsync(string folder, CancellationToken cancellationToken)
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
            entries.Add(new TfvcAddEntry(path, true, Directory.GetLastWriteTime(path), null));
        }

        string? collectionUrl = _session.Settings.CollectionUrl;
        foreach (string path in files)
        {
            if (collectionUrl is not null && _workspace.TryGetServerPath(path) is { } serverPath
                && await _session.Server.GetItemAsync(collectionUrl, serverPath, cancellationToken) is not null)
            {
                continue;
            }

            FileInfo info = new(path);
            entries.Add(new TfvcAddEntry(path, false, info.LastWriteTime, info.Length));
        }

        return entries;
    }
}
