# Changelog

All notable changes to XamlVisualEditor are documented in this file. The project
uses Semantic Versioning.

## [Unreleased]

### Added

- Added a coordinate strip below the design surface and the external previewer. It shows, in logical pixels relative to the root element, the pixel under the pointer (also outside the root within the viewport), the element under the pointer as `Type#Name`, the position and size of the selection with its change while moving or resizing, a distance measured with the new measure tool, and the zoom level. A click copies a value as `x,y`, `x,y,width,height` or `dx,dy`. The rulers mark the pixel under the pointer.
- The viewport toolbar has toggle buttons for the grid, the rulers and the coordinate strip, and the viewport settings set the grid size. The visible grid and snapping use the same size; at small zoom levels the grid thins out its lines. Grid, snapping, grid size, rulers and coordinate strip are stored as `DesignerViewport` in `editor-options.json` and apply to all designer documents.
- The zoom level keeps whole percentages; fitting to the viewport rounds down.
- The context menu of a file document tab offers "Open in File Explorer", "Open in Solution Explorer", "Copy Path" and "Copy File Name", in the tabbed and the MDI layout. The two "Open in" commands show the File Explorer or Solution Explorer panel, expand its tree to the file and select it; the status bar tells when the file is not part of the tree. Extension trees take part through `IExtensionTreeItemPathProvider`, which the File Explorer entries implement.
- Added a save prompt for modified files when a tab, the workspace or the application closes; automatic saving saves without asking.
- Saving a file that changed on disk since the editor read it asks whether to overwrite it with the editor text or to reload it from disk. Declining keeps the editor text, and automatic saves of that document stop asking until it is saved manually.
- Added the option to reopen the last workspace or folder at startup.
- Added the option to list folders before files in the File Explorer (Options, File Explorer; on by default). It also holds when the File Explorer sorts by its column header, in both directions; the setting is stored as `FileExplorerFoldersBeforeFiles`.
- Added the optional TFVC extension (Windows only) with a source control explorer, a pending changes panel, and automatic check-out of versioned files on their first edit. Its Pending Changes, Source Control Explorer and History panels are listed under View › Panels and in the Command Palette. Its messages follow the editor language. It uses tf.exe and the TFVC REST API with the current Windows credentials; the only required setting is the collection URL.
- Added changeset details to the TFVC extension: the history context menu shows the changed files of a changeset in the TFVC Changes panel, where the author can revise and save the check-in comment. The history can be filtered by user and is sorted by changeset, newest first.
- Added an offline mode to the TFVC extension: when the server is unreachable at startup, XVE offers to work offline, removes the read-only attribute of an edited versioned file only after confirmation, and proposes the local changes for pending when going online again until they are applied or declined. The Extensions menu has a TFVC submenu for going offline and online.
- Added "Add Items to Folder..." to the TFVC source control explorer, which adds unversioned files and folders below a mapped folder.
- Added the text editing extension: a context menu for the text and code editors with clipboard, find and replace, go to line and matching bracket, a Format submenu (toggle comment, upper and lower case, indentation, line sorting, enclosing in brackets), an Outlining submenu and a Display submenu with check-marked view toggles and zoom. The Editor Options dialog sets font, display, editing, link and color options with a live preview. The options and the caret position of each file persist across sessions.
- The categories of the Properties panel collapse and expand one by one or all at once, and a filter shows a single category, which stays selected when the selection changes. The option to collapse the other categories (`propertyEditor.collapseOtherCategories`, on by default) starts the panel with only the essential properties and the local values expanded. Flag enumerations are edited with check boxes, numbers and grid lengths with their unit, and a property can be set to null or reset; emptying the field of a value-type property removes its assignment. Property elements, collections, templates and Grid row and column definitions open in a structure editor that adds, moves and removes elements and attributes; the Events section sets, opens and removes event handlers. The local-values filter also keeps the unset essential properties, such as Width and Height, in the grouped and in the flat list, and the flat list starts with the essential properties in the order of their group. Text fields write their value on Enter or when they lose focus instead of on every keystroke.
- Added the optional Flowery.NET theme extension, a separate extension package that offers the Flowery.NET palettes as editor themes and as preview themes; it does not change the editor's application theme and ships its own `Flowery.NET.dll`.
- The Toolbox groups the controls by assembly with its version and lists the controls of the workspace's own projects separately from the standard controls. Library, category and search filters narrow the list. Assemblies can be marked as favorites, which applies to every workspace, and hidden or collapsed, which each workspace remembers. The standard controls are available while the project controls load; that load shows its progress and can be cancelled and retried. Inserting a control needs only the active designer document, also during a load.
- The Command Palette lists every command of the built-in extensions with a title, including the panel toggles of the View menu, the edit and navigation commands and the debugger panels. "Set Startup Project" asks for the project when it runs without one.
- Added the Window menu with the window layout commands, which leave the View menu, and a Manage Layouts dialog that renames and deletes saved layouts.
- Extensions can contribute to the editor context menu (`ExtensionMenuLocations.EditorContext`), group menu items into submenus (`ExtensionMenuContribution.Submenu` and `SubmenuGroup`), show check marks that follow a condition (`ExtensionMenuContribution.CheckedWhen`) over their own context keys (`ExtensionContext.ContextKeys`), set selection and caret in one step (`IEditorDocument.SetSelection`), limit a keybinding to the focused text editor (`CommandMetadata.KeybindingScope`) and set editor view options and actions through `ExtensionContext.TextEditorView`; `textEditor.hasActiveEditor` tells whether the active document shows an editor.
- Read-only files show a banner and a title marker in the editor.
- The status bar shows startup work, extension activation, TFVC initialization and the language-service warmup of a loaded workspace while they run, and shows "Ready" only after the last of them ended. Extensions announce their own background work through `ExtensionContext.BackgroundActivities`.
- Extensions can redirect a file the user opens to another file through `ExtensionContext.DocumentOpenInterceptors`, for example to open a generated file in place of its source; navigation into a file is not redirected. `ConfirmationDialogRequest.CancelText` labels the declining button.
- The XAML code editor marks the element selected in the designer with an arrow at its first line and a bar along its lines in the gutter, and a selection in the designer scrolls the element start away from the viewport edge. The "Follow" toggle in the code editor toolbar turns this off.
- An interceptor cancels the open by throwing `OperationCanceledException`. Extensions show a modal, cancellable progress dialog through `ProgressDialogRequest` while long work runs.
- The Problems panel restates common XAML loader errors of the external previewer in plain words and points to their source position: an invalid value names its attribute and, for an enumeration, the allowed values; an unknown property or element names the misspelled name; text that a panel or a single-child element cannot hold is named and marked; a second child of a single-child element names the property that takes it.
- The Git extension shows the branch of the workspace repository in the status bar, with the commits ahead of and behind its upstream and the number of changed files, for example `main ↑1 ↓2 · 5`; a click opens the Git Changes panel. The panel is listed under View › Panels, Ctrl+Shift+G opens it, and the Extensions › Git submenu and the Command Palette offer refresh, the "Git" output channel, stage all, unstage all, commit and creating a repository. Stage, unstage and discard (after a confirmation) are also in the Git submenu of the editor context menu and work on the active document; the output channel lists every git command the extension runs, with user names and passwords in URLs masked. Extensions can read the context keys `git.available`, `git.isRepository`, `git.hasRemote`, `git.hasUpstream`, `git.isMerging`, `git.hasConflicts` and `git.autoRefresh`.

