# Toward a Practical AXAML Form Editor

## Goal

The visual editor should create and change forms without constant switching to manual XAML editing. The benchmark is the workflows of a complete desktop form designer. A visible control or an exported preview image alone does not meet this goal.

Canvas, Grid and StackPanel belong to the same feature scope from the start. Adaptive layouts are not converted to absolute coordinates. The support must also work for project-internal and external control libraries without building library names or special cases into the editor.

The following sections are requirements and acceptance criteria, not a claim about capabilities that already exist.

## Mandatory UX Requirements

- Select controls directly, also in nested containers. Keep the selection in the designer, the structure tree and the XAML editor in sync.
- Offer multi-selection, rectangle selection, keyboard navigation, Escape to cancel, moving with the arrow keys and understandable context actions.
- Insert controls from a searchable toolbox organized by library and category. Offer the types of the loaded project and its references automatically.
- While inserting, show the target container, the insertion position and the expected result before the drop. Reject invalid targets visibly.
- Offer moving, resizing, aligning, distributing, copying, pasting, duplicating, deleting and changing the container as coherent actions.
- Every completed user action corresponds to exactly one undo step. Escape leaves no partial change. Undo and redo cover properties, hierarchy, resources and names.
- Edit properties by type, explain invalid input at the field and do not discard changes silently. Distinguish explicit empty values, local values and values reset to the style or default.
- Saving and reopening must produce the same structure, values and rendering. Keep unknown XAML structures and regions that were not edited.
- Show load, render and type errors with the affected file, control and cause. A placeholder control is not a successfully supported rendering.
- Stay usable with large forms: allow canceling work, do not block the UI thread with full assembly scans and update the affected part instead of the whole document.

## Layout-Dependent Interaction

| Action | Canvas | Grid | StackPanel |
|---|---|---|---|
| Insert | Position in the canvas, sensible initial size | Visible target cell and matching row/column assignment | Visible insertion line between siblings |
| Move | `Canvas.Left` and `Canvas.Top`, grid and guides | Cell change or deliberate alignment/margins | Change the order, no ineffective canvas coordinates |
| Resize | Explicit size respecting min/max | Size, alignment and margins; keep auto and star sizes | Desired size across the stacking direction and matching margins |
| Edit container | Size and coordinate system | Rows, columns, auto/pixel/star, spans and splitters | Orientation, spacing and order |
| Change container | Transfer the position sensibly to the new parent | Set row/column/span appropriately | Insert at a matching position in the order |

When the parent changes, layout values that no longer fit are removed or adjusted in the same undo transaction. Leave no combination of invisible old values and new placement rules. Border, ContentControl and comparable controls respect their limited number of children; do not replace existing content without asking.

## Support for External Controls

| Area | Required behavior |
|---|---|
| Discovery | Detect public instantiable controls from project and package dependencies; distinguish types with the same name by namespace and assembly |
| Toolbox | Actually available types, search terms, library, category and an understandable display name; add valid XML namespaces on insert |
| Metadata | Capture inherited CLR, styled, direct and attached properties, enum/flags, nullable, events, content and collection properties |
| Rendering | Render the actual controls with their styles, control themes, resources and templates |
| Properties | Choose the editor by metadata and value type; no fixed name list as a prerequisite for third-party controls |
| Content | Edit content properties, tabs, items, headers and nested controls in the structure tree and visually |
| Design time | Offer a documented general design-time contract for controls with runtime dependencies; keep errors and missing prerequisites visible |
| Persistence | Keep namespaces, bindings, resource references, templates and local values while editing |

The library support is verified with a separate generic test library. Product-specific libraries serve as additional integration evidence outside the public sources and tests. A known control name must not be the condition for a property or a container to work.

The [Design-Time Contract for Control Libraries](DESIGN-TIME-CONTROLS.md) describes preview values, construction and the distinction from the process-wide design mode.

## Properties and Resources

The properties panel needs matching editors for numbers, Boolean and nullable values, enum/flags, colors and brushes, Thickness, CornerRadius, Point/Size/Rect and GridLength. Local values, style values and defaults must be recognizable.

Collections and templates need structural editors with adding, removing, ordering and nested editing. A text field labeled "Collection" or "Template" is not sufficient for that.

Bindings, StaticResource, DynamicResource and complex property elements stay expressions or structures. A change to a neighboring simple property must not turn them into text values. Removing a local value is a reset action of its own; an empty string is an explicitly set value.

