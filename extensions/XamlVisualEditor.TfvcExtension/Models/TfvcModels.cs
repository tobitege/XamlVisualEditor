using System;
using System.Collections.Generic;
using System.IO;

namespace XamlVisualEditor.TfvcExtension.Models;

/// <summary>User configuration of the TFVC connection.</summary>
/// <param name="CollectionUrl">Project collection URL, for example <c>https://server/tfs/DefaultCollection</c>.</param>
/// <param name="TfExePath">Optional explicit tf.exe path; null selects automatic detection.</param>
public sealed record TfvcSettings(string? CollectionUrl, string? TfExePath)
{
    /// <summary>Gets settings without any configured value.</summary>
    public static TfvcSettings Empty { get; } = new(null, null);

    /// <summary>Gets whether a collection URL is configured.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(CollectionUrl);
}

/// <summary>Result of one tf.exe invocation.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="StandardOutput">Captured standard output.</param>
/// <param name="StandardError">Captured standard error.</param>
public sealed record TfCommandResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Gets whether tf.exe reported success.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>Gets standard output and standard error as one message.</summary>
    public string CombinedOutput
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StandardError))
            {
                return StandardOutput.Trim();
            }

            if (string.IsNullOrWhiteSpace(StandardOutput))
            {
                return StandardError.Trim();
            }

            return StandardOutput.Trim() + Environment.NewLine + StandardError.Trim();
        }
    }
}

/// <summary>A working folder mapping of a TFVC workspace.</summary>
/// <param name="ServerPath">Server path such as <c>$/Project/Folder</c>.</param>
/// <param name="LocalPath">Mapped local directory; empty for cloaked folders.</param>
/// <param name="IsCloaked">True when the server path is cloaked.</param>
public sealed record TfvcWorkingFolder(string ServerPath, string LocalPath, bool IsCloaked);

/// <summary>A TFVC workspace of the current user.</summary>
public sealed class TfvcWorkspace
{
    /// <summary>Creates a workspace description.</summary>
    public TfvcWorkspace(string name, string? ownerDisplayName, string? computer, IReadOnlyList<TfvcWorkingFolder> folders)
    {
        Name = name;
        OwnerDisplayName = ownerDisplayName;
        Computer = computer;
        Folders = folders;
    }

    /// <summary>Gets the workspace name.</summary>
    public string Name { get; }

    /// <summary>Gets the display name of the owner.</summary>
    public string? OwnerDisplayName { get; }

    /// <summary>Gets the computer that hosts the workspace.</summary>
    public string? Computer { get; }

    /// <summary>Gets the working folder mappings.</summary>
    public IReadOnlyList<TfvcWorkingFolder> Folders { get; }

    /// <summary>Gets the display text used in workspace selectors.</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(OwnerDisplayName) ? Name : Name + " (" + OwnerDisplayName + ")";

    /// <summary>Gets the first mapped local root that exists on disk.</summary>
    public string? GetWorkingDirectory()
    {
        foreach (TfvcWorkingFolder folder in Folders)
        {
            if (!folder.IsCloaked && Directory.Exists(folder.LocalPath))
            {
                return folder.LocalPath;
            }
        }

        return null;
    }

