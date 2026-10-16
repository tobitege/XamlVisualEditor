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

    /// <summary>
    /// cloud_arrow_down_20_regular, path data of the Fluent UI System Icons asset SVG: a cloud with a download badge,
    /// for fetch, which changes only the remote-tracking branches.
    /// </summary>
    public const string Fetch = "M10 2C12.8166 2 14.4145 3.92329 14.6469 6.24599L14.7179 6.24599C16.5306 6.24599 18 7.75792 18 9.62299C18 9.71829 17.9962 9.81267 17.9886 9.90598C17.6666 9.50435 17.2919 9.14686 16.8748 8.84404C16.5644 7.91106 15.7121 7.24599 14.718 7.24599L14.6469 7.24599C14.1332 7.24601 13.703 6.85673 13.6518 6.34554C13.4497 4.32493 12.1085 3 10 3C7.88606 3 6.55108 4.31588 6.34818 6.34547C6.29707 6.85669 5.86688 7.24601 5.3531 7.24599L5.28207 7.24599C4.02819 7.24599 3 8.30392 3 9.62299C3 10.9421 4.02819 12 5.28205 12H7.94761C7.86058 12.323 7.80097 12.6572 7.77144 13H5.28205C3.46942 13 2 11.4881 2 9.62299C2 7.82009 3.3731 6.34717 5.10199 6.25098L5.35314 6.24599C5.58687 3.90802 7.18335 2 10 2ZM13.5 18C11.0147 18 9 15.9853 9 13.5C9 11.0147 11.0147 9 13.5 9C15.9853 9 18 11.0147 18 13.5C18 15.9853 15.9853 18 13.5 18ZM15.1023 13.1023L14 14.2045V11.5C14 11.2239 13.7761 11 13.5 11C13.2239 11 13 11.2239 13 11.5V14.2045L11.8977 13.1023C11.6781 12.8826 11.3219 12.8826 11.1023 13.1023C10.8826 13.3219 10.8826 13.6781 11.1023 13.8977L13.1023 15.8977C13.3219 16.1174 13.6781 16.1174 13.8977 15.8977L15.8977 13.8977C16.1174 13.6781 16.1174 13.3219 15.8977 13.1023C15.6781 12.8826 15.3219 12.8826 15.1023 13.1023Z";

    /// <summary>
    /// arrow_download_20_regular, the same geometry as the load button of the animation editor: an arrow down to a
    /// bar, for pull, which brings the commits into the working tree.
    /// </summary>
    public const string Pull = "M15.5 17a.5.5 0 0 1 .09 1H4.5a.5.5 0 0 1-.09-1H15.5M10 2a.5.5 0 0 1 .5.41V14.3l3.64-3.65a.5.5 0 0 1 .64-.06l.07.06c.17.17.2.44.06.63l-.06.07-4.5 4.5a.5.5 0 0 1-.25.14L10 16a.5.5 0 0 1-.4-.2l-4.46-4.45a.5.5 0 0 1 .64-.76l.07.06 3.65 3.64V2.5c0-.27.22-.5.5-.5";

    /// <summary>arrow_download_20_regular mirrored at the horizontal center line: an arrow up to a bar.</summary>
    public const string Push = "M15.5 3A0.5 0.5 0 0 0 15.59 2L4.5 2A0.5 0.5 0 0 0 4.41 3L15.5 3M10 18A0.5 0.5 0 0 0 10.5 17.59L10.5 5.7L14.14 9.35A0.5 0.5 0 0 0 14.78 9.41L14.85 9.35C15.02 9.18 15.05 8.91 14.91 8.72L14.85 8.65L10.35 4.15A0.5 0.5 0 0 0 10.1 4.01L10 4A0.5 0.5 0 0 0 9.6 4.2L5.14 8.65A0.5 0.5 0 0 0 5.78 9.41L5.85 9.35L9.5 5.71L9.5 17.5C9.5 17.77 9.72 18 10 18";

    /// <summary>arrow_bidirectional_left_right_20_regular turned by 90 degrees: arrows up and down.</summary>
    public const string Sync = "M13.85 7.35C13.65 7.55 13.35 7.55 13.15 7.35L10.5 4.71L10.5 15.29L13.15 12.65A0.5 0.5 90 0 1 13.85 13.35L10.35 16.85C10.15 17.05 9.85 17.05 9.65 16.85L6.15 13.35A0.5 0.5 90 0 1 6.85 12.65L9.5 15.29L9.5 4.71L6.85 7.35A0.5 0.5 90 0 1 6.15 6.65L9.65 3.15A0.5 0.5 90 0 1 10.35 3.15L13.85 6.65C14.05 6.85 14.05 7.15 13.85 7.35";

    /// <summary>add_20_regular</summary>
    public const string CreateBranch = Stage;

    /// <summary>delete_20_regular, the same geometry as the delete button of the animation editor.</summary>
    public const string DeleteBranch = "M8.5 4h3a1.5 1.5 0 0 0-3 0m-1 0a2.5 2.5 0 0 1 5 0h5a.5.5 0 0 1 0 1h-1.05l-1.2 10.34A3 3 0 0 1 12.27 18H7.73a3 3 0 0 1-2.98-2.66L3.55 5H2.5a.5.5 0 0 1 0-1zM5.74 15.23A2 2 0 0 0 7.73 17h4.54a2 2 0 0 0 1.99-1.77L15.44 5H4.56zM8.5 7.5c.28 0 .5.22.5.5v6a.5.5 0 0 1-1 0V8c0-.28.22-.5.5-.5M12 8a.5.5 0 0 0-1 0v6a.5.5 0 0 0 1 0z";

    /// <summary>checkmark_20_regular; marks the current branch and switches to a branch.</summary>
    public const string CurrentBranch = Commit;
}
