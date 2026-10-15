# Editor Themes from Extensions

**Tools > Options > XVE theme** lists the enabled editor theme providers and their
themes. A change shows on the XVE interface immediately. **Save** keeps the selection
and restores it at the next start. **Cancel**, **ESC** and closing the dialog restore the
previously active theme. The selection for the designer and the preview is stored
separately and stays unchanged.

**XVE default** offers System, Light and Dark. When a selected extension is no longer
available or returns an invalid palette, XVE uses the dark default theme and shows the
error. The stored provider selection is kept, so enabling the extension again restores
it.

## Registration

An extension can provide editor themes, preview themes or both:

```json
{
  "name": "sample-themes",
  "publisher": "example",
  "version": "1.0.0",
  "contributes": {
    "editorThemes": [
      {
        "id": "palette",
        "name": "Sample themes",
        "assembly": "lib/net10.0/Sample.Themes.dll",
        "type": "Sample.Themes.EditorThemeProvider"
      }
    ]
  }
}
```

The public provider needs a parameterless constructor and implements
`XamlVisualEditor.Core.IEditorThemeProvider`. `GetThemes()` returns stable IDs, display
names and the light or dark variant of each theme. `GetPalette(themeId)` returns an
`EditorThemePalette` with valid ARGB color values for every role of
`EditorThemeColor`. Display names may change; stored IDs stay stable.

The provider supplies data. It changes neither `Application.Current` nor global theme
managers. XVE applies the colors to the Fluent controls, Dock, DataGrid and its own
interface. The local form preview keeps its own resources and its theme variant;
external preview processes do not receive this editor palette.

The provider DLL and its private runtime dependencies must be part of the package.
XVE loads them into an assembly context of their own; Avalonia and the XVE Core
contract are shared with the host. Before disabling, replacing or uninstalling the
extension, XVE resets the interface and releases the provider instances.
