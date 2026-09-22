# XAMLStudio: Übernahmekandidaten und Verbesserungen für XVE

Analyse vom 21.09.2026, dokumentiert am 22.09.2026.

## Grundlage und Umfang

Verglichen wurden das README und relevante Implementierungen aus
[tobitege/XAMLStudio](https://github.com/tobitege/XAMLStudio) mit XamlVisualEditor.

- XAMLStudio: Branch `dev`, Commit `92267797838d8e619dd26a0b3e6af9ba7944e71c`.
- XVE: Commit `62909f6`.
- Die Bewertung beruht auf dem Quellcode. XAMLStudio wurde dafür weder gebaut
  noch gestartet. Es wurden keine Funktionen portiert.

XAMLStudio ist UWP-basiert. Seine Bibliotheken sollten deshalb nicht direkt in
XVE eingebunden werden. Die interessanten Funktionen lassen sich passend zur
vorhandenen Avalonia-Architektur umsetzen.

Den größten zusätzlichen Nutzen bieten Entwurfswiederherstellung, Beispieldaten
und Binding-Diagnostik. XVE besitzt bereits Autosave, Unterstützung für
Design-DataContexts, Toolbox-Filter, visuelle und logische Baumansichten sowie
Theme-Erweiterungen. Diese Funktionen sind keine pauschalen Lücken.

## Priorisierte Übernahmekandidaten

| Priorität | Kandidat | Konkreter Nutzen für XVE |
|---|---|---|
| Hoch | Ungespeicherte Sitzungen wiederherstellen | Dokumentpuffer, Arbeitsbereiche und aktive Ansicht getrennt von den Originaldateien sichern. Nach Neustart oder Absturz können ungespeicherte Entwürfe wiederhergestellt werden. |
| Hoch | Editor für Beispieldaten | JSON aus einem Editor oder einer Datei als Vorschau-Datenkontext verwenden. Damit lassen sich leere Listen, lange Texte, fehlende Werte und unterschiedliche Datensätze gezielt im Formular testen. HTTP-Datenquellen sind eine mögliche spätere Ergänzung. |
| Hoch | Binding-Debugger | Binding-Pfad, Zielproperty, DataContext-Typ und Fehler mit Navigation zur AXAML-Stelle anzeigen. Diagnosedaten kommen aus dem vorhandenen Previewer-Prozess. Ein Verlauf muss begrenzt sein. |
| Mittel | Breadcrumb-Navigation | Eine klickbare Elementhierarchie einschließlich `x:Name` und `x:Key` anzeigen, etwa `Window → Hauptbereich [Grid] → Speichern [Button]`. Dafür den vorhandenen AST und die Auswahl-Synchronisation verwenden. |
| Mittel | Ressourcen-Vorschau | Tatsächlich aufgelöste Farben, Brushes, Styles und Ressourcen sichtbar machen. Das ergänzt den vorhandenen Ressourcen-/Struktureditor um Laufzeitinformationen. |
| Mittel | Zustandsinspektor | Aktive Klassen und Pseudoklassen eines Avalonia-Controls und ihren Einfluss auf die Darstellung untersuchen. Dies ist das passende Gegenstück zum Visual-State-Explorer von XAMLStudio. |
| Kleine Ergänzung | Dokumentation aus der Toolbox öffnen | Pro Controltyp einen Dokumentationslink anbieten. Bibliotheken oder Extensions können ihre eigenen Links bereitstellen. |

## 1. Entwurfs- und Sitzungswiederherstellung

XAMLStudio serialisiert offene Dokumente einschließlich ihres Inhalts sowie
Arbeitsbereiche und die aktive Ansicht. Vor einem Renderdurchlauf wird der
Arbeitsstand ebenfalls gesichert. Beim Wiederherstellen werden die Dateizugriffe
erneut hergestellt und die gespeicherten Dokumentobjekte wieder geöffnet.

Quellen:

- [SuspendAndResumeService](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Services/SuspendAndResumeService.cs)
- [SuspensionState](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Services/SuspensionState.cs)
- [FileBackedDocument](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Models/FileBackedDocument.cs)
- [MainViewModel.RestoreWorkspaceAsync](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/ViewModels/MainViewModel.cs)

In XVE ruft `AutoSaveAsync` den normalen `SaveCommand` auf. Das Speichern des
Dock-Layouts nimmt die Laufzeitdokumente vor der Serialisierung aus dem Layout;
die Dokument-ViewModels sind außerdem mit `IgnoreDataMember` markiert.
Damit ersetzt dieser Ablauf keine Sicherung ungespeicherter Dokumentpuffer.

XVE-Bezug:

- [Autosave in ShellViewModels](../src/XamlVisualEditor.Shell.ViewModels/ShellViewModels.cs)
- [Dokumente und Layout-Speicherung in XamlEditorDockFactory](../src/XamlVisualEditor.Shell.ViewModels/XamlEditorDockFactory.cs)

Empfehlung: eine eigene lokale Entwurfssicherung ergänzen. Dabei auch leere
Dokumente und Konflikte mit zwischenzeitlich extern geänderten Dateien
berücksichtigen. XAMLStudio lässt beim Wiederherstellen Dokumente ohne Inhalt
aus; diese Einschränkung sollte XVE nicht übernehmen. Sicherungsdateien und
persönliche Pfade gehören außerhalb des Repositorys.

## 2. Editor für Beispieldaten

XAMLStudio besitzt einen Datenquellen-Editor, lädt JSON-Dateien und kann JSON von
einer HTTP-Quelle abrufen. Der resultierende Datenkontext wird an die Vorschau
übergeben.

Quellen:

- [DataSources](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/DataSources.xaml.cs)
- [ParseDataContext und RefreshLiveDataContext](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/ViewModels/DocumentViewModel.Methods.cs)

XVE berücksichtigt bereits Design-DataContexts, unter anderem durch
`NormalizeDesignDataContext` in
[PreviewerLaunchService](../src/XamlVisualEditor.Shell.ViewModels/PreviewerLaunchService.cs).
Ein eigener Editor für wechselnde Beispieldatensätze wäre eine Ergänzung dieser
Grundlage.

Die JSON-Verarbeitung sollte XVE anders aufbauen: XAMLStudio probiert mehrere
Deserialisierungen nacheinander und verschluckt deren Fehler. Bei uns sollten
Parsingfehler sichtbar sein. Ein ausdrückliches Zurücksetzen, `null` und leere
Sammlungen müssen eindeutig behandelt werden.

Für Compiled Bindings müssen die Beispieldaten zum deklarierten `x:DataType`
passen. Ein `ExpandoObject` allein genügt dafür nicht. Typisierte
Design-DataContexts müssen Teil des Konzepts bleiben; ein pauschaler Wechsel auf
Reflection-Bindings passt nicht zu den XVE-Konventionen.

Referenz: [Avalonia Compiled Bindings](https://docs.avaloniaui.net/docs/data-binding/compiled-bindings).

## 3. Binding-Diagnostik

XAMLStudio erfasst für Bindings die Zielproperty, Quellposition, Eingangs- und
Converter-Ergebnisse, Fehler, Zeitpunkte und einen Verlauf. Die Oberfläche kann
die letzten Werte und den Verlauf darstellen.

Quellen:

- [XamlBindingInfo](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Models/XamlBindingInfo.cs)
- [ConversionRecord](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Models/ConversionRecord.cs)
- [Binding-Ansicht](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Binding.xaml)

Die technische Umsetzung ist kein unverändert übernehmbarer Baustein:
XAMLStudio verändert Binding-Ausdrücke für die Vorschau und ersetzt Converter
durch einen Wrapper. Bei Converter-Fehlern liefert der Wrapper `null`. Der
Verlauf speichert Werte und Ergebnisse ohne Größenbegrenzung.

- [Binding-Umschreibung](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Services/XamlRenderService/XamlRenderService.ParseBinding.cs)
- [Converter-Wrapper](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Converters/XamlBindingWrapperConverter.cs)

Für XVE sollte die erste Ausbaustufe Avalonia-Bindingfehler erfassen, einem
Control beziehungsweise einer Quellstelle zuordnen und im Editor zugänglich
machen. Die vorhandenen Previewer-Nachrichten und Quellidentitäten bieten dafür
Anknüpfungspunkte:

- [PreviewerTcpSession](../src/XamlVisualEditor.Shell.ViewModels/PreviewerTcpSession.cs)
- [DesignerHostSnapshot](../src/XamlVisualEditor.Shell.ViewModels/DesignerHostSnapshot.cs)

Vollständige Converter-Wertverläufe benötigen eine gesonderte
Machbarkeitsprüfung für Avalonia 12. Die Diagnose soll die Binding-Semantik
erhalten und nur begrenzte, serialisierbare Daten zum Editor übertragen.

Referenz: [Avalonia Binding Debugging](https://docs.avaloniaui.net/docs/data-binding/binding-debugging).

## 4. Breadcrumb-Navigation

XAMLStudio bildet aus der Cursorposition die Hierarchie der umgebenden
XML-Elemente. `x:Name` oder `x:Key` ergänzt den Elementtyp. Ein Klick springt zur
zugehörigen Position im Quelltext.

Quelle: [UpdateBreadcrumbs und BreadcrumbBar_ItemClicked](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Document.xaml.cs).

Die Bedienidee lässt sich gut übertragen. XVE sollte dabei den aktuellen AST
und die vorhandene Auswahl-Synchronisation wiederverwenden. XAMLStudio berechnet
die Navigation bei Cursoränderungen teilweise durch erneutes Parsen; im Code
ist dieser Aufwand selbst als verbesserungsbedürftig markiert.

## 5. Ressourcen und Control-Zustände untersuchen

Der ResourceViewer zeigt Ressourcenwerte und erzeugt Vorschauen für Styles.
Der VisualStateWatcher beobachtet die Zustandsgruppen eines Controls und deren
aktiven Zustand.

- [ResourceViewer](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Controls/ResourceViewer/ResourceViewer.cs)
- [VisualStateWatcher](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit/Helpers/VisualStateWatcher.cs)

XVE besitzt bereits einen strukturellen Eigenschafteneditor und unterscheidet
lokale Werte, Bindings, Ressourcen und Standardwerte. Ein Laufzeitinspektor
könnte zusätzlich die tatsächlich aufgelösten Ressourcen und aktiven
Control-Zustände zeigen.

XVE-Bezug: [PropertyEditorPanelViewModel](../extensions/XamlVisualEditor.PropertyEditorExtension/PropertyEditorPanelViewModel.cs).

UWP-VisualStateGroups lassen sich nicht direkt auf Avalonia übertragen. Für
Avalonia ist zunächst eine Anzeige aktiver Klassen und Pseudoklassen sinnvoll.
Ein erzwungenes Umschalten beliebiger Pseudoklassen ist gesondert zu prüfen;
deren Schreibzugriff ist geschützt. Die reflektionsbasierte Erzeugung beliebiger
Controls im ResourceViewer passt ebenfalls nicht unverändert zu XVE.

Referenz: [Avalonia Classes](https://docs.avaloniaui.net/api/avalonia/controls/classes).

## 6. Dokumentationszugriff aus der Toolbox

XAMLStudio ermittelt pro Controltyp einen Dokumentationslink und öffnet ihn aus
der Toolbox. XVE zeigt dort bisher hauptsächlich Typ, Assembly und Kategorie.

- [Dokumentationsaktion in XAMLStudio](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio/Views/Toolbox.xaml.cs)
- [ToolboxPanelViewModel in XVE](../extensions/XamlVisualEditor.ToolboxExtension/ToolboxPanelViewModel.cs)

Ein optionaler Dokumentationslink pro Bibliothek oder Extension wäre eine
überschaubare Ergänzung. Die vorhandene Toolbox mit ihren Filtern und Favoriten
bleibt die Grundlage.

## Architektur und tatsächliche Codeübernahme

XVE sollte seinen vorhandenen Previewer, die Assembly-Snapshots, den AST,
Undo/Redo und die Theme-Erweiterungen beibehalten. XAMLStudios In-Process-Renderer
auf Basis von `XamlReader.Load` und seine Monaco-Anbindung liefern keinen
erkennbaren Grund für einen Architekturwechsel.

Gut übertragbar sind Bedienabläufe, Diagnosemodelle und einzelne Testfälle.
Beispielsweise enthält
[BindingParserTests](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/XamlStudio.Toolkit.UnitTests/BindingParserTests.cs)
Fälle für Converter-Parameter, Anführungszeichen und verschachtelte Ausdrücke.
Vor einer Übernahme ist abzugleichen, welche davon die vorhandenen XVE-Tests
bereits abdecken.

Das Repository steht unter der
[MIT-Lizenz](https://github.com/tobitege/XAMLStudio/blob/92267797838d8e619dd26a0b3e6af9ba7944e71c/LICENSE).
Bei tatsächlicher Codeübernahme müssen die zugehörigen Copyright- und
Lizenzhinweise erhalten bleiben. Drittanbieterabhängigkeiten werden dadurch
nicht automatisch Teil von XVE.

## Empfohlene Reihenfolge

1. Lokale Entwurfs- und Sitzungswiederherstellung.
2. Beispieldateneditor mit passenden typisierten Design-DataContexts.
3. Binding-Diagnostik im vorhandenen Previewer-Prozess.
4. Ressourcen- und Zustandsinspektion.

Breadcrumbs und Dokumentationslinks eignen sich als kleinere unabhängige
Verbesserungen.