Event handlers need separate semantics: an empty handler field removes the assignment. Renames and navigation to the handler definition must not leave invalid references behind.

## Technical Basis

The AST and the related AXAML text together form the persistent document representation. The designer, the properties panel, the structure tree and the code editor must use the same change and transaction path. Do not build independent truths in the individual views.

The design view must resolve the same relevant properties, resources and content as the authoritative Avalonia preview. A manual subset of known standard controls is not enough for arbitrary libraries. Before extending the render path, it must be clear how selection and AST mapping are kept for real controls and their templates.

Separate type discovery from instantiation. The toolbox must not construct every control to list it. Use existing metadata and safe assembly analysis; do not introduce unbounded reflection over all loaded types.

## Verified Starting Points in the Existing Code

- `ToolboxPanelViewModel.ReloadAsync` processes the type inventory of the active workspace delivered through `IControlCatalog`. Controls sit in alphabetically sorted assembly expanders with search, library and category filters. Explicit custom catalog entries can name their assembly; entries without an assembly are listed separately under "Custom entries".
- `TypeMetadataService.GetAvailableTypes` reads a PE type catalog across assemblies, including inheritance, constructibility and design-time metadata. XML, CLR and `using:` namespaces are supported. `MetadataControlCatalog` limits third-party entries to the project assemblies and is updated when the workspace changes.
- `ControlFactory` uses the actual type identity instead of the short name alone when it creates controls. The individual property and attached-property handling still has to be checked against actual Avalonia rendering.
- The active properties view lives in `XamlVisualEditor.PropertyEditorExtension`; the library component of the same name is not the only benchmark for the behavior of the interface.
- The active properties view opens structural collection and template editors. Grid definitions can be edited with auto, pixel and star sizes; cell assignments and spans are adjusted together when the definitions change. The complete acceptance of all collection and template kinds is still required.

These findings determine the first work packages. They do not replace a complete inventory or a practical usability proof.

`ToolboxCatalogWorkflowTests` checks the assembly grouping with two real external assemblies: bound groups, alphabetical sorting, search, selection, creating a third-party control with the same name, freeing space and keeping the state when collapsing, and removing entries after a scope change. `ToolboxExtensionTests` additionally checks the grouping of custom catalog entries with unchanged insertion arguments. These headless and integration tests do not replace a desktop acceptance of the following work packages.

## Work Packages and Acceptance

### 1. Document and Property Fidelity

Distinguish empty values and reset. Apply changes and event assignments through a shared transaction path. Keep bindings, resource references and nested property elements when neighboring values are edited. Save changes, reopen them and check undo/redo.

Restoring uses complete document checkpoints with stable node IDs. Nested edit transactions create one undo entry; a rollback discards only the running operation. The designer binds properties, insert, delete, wrap and gestures to these transactions. Escape and a lost pointer capture cancel running move and resize gestures.

`DesignerTransactionWorkflowTests` checks, for Canvas, Grid and StackPanel, a combined structure and property edit with deleting a subtree, undo/redo, cancel, saving and reopening. Bindings, resources and identities are compared. An additional flow keeps incomplete XAML code on save and prevents visual edits from overwriting it. The source fidelity checks and desktop acceptance below complement this flow.

The attribute flow in the same test suite compares the complete text before and after a local change. It covers comments, processing instructions, bindings, resource references, templates, quotes and LF/CRLF line breaks as well as undo/redo, saving and reopening. Changes to `xml:space` are additionally checked against the reparsed document model. Structural source preservation takes unchanged subtrees and their spelling from the original. The workflow tests also check reordering, deleting, moving anonymous subtrees, property elements and newly inserted blocks with unchanged string literals.

### 2. End-to-End Interaction for All Three Layout Kinds

Create and edit one complete Canvas, Grid and StackPanel form each visually. Implement inserting, selection, moving, resizing, changing the parent and ordering with mouse and keyboard. The three layout kinds are accepted together.

`LayoutEditService` bundles the capacity, cycle and placement rules for implicit content and explicit property elements. Toolbox insertion, wrapping, deleting and surface or tree drops use these rules. Reparenting removes Canvas, Grid and Dock assignments that are no longer valid; other values and bindings are kept. Grid assignments and spans are limited to the existing definitions.

Toolbox drags carry the type and namespace. The design surface determines the target under the pointer and uses Canvas coordinates, Grid cells or the insertion position of a StackPanel. `LayoutEditingWorkflowTests` checks the container change with undo, saving, reopening, occupied content slots and a container from an external library. The complete mouse and keyboard acceptance is still required separately.

