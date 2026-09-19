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
- Die aktiven Collection- und Template-Editoren in `PropertyEditorPanelView.axaml` sind Textfelder. Strukturelle Bearbeitung ist damit noch nicht nachgewiesen.

Diese Befunde bestimmen die ersten Arbeitspakete. Sie ersetzen keine vollständige Bestandsaufnahme und keinen praktischen Bediennachweis.

`ToolboxCatalogWorkflowTests` prüft die Assembly-Gruppierung mit zwei echten externen Assemblies: gebundene Gruppen, alphabetische Sortierung, Suche, Auswahl, die Erzeugung eines gleichnamigen Fremdcontrols, Platzfreigabe und Zustandserhalt beim Einklappen sowie das Entfernen von Einträgen nach einem Scopewechsel. `ToolboxExtensionTests` prüft zusätzlich die Gruppierung benutzerdefinierter Katalogeinträge mit unveränderten Einfügeargumenten. Diese Headless- und Integrationstests ersetzen keine Desktop-Abnahme der folgenden Arbeitspakete.

## Arbeitspakete und Abnahme

### 1. Dokument- und Eigenschaftstreue

Leerwerte und Reset unterscheiden. Änderungen und Eventzuordnungen über einen gemeinsamen Transaktionspfad übernehmen. Bindings, Ressourcenverweise und verschachtelte Property-Elemente beim Bearbeiten benachbarter Werte erhalten. Änderungen speichern, erneut öffnen und Undo/Redo prüfen.

Die Wiederherstellung erfolgt über vollständige Dokument-Checkpoints mit stabilen Knoten-IDs. Verschachtelte Edit-Transaktionen erzeugen einen Undo-Eintrag; Rollback verwirft ausschließlich die laufende Operation. Der Designer bindet Eigenschaften, Einfügen, Löschen, Umschließen und Gesten an diese Transaktionen. Escape und verlorener Pointer-Capture brechen laufende Verschiebe-/Resize-Gesten ab.

`DesignerTransactionWorkflowTests` prüft für Canvas, Grid und StackPanel einen kombinierten Struktur-/Eigenschaftseingriff mit Löschen eines Teilbaums, Undo/Redo, Abbruch, Speicherung und erneutem Öffnen. Bindings, Ressourcen und Identitäten werden dabei verglichen. Ein zusätzlicher Ablauf erhält unvollständigen XAML-Code beim Speichern und verhindert dessen Überschreiben durch visuelle Eingriffe. Die verbleibende Quelltreue bei normalen Serialisierungsänderungen sowie die sichtbare Bedienung sind damit noch nicht vollständig abgenommen.

### 2. Durchgängige Bedienung aller drei Layoutarten

Je ein vollständiges Canvas-, Grid- und StackPanel-Formular visuell erstellen und bearbeiten. Einfügen, Auswahl, Verschieben, Größenänderung, Parent-Wechsel und Reihenfolge mit Maus und Tastatur umsetzen. Die drei Layoutarten werden gemeinsam abgenommen.

`LayoutEditService` bündelt Kapazitäts-, Zyklus- und Platzierungsregeln für implizite Inhalte und explizite Property-Elemente. Toolbox-Einfügen, Umschließen, Löschen sowie Surface-/Tree-Drops verwenden diese Regeln. Umhängen entfernt nicht mehr gültige Canvas-, Grid- und Dock-Zuordnungen; andere Werte und Bindings bleiben erhalten. Grid-Zuordnungen und Spans werden auf vorhandene Definitionen begrenzt.

Toolbox-Drags transportieren Typ und Namespace. Die Designfläche ermittelt das Ziel unter dem Zeiger und verwendet Canvas-Koordinaten, Grid-Zellen beziehungsweise die Einfügeposition eines StackPanels. `LayoutEditingWorkflowTests` prüft den Containerwechsel mit Undo, Speicherung, erneutem Öffnen, belegten Content-Slots und einem Container aus einer externen Bibliothek. Die vollständige Maus-/Tastatur-Abnahme bleibt gesondert erforderlich.

### 3. Projektbezogene Toolbox und Fremdcontrols

Controls aus dem aktiven Projekt und seinen Abhängigkeiten in der Toolbox anbieten. Typidentität, Namespace-Ergänzung, Vererbung, Themes und Inhaltsmodelle prüfen. Den kompletten Ablauf aus Arbeitspaket 2 mit Controls einer externen Bibliothek wiederholen.

