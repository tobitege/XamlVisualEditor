# Ausbau zum praxistauglichen AXAML-Formulareditor

## Ziel

Der visuelle Editor soll Formulare ohne ständigen Wechsel zur manuellen XAML-Bearbeitung erstellen und ändern können. Maßstab sind die Arbeitsabläufe eines vollständigen Desktop-Formulardesigners. Ein sichtbares Control oder ein exportiertes Vorschaubild allein erfüllt dieses Ziel nicht.

Canvas, Grid und StackPanel gehören von Anfang an zum gleichen Funktionsumfang. Adaptive Layouts werden nicht in absolute Koordinaten umgewandelt. Die Unterstützung muss auch für projektinterne und externe Controlbibliotheken funktionieren, ohne Bibliotheksnamen oder Sonderfälle im Editor fest einzubauen.

Die folgenden Abschnitte sind Anforderungen und Abnahmekriterien, keine Behauptung über bereits vorhandene Fähigkeiten.

## Verbindliche UX-Anforderungen

- Controls direkt auswählen, auch in verschachtelten Containern. Auswahl im Designer, im Strukturbaum und im XAML-Editor synchron halten.
- Mehrfachauswahl, Rechteckauswahl, Tastaturnavigation, Escape zum Abbrechen, Verschieben per Pfeiltaste und verständliche Kontextaktionen anbieten.
- Controls aus einer durchsuchbaren, nach Bibliothek und Kategorie gegliederten Toolbox einfügen. Typen aus dem geladenen Projekt und dessen Referenzen automatisch anbieten.
- Beim Einfügen den Zielcontainer, die Einfügeposition und das erwartete Ergebnis vor dem Loslassen anzeigen. Ungültige Ziele sichtbar ablehnen.
- Verschieben, Größe ändern, Ausrichten, Verteilen, Kopieren, Einfügen, Duplizieren, Löschen und Containerwechsel als zusammenhängende Aktionen anbieten.
- Jede abgeschlossene Benutzeraktion entspricht genau einem Undo-Schritt. Escape hinterlässt keine Teiländerung. Undo und Redo umfassen Eigenschaften, Hierarchie, Ressourcen und Namen.
- Eigenschaften nach Typ bearbeiten, ungültige Eingaben am Feld erklären und Änderungen nicht stillschweigend verwerfen. Explizite Leerwerte, lokale Werte und auf Style oder Standard zurückgesetzte Werte unterscheiden.
- Speichern und erneutes Öffnen müssen dieselbe Struktur, Werte und Darstellung ergeben. Unbekannte XAML-Strukturen und nicht bearbeitete Bereiche erhalten.
- Lade-, Render- und Typfehler mit betroffener Datei, Control und Ursache anzeigen. Ein Ersatz-Control ist keine erfolgreich unterstützte Darstellung.
- Bei großen Formularen bedienbar bleiben: Arbeit abbrechen können, UI-Thread nicht mit vollständigen Assembly-Scans blockieren und den betroffenen Teil statt des ganzen Dokuments aktualisieren.

## Layoutabhängige Bedienung

| Aktion | Canvas | Grid | StackPanel |
|---|---|---|---|
| Einfügen | Position im Canvas, sinnvolle Ausgangsgröße | Sichtbare Zielzelle und passende Zeilen-/Spaltenzuordnung | Sichtbare Einfügelinie zwischen Geschwistern |
| Verschieben | `Canvas.Left` und `Canvas.Top`, Raster und Hilfslinien | Zellwechsel oder bewusste Ausrichtung/Abstände | Reihenfolge ändern, keine wirkungslosen Canvas-Koordinaten |
| Größe ändern | Explizite Größe unter Berücksichtigung von Min/Max | Größe, Alignment und Abstände; Auto- und Sternmaße erhalten | Gewünschte Größe quer zur Stapelrichtung und passende Abstände |
| Container bearbeiten | Größe und Koordinatensystem | Zeilen, Spalten, Auto/Pixel/Stern, Spans und Splitter | Orientierung, Spacing und Reihenfolge |
| Containerwechsel | Position sinnvoll auf neuen Parent übertragen | Row/Column/Span passend setzen | Reihenfolge passend einfügen |

Beim Parent-Wechsel werden nicht mehr passende Layoutwerte in derselben Undo-Transaktion entfernt oder angepasst. Keine Kombination aus unsichtbaren Altwerten und neuen Platzierungsregeln hinterlassen. Border, ContentControl und vergleichbare Controls beachten ihre begrenzte Anzahl an Inhalten; bestehende Inhalte nicht ungefragt ersetzen.

## Unterstützung externer Controls

| Bereich | Erforderliches Verhalten |
|---|---|
| Entdeckung | Öffentliche instanziierbare Controls aus Projekt- und Paketabhängigkeiten erkennen; gleichnamige Typen nach Namespace und Assembly unterscheiden |
| Toolbox | Tatsächlich verfügbare Typen, Suchbegriffe, Bibliothek, Kategorie und verständlicher Anzeigename; gültige XML-Namespaces beim Einfügen ergänzen |
| Metadaten | Geerbte CLR-, Styled-, Direct- und Attached-Properties, Enum/Flags, Nullable, Events, Inhalts- und Collectioneigenschaften erfassen |
| Darstellung | Die tatsächlichen Controls mit zugehörigen Styles, ControlThemes, Ressourcen und Templates darstellen |
| Eigenschaften | Editor anhand von Metadaten und Werttyp wählen; keine feste Namensliste als Voraussetzung für Fremdcontrols |
| Inhalte | Content-Eigenschaften, Tabs, Items, Header und verschachtelte Controls im Strukturbaum und visuell bearbeiten |
| Designzeit | Für Controls mit Laufzeitabhängigkeiten einen dokumentierten allgemeinen Designzeit-Vertrag anbieten; Fehler und fehlende Voraussetzungen sichtbar lassen |
| Persistenz | Namespaces, Bindings, Ressourcenverweise, Templates und lokale Werte beim Bearbeiten erhalten |