### 3. Project-Related Toolbox and Third-Party Controls

Offer controls of the active project and its dependencies in the toolbox. Check type identity, namespace addition, inheritance, themes and content models. Repeat the complete flow of work package 2 with controls of an external library.

Library-specific preview themes through extensions:

- [x] Provide a general theme provider interface in the extension SDK. The XVE core has no dependency on a concrete control library or its theme manager. Library- and project-specific adapters stay outside the XVE core.
- [x] Offer a compact, XVE-owned theme ComboBox in the designer/previewer toolbar. It shows the theme names delivered by the provider and uses a standard control, not a theme selection control of a third-party library. The selection stays reachable in a narrow toolbar through shared wrapping; full names stay recognizable.
- [x] Take the theme catalog from the initialized target context, including project-specific registered themes. With several providers, show the origin and themes of the same name unambiguously. Use stable provider and theme IDs, not the display names, as keys.
- [x] Apply library-specific themes in the isolated previewer with the dependencies of the loaded project. The switch changes neither the XVE editor theme nor the AXAML source. Clearly distinguish the general light/dark variant from a library palette in the interface.
- [x] Persist the selected provider/theme combination per workspace and restore it on reopening. Offer a selection for the project default; report providers or themes that are no longer available clearly instead of silently applying another theme.
- [x] Check name selection, theme switch, restoring and document/workspace changes with real rendered controls. Include windowless UserControls and explicit Design.PreviewWith previews; keep the selection and zoom in the designer.
- [x] **Transparency and theme backgrounds.** Show the interplay of a local Background, resetting to style/default, explicit null and theme resources understandably. Provide a simple way to a theme-dependent background color without overwriting fixed AXAML color values unasked.

- [x] Group the toolbox controls by assembly in expanders of their own instead of showing all third-party controls in one shared block. Sort groups and controls alphabetically. The groups get understandable library names, for example a "Flowery.NET" section for its controls.

`LibraryThemeWorkflowTests` runs the application theme path with real controls of a generic library. The flow checks inline resources, configured theme objects, inherited controls, `Application.DataTemplates` and the precedence of form-local styles and templates. Declarative styles and resources are created per preview as one shared AXAML context. This is no proof for arbitrary programmatic application initialization or process-wide design mode dependencies.

On the desktop, the project default, Light and Business were checked with standard and library buttons in a UserControl and in a styles file with `Design.PreviewWith`. The source, selection and zoom stay unchanged on a theme switch; the selection is restored after a restart and when returning to the workspace. On a workspace change, existing hosts end and open previews start in the new context. A test project without the required library DLL produces a load error; returning to the complete snapshot restores the preview. This error does not prove a successful preview of the incomplete test project.

The property help distinguishes local values, transparency, resource references, style/default and explicit null. Brush fields explain the direct input of `{DynamicResource resourceKey}`. Text fields keep their focus when an expression starts and when a value is set for the first time; search and grouping do not move an input that is not confirmed yet. In the native previewer, a background with `DaisyBase200Brush` changes from `#1C1C1C` with Business to `#F2F2F2` with Light. The resource reference and the undo history stay unchanged on the theme switch. Text input, null, reset and complete undo were additionally checked with and without an original local assignment.

### 4. Structure, Resource and Event Editors

Edit collections, tabs, menus, columns, complex values, templates and resources structurally. Integrate event handlers and naming. Changes stay part of the same undo/redo history.

In the native properties dock, resources, Menu.Items, TabControl.Items, DataGrid.Columns, Button.Template and a Children collection with a library button were edited. Invalid colors and column widths stay in the dialog as correctable drafts. Valid edits create one undo step each; undo and redo compare the complete document text. Cancel discards a changed draft. The values saved together and existing Binding, TemplateBinding and resource references are kept after reopening; the preview renders without local control instances.

### 5. Usability and Load

Check keyboard-only flows, focus, tab order, context menus, error correction and large documents in the actual application. Offer progress and cancellation for expensive operations. Define and measure which document sizes can be edited smoothly on the target machine.

## Shared Acceptance Set

Use standard controls and external controls for Canvas, Grid and StackPanel each. Cover at least these combinations:

