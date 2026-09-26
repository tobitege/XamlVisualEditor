# Changelog

All notable changes to XamlVisualEditor are documented in this file. The project
uses Semantic Versioning.

## [Unreleased]

### Added

- Added a save prompt for modified files when a tab, the workspace or the application closes; automatic saving saves without asking.
- Added the option to reopen the last workspace or folder at startup.
- Added the option to list folders before files in the Solution Explorer; it is on by default.
- Added `--mcp` to start the local HTTP MCP server from the command line.
- Added MCP command-line options for the transport, HTTP port, and HTTP path.
- Added per-user persistence for the main-window size and designer/code splitter ratios.
- Added automatic preview-host selection for library workspaces that use a separate application assembly.
- Added preview frame export through the IDE bridge.
- Added visible progress status for workspace loading and design-surface rebuilds.
- Added the TFVC extension with a source control explorer, a pending changes panel, and automatic check-out of versioned files on their first edit. It uses tf.exe and the TFVC REST API with the current Windows credentials; the only required setting is the collection URL.
- Added changeset details to the TFVC extension: the history context menu shows the changed files of a changeset in the TFVC Changes panel, where the author can revise and save the check-in comment. The history can be filtered by user and is sorted by changeset, newest first.
- Added the text editing extension: a context menu for the text and code editors with clipboard, find and replace, go to line and matching bracket, a Format submenu (toggle comment, upper and lower case, indentation, line sorting, enclosing in brackets), an Outlining submenu and a Display submenu with check-marked view toggles and zoom. The Editor Options dialog sets font, display, editing, link and color options with a live preview. The options and the caret position of each file persist across sessions.
- Added the Window menu with the window layout commands, which leave the View menu, and a Manage Layouts dialog that renames and deletes saved layouts.
- Extensions can contribute to the editor context menu (`ExtensionMenuLocations.EditorContext`), group menu items into submenus (`ExtensionMenuContribution.Submenu` and `SubmenuGroup`), show check marks that follow a condition (`ExtensionMenuContribution.CheckedWhen`) over their own context keys (`ExtensionContext.ContextKeys`), set selection and caret in one step (`IEditorDocument.SetSelection`), limit a keybinding to the focused text editor (`CommandMetadata.KeybindingScope`) and set editor view options and actions through `ExtensionContext.TextEditorView`; `textEditor.hasActiveEditor` tells whether the active document shows an editor.
- Added an offline mode to the TFVC extension: when the server is unreachable at startup, XVE offers to work offline, removes the read-only attribute of an edited versioned file only after confirmation, and proposes the local changes for pending once when going online again. The Extensions menu has a TFVC submenu for going offline and online.
- Added "Add Items to Folder..." to the TFVC source control explorer, which adds unversioned files and folders below a mapped folder.
- Read-only files show a banner and a title marker in the editor.
- Extensions can refine the rendered design preview through `IDesignPreviewHook` and `DesignPreviewHookRegistry`.
- Enabled installed extension packages whose manifest names a `main` assembly are loaded into the default load context and activated.
- `XVE_DESIGN_APP_XAML` names the App.axaml whose themes and resources the design surface applies, for library workspaces without an application of their own.
- The status bar shows startup work, extension activation, TFVC initialization and the language-service warmup of a loaded workspace while they run, and shows "Ready" only after the last of them ended. Extensions announce their own background work through `ExtensionContext.BackgroundActivities`.
- Extensions can redirect a file the user opens to another file through `ExtensionContext.DocumentOpenInterceptors`, for example to open a generated file in place of its source; navigation into a file is not redirected. `ConfirmationDialogRequest.CancelText` labels the declining button.
- The XAML code editor marks the element selected in the designer with an arrow at its first line and a bar along its lines in the gutter, and a selection in the designer scrolls the element start away from the viewport edge. The "Follow" toggle in the code editor toolbar turns this off.
- An interceptor cancels the open by throwing `OperationCanceledException`. Extensions show a modal, cancellable progress dialog through `ProgressDialogRequest` while long work runs.

### Changed