Die Bibliotheksunterstützung wird mit einer separaten generischen Testbibliothek geprüft. Produktspezifische Bibliotheken dienen als zusätzlicher Integrationsnachweis außerhalb der öffentlichen Quellen und Tests. Ein bekannter Controlname darf nicht die Bedingung dafür sein, dass eine Eigenschaft oder ein Container funktioniert.

Der [Designzeit-Vertrag fuer Controlbibliotheken](DESIGN-TIME-CONTROLS.md) beschreibt Vorschauwerte, Konstruktion und die Abgrenzung zum prozessweiten Designmodus.

## Eigenschaften und Ressourcen

Das Eigenschaftenfenster benötigt passende Editoren für Zahlen, boolesche und nullable Werte, Enum/Flags, Farben und Brushes, Thickness, CornerRadius, Point/Size/Rect und GridLength. Lokale Werte, Stylewerte und Defaults müssen erkennbar sein.

Collections und Templates brauchen strukturelle Editoren mit Hinzufügen, Entfernen, Reihenfolge und verschachtelter Bearbeitung. Ein Textfeld mit der Bezeichnung "Collection" oder "Template" ist dafür nicht ausreichend.

Bindings, StaticResource, DynamicResource und komplexe Property-Elemente bleiben als Ausdrücke beziehungsweise Strukturen erhalten. Eine Änderung an einer benachbarten einfachen Eigenschaft darf sie nicht in Textwerte umwandeln. Das Entfernen eines lokalen Werts ist eine eigene Reset-Aktion; ein leerer String ist ein ausdrücklich gesetzter Wert.

Eventhandler brauchen eine getrennte Semantik: Ein leeres Handlerfeld entfernt die Zuordnung. Namensänderungen und Navigation zur Handlerdefinition dürfen keine ungültigen Referenzen zurücklassen.

## Technische Grundlage

Das AST und der zugehörige AXAML-Text bilden gemeinsam die persistente Dokumentrepräsentation. Designer, Eigenschaftenfenster, Strukturbaum und Codeeditor müssen denselben Änderungs- und Transaktionspfad verwenden. Keine voneinander unabhängigen Wahrheiten in den einzelnen Ansichten aufbauen.

Die Designansicht muss dieselben relevanten Eigenschaften, Ressourcen und Inhalte auflösen wie die maßgebliche Avalonia-Vorschau. Eine manuelle Teilmenge bekannter Standardcontrols reicht für beliebige Bibliotheken nicht aus. Vor einer Erweiterung des Renderpfads muss feststehen, wie Auswahl und AST-Zuordnung bei echten Controls und ihren Templates erhalten bleiben.

Typentdeckung und Instanziierung trennen. Die Toolbox darf nicht zum Auflisten jedes Control konstruieren. Bereits vorhandene Metadaten und sichere Assembly-Analyse nutzen; keine unbeschränkte Reflection über sämtliche geladenen Typen einführen.

## Belegte Ansatzpunkte im vorhandenen Code

- `ToolboxPanelViewModel.ReloadAsync` verarbeitet den über `IControlCatalog` gelieferten Typbestand des aktiven Workspaces. Controls stehen in alphabetisch sortierten Assembly-Expandern mit Suche, Bibliotheks- und Kategoriefiltern. Explizite benutzerdefinierte Katalogeinträge können ihre Assembly angeben; nicht zugeordnete Einträge stehen getrennt unter "Custom entries".
- `TypeMetadataService.GetAvailableTypes` liest einen Assembly-übergreifenden PE-Typkatalog einschließlich Vererbung, Konstruierbarkeit und Designzeit-Metadaten. XML-, CLR- und `using:`-Namespaces werden unterstützt. `MetadataControlCatalog` beschränkt Drittanbieter-Einträge auf die Projekt-Assemblies und wird beim Workspacewechsel aktualisiert.
- `ControlFactory` berücksichtigt bei der Control-Erzeugung die tatsächliche Typidentität statt allein den kurzen Namen. Die einzelnen Property-/Attached-Property-Behandlungen bleiben gegen tatsächliches Avalonia-Rendering zu prüfen.
- Die aktive Eigenschaftenansicht liegt in `XamlVisualEditor.PropertyEditorExtension`; die gleichnamige Bibliothekskomponente ist nicht alleiniger Maßstab für das Verhalten der Oberfläche.
- Die aktive Eigenschaftenansicht öffnet strukturelle Collection- und Template-Editoren. Grid-Definitionen können mit Auto-, Pixel- und Sternmaßen bearbeitet werden; Zellzuordnungen und Spans werden beim Ändern der Definitionen gemeinsam angepasst. Die vollständige Abnahme aller Collection- und Template-Arten bleibt erforderlich.

Diese Befunde bestimmen die ersten Arbeitspakete. Sie ersetzen keine vollständige Bestandsaufnahme und keinen praktischen Bediennachweis.