### Changed

- Tool tabs no longer scroll: all tabs of a dock stay visible, and tabs that do not fit shrink and shorten their titles with an ellipsis.
- The splitter between design surface and XAML code and the splitters between docked panels show a muted accent color at rest and the full accent under the pointer and while dragging; a splitter beside a collapsed dock stays transparent.
- The word wrap and line number toggles and the font size of a document are editor options: a change in one document applies to every editor and persists.
- Loading a layout no longer adds panels on its own: a debugger tool or extension panel that the saved layout does not contain stays closed until the view menu opens it. An extension panel with `PersistDockState = false` therefore starts closed after a restart.
- A tab keeps its view while it is hidden: switching tabs no longer rebuilds panels and editors, so scroll positions, expanded nodes and editor state stay. The view of a closed tab is released.
- The first start and the reset layout command use the Debugging preset instead of a layout with every panel. A layout preset contains only the panels it lists, instead of hiding the others.
- The edit menu separates its groups; the navigation commands follow the editing commands in the group `5.navigation` instead of `navigation`.
- Tests that depend on file-system case sensitivity probe the test directory instead of assuming it from the operating system.
- Fixed the external previewer failing with "Unable to resolve type DesignNodeIdentity" for projects whose output does not contain the editor assemblies: the previewer host now loads the assembly of the design node identity properties from the editor directory before it compiles the XAML.
- Fixed the external previewer reporting "Design tree unavailable: Ambiguous match found" for controls that hide an inherited property with `new`, such as a Padding of their own type: the design tree reader walks the type hierarchy and takes the Avalonia Thickness declaration of Padding.
- A workspace remembers its session independently of the dock layout: opening it again reopens the documents that were open, in their order and including files outside the workspace, activates the document that was active and restores the startup project. The sessions of the 50 most recently used workspaces are kept in `workspace-sessions.json`; closing a workspace keeps its session.
- The Solution Explorer marks only the startup project, like Visual Studio: its name is bold after the ▶ marker, and the "RUN" label of other executable projects is gone. A tooltip on a project node tells whether it is the startup project or an executable project, and "Set as Startup Project" is available for executable projects only. The references folder is the first child of a project, whatever the sort option, and its name follows the editor language.
- Added the option "Show the full file path in the window title" (off by default) under Window in the Options dialog; the window title also follows a "Save As" of the active document. The tooltip of a document tab always shows the full path of its file.
- The Solution Explorer no longer sorts by a click on its column header and always lists folders before files, like Visual Studio. The File Explorer keeps its header sorting.
- Opening a problem from the Problems panel in a XAML code editor marks its location like a designer selection, but in the error color: a red bar and arrow in the gutter along the start tag that contains the diagnostic (or along its line outside a start tag), scrolled into the middle of the editor with the caret at the diagnostic. The marker disappears with the next edit. Extensions open a diagnostic location this way through `IEditorServices.OpenDiagnosticLocationAsync`.
- Fixed the external previewer failing with "Could not load file or assembly 'System.Xaml'" when the previewed project references a .NET Framework library: the previewer host no longer preloads project references whose assembly-level attributes come from an assembly the runtime cannot load, because the XAML compiler reads those attributes of every loaded assembly.
- `DesignPreviewHookRegistry.Register` ignores a hook that is already registered: the hook runs once per preview, the repeated registration's disposal does nothing, and the first registration keeps owning the hook.
- Breaking: `IDesignPreviewHook` no longer takes part in control instantiation. The design surface loads the document with the Avalonia runtime XAML loader and calls `PostProcess(Control root, MutableAstObjectNode rootNode)` once for the rendered root; `TryCreateControl` and the per-node post-processing in `ControlFactory` are gone. `DesignPreviewHookRegistry.Apply` runs the hooks and reports a failing hook without stopping the others.
- Installed extension packages are activated from the installation directory the package store extracts them to. `InstalledExtensionPackages.ResolveMainAssemblyPath` takes the package info, and `ExtractContent` is gone.
- The design artboard sizes a document without an explicit size from a measurement of the rendered root, bounded by `DesignCanvasSizing.MeasureConstraint`, and has no padding around the document; `DesignCanvasSizing.ArtboardPadding` is gone.
- The dock factory no longer keeps a list of closed tools (`IsToolClosed`, `MarkToolClosed`, `MarkToolReopened`, `ClearClosedTools`, `ReopenBuiltInTool`): a loaded layout keeps closed panels closed on its own.
- Tests isolate every persisted shell settings file through `ShellSettingsPaths.RootOverride`, which replaces `XamlEditorDockFactory.SettingsRootOverride`.
- Updated Avalonia from 12.1.1 to 12.1.3, Dock from 12.0.0.2 to 12.1.0.7, ReactiveUI from 23.2.28 to 26.0.1 with ReactiveUI.Avalonia 12.1.6, and System.Reactive from 6.1.0 to 7.0.0; commands and interactions use `RxVoid` instead of `Unit`.
- The external previewer and the design surface show their preview state (out of date, disconnected, waiting for the first frame, unavailable) in the coordinate strip below the preview instead of over the rendered frame, in the editor language; a long state wraps to two lines and keeps its full text as tooltip.
- Grid columns can be widened beyond the width of their panel: pressing a column header keeps the current column widths in pixels, so dragging a column edge scrolls the grid instead of squeezing the columns to its right. The message column of the Problems panel keeps a minimum width of 100 pixels.
- The design theme messages in the Output panel follow the editor language.
- Messages, comments and documentation that were still German are English.
- SourceLink is enabled only inside a git working copy, so the solution also builds from a copy without `.git`.
- Installed extension packages are extracted into their version directory when they are installed instead of on activation. Packages installed by an earlier version are converted the first time the installed list is read; a damaged package is skipped and logged instead of hiding all other packages.
- MCP consent is stored per workspace as an access level: a client that reconnects to a workspace with stored consent is granted without a new prompt, and revoking the consent ends the running sessions of that workspace. Session tokens are created per session and are no longer stored or shown in the MCP panel. HTTP sessions end after one hour without requests.
- An extension package that registers document open interceptors has to declare `"capabilities": ["documentOpenInterceptors"]` in `xve.extension.json`.
- The undo history keeps the newest 100 steps.
- The MCP server negotiates the protocol version with the client (2025-11-25, 2025-06-18 and 2025-03-26 over HTTP, additionally 2024-11-05 over stdio) and accepts JSON-RPC batches.
- The IDE bridge binds sessions to the open workspace and stores consent per workspace as an access level; session tokens are no longer stored or shown in the IDE bridge panel. Consent stored by an earlier version is read as read-only access.
- Discarding changes in the Git panel restores the working file from the index and keeps staged changes and staged new files; unstaging no longer asks for confirmation.
- The Git panel shows the status without being switched on first: the status refreshes when the git folder changes, for example after a branch switch in a terminal, and after every Git command of the editor. The panel switch, now "Watch: On/Off" and the menu item Extensions › Git › Watch Working Folder, only controls the watcher of the working folder, which is off by default and remembered. The panel is titled "Git Changes", and its buttons show icons.
- The extension manager shows the capabilities an extension declares.
- The CDP debug diagnostics are enabled with the MSBuild property `EnableCdpDiagnostics=true` instead of the Debug configuration and require the token written to the log at startup.
- At most four external previewer hosts run at the same time: the active document and the three most recently active ones.
- Extension SDK: registering menu, toolbar, Command Palette, view or property editor contributions again adds a registration instead of replacing the earlier one of the same extension, and disposing a registration removes only its own items. To replace contributions, dispose the previous registration before registering again. The lists keep the registrations of one extension in call order; the order of different extensions is unspecified. A theme provider ID that is already registered or appears twice in one registration throws `ArgumentException`, so a theme package whose provider ID another extension already registered is reported as a package problem instead of being listed twice.