- Nested containers and changing the parent.
- Inherited properties and attached properties of an external owner type.
- Empty texts, nullable values, enum/flags and explicit reset.
- Local and dynamic resources as well as bindings and templates.
- Selection, multi-selection, keyboard operation and canceling a running action.
- Undo/redo, saving and reopening with unchanged semantics.
- A failing control next to valid controls that stay usable.

Limit tests to concrete error risks and complete workflows. Do not add tests only for getters, setters or obvious property assignments. Proven errors as well as editing, undo/redo, saving and reopening with actually preserved values and structures come first.

Targeted integration tests prove the model and persistence. Use headless tests when they check a relevant UI contract. Usability and rendering quality additionally need a visible run in the application. Do not replace any of these levels with a successful build across the board.

## Toolbox, Interface Density and Diagnostics

- [x] **Extension management:** The top Extensions menu contains a fixed entry to the management dialog, also without menu contributions of loaded extensions. Opening and closing through the menu were checked in the UI test and on the desktop.
- [x] **Standard shortcuts:** Actually register the shown shortcuts for Open, Save, Save All, Previewer and Command Palette. Ctrl+S was checked with an edited document and in the Canvas flow with saving and reopening.
- [x] **Assembly filter per workspace:** The user can choose the shown assemblies through a multi-selection with check boxes. The selection is stored per workspace and restored on reopening. Search and assembly filter work together; newly available or temporarily missing assemblies must not lose stored selections.
- [x] **Favorites for third-party assemblies:** Every third-party section gets a directly usable favorite marker, for example a star. Favorite assemblies stand together before the other third-party assemblies; the alphabetical sorting is kept within both groups. Favorites are stored and do not bypass the assembly filter.
- [x] **Compact toolbox entries:** Reduce vertical padding, row height and spacing. Do not repeat information that already stands in the assembly group header in every control row; offer additional metadata as a tooltip when needed. Selection, focus and the insert action stay clearly recognizable and usable by keyboard.
- [x] **Restore the state of the bottom docks:** All bottom docks, including Output, Breakpoints and LSP Settings, keep their last chosen pinned, auto-hidden or expanded state across a restart. A closed dock with an inactive pin stays as a tab at the bottom edge and does not open on its own. Check and extend the existing layout persistence; in particular, LSP Settings must not overwrite a restored closed state at startup.
- [x] **Load and render errors in the Problems dock:** Check the existing diagnostics path. Show assemblies that were not found, unresolvable types and comparable load or render errors with the file, the affected element and the cause in the Problems dock instead of filling the visual designer with error messages or error controls. Navigate to the affected XAML location where possible. A kept last valid preview may only belong to the same document and must be recognizable as outdated; after the fix, outdated diagnostics disappear.
- [x] **Compact editor interface overall:** Reduce padding, minimum heights and spacing in the existing docks, lists, tree views, toolbars, menus and dialogs by shared rules. The goal is less unnecessary scrolling and better use of narrow docks, not a general reduction of the font size. Keep usability, focus rendering and error messages. This density adjustment concerns the editor interface, not the layout sizes, styles or themes of the edited forms and their rendered preview.

The acceptance covers workspace changes and restarts for filters, favorites and dock states, narrow dock widths for the compact display and an actually triggered assembly or type resolution error for the Problems display. An existing implementation counts as done only after this proof.

The external previewer delivers the actual XAML exception with structured position information. The element path maps it to the original line also after the preview serialization. Checked on the desktop are an unknown third-party control, navigation with Enter, fixing the error in the same host and switching to a second document that keeps rendering without errors. The last valid preview stays marked as outdated. Expanded bottom docks get a usable initial height and keep their size across restarts; the height already applies on the first expansion.

### Readability on Full HD Screens

- [x] The whole editor window stays readable and usable at 1920 x 1080 pixels with 100 % and 125 % scaling, independent of the resolution of the development monitor.
- [x] Do not reduce the font size to hide space problems. Wrap toolbars or use compact, labeled actions or actions explained by a tooltip; do not cut off essential controls.
- [x] Scroll bars occupy their own space and cover neither cell content nor actions. Narrow docks, search fields, context menus, dialogs and status displays are part of the acceptance.
- [x] Design, XAML and properties must stay practically usable together on Full HD as well. Auto-hidden docks and the split arrangement may create space but must not make features inaccessible.

The desktop check uses 1920 × 1080 logical pixels at 100 % and 1536 × 864 at an actually measured 125 %. Design and XAML stay visible together with Properties or Visual Tree. The toolbars wrap; scroll bars and property actions have separate space. The extensions dialog is fully usable at 125 %. The shared density also applies to directly created dialogs and editor toolbars; the font sizes stay unchanged. After the monitor change, the previous window position and size were restored.