`ToolboxCatalogWorkflowTests` prüft die Assembly-Gruppierung mit zwei echten externen Assemblies: gebundene Gruppen, alphabetische Sortierung, Suche, Auswahl, die Erzeugung eines gleichnamigen Fremdcontrols, Platzfreigabe und Zustandserhalt beim Einklappen sowie das Entfernen von Einträgen nach einem Scopewechsel. `ToolboxExtensionTests` prüft zusätzlich die Gruppierung benutzerdefinierter Katalogeinträge mit unveränderten Einfügeargumenten. Diese Headless- und Integrationstests ersetzen keine Desktop-Abnahme der folgenden Arbeitspakete.

## Arbeitspakete und Abnahme

### 1. Dokument- und Eigenschaftstreue

Leerwerte und Reset unterscheiden. Änderungen und Eventzuordnungen über einen gemeinsamen Transaktionspfad übernehmen. Bindings, Ressourcenverweise und verschachtelte Property-Elemente beim Bearbeiten benachbarter Werte erhalten. Änderungen speichern, erneut öffnen und Undo/Redo prüfen.

Die Wiederherstellung erfolgt über vollständige Dokument-Checkpoints mit stabilen Knoten-IDs. Verschachtelte Edit-Transaktionen erzeugen einen Undo-Eintrag; Rollback verwirft ausschließlich die laufende Operation. Der Designer bindet Eigenschaften, Einfügen, Löschen, Umschließen und Gesten an diese Transaktionen. Escape und verlorener Pointer-Capture brechen laufende Verschiebe-/Resize-Gesten ab.

`DesignerTransactionWorkflowTests` prüft für Canvas, Grid und StackPanel einen kombinierten Struktur-/Eigenschaftseingriff mit Löschen eines Teilbaums, Undo/Redo, Abbruch, Speicherung und erneutem Öffnen. Bindings, Ressourcen und Identitäten werden dabei verglichen. Ein zusätzlicher Ablauf erhält unvollständigen XAML-Code beim Speichern und verhindert dessen Überschreiben durch visuelle Eingriffe. Die nachfolgenden Quelltreueprüfungen und die Desktop-Abnahmen ergänzen diesen Ablauf.

Der Attributablauf in derselben Testsuite vergleicht den vollständigen Text vor und nach einer lokalen Änderung. Er umfasst Kommentare, Processing Instructions, Bindings, Ressourcenverweise, Templates, Anführungszeichen und LF-/CRLF-Zeilenumbrüche sowie Undo/Redo, Speicherung und erneutes Öffnen. Änderungen an `xml:space` werden zusätzlich gegen das erneut geparste Dokumentmodell geprüft. Die strukturelle Quellerhaltung übernimmt unveränderte Teilbäume und ihre Schreibweise aus dem Original. Die Workflowtests prüfen auch Umordnen, Löschen, Verschieben anonymer Teilbäume, Property-Elemente und neu eingefügte Blöcke mit unveränderten String-Literalen.

### 2. Durchgängige Bedienung aller drei Layoutarten

Je ein vollständiges Canvas-, Grid- und StackPanel-Formular visuell erstellen und bearbeiten. Einfügen, Auswahl, Verschieben, Größenänderung, Parent-Wechsel und Reihenfolge mit Maus und Tastatur umsetzen. Die drei Layoutarten werden gemeinsam abgenommen.

`LayoutEditService` bündelt Kapazitäts-, Zyklus- und Platzierungsregeln für implizite Inhalte und explizite Property-Elemente. Toolbox-Einfügen, Umschließen, Löschen sowie Surface-/Tree-Drops verwenden diese Regeln. Umhängen entfernt nicht mehr gültige Canvas-, Grid- und Dock-Zuordnungen; andere Werte und Bindings bleiben erhalten. Grid-Zuordnungen und Spans werden auf vorhandene Definitionen begrenzt.

Toolbox-Drags transportieren Typ und Namespace. Die Designfläche ermittelt das Ziel unter dem Zeiger und verwendet Canvas-Koordinaten, Grid-Zellen beziehungsweise die Einfügeposition eines StackPanels. `LayoutEditingWorkflowTests` prüft den Containerwechsel mit Undo, Speicherung, erneutem Öffnen, belegten Content-Slots und einem Container aus einer externen Bibliothek. Die vollständige Maus-/Tastatur-Abnahme bleibt gesondert erforderlich.

### 3. Projektbezogene Toolbox und Fremdcontrols

Controls aus dem aktiven Projekt und seinen Abhängigkeiten in der Toolbox anbieten. Typidentität, Namespace-Ergänzung, Vererbung, Themes und Inhaltsmodelle prüfen. Den kompletten Ablauf aus Arbeitspaket 2 mit Controls einer externen Bibliothek wiederholen.

Bibliotheksspezifische Vorschau-Themes über Extensions:

- [x] Eine allgemeine Theme-Provider-Schnittstelle im Extension-SDK bereitstellen. Der XVE-Kern enthält keine Abhängigkeit auf eine konkrete Control-Bibliothek oder deren Theme-Manager. Bibliotheks- und projektspezifische Adapter bleiben außerhalb des XVE-Kerns.
- [x] Eine kompakte, XVE-eigene Theme-ComboBox in der Designer-/Previewer-Toolbar anbieten. Sie zeigt die vom Provider gelieferten Theme-Namen und verwendet ein Standard-Control, kein Theme-Auswahl-Control einer Fremdbibliothek. Die Auswahl bleibt bei schmaler Toolbar durch gemeinsames Umbrechen erreichbar; vollständige Namen bleiben erkennbar.
- [x] Den Theme-Katalog aus dem initialisierten Zielkontext beziehen, einschließlich projektspezifisch registrierter Themes. Bei mehreren Providern Herkunft und gleichnamige Themes eindeutig darstellen. Stabile Provider- und Theme-IDs verwenden, nicht die Anzeigenamen als Schlüssel.
- [x] Bibliotheksspezifische Theme-Anwendung im isolierten Previewer mit den Abhängigkeiten des geladenen Projekts ausführen. Der Wechsel verändert weder das XVE-Editor-Theme noch den AXAML-Quelltext. Die allgemeine Hell-/Dunkel-Variante und eine Bibliothekspalette in der Oberfläche klar unterscheiden.
- [x] Die gewählte Provider-/Theme-Kombination pro Workspace persistieren und beim erneuten Öffnen wiederherstellen. Eine Auswahl für den Projektstandard anbieten; nicht mehr verfügbare Provider oder Themes verständlich melden, statt unbemerkt ein anderes Theme anzuwenden.
- [x] Namensauswahl, Theme-Wechsel, Wiederherstellung und Dokument-/Workspace-Wechsel mit echten gerenderten Controls prüfen. Fensterlose UserControls und explizite Design.PreviewWith-Vorschauen einbeziehen; Auswahl und Zoom im Designer erhalten.
- [x] **Transparenz und Theme-Hintergründe.** Das Zusammenspiel von lokalem Background, Zurücksetzen auf Style/Standard, explizitem null und Theme-Ressourcen verständlich darstellen. Einen einfachen Weg zu einer themeabhängigen Hintergrundfarbe vorsehen, ohne feste AXAML-Farbwerte ungefragt zu überschreiben.

- [x] Toolbox-Controls nach Assembly in eigenen Expandern gruppieren, statt sämtliche Drittanbieter-Controls in einem gemeinsamen Block anzuzeigen. Gruppen und Controls jeweils alphabetisch sortieren. Die Gruppen erhalten verständliche Bibliotheksnamen, beispielsweise eine Sektion "Flowery.NET" für deren Controls.

`LibraryThemeWorkflowTests` führt den Anwendungstheme-Pfad mit echten Controls einer generischen Bibliothek aus. Der Ablauf prüft Inline-Ressourcen, konfigurierte Theme-Objekte, geerbte Controls, `Application.DataTemplates` und den Vorrang formularlokaler Styles und Templates. Deklarative Styles und Ressourcen werden pro Vorschau als gemeinsamer AXAML-Kontext erzeugt. Das ist kein Nachweis für beliebige programmatische Anwendungsinitialisierung oder prozessweite Designmodus-Abhängigkeiten.

Am Desktop sind Projektstandard, Light und Business mit Standard- und Bibliotheks-Buttons in einem UserControl sowie einer Styles-Datei mit `Design.PreviewWith` geprüft. Quelltext, Auswahl und Zoom bleiben beim Theme-Wechsel erhalten; die Auswahl wird nach Neustart und bei der Rückkehr zum Workspace wiederhergestellt. Beim Workspace-Wechsel werden bestehende Hosts beendet und offene Vorschauen im neuen Kontext gestartet. Ein Testprojekt ohne die erforderliche Bibliotheks-DLL liefert einen Ladefehler; die Rückkehr zum vollständigen Snapshot stellt die Vorschau wieder her. Dieser Fehler belegt keine erfolgreiche Vorschau des unvollständigen Testprojekts.

Die Eigenschaften-Hilfe unterscheidet lokale Werte, Transparenz, Ressourcenverweise, Style/Standard und explizites null. Brush-Felder erläutern die direkte Eingabe von `{DynamicResource resourceKey}`. Textfelder behalten beim Beginn eines Ausdrucks und beim ersten Setzen eines Werts ihren Fokus; Suche und Gruppierung verschieben keine noch unbestätigte Eingabe. Im nativen Previewer wechselt ein Hintergrund mit `DaisyBase200Brush` von `#1C1C1C` bei Business zu `#F2F2F2` bei Light. Der Ressourcenverweis und die Undo-Historie bleiben beim Theme-Wechsel unverändert. Die Texteingabe, null, Reset und vollständiges Undo sind zusätzlich mit und ohne ursprüngliche lokale Zuweisung geprüft.

### 4. Struktur-, Ressourcen- und Eventeditoren

Collections, Tabs, Menüs, Spalten, komplexe Werte, Templates und Ressourcen strukturell bearbeiten. Eventhandler und Benennung integrieren. Änderungen bleiben Teil derselben Undo-/Redo-Historie.

Im nativen Properties-Dock sind Ressourcen, Menu.Items, TabControl.Items, DataGrid.Columns, Button.Template und eine Children-Collection mit einem Bibliotheks-Button bearbeitet. Ungültige Farben und Spaltenbreiten bleiben als korrigierbare Entwürfe im Dialog. Gültige Eingriffe erzeugen jeweils einen Undo-Schritt; Undo und Redo vergleichen den vollständigen Dokumenttext. Cancel verwirft einen geänderten Entwurf. Die gemeinsam gespeicherten Werte und bestehende Binding-, TemplateBinding- und Ressourcenverweise sind nach dem Wiederöffnen erhalten; die Vorschau rendert ohne lokale Control-Instanzen.

### 5. Bedienbarkeit und Belastung

Keyboard-Only-Abläufe, Fokus, Tab-Reihenfolge, Kontextmenüs, Fehlerkorrektur und große Dokumente in der tatsächlichen Anwendung prüfen. Fortschritt und Abbruch bei teuren Operationen anbieten. Festlegen und messen, welche Dokumentgrößen auf der Zielmaschine flüssig bearbeitbar sind.

## Gemeinsamer Abnahmesatz

Für Canvas, Grid und StackPanel jeweils Standardcontrols sowie externe Controls verwenden. Mindestens diese Kombinationen abdecken:

- Verschachtelte Container und Wechsel des Parents.
- Geerbte Eigenschaften und Attached-Properties eines externen Eigentümertyps.
- Leere Texte, Nullable-Werte, Enum/Flags und ausdrückliches Reset.
- Lokale und dynamische Ressourcen sowie Bindings und Templates.
- Auswahl, Mehrfachauswahl, Tastaturbedienung und Abbruch einer laufenden Aktion.
- Undo/Redo, Speichern und erneutes Öffnen mit unveränderter Semantik.
- Ein fehlerhaftes Control neben weiterhin bedienbaren, gültigen Controls.

Tests auf konkrete Fehlerrisiken und vollständige Arbeitsabläufe begrenzen. Keine zusätzlichen Tests allein für Getter, Setter oder offensichtliche Eigenschaftszuweisungen anlegen. Vorrang haben nachgewiesene Fehler sowie Bearbeiten, Undo/Redo, Speichern und erneutes Öffnen mit tatsächlich erhaltenen Werten und Strukturen.

Gezielte Integrationstests belegen Modell und Persistenz. Headless-Tests verwenden, wenn sie einen relevanten UI-Vertrag prüfen. Bedienbarkeit und Darstellungsqualität brauchen zusätzlich einen sichtbaren Durchlauf in der Anwendung. Keine dieser Ebenen pauschal durch einen erfolgreichen Build ersetzen.

## Toolbox, Oberflächendichte und Diagnose

- [x] **Extensions-Verwaltung:** Das obere Extensions-Menü enthält einen festen Zugang zum Verwaltungsdialog, auch ohne Menübeiträge geladener Erweiterungen. Öffnen und Schließen über das Menü sind im UI-Test und am Desktop geprüft.
- [x] **Standard-Tastenkürzel:** Die angezeigten Kürzel für Öffnen, Speichern, Alles speichern, Previewer und Command Palette tatsächlich registrieren. Ctrl+S ist mit einem bearbeiteten Dokument sowie im Canvas-Ablauf mit Speichern und erneutem Öffnen geprüft.
- [x] **Assembly-Filter je Workspace:** Der Benutzer kann die angezeigten Assemblies über eine Mehrfachauswahl mit Checkboxen festlegen. Die Auswahl wird je Workspace gespeichert und beim erneuten Öffnen wiederhergestellt. Suche und Assembly-Filter wirken gemeinsam; neu verfügbare oder vorübergehend fehlende Assemblies dürfen gespeicherte Auswahlen nicht verlieren.
- [x] **Favoriten für Drittanbieter-Assemblies:** Jede Drittanbieter-Sektion erhält eine direkt bedienbare Favoriten-Markierung, beispielsweise einen Stern. Favorisierte Assemblies stehen zusammen vor den übrigen Drittanbieter-Assemblies; innerhalb beider Gruppen bleibt die alphabetische Sortierung erhalten. Favoriten werden gespeichert und umgehen den Assembly-Filter nicht.
- [x] **Kompakte Toolbox-Einträge:** Vertikales Padding, Zeilenhöhe und Zwischenräume reduzieren. Informationen, die bereits im Assembly-Gruppenkopf stehen, nicht in jeder Control-Zeile wiederholen; zusätzliche Metadaten bei Bedarf als Tooltip anbieten. Auswahl, Fokus und Einfügeaktion bleiben eindeutig erkennbar und per Tastatur bedienbar.
- [x] **Zustand der unteren Docks wiederherstellen:** Alle unteren Docks, einschließlich Output, Breakpoints und LSP Settings, behalten ihren zuletzt gewählten angehefteten, automatisch ausgeblendeten beziehungsweise aufgeklappten Zustand über einen Neustart. Ein geschlossenes Dock mit inaktivem Pin bleibt als Reiter am unteren Rand und öffnet sich nicht selbstständig. Vorhandene Layout-Persistenz prüfen und ergänzen; insbesondere darf LSP Settings einen wiederhergestellten geschlossenen Zustand nicht beim Start überschreiben.
- [x] **Lade- und Renderfehler im Problems-Dock:** Den bestehenden Diagnoseweg prüfen. Nicht gefundene Assemblies, nicht auflösbare Typen und vergleichbare Lade- oder Renderfehler mit Datei, betroffenem Element und Ursache im Problems-Dock anzeigen, statt den visuellen Designer mit Fehlermeldungen oder Fehler-Controls zu füllen. Soweit möglich zur betroffenen XAML-Stelle navigieren. Eine erhaltene letzte gültige Vorschau darf nur zum selben Dokument gehören und muss als veraltet erkennbar sein; nach der Fehlerbehebung verschwinden überholte Diagnosen.
- [x] **Kompakte Editoroberfläche insgesamt:** Padding, Mindesthöhen und Abstände in den vorhandenen Docks, Listen, Baumansichten, Toolbars, Menüs und Dialogen nach gemeinsamen Regeln reduzieren. Ziel sind weniger unnötiges Scrollen und eine bessere Nutzung schmaler Docks, nicht eine allgemeine Verkleinerung der Schrift. Bedienbarkeit, Fokusdarstellung und Fehlermeldungen erhalten. Diese Dichteanpassung betrifft die Editoroberfläche, nicht die Layoutmaße, Styles oder Themes der bearbeiteten Formulare und ihrer gerenderten Vorschau.

