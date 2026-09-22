# Editor-Themes aus Extensions

Unter **Werkzeuge > Optionen > XVE-Theme** stehen die aktivierten Editor-Theme-Provider
und ihre Themes zur Auswahl. Ein Wechsel wird sofort auf der XVE-Oberfläche angezeigt.
**Speichern** übernimmt die Auswahl dauerhaft und stellt sie beim nächsten Start wieder her.
**Abbrechen**, **ESC** und das Schließen des Dialogs stellen das zuvor aktive Theme wieder her.
Die Auswahl für Designer und Vorschau wird getrennt gespeichert und bleibt unverändert.

**XVE-Standard** bietet System, Hell und Dunkel. Ist eine gewählte Extension nicht
mehr verfügbar oder liefert sie eine ungültige Palette, verwendet XVE das dunkle
Standard-Theme und zeigt den Fehler an. Die gespeicherte Provider-Auswahl bleibt
erhalten, sodass erneutes Aktivieren der Extension sie wiederherstellt.

## Registrierung

Eine Extension kann Editor-Themes, Vorschau-Themes oder beides bereitstellen:

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

Der öffentliche Provider benötigt einen parameterlosen Konstruktor und implementiert
`XamlVisualEditor.Core.IEditorThemeProvider`. `GetThemes()` liefert stabile IDs,
Anzeigenamen und die jeweilige Hell/Dunkel-Variante. `GetPalette(themeId)` liefert
ein `EditorThemePalette` mit gültigen ARGB-Farbwerten für sämtliche Rollen aus
`EditorThemeColor`. Anzeigenamen dürfen sich ändern; gespeicherte IDs bleiben stabil.

Der Provider liefert Daten. Er verändert weder `Application.Current` noch globale
Theme-Manager. XVE setzt die Farben für Fluent-Controls, Dock, DataGrid und seine
eigene Oberfläche um. Die lokale Formularvorschau behält eigene Ressourcen und ihre
Theme-Variante; externe Vorschauprozesse erhalten diese Editor-Palette nicht.

Die Provider-DLL und ihre privaten Laufzeitabhängigkeiten müssen im Paket liegen.
XVE lädt sie in einem eigenen Assembly-Kontext; Avalonia und der XVE-Core-Vertrag
werden mit dem Host geteilt. Vor Deaktivierung, Austausch oder Deinstallation setzt
XVE die Oberfläche zurück und gibt die Provider-Instanzen frei.

## Flowery.NET

`XamlVisualEditor.FloweryThemeExtension` enthält ab Version 0.2.0 sowohl
`editorThemes` als auch `designerThemes`. Der Editor-Provider liest die registrierten
Flowery-Paletten über `DaisyThemeManager.GetPaletteFactory`, ohne ein globales Theme
anzuwenden. Das Paket enthält auch die benötigte `Flowery.NET.dll`.

Nach dem dokumentierten Debug-Build wird das Paket mit diesem Aufruf erstellt:

```powershell
dotnet pack extensions/XamlVisualEditor.FloweryThemeExtension/XamlVisualEditor.FloweryThemeExtension.csproj -c Debug --no-build -o artifacts/packages
```

Das erzeugte Paket kann über die Erweiterungsverwaltung installiert und aktiviert
werden. Beide Theme-Auswahlen verwenden dieselbe Extension und bleiben unabhängig.
