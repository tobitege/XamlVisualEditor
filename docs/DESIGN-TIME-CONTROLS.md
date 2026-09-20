# Designzeit-Vertrag fuer Controlbibliotheken

Der Designer entdeckt Typen ueber Metadaten. Erst beim Rendern werden die tatsaechlichen Controls erzeugt. Eine Bibliothek muss weder ihren Namen im Editor registrieren noch von einer editorspezifischen Basisklasse ableiten.

## Konstruktion und Abhaengigkeiten

- Toolbox-Controls benoetigen einen oeffentlichen, parameterlosen Konstruktor. Abstrakte Typen und offene generische Typen sind keine einfuegbaren Controls.
- Die Vorschau fuehrt Bibliothekscode aus. Sie ist keine Sicherheitsisolierung fuer unbekannte Assemblies.
- Fehlende Assemblies, fehlerhafte Konstruktoren und nicht aufloesbare Ressourcen bleiben Fehler. Ein transparenter Platzhalter erhaelt nur den Layoutplatz und gilt nicht als erfolgreiche Control-Unterstuetzung.
- Ausfuehrliche Meldungen gehoeren in das Problems-Dock. Im Designer bleibt nur ein kurzer Status zur unvollstaendigen oder veralteten Vorschau.

## Designwerte in AXAML

Der Runtime-XAML-Loader verarbeitet das Dokument mit aktiviertem Compiler-Designmodus. Am Wurzel-Control uebernimmt der Editor folgende Avalonia-Designwerte in die Vorschau:

| Designwert | Wirkung |
|---|---|
| `Design.Width`, `Design.Height` | Vorschaugroesse, auch ueber Design-Bindings |
| `Design.DataContext` | Beispieldaten; Kind-Controls erben den Datenkontext nach den normalen Avalonia-Regeln |
| `Design.DesignStyle` | Zusaetzlicher Style fuer die Vorschau |

Die Uebernahme aendert nicht die Laufzeitwerte im AXAML-Dokument. Die normalen Control-Eigenschaften, Styles, Resources und Templates bleiben Bestandteil des Dokuments.

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

`PreviewData` ist eine von der Bibliothek bereitgestellte Beispielklasse. Sie sollte ohne Anmeldung, Datenbankzugriff oder Netzwerkverbindung konstruierbar sein. Styles und Ressourcen koennen mit den normalen Avalonia-Mitteln im Dokument oder in referenzierten Bibliotheken bereitgestellt werden.

Fenster behalten im Renderpfad ihren wirklichen Controltyp. Der Editor aktiviert dafuer keinen zusaetzlichen Plattformmodus. Namescopes, Vorfahrenbeziehungen und Ressourcenbindungen bleiben dadurch Teil derselben Controlhierarchie.

## Prozessweiter Designmodus

Der Compiler-Designmodus ist nicht dasselbe wie `Design.IsDesignMode`. Der eingebettete Designer setzt diese prozessweite Eigenschaft nicht. Ein globaler Schalter wuerde auch die Controls und Dienste des Editors selbst betreffen.

Eine Bibliothek, deren Konstruktoren oder spaetere Lebenszyklusmethoden ausschliesslich ueber `Design.IsDesignMode` ihre Laufzeitabhaengigkeiten abschalten, braucht einen isolierten Designzeit-Host. Die durchgaengige Integration eines solchen Hosts in die visuelle Bearbeitung ist noch offen. Diese Bibliotheken gelten deshalb nicht bereits durch eine erfolgreiche Typentdeckung als vollstaendig unterstuetzt.
