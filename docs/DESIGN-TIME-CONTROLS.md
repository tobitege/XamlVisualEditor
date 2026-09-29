# Design-Time Contract for Control Libraries

The designer discovers types through metadata. The actual controls are created only when the document renders. A library needs neither to register its name in the editor nor to derive from an editor-specific base class.

## Construction and Dependencies

- Toolbox controls need a public parameterless constructor. Abstract types and open generic types are not insertable controls.
- The preview runs library code. It is no security isolation for unknown assemblies.
- Missing assemblies, failing constructors and unresolvable resources remain errors. A transparent placeholder only keeps the layout space and does not count as successful control support.
- Detailed messages belong in the Problems dock. The designer only shows a short status for an incomplete or outdated preview.

## Design Values in AXAML

The runtime XAML loader processes the document with the compiler design mode enabled. On the root control, the editor applies the following Avalonia design values to the preview:

| Design value | Effect |
|---|---|
| `Design.Width`, `Design.Height` | Preview size, also through design bindings |
| `Design.DataContext` | Sample data; child controls inherit the data context according to the regular Avalonia rules |
| `Design.DesignStyle` | Additional style for the preview |

Applying these values does not change the runtime values in the AXAML document. The regular control properties, styles, resources and templates stay part of the document.

```xml
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:sample="clr-namespace:Sample.Controls;assembly=Sample.Controls"
             Design.Width="640" Design.Height="360">
  <Design.DataContext>
    <sample:PreviewData />
  </Design.DataContext>
  <TextBlock Text="{Binding Caption}" />
</UserControl>
```

`PreviewData` is a sample class provided by the library. It should be constructible without a sign-in, database access or network connection. Styles and resources can be provided with the regular Avalonia means in the document or in referenced libraries.

Windows keep their real control type in the render path. The editor does not enable an additional platform mode for them. Name scopes, ancestor relationships and resource bindings therefore stay part of the same control hierarchy.

## Process-Wide Design Mode

The compiler design mode is not the same as `Design.IsDesignMode`. The embedded designer does not set this process-wide property. A global switch would also affect the controls and services of the editor itself.

A library whose constructors or later lifecycle methods switch off their runtime dependencies only through `Design.IsDesignMode` needs an isolated design-time host. The end-to-end integration of such a host into visual editing is still open. These libraries therefore do not count as fully supported merely because their types were discovered.
