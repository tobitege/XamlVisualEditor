# XAMLStudio: Adoption Candidates and Improvements for XVE

Analysis of 2026-09-21, documented on 2026-09-22.

## Basis and Scope

The README and relevant implementations of
[tobitege/XAMLStudio](https://github.com/tobitege/XAMLStudio) were compared with XamlVisualEditor.

- XAMLStudio: branch `dev`, commit `92267797838d8e619dd26a0b3e6af9ba7944e71c`.
- XVE: commit `62909f6`.
- The assessment is based on the source code. XAMLStudio was neither built nor
  started for it. No features were ported.

XAMLStudio is based on UWP. Its libraries should therefore not be included in XVE
directly. The interesting features can be implemented in a way that fits the existing
Avalonia architecture.

Draft recovery, sample data and binding diagnostics offer the largest additional
benefit. XVE already has autosave, support for design data contexts, toolbox filters,
visual and logical tree views and theme extensions. These features are not general
gaps.

## Prioritized Adoption Candidates

| Priority | Candidate | Concrete benefit for XVE |
|---|---|---|
| High | Restore unsaved sessions | Back up document buffers, workspaces and the active view separately from the original files. Unsaved drafts can be restored after a restart or crash. |
| High | Sample data editor | Use JSON from an editor or a file as the preview data context. This makes it possible to test empty lists, long texts, missing values and different records in a form. HTTP data sources are a possible later addition. |
| High | Binding debugger | Show the binding path, target property, DataContext type and errors with navigation to the AXAML location. The diagnostic data comes from the existing previewer process. A history must be bounded. |
| Medium | Breadcrumb navigation | Show a clickable element hierarchy including `x:Name` and `x:Key`, for example `Window → MainArea [Grid] → Save [Button]`. Use the existing AST and the selection synchronization for it. |
| Medium | Resource preview | Show the actually resolved colors, brushes, styles and resources. This adds runtime information to the existing resource and structure editor. |
| Medium | State inspector | Examine the active classes and pseudo-classes of an Avalonia control and their effect on the rendering. This is the matching counterpart to the visual state explorer of XAMLStudio. |
| Small addition | Open documentation from the toolbox | Offer a documentation link per control type. Libraries or extensions can provide their own links. |

## 1. Draft and Session Recovery

XAMLStudio serializes open documents including their content, as well as workspaces
and the active view. The working state is also saved before a render pass. On
restore, the file accesses are established again and the saved document objects are
reopened.

Sources:

- [SuspendAndResumeService](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Services/SuspendAndResumeService.cs)
- [SuspensionState](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Services/SuspensionState.cs)
- [FileBackedDocument](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Models/FileBackedDocument.cs)
- [MainViewModel.RestoreWorkspaceAsync](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/ViewModels/MainViewModel.cs)

In XVE, `AutoSaveAsync` calls the regular `SaveCommand`. Saving the dock layout
removes the runtime documents from the layout before serialization; the document view
models are also marked with `IgnoreDataMember`. This flow therefore does not replace a
backup of unsaved document buffers.

XVE references:

- [Autosave in ShellViewModels](../src/XamlVisualEditor.Shell.ViewModels/ShellViewModels.cs)
- [Documents and layout saving in XamlEditorDockFactory](../src/XamlVisualEditor.Shell.ViewModels/XamlEditorDockFactory.cs)

Recommendation: add a separate local draft backup. It should also handle empty
documents and conflicts with files that changed externally in the meantime. XAMLStudio
skips documents without content on restore; XVE should not adopt this limitation.
Backup files and personal paths belong outside the repository.

## 2. Sample Data Editor

XAMLStudio has a data source editor, loads JSON files and can fetch JSON from an HTTP
source. The resulting data context is passed to the preview.

Sources:

- [DataSources](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/DataSources.xaml.cs)
- [ParseDataContext and RefreshLiveDataContext](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/ViewModels/DocumentViewModel.Methods.cs)

XVE already takes design data contexts into account, among others through
`NormalizeDesignDataContext` in
[PreviewerLaunchService](../src/XamlVisualEditor.Shell.ViewModels/PreviewerLaunchService.cs).
A dedicated editor for changing sample records would extend this basis.

XVE should build the JSON handling differently: XAMLStudio tries several
deserializations in a row and swallows their errors. In XVE, parsing errors should be
visible. An explicit reset, `null` and empty collections must be handled unambiguously.

For compiled bindings, the sample data must match the declared `x:DataType`. An
`ExpandoObject` alone is not enough for that. Typed design data contexts must remain
part of the concept; a general switch to reflection bindings does not fit the XVE
conventions.

Reference: [Avalonia Compiled Bindings](https://docs.avaloniaui.net/docs/data-binding/compiled-bindings).

## 3. Binding Diagnostics

For bindings, XAMLStudio records the target property, the source position, the input
and converter results, errors, timestamps and a history. The interface can show the
latest values and the history.

Sources:

- [XamlBindingInfo](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Models/XamlBindingInfo.cs)
- [ConversionRecord](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Models/ConversionRecord.cs)
- [Binding view](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Binding.xaml)

The technical implementation is not a building block that can be adopted unchanged:
XAMLStudio rewrites binding expressions for the preview and replaces converters with a
wrapper. On converter errors, the wrapper returns `null`. The history stores values and
results without a size limit.

- [Binding rewriting](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Services/XamlRenderService/XamlRenderService.ParseBinding.cs)
- [Converter wrapper](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Converters/XamlBindingWrapperConverter.cs)

For XVE, the first stage should capture Avalonia binding errors, map them to a control
or a source location and make them accessible in the editor. The existing previewer
messages and source identities offer starting points for this:

- [PreviewerTcpSession](../src/XamlVisualEditor.Shell.ViewModels/PreviewerTcpSession.cs)
- [DesignerHostSnapshot](../src/XamlVisualEditor.Shell.ViewModels/DesignerHostSnapshot.cs)

Complete converter value histories need a separate feasibility check for Avalonia 12.
The diagnostics should preserve the binding semantics and transfer only bounded,
serializable data to the editor.

Reference: [Avalonia Binding Debugging](https://docs.avaloniaui.net/docs/data-binding/binding-debugging).

## 4. Breadcrumb Navigation

XAMLStudio builds the hierarchy of the surrounding XML elements from the caret
position. `x:Name` or `x:Key` complements the element type. A click jumps to the
matching position in the source text.

Source: [UpdateBreadcrumbs and BreadcrumbBar_ItemClicked](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Document.xaml.cs).

The interaction idea transfers well. XVE should reuse the current AST and the existing
selection synchronization for it. XAMLStudio computes the navigation on caret changes
partly by parsing again; the code itself marks this effort as needing improvement.

## 5. Inspecting Resources and Control States

The ResourceViewer shows resource values and creates previews for styles. The
VisualStateWatcher observes the state groups of a control and their active state.

- [ResourceViewer](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Controls/ResourceViewer/ResourceViewer.cs)
- [VisualStateWatcher](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Helpers/VisualStateWatcher.cs)

XVE already has a structural property editor and distinguishes local values, bindings,
resources and default values. A runtime inspector could additionally show the actually
resolved resources and the active control states.

XVE reference: [PropertyEditorPanelViewModel](../extensions/XamlVisualEditor.PropertyEditorExtension/PropertyEditorPanelViewModel.cs).

UWP visual state groups do not transfer to Avalonia directly. For Avalonia, showing the
active classes and pseudo-classes is a sensible first step. Forcing arbitrary
pseudo-classes on needs a separate check; writing them is protected. The
reflection-based creation of arbitrary controls in the ResourceViewer does not fit XVE
unchanged either.

Reference: [Avalonia Classes](https://docs.avaloniaui.net/api/avalonia/controls/classes).

## 6. Documentation Access from the Toolbox

XAMLStudio determines a documentation link per control type and opens it from the
toolbox. XVE so far mainly shows the type, assembly and category there.

- [Documentation action in XAMLStudio](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Toolbox.xaml.cs)
- [ToolboxPanelViewModel in XVE](../extensions/XamlVisualEditor.ToolboxExtension/ToolboxPanelViewModel.cs)

An optional documentation link per library or extension would be a manageable
addition. The existing toolbox with its filters and favorites stays the basis.

## Architecture and Actual Code Adoption

XVE should keep its existing previewer, the assembly snapshots, the AST, undo/redo and
the theme extensions. XAMLStudio's in-process renderer based on `XamlReader.Load` and
its Monaco integration give no apparent reason for an architecture change.

Interaction flows, diagnostic models and individual test cases transfer well. For
example,
[BindingParserTests](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit.UnitTests/BindingParserTests.cs)
contains cases for converter parameters, quotes and nested expressions. Before adopting
them, check which of them the existing XVE tests already cover.

The repository is licensed under the
[MIT License](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/LICENSE).
When code is actually adopted, the related copyright and license notices must be kept.
Third-party dependencies do not automatically become part of XVE through this.

## Recommended Order

1. Local draft and session recovery.
2. Sample data editor with matching typed design data contexts.
3. Binding diagnostics in the existing previewer process.
4. Resource and state inspection.

Breadcrumbs and documentation links are suitable as smaller independent improvements.
