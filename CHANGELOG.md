# Changelog

All notable changes to XamlVisualEditor are documented in this file. The project
uses Semantic Versioning.

## [Unreleased]

### Added

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

### Changed

- Tool tabs no longer scroll: all tabs of a dock stay visible, and tabs that do not fit shrink and shorten their titles with an ellipsis.
- The splitter between design surface and XAML code is visible in every theme and highlights under the pointer.
- The word wrap and line number toggles and the font size of a document are editor options: a change in one document applies to every editor and persists.
- Loading a layout no longer adds panels on its own: a debugger tool or extension panel that the saved layout does not contain stays closed until the view menu opens it. An extension panel with `PersistDockState = false` therefore starts closed after a restart.
- A tab keeps its view while it is hidden: switching tabs no longer rebuilds panels and editors, so scroll positions, expanded nodes and editor state stay. The view of a closed tab is released.
- The first start and the reset layout command use the Debugging preset instead of a layout with every panel. A layout preset contains only the panels it lists, instead of hiding the others.
- The edit menu separates its groups; the navigation commands follow the editing commands in the group `5.navigation` instead of `navigation`.
- The C# language workspace now warms in the background after the initial workspace load.
- XVE now reads assemblies from a shared build output instead of unrelated output trees.
- Generated AXAML can select a matching source project through `XVE_PROJECT_SOURCE_ROOT`.
- The dock layout now saves before view teardown and loads during the next start.
- The designer now applies explicit `Grid.RowDefinitions` and `Grid.ColumnDefinitions` elements.

### Fixed

- Fixed indefinite project loading when MSBuild stalls or fails during metadata extraction.
- Fixed the status bar text, which kept its initial value because changes notified a wrong property name.
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
