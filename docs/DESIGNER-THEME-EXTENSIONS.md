# Designer theme extensions

Theme providers run in the isolated preview application. XVE loads only the
package manifest in its editor process. The provider uses the application's
initialized resources and the dependency versions selected by that application.

## Package manifest

Place `xve.extension.json` at the root of the NuGet package:

```json
{
  "name": "sample-themes",
  "publisher": "example",
  "version": "1.0.0",
  "displayName": "Sample preview themes",
  "contributes": {
    "designerThemes": [
      {
        "id": "palette",
        "name": "Sample themes",
        "assembly": "lib/net10.0/Example.Themes.dll",
        "type": "Example.Themes.PreviewThemeProvider",
        "requiresAssembly": "Example.Themes.Library"
      }
    ]
  }
}
```

`requiresAssembly` is optional. It names the theme library a project must reference
before the preview follows the editor theme (see below).

Include the named DLL and its runtime assets in the package. Assembly paths are
relative to the installed package and cannot traverse parent directories,
symbolic links or junctions. Provider IDs are local to the package; XVE stores
the stable full ID `example.sample-themes/palette` with the selected theme ID.
Display names are not persistence keys. A theme-only package does not need a
`main` entry point.

## Provider contract

Implement `XamlVisualEditor.Core.IDesignerThemeProvider` in a public class with a
public parameterless constructor. Reference the editor SDK contract; do not
bundle another copy of `XamlVisualEditor.Core.dll`.

- `GetThemes()` returns stable IDs, display names and light/dark metadata from
  the initialized target application, including its registered custom themes.
- `ApplyTheme(id)` applies the requested palette before controls are created.
- `ApplyTheme(null)` restores the application's original theme.
- Optional `IDisposable` support releases provider-owned subscriptions. It must
  not reset another provider's application state.

Calls run on the preview application's UI thread. Providers must not modify the
document source. They may use the target library's application-wide theme APIs
because those calls remain outside the editor process.

## Following the editor theme

Without a theme picked for the workspace, a document's preview follows the editor
theme from the options. Only providers that declare `requiresAssembly` take part, and
only for documents whose project references that assembly; other projects keep their
own theme.

- An editor theme from the same package uses the preview theme with the same ID.
- The builtin editor themes `light` and `dark` use the preview theme of the same name;
  `system` resolves to the variant the editor currently shows.
- A theme picked in the designer is stored for the workspace and wins. Picking the
  project default while the options supply a theme is stored as well; picking the
  supplied theme returns to following the options.

A theme that follows the options does not switch the document to the external
previewer; an explicit pick does.

## Lifecycle

Install and enable the package through Extensions. XVE extracts its payload into
a versioned installation directory and registers its providers. Bump the package
version for an update; an installed version is not overwritten in place.

Before changing or removing a provider, XVE stops affected preview processes to
release their assemblies. Previously running previews restart after registration
changes. Disabled packages contribute no themes. Missing providers remain
reported rather than silently replacing a saved selection with another theme.

## Flowery.NET adapter

`extensions/XamlVisualEditor.FloweryThemeExtension` contains an optional provider for Flowery.NET 3.4.0.
The editor application does not reference this project or Flowery.NET.
In previews, the provider applies only to projects that reference Flowery.NET and uses the target project's Flowery.NET assembly.

Build the package from the repository root:

```powershell
dotnet pack extensions/XamlVisualEditor.FloweryThemeExtension/XamlVisualEditor.FloweryThemeExtension.csproj -c Debug
```

Install the resulting package from the project's `bin/Debug` directory through Extensions.
The provider ID is `xamlvisualeditor.flowery-themes/palette`; the provider requires `Flowery.NET`.

The catalog uses the target application's registered themes, exclusions, and preferred order.
IDs use lowercase registered names. Labels use registered names, not private dropdown display aliases.
The provider adds `DaisyUITheme` only when the target application has no such style.

Palettes use a temporary resource layer. Project default restores the original resources, variant, and theme name.
The provider does not call `CustomThemeApplicator`, which can persist application settings.
Normal `ThemeChanged` subscribers still run inside the trusted preview process.
The package contains the adapter and `Flowery.NET.dll` for the editor theme provider, but not the editor SDK.
