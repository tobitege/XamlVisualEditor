namespace XamlVisualEditor.GitExtension;

/// <summary>
/// Fluent System Icons (20, regular) of the Git panel buttons. Path data is copied verbatim from @fluentui/svg-icons;
/// refresh, add and undo are the same geometries as in the animation editor toolbar. <see cref="WholeFile"/> is a
/// Material icon, and <see cref="Rename"/>, <see cref="RemoteUrl"/>, <see cref="Skip"/> and <see cref="CherryPick"/> are
/// drawn for the Git panels.
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

    /// <summary>history_20_regular, path data of the Fluent UI System Icons asset SVG; shows a history.</summary>
    public const string History = "M10 4C13.3137 4 16 6.68629 16 10C16 13.3137 13.3137 16 10 16C6.68629 16 4 13.3137 4 10C4 9.84443 4.00591 9.69034 4.0175 9.53793C4.03845 9.26258 3.83222 9.02239 3.55687 9.00144C3.28152 8.98049 3.04133 9.18673 3.02038 9.46207C3.00687 9.6397 3 9.8191 3 10C3 13.866 6.13401 17 10 17C13.866 17 17 13.866 17 10C17 6.13401 13.866 3 10 3C8.04094 3 6.27012 3.80499 5 5.10109V3.5C5 3.22386 4.77614 3 4.5 3C4.22386 3 4 3.22386 4 3.5V6.5C4 6.77614 4.22386 7 4.5 7H7.5C7.77614 7 8 6.77614 8 6.5C8 6.22386 7.77614 6 7.5 6H5.52772C6.62683 4.77191 8.2234 4 10 4ZM10 6.5C10 6.22386 9.77614 6 9.5 6C9.22386 6 9 6.22386 9 6.5V10.5C9 10.7761 9.22386 11 9.5 11H12.5C12.7761 11 13 10.7761 13 10.5C13 10.2239 12.7761 10 12.5 10H10V6.5Z";

    /// <summary>search_20_regular, path data of the Fluent UI System Icons asset SVG; searches the commit messages.</summary>
    public const string Search = "M13.7291 14.4362C12.5924 15.411 11.115 16 9.5 16C5.91015 16 3 13.0899 3 9.5C3 5.91015 5.91015 3 9.5 3C13.0899 3 16 5.91015 16 9.5C16 11.115 15.411 12.5924 14.4361 13.7292L17.8535 17.1465C18.0487 17.3417 18.0487 17.6583 17.8535 17.8536C17.6799 18.0271 17.4105 18.0464 17.2156 17.9114L17.1464 17.8536L13.7291 14.4362ZM13.0196 13.7266C13.276 13.5128 13.5127 13.2761 13.7265 13.0197C14.5216 12.0659 15 10.8388 15 9.5C15 6.46243 12.5376 4 9.5 4C6.46243 4 4 6.46243 4 9.5C4 12.5376 6.46243 15 9.5 15C10.8388 15 12.0658 14.5217 13.0196 13.7266Z";

    /// <summary>filter_dismiss_20_regular, path data of the Fluent UI System Icons asset SVG; clears the filters.</summary>
    public const string ClearFilter = "M12.5 15C12.7761 15 13 15.2239 13 15.5C13 15.7455 12.8231 15.9499 12.5898 15.9922L12.5 16H7.5C7.22386 16 7 15.7761 7 15.5C7 15.2545 7.17691 15.0501 7.41016 15.0078L7.5 15H12.5ZM14.5 11C14.7761 11 15 11.2239 15 11.5C15 11.7455 14.8231 11.9499 14.5898 11.9922L14.5 12H5.5C5.22386 12 5 11.7761 5 11.5C5 11.2545 5.17691 11.0501 5.41016 11.0078L5.5 11H14.5ZM14.5 1C16.9853 1 19 3.01472 19 5.5C19 7.98528 16.9853 10 14.5 10C12.0147 10 10 7.98528 10 5.5C10 3.01472 12.0147 1 14.5 1ZM9.20703 7C9.30563 7.34863 9.43768 7.68322 9.59961 8H3.5C3.22386 8 3 7.77614 3 7.5C3 7.25454 3.17691 7.05015 3.41016 7.00781L3.5 7H9.20703ZM16.3535 3.64648C16.1583 3.45122 15.8417 3.45122 15.6465 3.64648L14.5 4.79297L13.3535 3.64648C13.1583 3.45122 12.8417 3.45122 12.6465 3.64648C12.4512 3.84175 12.4512 4.15825 12.6465 4.35352L13.793 5.5L12.6465 6.64648C12.4512 6.84175 12.4512 7.15825 12.6465 7.35352C12.8417 7.54878 13.1583 7.54878 13.3535 7.35352L14.5 6.20703L15.6465 7.35352C15.8417 7.54878 16.1583 7.54878 16.3535 7.35352C16.5488 7.15825 16.5488 6.84175 16.3535 6.64648L15.207 5.5L16.3535 4.35352C16.5488 4.15825 16.5488 3.84175 16.3535 3.64648Z";

    /// <summary>dismiss_20_regular, path data of the Fluent UI System Icons asset SVG; returns to the whole repository.</summary>
    public const string ClearScope = "M4.08859 4.21569L4.14645 4.14645C4.32001 3.97288 4.58944 3.9536 4.78431 4.08859L4.85355 4.14645L10 9.293L15.1464 4.14645C15.32 3.97288 15.5894 3.9536 15.7843 4.08859L15.8536 4.14645C16.0271 4.32001 16.0464 4.58944 15.9114 4.78431L15.8536 4.85355L10.707 10L15.8536 15.1464C16.0271 15.32 16.0464 15.5894 15.9114 15.7843L15.8536 15.8536C15.68 16.0271 15.4106 16.0464 15.2157 15.9114L15.1464 15.8536L10 10.707L4.85355 15.8536C4.67999 16.0271 4.41056 16.0464 4.21569 15.9114L4.14645 15.8536C3.97288 15.68 3.9536 15.4106 4.08859 15.2157L4.14645 15.1464L9.293 10L4.14645 4.85355C3.97288 4.67999 3.9536 4.41056 4.08859 4.21569L4.14645 4.14645L4.08859 4.21569Z";

    /// <summary>arrow_download_20_regular, the same geometry as <see cref="Pull"/>; loads the next commits.</summary>
    public const string LoadMore = Pull;

    /// <summary>chevron_up_20_regular, path data of the Fluent UI System Icons asset SVG; goes to the previous hunk.</summary>
    public const string PreviousHunk = "M4.14708 12.3534C3.95147 12.1585 3.9509 11.8419 4.14582 11.6463L9.6108 6.16178C9.82574 5.94607 10.1751 5.94607 10.39 6.16178L15.855 11.6463C16.0499 11.8419 16.0493 12.1585 15.8537 12.3534C15.6581 12.5483 15.3415 12.5477 15.1466 12.3521L10.0004 7.18753L4.85418 12.3521C4.65927 12.5477 4.34269 12.5483 4.14708 12.3534Z";

    /// <summary>chevron_down_20_regular, path data of the Fluent UI System Icons asset SVG; goes to the next hunk.</summary>
    public const string NextHunk = "M15.8537 7.64582C16.0493 7.84073 16.0499 8.15731 15.855 8.35292L10.39 13.8374C10.1751 14.0531 9.82574 14.0531 9.6108 13.8374L4.14582 8.35292C3.9509 8.15731 3.95147 7.84073 4.14708 7.64582C4.34269 7.4509 4.65927 7.45147 4.85418 7.64708L10.0004 12.8117L15.1466 7.64708C15.3415 7.45147 15.6581 7.4509 15.8537 7.64582Z";

    /// <summary>
    /// unfold_more of Google Material Icons (Apache 2.0, see THIRD-PARTY-NOTICES.md); shows the whole file instead of
    /// the changes.
    /// </summary>
    public const string WholeFile = "M12 5.83L15.17 9l1.41-1.41L12 3 7.41 7.59 8.83 9 12 5.83zm0 12.34L8.83 15l-1.41 1.41L12 21l4.59-4.59L15.17 15 12 18.17z";

    /// <summary>copy_20_regular, the same geometry as the copy button of the animation editor.</summary>
    public const string Copy = "M7 7H5.5C4.67 7 4 7.67 4 8.5v6c0 .83.67 1.5 1.5 1.5h4c.65 0 1.2-.42 1.41-1h1.04a2.5 2.5 0 0 1-2.45 2h-4A2.5 2.5 0 0 1 3 14.5v-6A2.5 2.5 0 0 1 5.5 6H7zm7.5-4A2.5 2.5 0 0 1 17 5.5v6a2.5 2.5 0 0 1-2.5 2.5h-4A2.5 2.5 0 0 1 8 11.5v-6A2.5 2.5 0 0 1 10.5 3zm-4 1C9.67 4 9 4.67 9 5.5v6c0 .83.67 1.5 1.5 1.5h4c.83 0 1.5-.67 1.5-1.5v-6c0-.83-.67-1.5-1.5-1.5z";

    /// <summary>add_20_regular, the same geometry as <see cref="Stage"/>; stages the selected diff lines.</summary>
    public const string StageSelection = Stage;

    /// <summary>subtract_20_regular, the same geometry as <see cref="Unstage"/>; unstages the selected diff lines.</summary>
    public const string UnstageSelection = Unstage;

    /// <summary>arrow_undo_20_regular, the same geometry as <see cref="Discard"/>; discards the selected diff lines.</summary>
    public const string DiscardSelection = Discard;

    /// <summary>save_20_regular, the same geometry as the save button of the animation editor; stashes the changes.</summary>
    public const string Stash = "M3 5c0-1.1.9-2 2-2h8.38a2 2 0 0 1 1.41.59l1.62 1.62A2 2 0 0 1 17 6.62V15a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2zm2-1a1 1 0 0 0-1 1v10a1 1 0 0 0 1 1v-4.5c0-.83.67-1.5 1.5-1.5h7c.83 0 1.5.67 1.5 1.5V16a1 1 0 0 0 1-1V6.62a1 1 0 0 0-.3-.7L14.1 4.28a1 1 0 0 0-.71-.29H13v2.5c0 .83-.67 1.5-1.5 1.5h-4A1.5 1.5 0 0 1 6 6.5V4zm2 0v2.5c0 .28.22.5.5.5h4a.5.5 0 0 0 .5-.5V4zm7 12v-4.5a.5.5 0 0 0-.5-.5h-7a.5.5 0 0 0-.5.5V16z";

    /// <summary>
    /// document_add_20_regular, the same geometry as the new resource button of the animation editor; stashes the
    /// changes with the untracked files.
    /// </summary>
    public const string StashIncludeUntracked = "M6 2a2 2 0 0 0-2 2v5.2q.49-.13 1-.18V4a1 1 0 0 1 1-1h4v3.5c0 .83.67 1.5 1.5 1.5H15v8a1 1 0 0 1-1 1h-3.6q-.27.54-.66 1H14a2 2 0 0 0 2-2V7.41c0-.4-.16-.78-.44-1.06l-3.91-3.91A1.5 1.5 0 0 0 10.59 2zm8.8 5h-3.3a.5.5 0 0 1-.5-.5V3.2zM10 14.5a4.5 4.5 0 1 1-9 0 4.5 4.5 0 0 1 9 0m-4-2a.5.5 0 0 0-1 0V14H3.5a.5.5 0 0 0 0 1H5v1.5a.5.5 0 0 0 1 0V15h1.5a.5.5 0 0 0 0-1H6z";

    /// <summary>
    /// clipboard_paste_20_regular, the same geometry as the paste button of the animation editor; applies a stash and
    /// keeps it.
    /// </summary>
    public const string ApplyStash = "M4.5 4h1.59c.2.58.76 1 1.41 1h3c.65 0 1.2-.42 1.41-1h1.59c.28 0 .5.22.5.5v1a.5.5 0 0 0 1 0v-1c0-.83-.67-1.5-1.5-1.5h-1.59c-.2-.58-.76-1-1.41-1h-3c-.65 0-1.2.42-1.41 1H4.5C3.67 3 3 3.67 3 4.5v12c0 .83.67 1.5 1.5 1.5h3a.5.5 0 0 0 0-1h-3a.5.5 0 0 1-.5-.5v-12c0-.28.22-.5.5-.5m3 0a.5.5 0 0 1 0-1h3a.5.5 0 0 1 0 1zm3 3C9.67 7 9 7.67 9 8.5v8c0 .83.67 1.5 1.5 1.5h5c.83 0 1.5-.67 1.5-1.5v-8c0-.83-.67-1.5-1.5-1.5zM10 8.5c0-.28.22-.5.5-.5h5c.28 0 .5.22.5.5v8a.5.5 0 0 1-.5.5h-5a.5.5 0 0 1-.5-.5z";

    /// <summary>The same geometry as <see cref="Push"/>: an arrow up from a bar; applies a stash and removes it.</summary>
    public const string PopStash = Push;

    /// <summary>delete_20_regular, the same geometry as <see cref="DeleteBranch"/>; removes a stash.</summary>
    public const string DropStash = DeleteBranch;

    /// <summary>dismiss_20_regular, the same geometry as <see cref="ClearScope"/>; aborts the operation in progress.</summary>
    public const string Abort = ClearScope;

    /// <summary>checkmark_20_regular, the same geometry as <see cref="Commit"/>; continues the operation in progress.</summary>
    public const string Continue = Commit;

    /// <summary>
    /// Two chevrons to the right, drawn for the Git panels in the 20 by 20 grid of the Fluent icons; skips the current
    /// step of the operation in progress.
    /// </summary>
    public const string Skip =
        "M4.15 4.15a.5.5 0 0 1 .7 0l5.5 5.5a.5.5 0 0 1 0 .7l-5.5 5.5a.5.5 0 0 1-.7-.7L9.29 10 4.15 4.85a.5.5 0 0 1 0-.7zm6 0a.5.5 0 0 1 .7 0l5.5 5.5a.5.5 0 0 1 0 .7l-5.5 5.5a.5.5 0 0 1-.7-.7L15.29 10l-5.14-5.15a.5.5 0 0 1 0-.7z";

    /// <summary>
    /// Two cherries on their stems, drawn for the Git panels in the 20 by 20 grid of the Fluent icons; cherry-picks a
    /// commit.
    /// </summary>
    public const string CherryPick =
        "M6.5 11.5a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5zm0 1a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zm7-2a2.5 2.5 0 1 1 0 5a2.5 2.5 0 1 1 0-5zm0 1a1.5 1.5 0 1 0 0 3a1.5 1.5 0 1 0 0-3zM6.06 11.27l4.5-8.5.88.46-4.5 8.5zm6.96-.67-2.5-7.5.95-.32 2.5 7.5z";

    /// <summary>arrow_undo_20_regular, the same geometry as <see cref="Discard"/>; reverts a commit.</summary>
    public const string Revert = Discard;

    /// <summary>eye_20_regular, the same geometry as <see cref="WatchWorkingFolder"/>; opens a conflicted file.</summary>
    public const string OpenFile = WatchWorkingFolder;

    /// <summary>chevron_up_20_regular, the same geometry as <see cref="PreviousHunk"/>: the upper side of a conflict block.</summary>
    public const string AcceptOurs = PreviousHunk;

    /// <summary>chevron_down_20_regular, the same geometry as <see cref="NextHunk"/>: the lower side of a conflict block.</summary>
    public const string AcceptTheirs = NextHunk;

    /// <summary>checkmark_20_regular, the same geometry as <see cref="Commit"/>; marks a conflicted file resolved.</summary>
    public const string MarkResolved = Commit;

    /// <summary>
    /// A text cursor, drawn for the Git panels in the 20 by 20 grid of the Fluent icons; renames a branch or a remote.
    /// </summary>
    public const string Rename = "M6.5 3h7a.5.5 0 0 1 0 1h-3v12h3a.5.5 0 0 1 0 1h-7a.5.5 0 0 1 0-1h3V4h-3a.5.5 0 0 1 0-1z";

    /// <summary>
    /// A globe of a ring, a meridian and the equator, drawn for the Git panels in the 20 by 20 grid of the Fluent icons;
    /// changes the URL of a remote.
    /// </summary>
    public const string RemoteUrl =
        "M10 2a8 8 0 1 1 0 16a8 8 0 1 1 0-16zm0 1a7 7 0 1 0 0 14a7 7 0 1 0 0-14zm0 .5a3 6.5 0 1 1 0 13a3 6.5 0 1 1 0-13zm0 1a2 5.5 0 1 0 0 11a2 5.5 0 1 0 0-11zM3 9.5h14v1H3z";

    /// <summary>eye_20_regular, the same geometry as <see cref="WatchWorkingFolder"/>; shows the files of a stash.</summary>
    public const string ShowStash = WatchWorkingFolder;

    /// <summary>add_20_regular, the same geometry as <see cref="CreateBranch"/>; creates a branch from a stash.</summary>
    public const string StashBranch = CreateBranch;

    /// <summary>delete_20_regular, the same geometry as <see cref="DeleteBranch"/>; removes every stash.</summary>
    public const string ClearStashes = DeleteBranch;

    /// <summary>add_20_regular, the same geometry as <see cref="Stage"/>; adds a remote.</summary>
    public const string AddRemote = Stage;

    /// <summary>delete_20_regular, the same geometry as <see cref="DeleteBranch"/>; removes a remote.</summary>
    public const string RemoveRemote = DeleteBranch;

    /// <summary>tag_20_regular, path data of the Fluent UI System Icons asset SVG; marks the Tags tab and a tag.</summary>
    public const string Tag = "M14 7C14.5523 7 15 6.55228 15 6C15 5.44772 14.5523 5 14 5C13.4477 5 13 5.44772 13 6C13 6.55228 13.4477 7 14 7ZM11.1292 2.00004C10.5951 1.99654 10.0818 2.20681 9.7037 2.58399L3.02216 9.24852C2.23941 10.0293 2.2386 11.297 3.02036 12.0788L7.96875 17.0271C8.7498 17.8082 10.0161 17.8082 10.7972 17.0271L17.428 10.3963C17.8038 10.0205 18.0146 9.51063 18.0138 8.97917L18.0065 4.02911C18.0049 2.93081 17.1179 2.0393 16.0196 2.0321L11.1292 2.00004ZM10.4099 3.29199C10.599 3.1034 10.8556 2.99827 11.1227 3.00002L16.013 3.03208C16.5622 3.03568 17.0057 3.48143 17.0065 4.03058L17.0138 8.98065C17.0142 9.24637 16.9088 9.50133 16.7209 9.68923L10.0901 16.32C9.69955 16.7106 9.06638 16.7106 8.67586 16.32L3.72746 11.3716C3.33659 10.9808 3.33699 10.3469 3.72836 9.95653L10.4099 3.29199Z";
}