- Tool tabs no longer scroll: all tabs of a dock stay visible, and tabs that do not fit shrink and shorten their titles with an ellipsis.
- The splitter between design surface and XAML code is visible in every theme and highlights under the pointer.
- The word wrap and line number toggles and the font size of a document are editor options: a change in one document applies to every editor and persists.
- Loading a layout no longer adds panels on its own: a debugger tool or extension panel that the saved layout does not contain stays closed until the view menu opens it. An extension panel with `PersistDockState = false` therefore starts closed after a restart.
- A tab keeps its view while it is hidden: switching tabs no longer rebuilds panels and editors, so scroll positions, expanded nodes and editor state stay. The view of a closed tab is released.
- The first start and the reset layout command use the Debugging preset instead of a layout with every panel. A layout preset contains only the panels it lists, instead of hiding the others.
- The edit menu separates its groups; the navigation commands follow the editing commands in the group `5.navigation` instead of `navigation`.
- The C# language workspace now warms in the background after the initial workspace load.
- The messages of the TFVC output channel and the tf.exe timeout message follow the editor language; a unit test keeps the German TFVC resources complete and their placeholders aligned.
- Selecting a control with a large property set no longer stalls the UI: the property panel replaces its entry, event and grouped row collections with a single reset instead of per-item inserts, creates the entry commands on first use, sets initial entry values without change notifications, and notifies value changes through direct callbacks instead of per-entry subscriptions.
- The design surface clamps Grid.Row and Grid.Column to zero before writing them, so a negative target index no longer throws from an input handler.
- Tests that depend on file-system case sensitivity probe the test directory instead of assuming it from the operating system.
- Fixed the external previewer failing with "Unable to resolve type DesignNodeIdentity" for projects whose output does not contain the editor assemblies: the previewer host now loads the assembly of the design node identity properties from the editor directory before it compiles the XAML.
- Fixed the external previewer reporting "Design tree unavailable: Ambiguous match found" for controls that hide an inherited property with `new`, such as a Padding of their own type: the design tree reader walks the type hierarchy and takes the Avalonia Thickness declaration of Padding.
- A workspace remembers its session independently of the dock layout: opening it again reopens the documents that were open, in their order and including files outside the workspace, activates the document that was active and restores the startup project. The sessions of the 50 most recently used workspaces are kept in `workspace-sessions.json`; closing a workspace keeps its session.
- The Solution Explorer marks only the startup project, like Visual Studio: its name is bold after the ▶ marker, and the "RUN" label of other executable projects is gone. A tooltip on a project node tells whether it is the startup project or an executable project, and "Set as Startup Project" is available for executable projects only. The references folder is the first child of a project, whatever the sort option, and its name follows the editor language.
- Added the option "Show the full file path in the window title" (off by default) under Window in the Options dialog; the window title also follows a "Save As" of the active document. The tooltip of a document tab always shows the full path of its file.
- Opening a problem from the Problems panel in a XAML code editor marks its location like a designer selection, but in the error color: a red bar and arrow in the gutter along the start tag that contains the diagnostic (or along its line outside a start tag), scrolled into the middle of the editor with the caret at the diagnostic. The marker disappears with the next edit. Extensions open a diagnostic location this way through `IEditorServices.OpenDiagnosticLocationAsync`.
- Fixed the external previewer failing with "Could not load file or assembly 'System.Xaml'" when the previewed project references a .NET Framework library: the previewer host no longer preloads project references whose assembly-level attributes come from an assembly the runtime cannot load, because the XAML compiler reads those attributes of every loaded assembly.
- `DesignPreviewHookRegistry.Register` ignores a hook that is already registered: the hook runs once per preview, the repeated registration's disposal does nothing, and the first registration keeps owning the hook.
- XVE now reads assemblies from a shared build output instead of unrelated output trees.
- Generated AXAML can select a matching source project through `XVE_PROJECT_SOURCE_ROOT`.
- The dock layout now saves before view teardown and loads during the next start.
- The designer now applies explicit `Grid.RowDefinitions` and `Grid.ColumnDefinitions` elements.

### Fixed