Die Abnahme umfasst Workspace-Wechsel und Neustart für Filter, Favoriten und Dock-Zustände, schmale Dock-Breiten für die kompakte Darstellung sowie einen tatsächlich ausgelösten Assembly- oder Typauflösungsfehler für die Problems-Anzeige. Bereits vorhandene Implementierung gilt erst nach diesem Nachweis als erledigt.

Der externe Previewer liefert die eigentliche XAML-Ausnahme mit strukturierten Positionsangaben. Der Elementpfad ordnet sie auch nach der Vorschau-Serialisierung der Originalzeile zu. Im Desktop geprüft sind ein unbekanntes Fremdcontrol, Navigation mit Enter, Fehlerbehebung im selben Host und der Wechsel zu einem weiterhin fehlerfrei gerenderten zweiten Dokument. Die letzte gültige Vorschau bleibt als veraltet markiert. Ausgeklappte untere Docks erhalten eine nutzbare Anfangshöhe und behalten ihre Größe über Neustarts; die Höhe wird bereits beim ersten Aufklappen angewendet.

### Lesbarkeit auf Full-HD-Bildschirmen

- [x] Das gesamte Editorfenster bleibt bei 1920 x 1080 Pixeln mit 100 % und 125 % Skalierung lesbar und bedienbar, unabhängig von der Auflösung des Entwicklungsmonitors.
- [x] Die Schrift nicht verkleinern, um Platzprobleme zu verdecken. Toolbars umbrechen oder kompakte, beschriftete beziehungsweise mit Tooltip erklärte Aktionen verwenden; wesentliche Bedienelemente nicht abschneiden.
- [x] Scrollbars belegen ihren eigenen Platz und überdecken weder Zellinhalte noch Aktionen. Schmale Docks, Suchfelder, Kontextmenüs, Dialoge und Statusanzeigen gehören zur Abnahme.
- [x] Entwurf, XAML und Eigenschaften müssen auch auf Full HD praktisch gemeinsam nutzbar bleiben. Automatisch ausgeblendete Docks und Split-Anordnung dürfen Platz schaffen, aber keine Funktionen unzugänglich machen.

Die Desktop-Prüfung verwendet 1920 × 1080 logische Pixel bei 100 % und 1536 × 864 bei tatsächlich gemessenen 125 %. Entwurf und XAML bleiben mit Properties beziehungsweise Visual Tree gemeinsam sichtbar. Die Toolbars umbrechen; Scrollbars und Eigenschaftsaktionen haben getrennten Platz. Der Extensions-Dialog ist bei 125 % vollständig bedienbar. Die gemeinsame Dichte gilt auch für direkt erzeugte Dialoge und Editor-Toolbars; die Schriftgrößen bleiben unverändert. Nach dem Monitorwechsel wurden die vorherige Fensterposition und Größe wiederhergestellt.

## Designer-Toolbar und Visual Tree

- [x] Vorhandene Layoutbefehle zusätzlich als kompakte Toolbar anbieten: links, horizontal zentriert, rechts, oben, vertikal zentriert und unten ausrichten; horizontal und vertikal verteilen; gleiche Breite und gleiche Höhe.
- [x] PathIcons mit Tooltips und zugänglichen Namen verwenden. Die Toolbar nutzt dieselben Befehle und Undo-Transaktionen wie das Kontextmenü und bricht bei wenig Platz um.
- [x] Alle zehn Toolbaraktionen im laufenden Editor mit gemeinsamer Auswahl von Standard- und Fremdcontrols im Grid bedienen. Jede Aktion mit einem Undo quelltextgetreu zurücknehmen; Redo am Ausrichtungsbefehl prüfen.
- [x] Alle Toolbaraktionen auch in Canvas und StackPanel am laufenden Editor prüfen, einschließlich Hauptauswahl, ungültiger Auswahl, Undo/Redo und Speichern.
- [x] Toolbaränderungen nach Speichern und erneutem Öffnen des Dokuments prüfen.
- [x] Spalten im Visual Tree manuell verstellbar machen und mit geringerem Einzug sowie kompakteren Abständen mehr Platz für Typ- und Controlnamen schaffen.
- [x] Spaltengrenzen im laufenden Editor per Maus verschieben und die Änderung der tatsächlichen Spaltenbreiten prüfen.
- [x] Doppelklick zur Größenanpassung und Lesbarkeit tiefer Hierarchien in einem schmalen Dock prüfen.

Im 337 Pixel breiten Visual-Tree-Dock sind acht Knoten über sechs Ebenen per Tastatur aufgeklappt und ausgewählt. Ein tatsächlicher Doppelklick verändert die erste Spaltenbreite von rund 142 auf 188 Pixel. Lange Typ- und Controlnamen erhalten Ellipsen und vollständige Tooltips; die Children-Spalte bemisst sich nach ihrem Inhalt und ihrer Überschrift.

## Automation-IDs und isolierter Design-Host