### Fixed

- Fixed moving a control with a row or column span in a Grid of the design surface: the move keeps the spans inside the grid.
- Fixed the File Explorer freezing the window on folders with thousands of entries; a loaded folder level is added to the tree in one step.
- Fixed the Solution Explorer search missing files in collapsed folders: the search matches any part of a name, ignoring case, and opens the folders above every match while the tree keeps its structure; clearing the search restores the folders that were open before.
- Fixed the Solution Explorer missing the XAML files of an SDK-style project that lists other items, such as a `None Update` of a text file: the default XAML files of the project folder are always part of the tree unless `EnableDefaultItems` or `EnableDefaultAvaloniaItems` is false, and `AvaloniaXaml Remove` patterns still exclude files. The tree search and "Open in Solution Explorer" find these files now.
- Fixed the output naming the line of the previewer's internal preview copy for a XAML error of the external previewer: the entry now names the position in the source document, like the Problems panel, and the log no longer repeats the error with its stack trace. Exceptions without a XAML position are still logged in full.
- Fixed `IEditorServices.OpenLocationAsync` failing with "Call from invalid thread" when the file was not open yet: after opening the document, the caret and selection are set on the UI thread.
- Fixed the line and column in the status bar, which stayed at 1, 1 for XAML documents.
- Fixed property edits with several selected controls, which changed only the first control. The property editor now shows the properties shared by all selected controls, shows differing values as mixed, which a reset removes from every selected control (their fields say "Mixed values" instead of showing the default value or nothing), and applies an edit to every selected control as one undo step that is rolled back completely when one control fails. An edit goes to the controls that were selected when it was typed, also when the selection changes before it is applied. Names, collections, templates and events stay single-selection edits.
- Fixed the selection in the designer, which stayed after a click on the empty area around the document.
- Fixed previews that failed with an unresolved `using:` namespace when no code had loaded the referenced library yet; the previewer now loads the application libraries the project references before it compiles the XAML.
- Fixed Solution Explorer entries such as `Y:` or `..` for files outside the project folder; these files no longer appear in the tree.
- Fixed the pointer cursor over the selection: resize handles show the resize direction, and the selection border shows the move cursor and moves the selection.
- Opening files, folders and workspaces rejects paths that are not fully qualified in the canonical syntax of the operating system; on Windows only drive paths and UNC paths with backslashes are accepted. Recent entries with such paths are dropped and removed from the stored history.
- Fixed binding errors of the tool chrome when the last tool of a dock closes, gets pinned or leaves the auto-hide preview.
- Fixed the status bar showing line and column while no text editor is visible.
- Fixed text and XAML editors that showed a newly opened file without syntax colors until the view scrolled.
- Fixed opening a file with many diagnostics freezing the editor: the problems list is rebuilt once per result instead of once per diagnostic.
- Fixed unreadable text of a selected row under the mouse in the explorer and other lists.
- Fixed C# files outside a loaded project reporting every reference to another assembly as an error; they report syntax errors only.
- Fixed a C# file outside the loaded projects pushing the project workspace aside, after which files of the projects lost their references as well.
- Fixed saving a window layout while several documents were open: the focus moved to another open document during the save and was written into the layout, which then failed to load and was replaced by the default layout. Such a layout file loads again without the document.
- Fixed panels whose list stopped following the selection after their tab was hidden and shown again.
- Fixed the C# language service writing documents to disk, which crashed the application for read-only files.
- Fixed missing satellite resource assemblies being reported as unresolved workspace assemblies.
- Fixed closing the editor logging a binding error for every tab, pin button and document: the dock views let go of their dockables before the layout is saved.
- Fixed the references panel and the extension manager keeping their subscriptions after the shell was disposed; the throttled references filter still reached the UI dispatcher afterwards.
- Fixed a panel shown again from the View menu staying invisible after its side dock had been emptied: the side dock gets its default width back.
- Fixed the Problems panel crashing when its last problem disappeared after the panel had been pinned from the auto-hide bar: the panel replaces its root list instead of clearing it in place.
- Fixed stopping the MCP server failing with "The handle is invalid" when the listener closed during a pending accept.
- Fixed Git panel commands reading file names with characters such as `*`, `?` or `[` as patterns, which could stage, restore or remove other files: paths are passed to Git as literal pathspecs.
- Fixed the Git panel listing files whose names contain characters outside ASCII, such as umlauts, as quoted escape sequences, after which staging, unstaging and discarding them failed: Git writes the names unquoted and its output is read as UTF-8. Reading the status no longer writes the Git index. Commit messages reach Git through standard input, so their length is not limited by the command line.
- Fixed the preview theme selector showing the raw provider id of a saved theme before the previewer reported its themes: the entry names the theme and says that it waits for the preview, and it shows the theme as unavailable once the previewer exits, fails to start or its start is cancelled at the trust prompt without reporting it, also after switching document tabs, until a previewer starts again. When several theme providers are registered, or the saved theme belongs to another provider than the registered one, the entry also names the provider. Selecting another entry removes the waiting entry.
- Fixed the preview strip waiting for a first frame after the previewer start was cancelled at the trust prompt; it says that the start was cancelled until a previewer starts again. Switching back to the document no longer asks for trust again; turning the previewer off and on starts it again.
- Fixed the untranslated "Theme default" entry of the preview theme selector; it reads "Project default" and follows the UI language ("Projektstandard" in German).
- Fixed German messages that addressed the user formally; all German texts use the informal form.
- Fixed disposing a replaced command handler, command metadata or view provider registration, which removed its replacement.
- Fixed a theme package with an unreadable manifest or a taken provider ID breaking the extension list: the package is logged as a problem and stays unregistered, and its other provider kind is not registered either.