## Designer Toolbar and Visual Tree

- [x] Additionally offer the existing layout commands as a compact toolbar: align left, center horizontally, right, top, center vertically and bottom; distribute horizontally and vertically; same width and same height.
- [x] Use PathIcons with tooltips and accessible names. The toolbar uses the same commands and undo transactions as the context menu and wraps when space is short.
- [x] Use all ten toolbar actions in the running editor with a shared selection of standard and third-party controls in the Grid. Undo each action source-faithfully with one undo; check redo on the align command.
- [x] Check all toolbar actions in Canvas and StackPanel in the running editor as well, including the primary selection, an invalid selection, undo/redo and saving.
- [x] Check toolbar changes after saving and reopening the document.
- [x] Make the columns in the Visual Tree resizable manually and create more space for type and control names with a smaller indent and more compact spacing.
- [x] Move column borders with the mouse in the running editor and check the change of the actual column widths.
- [x] Check double-click auto-sizing and the readability of deep hierarchies in a narrow dock.

In the Visual Tree dock, 337 pixels wide, eight nodes across six levels were expanded and selected by keyboard. An actual double click changes the first column width from about 142 to 188 pixels. Long type and control names get ellipses and complete tooltips; the Children column is sized by its content and its header.

## Automation IDs and Isolated Design Host

- [x] Offer an option, off by default, for automatic `AutomationProperties.AutomationId` in the settings of the drawing surface. New controls and copies get document-wide unique IDs when needed; existing controls stay unchanged.
- [x] Check standard and third-party controls, duplicating, saving/reopening and shared undo steps for insertion and ID assignment.
- [x] Size the settings popup and the input field for the canvas spacing so the number value and the spin buttons stay visible at the same time.
- [x] Manage the previewer process and the transport session together; merge parallel start requests and protect replacement sessions against outdated exit events.
- [x] End the previewer of a document when the document closes. The process end and a new transport session on reopening were checked on the desktop.
- [x] Read project assembly metadata without loading the assembly into the editor process.
- [x] Transfer and check measured control positions from the isolated host with stable AST IDs and an identifier of the rendered document state.
- [x] Connect the click selection in the previewer with the document model; distinguish runtime instances and take scaling, clipping and outdated host data into account.
- [x] Edit properties in the properties dock without a local control instance through the panel workflow; check nullable/flags values, reset, templates, validation, undo/redo and the diagnostics display when the renderer changes.
- [x] On Windows, check through CDP a windowless UserControl with a standard button and a Flowery button in the external host: click selection, resizing, Grid cell change, unchanged source while dragging and exact undo/redo. The local control cache stays empty.
- [x] Extend the native acceptance to further concrete forms, direct input in the properties dock, Canvas/StackPanel gestures and complex control and theme configurations.
- [x] Use this host data for selection, adorners and editing without additionally instantiating the third-party controls in the editor process.
- [x] Keep the existing line ending convention when inserting new XAML blocks; do not insert CRLF blocks into LF documents.

Canvas and StackPanel were additionally checked on the desktop with standard and library buttons. Proven are selection, keyboard changes, reordering or moving by mouse, Canvas resizing, unchanged source while dragging, cancel with Escape and source-faithful undo/redo. Saved sizes and orders are kept on reopening. The local control map stays empty. A document change discards the previous host geometries immediately; late geometries of the previous state are rejected.

Number values are edited as validated text input. The UI flow checks German and English decimal separators, an invalid draft with an error message, its correction with the selection and focus kept, and source-faithful undo. The same flow was checked in the native properties dock with an externally rendered library control. An active property search creates no new input fields while the set of matches stays unchanged.

## Independent Assembly Snapshots

- [x] Separate project sources and runtime assemblies through a local workspace configuration. Metadata, C# references and the previewer use the snapshot. Missing outputs must trigger neither a search in live build folders nor an automatic project build.
- [x] Check an explicitly selected preview application including its runtime files. Report an understandable error message for an incomplete selection.
- [x] Check the library preview with a locked live DLL and the configured workspace with a real external preview. The editor and its previewer load no modules from the live output folder.

[ASSEMBLY-SNAPSHOTS.md](ASSEMBLY-SNAPSHOTS.md) describes the setup and the manual update.

## Completed Acceptance

