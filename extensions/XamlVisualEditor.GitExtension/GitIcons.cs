namespace XamlVisualEditor.GitExtension;

/// <summary>
/// Fluent System Icons (20, regular) of the Git panel buttons. Path data is copied verbatim from @fluentui/svg-icons;
/// refresh, add and undo are the same geometries as in the animation editor toolbar.
/// </summary>
public static class GitIcons
{
    /// <summary>arrow_sync_20_regular</summary>
    public const string Refresh = "M11.41 3.64a.5.5 0 0 0 0-.71L9.3.8a.5.5 0 0 0-.7.7l1 1a7.5 7.5 0 0 0-4.08 13.5.5.5 0 0 0 .6-.8A6.5 6.5 0 0 1 10.14 3.5L8.59 5.04a.5.5 0 0 0 .7.7zM8.6 16.36a.5.5 0 0 0 0 .71l2.12 2.12a.5.5 0 0 0 .7-.7l-1-1a7.5 7.5 0 0 0 4.07-13.5.5.5 0 1 0-.59.8q.37.28.7.61a6.5 6.5 0 0 1-4.73 11.1l1.55-1.55a.5.5 0 1 0-.7-.7z";

    /// <summary>add_20_regular</summary>
    public const string Stage = "M10 2.5c.28 0 .5.22.5.5v6.5H17a.5.5 0 0 1 0 1h-6.5V17a.5.5 0 0 1-1 0v-6.5H3a.5.5 0 0 1 0-1h6.5V3c0-.28.22-.5.5-.5";

    /// <summary>subtract_20_regular</summary>
    public const string Unstage = "M3 10c0-.28.22-.5.5-.5h13a.5.5 0 0 1 0 1h-13A.5.5 0 0 1 3 10";

    /// <summary>arrow_undo_20_regular</summary>
    public const string Discard = "M5 2.5a.5.5 0 0 0-1 0v4.9c0 .33.27.6.6.6h4.9a.5.5 0 0 0 0-1H5.9l3.48-3.02a4 4 0 0 1 5.25 6.04l-8.17 7.1a.5.5 0 0 0 .65.76l8.17-7.1a5 5 0 0 0-6.56-7.55L5 6.46z";

    /// <summary>add_square_multiple_20_regular</summary>
    public const string StageAll = "M16 5.27V13a3 3 0 0 1-3 3H5.27c.34.6.99 1 1.73 1h6a4 4 0 0 0 4-4V7a2 2 0 0 0-1-1.73M11.5 9.5a.5.5 0 0 0 0-1h-2v-2a.5.5 0 1 0-1 0v2h-2a.5.5 0 0 0 0 1h2v2a.5.5 0 0 0 1 0v-2zM13 3a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5c0-1.1.9-2 2-2zm1 2a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1z";

    /// <summary>subtract_square_multiple_20_regular</summary>
    public const string UnstageAll = "M16 5.27V13a3 3 0 0 1-3 3H5.27c.34.6.99 1 1.73 1h6a4 4 0 0 0 4-4V7a2 2 0 0 0-1-1.73M11.5 9.5a.5.5 0 0 0 0-1h-5a.5.5 0 1 0 0 1zM13 3a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5c0-1.1.9-2 2-2zm1 2a1 1 0 0 0-1-1H5a1 1 0 0 0-1 1v8a1 1 0 0 0 1 1h8a1 1 0 0 0 1-1z";

    /// <summary>checkmark_20_regular</summary>
    public const string Commit = "M3.37 10.17a.5.5 0 0 0-.74.66l4 4.5c.19.22.52.23.72.02l10.5-10.5a.5.5 0 0 0-.7-.7L7.02 14.27z";

    /// <summary>eye_20_regular</summary>
    public const string WatchWorkingFolder = "M3.26 11.6A7 7 0 0 1 10 6c3.2 0 6.06 2.33 6.74 5.6a.5.5 0 0 0 .98-.2A8 8 0 0 0 10 5a8 8 0 0 0-7.72 6.4.5.5 0 0 0 .98.2M10 8a3.5 3.5 0 1 0 0 7 3.5 3.5 0 0 0 0-7m-2.5 3.5a2.5 2.5 0 1 1 5 0 2.5 2.5 0 0 1-5 0";

    /// <summary>panel_right_20_regular</summary>
    public const string ToggleDiff = "M18 6a3 3 0 0 0-3-3H5a3 3 0 0 0-3 3v7a3 3 0 0 0 3 3h10a3 3 0 0 0 3-3zm-6.5-2v11H5a2 2 0 0 1-2-2V6c0-1.1.9-2 2-2zm1 0H15a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-2.5z";
}