- [x] In den Settings der Zeichenfläche eine standardmäßig ausgeschaltete Option für automatische `AutomationProperties.AutomationId` anbieten. Neue Controls und Kopien erhalten bei Bedarf dokumentweit eindeutige IDs; bestehende Controls bleiben unverändert.
- [x] Standard- und Fremdcontrols, Duplizieren, Speichern/Wiederöffnen sowie gemeinsame Undo-Schritte für Einfügen und ID-Vergabe prüfen.
- [x] Das Settings-Popup und das Eingabefeld für den Canvas-Abstand so bemessen, dass Zahlenwert und Pfeiltasten gleichzeitig sichtbar bleiben.
- [x] Previewer-Prozess und Transport-Session gemeinsam verwalten; parallele Startanforderungen zusammenführen und Ersatz-Sessions vor veralteten Beendigungsereignissen schützen.
- [x] Beim Schließen eines Dokuments dessen Previewer beenden. Prozessende und eine neue Transport-Session beim Wiederöffnen sind am Desktop geprüft.
- [x] Projektassembly-Metadaten ohne Laden der Assembly in den Editorprozess lesen.
- [x] Gemessene Controlpositionen aus dem isolierten Host mit stabilen AST-IDs und einer Kennung des gerenderten Dokumentstands übertragen und prüfen.
- [x] Die Klickauswahl im Previewer mit dem Dokumentmodell verbinden; Laufzeitinstanzen unterscheiden und Skalierung, Clipping sowie veraltete Hostdaten berücksichtigen.
- [x] Eigenschaften im Properties-Dock ohne lokale Control-Instanz über den Panel-Workflow bearbeiten; Nullable-/Flags-Werte, Zurücksetzen, Templates, Validierung, Undo/Redo und die Diagnoseanzeige beim Rendererwechsel prüfen.
- [x] Unter Windows per CDP ein fensterloses UserControl mit Standard-Button und Flowery-Button im externen Host prüfen: Klickauswahl, Größenänderung, Grid-Zellwechsel, unveränderten Quelltext während des Ziehens und exaktes Undo/Redo. Der lokale Control-Cache bleibt dabei leer.
- [x] Die native Abnahme auf weitere konkrete Formulare, direkte Eingaben im Properties-Dock, Canvas-/StackPanel-Gesten sowie komplexe Control- und Theme-Konfigurationen ausweiten.
- [x] Diese Hostdaten für Auswahl, Adorner und Bearbeitung verwenden, ohne die fremden Controls zusätzlich im Editorprozess zu instanziieren.
- [x] Beim Einfügen neuer XAML-Blöcke die vorhandene Zeilenendekonvention erhalten; keine CRLF-Blöcke in LF-Dokumente einsetzen.

Canvas und StackPanel sind zusätzlich mit Standard- und Bibliotheks-Buttons am Desktop geprüft. Belegt sind Auswahl, Tastaturänderungen, Umordnen beziehungsweise Verschieben per Maus, Canvas-Resize, unveränderter Quelltext während des Ziehens, Abbruch mit Escape und quelltextgetreues Undo/Redo. Gespeicherte Größen und Reihenfolgen bleiben beim Wiederöffnen erhalten. Die lokale Control-Map bleibt leer. Eine Dokumentänderung verwirft die bisherigen Host-Geometrien sofort; verspätete Geometrien des vorherigen Stands werden abgewiesen.

Zahlenwerte werden als validierte Texteingaben bearbeitet. Der UI-Ablauf prüft deutsche und englische Dezimaltrennzeichen, einen ungültigen Entwurf mit Fehlermeldung, dessen Korrektur bei erhaltener Auswahl und Fokus sowie quelltextgetreues Undo. Der gleiche Ablauf ist im nativen Properties-Dock mit einem extern gerenderten Bibliotheks-Control geprüft. Eine aktive Eigenschaftssuche erzeugt bei unveränderter Treffermenge keine neuen Eingabefelder.

## Unabhängige Assembly-Snapshots

- [x] Projektquellen und Laufzeit-Assemblies über eine lokale Workspace-Konfiguration trennen. Metadaten, C#-Referenzen und Previewer verwenden den Snapshot. Fehlende Ausgaben dürfen weder eine Suche in Live-Buildordnern noch einen automatischen Projektbuild auslösen.
- [x] Eine ausdrücklich ausgewählte Preview-Anwendung einschließlich ihrer Laufzeitdateien prüfen. Bei unvollständiger Auswahl eine verständliche Fehlermeldung liefern.
- [x] Die Bibliotheksvorschau mit gesperrter Live-DLL und den konfigurierten Workspace mit einer echten externen Vorschau prüfen. Der Editor und sein Previewer laden dabei keine Module aus dem Live-Ausgabeordner.

Die Einrichtung und das manuelle Aktualisieren beschreibt [ASSEMBLY-SNAPSHOTS.md](ASSEMBLY-SNAPSHOTS.md).

## Abgeschlossene Abnahme

Diese Liste hält die Abnahme der obigen Anforderungen fest; sie ersetzt oder verkleinert den Abnahmesatz nicht.

- [x] Unberührte Quellbereiche auch bei strukturellen Änderungen erhalten und mit Bearbeiten, Undo/Redo, Speichern und Wiederöffnen prüfen.
- [x] Den isolierten Designzeit-Host durchgängig mit der visuellen Bearbeitung verbinden, soweit Controls einen prozessweiten Designmodus oder eine eigene Anwendungsinitialisierung benötigen.
- [x] Die vollständigen Maus- und Tastaturabläufe für Canvas, Grid und StackPanel mit Standard- und Fremdcontrols in der Desktop-Anwendung abnehmen.