This list records the acceptance of the requirements above; it neither replaces nor reduces the acceptance set.

- [x] Keep untouched source regions also on structural changes and check them with editing, undo/redo, saving and reopening.
- [x] Connect the isolated design-time host end to end with visual editing, as far as controls need a process-wide design mode or an application initialization of their own.
- [x] Accept the complete mouse and keyboard flows for Canvas, Grid and StackPanel with standard and third-party controls in the desktop application.

In addition to the gestures, the container change through cut and paste was checked for standard and library buttons through all three layout kinds. Unsuitable Canvas and Grid assignments are removed. Duplicating, deleting and multi-selection also work with a focused external preview. Through the toolbox, both kinds of controls were inserted into every container, saved together and rendered externally again. New namespaces are declared at the root element without collisions; existing prefixes and comments are kept.
- [x] Fully accept collections, tabs, menus, columns, templates and resources including error correction in the actual properties dock.
- [x] Check the new Problems display including navigation, error fixing and session changes on the desktop.
- [x] Check the shared interface density in narrow docks, menus and dialogs as well as on monitor and DPI changes.
- [x] Measure usability, progress, cancellation and usable document sizes on the target machine and record the proven limits.
- [x] Limit local property changes in the external host to the affected controls. The host applies changed attributes to existing instances. Adding and resetting registered Avalonia properties, including attached properties, is supported. Reset removes the local value and makes styles or inherited values effective again. All instances of a source node change together; failed changes are rolled back. Structure, binding, name and theme changes keep using the full runtime loader so their document context is rebuilt.
- [x] Prove the thread assignment of the complete type catalog capture. On opening and after a workspace build, both `LoadAssemblies` and the initial `GetAvailableTypes` run on a worker. `WorkspaceResponsivenessTests` checks both thread IDs against the UI thread. The control catalog of the extensions and the type selection of the structure editor read their catalog on a worker as well.

`DesignerUpdateProtocolTests` checks the change scope, rollback and outdated revisions. `RuntimeControlPatchTests` checks kept instances, resources, name bindings and setting and resetting on a third-party control. `PreviewerLaunchServiceTests` additionally runs the update and rollback in the real library host with and without an assembly snapshot and checks the reported geometry. Unsupported changes stay editable through the full loader.

### Measured Document Sizes and Load Cancellation

Measurement of 2026-09-21 in the Debug desktop build on an Intel Core i7-13700K with 24 logical processors and about 64 GiB RAM. The workspace was already loaded. The Canvas test files contain standard buttons and library buttons in equal parts on a fixed drawing surface of 1000 × 800; in the large cases, controls lie outside the visible area. Exactly one previewer was active per measurement. The local control map stayed empty. The following baseline measurement uses a full rebuild for every change:

| Controls | AXAML bytes | Open until host geometry | Move until new host geometry | Longest UI response on open / move | Host working set after open / move |
|---:|---:|---:|---:|---:|---:|
| 100 | 11,553 | 2.81 s | 1.80 s | 328 / 901 ms | 262 / 293 MiB |
| 1,000 | 115,138 | 6.63 s | 3.03 s | 359 / 925 ms | 611 / 619 MiB |
| 5,000 | 582,538 | 11.26 s | 10.89 s | 711 / 1,390 ms | 1,197 / 2,122 MiB |

With the targeted update of the same move action, the values are:

| Controls | Move until new host geometry | Median / longest UI response | Host working set after move |
|---:|---:|---:|---:|
| 100 | 0.54 s | 2.8 / 398 ms | 242 MiB |
| 1,000 | 0.84 s | 4.0 / 608 ms | 651 MiB |
| 5,000 | 1.60 s | 10.8 / 981 ms | 1,247 MiB |

These are individual measured runs, not statistical limits or a general performance promise. The working set is a snapshot, not a peak. Updating existing controls shortens the preview wait time; reading, parsing and geometry transfer still depend on the document size. 5,000 controls were loaded and edited as a load case but not accepted as a smooth working range. A measurable delay remains at 1,000 controls as well. Another opening run with 1,000 controls took 8.53 s; the change does not shorten the initial build-up.

Reading and parsing can be canceled, with visible load phases and a Cancel action. While loading, the content and saving are locked. A dedicated cancel run with 20,000 simple controls checked the visible progress display and the focused Cancel button during parsing. Afterwards no document stayed open and no previewer process was active. The cancel test of the sync engine keeps the previously loaded document and its undo history.