## [0.2.0] - 2026-09-01

### Added

- Added design-surface theming from the workspace application's `App.axaml`; `XVE_DESIGN_APP_XAML` names the application for library workspaces without one.
- Added the `IDesignPreviewHook` extension point: extensions can take over control instantiation for the design preview or post-process finished nodes.
- Added activation of enabled installed extension packages: the archive is extracted, the manifest's main assembly is loaded, and its extension entry types are activated.
- Added `--mcp` to start the local HTTP MCP server from the command line.
- Added MCP command-line options for the transport, HTTP port, and HTTP path.
- Added per-user persistence for the main-window size and designer/code splitter ratios.
- Added automatic preview-host selection for library workspaces that use a separate application assembly.
- Added preview frame export through the IDE bridge.
- Added visible progress status for workspace loading and design-surface rebuilds.

### Changed

- The design preview now materializes property elements that carry control content and applies attributes outside its built-in list through registered Avalonia or writable CLR properties.
- The design artboard now sizes to the document: an explicit size wins, otherwise the artboard follows the rendered document bounds above the declared minimum instead of clipping at the default size.
- The C# language workspace now warms in the background after the initial workspace load.
- XVE now reads assemblies from a shared build output instead of unrelated output trees.
- Generated AXAML can select a matching source project through `XVE_PROJECT_SOURCE_ROOT`.
- The dock layout now saves before view teardown and loads during the next start.
- The bottom tool dock starts closed and opens only after a user action.
- The designer now applies explicit `Grid.RowDefinitions` and `Grid.ColumnDefinitions` elements.

