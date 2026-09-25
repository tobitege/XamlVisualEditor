using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using XamlVisualEditor.Extensions;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.Services;

/// <summary>
/// Finds the local changes made while TFVC was offline and pends the ones the user selects.
/// </summary>
/// <remarks>
/// <c>tf reconcile /promote /preview</c> lists added, deleted and changed files without a pending change; a changed
/// versioned file appears like an added one. A missing file is a delete, and a file that exists on the server is an
/// edit; a file made writable offline counts as versioned only when the server cannot be asked.
/// </remarks>
public sealed class TfvcOfflineReconciler
{
    private readonly TfvcSession _session;
    private readonly IWorkspaceInfo _workspaceInfo;
    private readonly IExtensionLogger _logger;

    /// <summary>Creates the reconciler.</summary>
    public TfvcOfflineReconciler(TfvcSession session, IWorkspaceInfo workspaceInfo, IExtensionLogger logger)
    {
        _session = session;
        _workspaceInfo = workspaceInfo;
        _logger = logger;
    }

    /// <summary>
    /// Lists the local changes of the selected workspace: below the folder of the editor workspace when it is mapped,
    /// and every file made writable offline.
    /// </summary>
    public async Task<IReadOnlyList<TfvcOfflineChange>> DetectAsync(CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        string? collectionUrl = _session.Settings.CollectionUrl;
        if (workspace is null || string.IsNullOrWhiteSpace(collectionUrl))
        {
            return Array.Empty<TfvcOfflineChange>();
        }

        HashSet<string> candidates = new(StringComparer.OrdinalIgnoreCase);
        string? scope = GetScopeFolder();
        if (scope is not null)
        {
            candidates.UnionWith(await _session.Client.PreviewLocalChangesAsync(workspace, scope, cancellationToken));
        }

        foreach (string path in _session.OfflineEdits)
        {
            if (workspace.ContainsLocalPath(path))
            {
                candidates.Add(path);
            }
        }

        List<TfvcOfflineChange> changes = new();
        foreach (string path in candidates)
        {
            if (Directory.Exists(path) || _session.FindPendingChangeByLocalPath(path) is not null)
            {
                continue;
            }

            if (!File.Exists(path))
            {
                changes.Add(new TfvcOfflineChange(path, TfvcOfflineChangeKind.Delete));
                continue;
            }

            bool isVersioned = await IsVersionedAsync(workspace, collectionUrl, path, cancellationToken);
            changes.Add(new TfvcOfflineChange(path, isVersioned ? TfvcOfflineChangeKind.Edit : TfvcOfflineChangeKind.Add));
        }

        changes.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.LocalPath, right.LocalPath));
        _logger.Info("Local changes below " + (scope ?? "no editor workspace") + ": " + candidates.Count + " candidate(s), "
            + changes.Count + " change(s) without pending change.");
        return changes;
    }

    /// <summary>Pends the selected changes in the selected workspace; checked-out files lose their offline edit record.</summary>
    public async Task ApplyAsync(IReadOnlyList<TfvcOfflineChange> changes, CancellationToken cancellationToken)
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        if (workspace is null || changes.Count == 0)
        {
            return;
        }

        List<string> edits = new();
        List<string> adds = new();
        List<string> deletes = new();
        foreach (TfvcOfflineChange change in changes)
        {
            (change.Kind switch
            {
                TfvcOfflineChangeKind.Edit => edits,
                TfvcOfflineChangeKind.Add => adds,
                _ => deletes
            }).Add(change.LocalPath);
        }

        try
        {
            if (edits.Count > 0)
            {
                await _session.Client.CheckOutAsync(workspace, edits, recursive: false, cancellationToken);
                await _session.ForgetOfflineEditsAsync(edits, cancellationToken);
            }

            if (adds.Count > 0)
            {
                await _session.Client.AddAsync(workspace, adds, cancellationToken);
            }

            if (deletes.Count > 0)
            {
                await _session.Client.DeleteAsync(workspace, deletes, cancellationToken);
            }
        }
        finally
        {
            await _session.RefreshPendingChangesAsync(cancellationToken);
        }
    }

    // The server decides; the offline edit record only counts when the server cannot be asked.
    private async Task<bool> IsVersionedAsync(TfvcWorkspace workspace, string collectionUrl, string path, CancellationToken cancellationToken)
    {
        string? serverPath = workspace.TryGetServerPath(path);
        if (serverPath is null)
        {
            return false;
        }

        try
        {
            return await _session.Server.GetItemAsync(collectionUrl, serverPath, cancellationToken) is not null;
        }
        catch (TfvcServerException ex)
        {
            _logger.Warn("TFVC item lookup failed for " + serverPath + ": " + ex.Message);
            return _session.OfflineEdits.Contains(path);
        }
    }

    // The folder of the editor workspace when the selected TFVC workspace maps it. A mapping root can hold far more
    // than the edited project, so without an editor workspace only the files made writable offline are considered.
    private string? GetScopeFolder()
    {
        TfvcWorkspace? workspace = _session.CurrentWorkspace;
        string? editorPath = _workspaceInfo.WorkspacePath;
        string? editorFolder = editorPath is null ? null : Directory.Exists(editorPath) ? editorPath : Path.GetDirectoryName(editorPath);
        return workspace is not null && editorFolder is not null && Directory.Exists(editorFolder) && workspace.ContainsLocalPath(editorFolder)
            ? editorFolder
            : null;
    }
}
