# Third-party notices

## Microsoft Fluent System Icons

The editor uses SVG path data of regular 20px icons from `@fluentui/svg-icons`.

Source: <https://github.com/microsoft/fluentui-system-icons>

Path data from version `1.1.341`:

- `src/XamlVisualEditor.Shell.ViewModels/DesignerLayoutEditor.cs` (layout toolbar): `align_left`, `align_center_vertical`, `align_right`, `align_top`, `align_center_horizontal`, `align_bottom`, `align_space_evenly_horizontal`, `align_space_evenly_vertical`, `arrow_autofit_width`, `arrow_autofit_height`, and `grid`.
- `src/XamlVisualEditor.Shell.ViewModels/AnimationEditorIcons.cs` (animation editor): `play`, `stop`, `arrow_undo`, `arrow_redo`, `add`, `diamond`, `copy`, `clipboard_paste`, `delete`, `arrow_sync`, `arrow_download`, `save`, `document_add`, and `target`.
- `src/XamlVisualEditor.Designer.Core/DesignerViewportIcons.cs` (designer viewport and coordinate strip): `grid_dots`, `ruler`, `panel_bottom`, and `arrow_bidirectional_left_right`.
- `src/XamlVisualEditor.CodeEditor/CodeEditorIcons.cs` (code editor): `target`.

Path data from an earlier release (identical in version `1.1.261`):

- `src/XamlVisualEditor.App/MainWindow.axaml` (main toolbar): `document_add`, `folder_open`, `save`, `play`, and `textbox`.
- `extensions/XamlVisualEditor.WorkspaceExtension/WorkspaceExtension.cs` (workspace commands): `window_console`, `arrow_undo`, `arrow_redo`, `cut`, `copy`, `clipboard_paste`, and `delete`.

The center icon names describe the reference line, not the direction of movement.
The three disjoint paths of `align_space_evenly_vertical` are combined without changing their coordinates.

MIT License

Copyright (c) 2020 Microsoft Corporation

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## Google Material Icons

The text editor toolbars and the Git compare document use SVG path data of 24px icons from Material Icons (filled style).

Source: <https://github.com/google/material-design-icons>

- `src/XamlVisualEditor.App/Views/XamlCodeEditorView.axaml`: `format_align_justify`, `wrap_text` (adapted), and `format_list_numbered`.
- `src/XamlVisualEditor.App/Views/TextFileView.axaml`: `wrap_text` (adapted) and `format_list_numbered`.
- `extensions/XamlVisualEditor.GitExtension/GitIcons.cs`: `unfold_more`.

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