- [ ] Toolbox-Controls nach Assembly in eigenen Expandern gruppieren, statt sämtliche Drittanbieter-Controls in einem gemeinsamen Block anzuzeigen. Gruppen und Controls jeweils alphabetisch sortieren. Die Gruppen erhalten verständliche Bibliotheksnamen, beispielsweise eine Sektion "Flowery.NET" für deren Controls.

### 4. Struktur-, Ressourcen- und Eventeditoren

Collections, Tabs, Menüs, Spalten, komplexe Werte, Templates und Ressourcen strukturell bearbeiten. Eventhandler und Benennung integrieren. Änderungen bleiben Teil derselben Undo-/Redo-Historie.

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

- [ ] **Assembly-Filter je Workspace:** Der Benutzer kann die angezeigten Assemblies über eine Mehrfachauswahl mit Checkboxen festlegen. Die Auswahl wird je Workspace gespeichert und beim erneuten Öffnen wiederhergestellt. Suche und Assembly-Filter wirken gemeinsam; neu verfügbare oder vorübergehend fehlende Assemblies dürfen gespeicherte Auswahlen nicht verlieren.
- [ ] **Favoriten für Drittanbieter-Assemblies:** Jede Drittanbieter-Sektion erhält eine direkt bedienbare Favoriten-Markierung, beispielsweise einen Stern. Favorisierte Assemblies stehen zusammen vor den übrigen Drittanbieter-Assemblies; innerhalb beider Gruppen bleibt die alphabetische Sortierung erhalten. Favoriten werden gespeichert und umgehen den Assembly-Filter nicht.
- [ ] **Kompakte Toolbox-Einträge:** Vertikales Padding, Zeilenhöhe und Zwischenräume reduzieren. Informationen, die bereits im Assembly-Gruppenkopf stehen, nicht in jeder Control-Zeile wiederholen; zusätzliche Metadaten bei Bedarf als Tooltip anbieten. Auswahl, Fokus und Einfügeaktion bleiben eindeutig erkennbar und per Tastatur bedienbar.
- [ ] **Zustand der unteren Docks wiederherstellen:** Alle unteren Docks, einschließlich Output, Breakpoints und LSP Settings, behalten ihren zuletzt gewählten angehefteten, automatisch ausgeblendeten beziehungsweise aufgeklappten Zustand über einen Neustart. Ein geschlossenes Dock mit inaktivem Pin bleibt als Reiter am unteren Rand und öffnet sich nicht selbstständig. Vorhandene Layout-Persistenz prüfen und ergänzen; insbesondere darf LSP Settings einen wiederhergestellten geschlossenen Zustand nicht beim Start überschreiben.
- [ ] **Lade- und Renderfehler im Problems-Dock:** Den bestehenden Diagnoseweg prüfen. Nicht gefundene Assemblies, nicht auflösbare Typen und vergleichbare Lade- oder Renderfehler mit Datei, betroffenem Element und Ursache im Problems-Dock anzeigen, statt den visuellen Designer mit Fehlermeldungen oder Fehler-Controls zu füllen. Soweit möglich zur betroffenen XAML-Stelle navigieren. Eine erhaltene letzte gültige Vorschau darf nur zum selben Dokument gehören und muss als veraltet erkennbar sein; nach der Fehlerbehebung verschwinden überholte Diagnosen.
- [ ] **Kompakte Editoroberfläche insgesamt:** Padding, Mindesthöhen und Abstände in den vorhandenen Docks, Listen, Baumansichten, Toolbars, Menüs und Dialogen nach gemeinsamen Regeln reduzieren. Ziel sind weniger unnötiges Scrollen und eine bessere Nutzung schmaler Docks, nicht eine allgemeine Verkleinerung der Schrift. Bedienbarkeit, Fokusdarstellung und Fehlermeldungen erhalten. Diese Dichteanpassung betrifft die Editoroberfläche, nicht die Layoutmaße, Styles oder Themes der bearbeiteten Formulare und ihrer gerenderten Vorschau.

Die Abnahme umfasst Workspace-Wechsel und Neustart für Filter, Favoriten und Dock-Zustände, schmale Dock-Breiten für die kompakte Darstellung sowie einen tatsächlich ausgelösten Assembly- oder Typauflösungsfehler für die Problems-Anzeige. Bereits vorhandene Implementierung gilt erst nach diesem Nachweis als erledigt.