    /// <summary>Maps a local path to its server path; returns null for unmapped or cloaked paths.</summary>
    public string? TryGetServerPath(string localPath)
    {
        string normalized = NormalizeLocalPath(localPath);
        TfvcWorkingFolder? best = null;
        foreach (TfvcWorkingFolder folder in Folders)
        {
            if (folder.IsCloaked || string.IsNullOrEmpty(folder.LocalPath))
            {
                continue;
            }

            string root = NormalizeLocalPath(folder.LocalPath);
            if (IsSameOrChildLocalPath(normalized, root) && (best is null || root.Length > NormalizeLocalPath(best.LocalPath).Length))
            {
                best = folder;
            }
        }

        if (best is null)
        {
            return null;
        }

        string bestRoot = NormalizeLocalPath(best.LocalPath);
        string relative = normalized.Substring(bestRoot.Length).TrimStart(Path.DirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/');
        string serverPath = relative.Length == 0 ? best.ServerPath : CombineServerPath(best.ServerPath, relative);
        return IsCloakedServerPath(serverPath) ? null : serverPath;
    }

    /// <summary>Maps a server path to its local path; returns null for unmapped or cloaked paths.</summary>
    public string? TryGetLocalPath(string serverPath)
    {
        string normalized = NormalizeServerPath(serverPath);
        TfvcWorkingFolder? best = null;
        foreach (TfvcWorkingFolder folder in Folders)
        {
            string root = NormalizeServerPath(folder.ServerPath);
            if (IsSameOrChildServerPath(normalized, root) && (best is null || root.Length > NormalizeServerPath(best.ServerPath).Length))
            {
                best = folder;
            }
        }

        if (best is null || best.IsCloaked || string.IsNullOrEmpty(best.LocalPath))
        {
            return null;
        }

        string relative = normalized.Substring(NormalizeServerPath(best.ServerPath).Length).TrimStart('/');
        if (relative.Length == 0)
        {
            return NormalizeLocalPath(best.LocalPath);
        }

        foreach (string segment in relative.Split('/'))
        {
            if (segment == "..")
            {
                return null;
            }
        }

        return Path.Combine(NormalizeLocalPath(best.LocalPath), relative.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Gets whether a local path lies inside a non-cloaked mapping of this workspace.</summary>
    public bool ContainsLocalPath(string localPath) => TryGetServerPath(localPath) is not null;

    /// <summary>Combines a server folder path with a relative path.</summary>
    public static string CombineServerPath(string serverFolder, string relative)
    {
        string folder = NormalizeServerPath(serverFolder);
        return folder == "$/" ? "$/" + relative : folder + "/" + relative;
    }

    /// <summary>Normalizes a server path by removing trailing slashes except for the root.</summary>
    public static string NormalizeServerPath(string serverPath)
    {
        string trimmed = serverPath.Trim().Replace('\\', '/');
        if (trimmed == "$" || trimmed == "$/")
        {
            return "$/";
        }

        return trimmed.TrimEnd('/');
    }

    /// <summary>Normalizes a local path to a full path without a trailing separator.</summary>
    public static string NormalizeLocalPath(string localPath)
    {
        string full = Path.GetFullPath(localPath);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Gets whether <paramref name="path"/> equals or lies below <paramref name="root"/> (server paths).</summary>
    public static bool IsSameOrChildServerPath(string path, string root)
    {
        if (root == "$/")
        {
            return path.StartsWith("$/", StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(path, root, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsCloakedServerPath(string serverPath)
    {
        string normalized = NormalizeServerPath(serverPath);
        TfvcWorkingFolder? best = null;
        foreach (TfvcWorkingFolder folder in Folders)
        {
            string root = NormalizeServerPath(folder.ServerPath);
            if (IsSameOrChildServerPath(normalized, root) && (best is null || root.Length > NormalizeServerPath(best.ServerPath).Length))
            {
                best = folder;
            }
        }

        return best?.IsCloaked == true;
    }

    private static bool IsSameOrChildLocalPath(string path, string root)
    {
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>A pending change of a TFVC workspace.</summary>
/// <param name="ServerPath">Server path of the item.</param>
/// <param name="LocalPath">Local path of the item, when mapped.</param>
/// <param name="ChangeTypes">Change type names as reported by tf.exe, for example <c>Edit</c> or <c>Add</c>.</param>
/// <param name="IsFolder">True for folder items.</param>
/// <param name="Version">Workspace version of the item; 0 for new items.</param>
/// <param name="LockLevel">Lock level, when the change holds a lock.</param>
/// <param name="SourceServerPath">Source server path of a rename.</param>
public sealed record TfvcPendingChange(
    string ServerPath,
    string? LocalPath,
    IReadOnlyList<string> ChangeTypes,
    bool IsFolder,
    int Version,
    string? LockLevel,
    string? SourceServerPath)
{
    /// <summary>Gets whether the change contains the given change type.</summary>
    public bool HasChangeType(string changeType)
    {
        foreach (string type in ChangeTypes)
        {
            if (string.Equals(type, changeType, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>A version-controlled item on the server.</summary>
/// <param name="ServerPath">Server path of the item.</param>
/// <param name="IsFolder">True for folders.</param>
/// <param name="Version">Changeset of the latest version.</param>
/// <param name="ChangeDate">Date of the latest check-in.</param>
public sealed record TfvcServerItem(string ServerPath, bool IsFolder, int Version, DateTimeOffset? ChangeDate)
{
    /// <summary>Gets the last path segment.</summary>
    public string Name
    {
        get
        {
            if (ServerPath == "$/")
            {
                return "$/";
            }

            int index = ServerPath.LastIndexOf('/');
            return index >= 0 ? ServerPath.Substring(index + 1) : ServerPath;
        }
    }
}

/// <summary>A changeset of the history of an item.</summary>
/// <param name="Id">Changeset number.</param>
/// <param name="Author">Display name of the author.</param>
/// <param name="CreatedDate">Check-in date.</param>
/// <param name="Comment">Check-in comment.</param>
/// <param name="AuthorId">Identity ID of the author.</param>
/// <param name="AuthorUniqueName">Account name of the author, for example <c>DOMAIN\user</c>.</param>
public sealed record TfvcChangesetInfo(
    int Id,
    string? Author,
    DateTimeOffset? CreatedDate,
    string? Comment,
    string? AuthorId = null,
    string? AuthorUniqueName = null);

/// <summary>A changed item of a changeset.</summary>
/// <param name="ServerPath">Server path of the item.</param>
/// <param name="ChangeType">Change type list as reported by the server, for example <c>edit, encoding</c>.</param>
public sealed record TfvcChangesetChange(string ServerPath, string ChangeType);

/// <summary>A label that contains an item.</summary>
/// <param name="Name">Label name.</param>
/// <param name="Owner">Display name of the owner.</param>
/// <param name="ModifiedDate">Date of the last change of the label.</param>
/// <param name="Description">Label comment.</param>
/// <param name="Scope">Server path the label is scoped to.</param>
public sealed record TfvcLabelInfo(string Name, string? Owner, DateTimeOffset? ModifiedDate, string? Description, string? Scope);

/// <summary>Summary of a work item.</summary>
/// <param name="Id">Work item ID.</param>
/// <param name="WorkItemType">Work item type name.</param>
/// <param name="Title">Work item title.</param>
/// <param name="State">Work item state.</param>
public sealed record TfvcWorkItemInfo(int Id, string? WorkItemType, string? Title, string? State);

/// <summary>Version control state of a local file.</summary>
public enum TfvcFileStateKind
{
    /// <summary>The state has not been determined yet.</summary>
    Unknown,

    /// <summary>The file is outside every mapping of the known workspaces.</summary>
    NotInWorkspace,

    /// <summary>The file is mapped but does not exist on the server.</summary>
    NotVersioned,

    /// <summary>The file is versioned and has no pending change.</summary>
    CheckedIn,

    /// <summary>The file has a pending change.</summary>
    Pending
}

/// <summary>Version control state of a local file together with its context.</summary>
/// <param name="Kind">State kind.</param>
/// <param name="Workspace">Workspace that maps the file, if any.</param>
/// <param name="ServerPath">Server path of the file, if mapped.</param>
/// <param name="PendingChange">Pending change of the file, if any.</param>
public sealed record TfvcFileState(TfvcFileStateKind Kind, TfvcWorkspace? Workspace, string? ServerPath, TfvcPendingChange? PendingChange)
{
    /// <summary>Gets an unknown state.</summary>
    public static TfvcFileState Unknown { get; } = new(TfvcFileStateKind.Unknown, null, null, null);

    /// <summary>Gets a state for files outside of all workspaces.</summary>
    public static TfvcFileState NotInWorkspace { get; } = new(TfvcFileStateKind.NotInWorkspace, null, null, null);
}