- Fixed the output naming the line of the previewer's internal preview copy for a XAML error of the external previewer: the entry now names the position in the source document, like the Problems panel, and the log no longer repeats the error with its stack trace. Exceptions without a XAML position are still logged in full.
- Fixed `IEditorServices.OpenLocationAsync` failing with "Call from invalid thread" when the file was not open yet: after opening the document, the caret and selection are set on the UI thread.
- Fixed indefinite project loading when MSBuild stalls or fails during metadata extraction.
- Fixed the status bar text, which kept its initial value because changes notified a wrong property name.
- Fixed the line and column in the status bar, which stayed at 1, 1 for XAML documents.
- Fixed property edits with several selected controls, which changed only the first control. The property editor now shows the properties shared by all selected controls, shows differing values as not set, and applies an edit to every selected control as one undo step. Names, collections, templates and events stay single-selection edits.
- Fixed the selection in the designer, which stayed after a click on the empty area around the document.
- Fixed previews that failed with an unresolved `using:` namespace when no code had loaded the referenced library yet; the previewer now loads the application libraries the project references before it compiles the XAML.
- Fixed the read-only notice, which also showed for files that source control checks out on the first edit; it now shows only when the write protection stays, for example while TFVC works offline.
- Fixed Solution Explorer entries such as `Y:` or `..` for files outside the project folder; these files no longer appear in the tree.
- Fixed the pointer cursor over the selection: resize handles show the resize direction, and the selection border shows the move cursor and moves the selection.
- Opening files, folders and workspaces rejects paths that are not fully qualified in the canonical syntax of the operating system; on Windows only drive paths and UNC paths with backslashes are accepted. Recent entries with such paths are dropped and removed from the stored history.
- Fixed workspace access errors caused by changes to the process-wide current directory.
- Fixed preview startup for library projects that do not contain an application entry point.
- Fixed preview sessions that displayed an endless wait after the preview host stopped.
- Fixed missing preview-host errors in the preview surface.
- Fixed concurrent access to `recent-files.json` during application startup.
- Fixed incorrect workspace selection for generated AXAML outside a source project.
- Fixed binding errors of the tool chrome when the last tool of a dock closes, gets pinned or leaves the auto-hide preview.
- Fixed the view menu not reopening a debugger tool that was closed in its dock.
- Fixed the name of the active window layout getting lost at restart, which also made Save ask for a name again.
- Fixed the status bar showing line and column while no text editor is visible.
- Fixed text and XAML editors that showed a newly opened file without syntax colors until the view scrolled.
- Fixed a restored caret that scrolled a newly opened file sideways before the editor had its width and gutter.
- Fixed opening a file with many diagnostics freezing the editor: the problems list is rebuilt once per result instead of once per diagnostic.
- Fixed unreadable text of a selected row under the mouse in the explorer and other lists.
- Fixed C# files outside a loaded project reporting every reference to another assembly as an error; they report syntax errors only.
- Fixed a C# file outside the loaded projects pushing the project workspace aside, after which files of the projects lost their references as well.
- Fixed saving a window layout while several documents were open: the focus moved to another open document during the save and was written into the layout, which then failed to load and was replaced by the default layout. Such a layout file loads again without the document.
- Fixed panels whose list stopped following the selection after their tab was hidden and shown again, for example the TFVC explorer after loading a workspace.
- Fixed the TFVC panels forgetting whether their connection settings were shown; each panel keeps its own choice.
- Fixed the C# language service writing documents to disk, which crashed the application for read-only files.
- Fixed save, delete and rename errors showing system text; they report read-only, access-denied and in-use files in the current language.
- Fixed the design artboard clipping documents without an explicit size at 800 x 600; it grows from the declared minimum size to the rendered content.
- Fixed tests reading and overwriting the user's persisted layout files.
- Fixed expanding a folder in the TFVC explorer scrolling the tree back to its root; only the rows of the expanded folder are replaced.
- Fixed missing satellite resource assemblies being reported as unresolved workspace assemblies.
- Fixed closing the editor logging a binding error for every tab, pin button and document: the dock views let go of their dockables before the layout is saved.
- Fixed the references panel and the extension manager keeping their subscriptions after the shell was disposed; the throttled references filter still reached the UI dispatcher afterwards.

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