Zusätzlich zu den Gesten ist der Containerwechsel per Ausschneiden und Einfügen für Standard- und Bibliotheks-Buttons durch alle drei Layoutarten geprüft. Unpassende Canvas- und Grid-Zuordnungen werden entfernt. Duplizieren, Löschen und Mehrfachauswahl funktionieren auch mit fokussierter externer Vorschau. Über die Toolbox sind beide Controlarten in jeden Container eingefügt, gemeinsam gespeichert und erneut extern gerendert. Neue Namensräume werden kollisionsfrei am Wurzelelement deklariert; vorhandene Präfixe und Kommentare bleiben erhalten.
- [x] Collections, Tabs, Menüs, Spalten, Templates und Ressourcen einschließlich Fehlerkorrektur im tatsächlichen Eigenschaften-Dock vollständig abnehmen.
- [x] Die neue Problems-Anzeige einschließlich Navigation, Fehlerbehebung und Session-Wechsel im Desktop prüfen.
- [x] Die gemeinsame Oberflächendichte in schmalen Docks, Menüs und Dialogen sowie beim Monitor- und DPI-Wechsel prüfen.
- [x] Bedienbarkeit, Fortschritt, Abbruch und nutzbare Dokumentgrößen auf der Zielmaschine messen und die belegten Grenzen festhalten.
- [x] Lokale Eigenschaftsänderungen im externen Host auf die betroffenen Controls begrenzen. Der Host übernimmt geänderte Attribute in bestehende Instanzen. Hinzufügen und Zurücksetzen registrierter Avalonia-Properties einschließlich Attached Properties sind unterstützt. Zurücksetzen entfernt den lokalen Wert und macht Styles oder geerbte Werte wieder wirksam. Alle Instanzen eines Quellknotens werden gemeinsam geändert; fehlgeschlagene Änderungen werden zurückgenommen. Struktur-, Binding-, Namens- und Theme-Änderungen verwenden weiterhin den vollständigen Runtime-Lader, damit deren Dokumentkontext neu aufgebaut wird.
- [x] Die Thread-Zuordnung der vollständigen Typkatalog-Erfassung belegen. Beim Öffnen und nach einem Workspace-Build laufen sowohl `LoadAssemblies` als auch das initiale `GetAvailableTypes` auf einem Worker. `WorkspaceResponsivenessTests` prüft beide Thread-IDs gegenüber dem UI-Thread. Der Control-Katalog der Extensions und die Typauswahl des Struktureditors lesen ihren Katalog ebenfalls auf einem Worker.

`DesignerUpdateProtocolTests` prüft Änderungsumfang, Rücknahme und veraltete Revisionen. `RuntimeControlPatchTests` prüft erhaltene Instanzen, Ressourcen, Namensbindungen sowie das Setzen und Zurücksetzen an einem Fremdcontrol. `PreviewerLaunchServiceTests` führt die Aktualisierung und Rücknahme zusätzlich im echten Bibliotheks-Host mit und ohne Assembly-Snapshot aus und prüft die gemeldete Geometrie. Nicht unterstützte Änderungen bleiben über den vollständigen Lader bearbeitbar.

### Gemessene Dokumentgrößen und Ladeabbruch

Messung vom 21.09.2026 im Debug-Desktop auf einem Intel Core i7-13700K mit 24 logischen Prozessoren und rund 64 GiB RAM. Der Workspace war bereits geladen. Die Canvas-Testdateien enthalten zu gleichen Teilen Standard-Buttons und Bibliotheks-Buttons bei einer festen Zeichenfläche von 1000 × 800; bei großen Fällen liegen Controls außerhalb des sichtbaren Ausschnitts. Pro Messung war genau ein Previewer aktiv. Die lokale Control-Map blieb leer. Die folgende Ausgangsmessung verwendet bei jeder Änderung einen vollständigen Neuaufbau:

| Controls | AXAML-Bytes | Öffnen bis Hostgeometrie | Verschieben bis neue Hostgeometrie | Längste UI-Antwort beim Öffnen / Verschieben | Host-Arbeitssatz nach Öffnen / Verschieben |
|---:|---:|---:|---:|---:|---:|
| 100 | 11.553 | 2,81 s | 1,80 s | 328 / 901 ms | 262 / 293 MiB |
| 1.000 | 115.138 | 6,63 s | 3,03 s | 359 / 925 ms | 611 / 619 MiB |
| 5.000 | 582.538 | 11,26 s | 10,89 s | 711 / 1.390 ms | 1.197 / 2.122 MiB |

Mit der gezielten Aktualisierung derselben Verschiebeaktion ergeben sich folgende Werte:

| Controls | Verschieben bis neue Hostgeometrie | Median / längste UI-Antwort | Host-Arbeitssatz nach Verschieben |
|---:|---:|---:|---:|
| 100 | 0,54 s | 2,8 / 398 ms | 242 MiB |
| 1.000 | 0,84 s | 4,0 / 608 ms | 651 MiB |
| 5.000 | 1,60 s | 10,8 / 981 ms | 1.247 MiB |

Dies sind einzelne gemessene Läufe, keine statistischen Grenzwerte oder allgemeine Leistungszusage. Der Arbeitssatz ist eine Momentaufnahme, kein Peak. Die Aktualisierung vorhandener Controls verkürzt die Vorschauwartezeit; Lesen, Parsen und Geometrieübertragung bleiben von der Dokumentgröße abhängig. 5.000 Controls sind als Belastungsfall geladen und bearbeitet, aber nicht als flüssiger Arbeitsbereich abgenommen. Auch bei 1.000 Controls bleibt eine messbare Verzögerung. Ein weiterer Öffnungslauf mit 1.000 Controls benötigte 8,53 s; die Änderung verkürzt nicht den initialen Aufbau.

Das Lesen und Parsen erfolgt abbrechbar mit sichtbaren Ladephasen und einer Cancel-Aktion. Während des Ladens sind Inhalt und Speichern gesperrt. Ein eigener Abbruchlauf mit 20.000 einfachen Controls hat die sichtbare Fortschrittsanzeige und den fokussierten Cancel-Button während des Parsens geprüft. Danach blieb kein Dokument geöffnet und kein Previewer-Prozess aktiv. Der Abbruchtest des Sync-Engines erhält das zuvor geladene Dokument und dessen Undo-Historie.