### Fixed

- Fixed the test suites reading and overwriting the user's persisted layout files: the dock factory settings root is now overridable, and every test bootstrap points it at an isolated temporary directory.
- Fixed `diagnostics.get` on the IDE bridge returning nothing for designer documents: the diagnostics service now resolves any open document by path (not just the active text document) and maps designer XAML diagnostics into the language diagnostics it reports, publishes, and snapshots.
- Fixed the collapsed bottom dock expanding on every start: loading treated the persisted zero proportion as damage and reset it, and extension views registering after startup added and activated their tool over the restored layout. Zero now stays collapsed, and late extension tools go to the pin strip instead of activating.
- Fixed closed tool panels reappearing on every start: the shell recreates missing default and extension tools, so closed tools are now remembered in a persisted list, removed from loaded layouts, skipped by the recreation paths, and revived only through the view menu (a layout reset clears the list).
- Fixed designer selection picking a container instead of the clicked child: hit-testing now walks the real visual tree in z-order (templated controls included) and maps the hit to the nearest design item, instead of probing the logical structure only.
- Fixed a crash when nudging a selected control with arrow keys inside a grid without explicit row or column definitions: the target index underflowed to -1 and Grid.SetRow/SetColumn rejected it, terminating the application.
- Fixed the design surface stealing arrow keys from the code editor: its window-level key handler now acts only while focus is inside the surface.
- Fixed a UI freeze of 30+ seconds when selecting a control with a large property set: the property panel now replaces its entry collections with a single reset instead of per-item inserts, creates entry commands lazily, and notifies entry changes through direct callbacks instead of per-entry subscriptions.
- Fixed the design artboard clipping the document: it froze an early measurement taken before templates and deferred content finished loading, and border strokes on the document edge ended on the clip boundary. The document now sits at the artboard origin and sizes itself, and the artboard follows the rendered bounds plus a small padding.
- Fixed the saved dock layout never loading back: it was serialized polymorphically but deserialized against the concrete root type, and persisted open documents and the pinned dock made the file unreadable, so every start silently deleted it and rebuilt the default layout.
- Fixed a restored layout rendering as empty: the serializer materializes Active-/Default-/FocusedDockable references as duplicate subtrees, so the shell rendered an unwired copy while panels were added to the visible tree; loading now repoints these references into the visible tree by id.
- Fixed pinned tools multiplying across restarts: the layout search helpers now cover the pinned and hidden dockable lists, and loading prunes duplicates that earlier versions accumulated.
- Fixed the main window forgetting its screen position and maximized state; both are now persisted and restored, and a stored position on a disconnected monitor falls back to the default placement.
- Fixed attribute-form `RowDefinitions`/`ColumnDefinitions` being cleared instead of parsed, which collapsed every grid child into one implicit cell.
- Fixed indefinite project loading when MSBuild stalls or fails during metadata extraction.
- Fixed workspace access errors caused by changes to the process-wide current directory.
- Fixed preview startup for library projects that do not contain an application entry point.
- Fixed preview sessions that displayed an endless wait after the preview host stopped.
- Fixed missing preview-host errors in the preview surface.
- Fixed concurrent access to `recent-files.json` during application startup.
- Fixed incorrect workspace selection for generated AXAML outside a source project.

## [0.1.0] - 2026-08-20

### Added

- Cross-platform Avalonia XAML visual editor with live designer and code editor.
- XAML parsing, AST, serialization, intellisense, C# services, and LSP support.
- Dock-based shell, workspace tooling, terminal, Git, debugging, collaboration,
  ACP, MCP, animation, property editing, and tree inspection.
- Native .NET extension SDK and built-in extension package set.
- Unit, integration, performance, and Avalonia Headless UI test suites.
- Linux, Windows, and macOS release archives for x64 and Arm64.
- Source-linked NuGet packages and symbol packages.

[Unreleased]: https://github.com/wieslawsoltes/XamlVisualEditor/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/wieslawsoltes/XamlVisualEditor/releases/tag/v0.1.0
