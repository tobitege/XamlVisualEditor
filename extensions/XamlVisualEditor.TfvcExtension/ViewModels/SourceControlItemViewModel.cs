using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using ReactiveUI;
using ReactiveUI.SourceGenerators;
using XamlVisualEditor.TfvcExtension.Localization;
using XamlVisualEditor.TfvcExtension.Models;

namespace XamlVisualEditor.TfvcExtension.ViewModels;

/// <summary>A server item of the source control explorer.</summary>
public sealed partial class SourceControlItemViewModel : ReactiveObject
{
    /// <summary>Creates an item node.</summary>
    public SourceControlItemViewModel(TfvcServerItem item, object? icon, bool isPlaceholder = false)
    {
        Item = item;
        Icon = icon;
        IsPlaceholder = isPlaceholder;
        Name = isPlaceholder ? TfvcStrings.Loading : item.Name;
        ChangesetText = item.Version > 0 && !isPlaceholder ? item.Version.ToString(CultureInfo.CurrentCulture) : string.Empty;
        LastCheckInText = item.ChangeDate is { } date && !isPlaceholder
            ? date.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : string.Empty;
        HasChildren = item.IsFolder && !isPlaceholder;
    }

    /// <summary>Gets the server item.</summary>
    public TfvcServerItem Item { get; }

    /// <summary>Gets the server path.</summary>
    public string ServerPath => Item.ServerPath;

    /// <summary>Gets the displayed name.</summary>
    public string Name { get; }

    /// <summary>Gets the icon.</summary>
    public object? Icon { get; }

    /// <summary>Gets whether this node is the loading placeholder of a folder.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>Gets whether the item is a folder.</summary>
    public bool IsFolder => Item.IsFolder;

    /// <summary>Gets the latest changeset as text.</summary>
    public string ChangesetText { get; }

    /// <summary>Gets the date of the last check-in as text.</summary>
    public string LastCheckInText { get; }

    /// <summary>Gets the child items.</summary>
    public ObservableCollection<SourceControlItemViewModel> Children { get; } = new();

    /// <summary>Gets or sets whether the children have been loaded.</summary>
    public bool IsLoaded { get; set; }

    /// <summary>Gets or sets whether the node is expanded.</summary>
    [Reactive]
    public partial bool IsExpanded { get; set; }

    /// <summary>Gets or sets whether the node can have children.</summary>
    [Reactive]
    public partial bool HasChildren { get; set; }

    /// <summary>Gets the mapped local path in the selected workspace.</summary>
    [Reactive]
    public partial string? LocalPath { get; private set; }

    /// <summary>Gets the latest changeset; the sort key of the changeset column.</summary>
    public int Version => Item.Version;

    /// <summary>Gets the date of the last check-in; the sort key of the check-in column.</summary>
    public DateTimeOffset? LastCheckIn => Item.ChangeDate;

    /// <summary>Gets the size of the local file in bytes; the sort key of the size column.</summary>
    [Reactive]
    public partial long? LocalSizeBytes { get; private set; }

    /// <summary>Gets the last write time of the local item; the sort key of the modified column.</summary>
    [Reactive]
    public partial DateTime? LocalModified { get; private set; }

    /// <summary>Gets the size of the local file.</summary>
    [Reactive]
    public partial string LocalSizeText { get; private set; } = string.Empty;

    /// <summary>Gets the last write time of the local item, or its mapping state when it is not on disk.</summary>
    [Reactive]
    public partial string LocalModifiedText { get; private set; } = string.Empty;

    /// <summary>Gets the tooltip of the local columns.</summary>
    [Reactive]
    public partial string? LocalToolTip { get; private set; }

    /// <summary>Gets whether the mapped local item exists.</summary>
    [Reactive]
    public partial bool IsDownloaded { get; private set; }

    /// <summary>Gets the displayed pending change.</summary>
    [Reactive]
    public partial string PendingChangeText { get; private set; } = string.Empty;

    /// <summary>Updates the workspace dependent columns.</summary>
    public void UpdateWorkspaceState(TfvcWorkspace? workspace, TfvcPendingChange? pendingChange, string pendingChangeText)
    {
        if (IsPlaceholder)
        {
            return;
        }

        string? localPath = workspace?.TryGetLocalPath(ServerPath);
        LocalPath = localPath;
        LocalToolTip = localPath;
        FileSystemInfo? info = localPath is null ? null : IsFolder ? new DirectoryInfo(localPath) : new FileInfo(localPath);
        IsDownloaded = info?.Exists == true;
        if (info is null || !info.Exists)
        {
            LocalSizeBytes = null;
            LocalModified = null;
            LocalSizeText = string.Empty;
            LocalModifiedText = localPath is null ? TfvcStrings.NotMapped : TfvcStrings.NotDownloaded;
        }
        else
        {
            LocalSizeBytes = info is FileInfo file ? file.Length : null;
            LocalModified = info.LastWriteTime;
            LocalSizeText = LocalSizeBytes is { } bytes ? FormatSize(bytes) : string.Empty;
            LocalModifiedText = info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
        }

        PendingChangeText = pendingChange is null ? string.Empty : pendingChangeText;
    }

    /// <summary>Formats a file size with a binary unit, for example "12.3 KB".</summary>
    public static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? bytes.ToString("N0", CultureInfo.CurrentCulture) + " " + units[0]
            : value.ToString("0.0", CultureInfo.CurrentCulture) + " " + units[unit];
    }
}
