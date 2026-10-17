# Plan: Git-Integration für XamlVisualEditor (erster PR)

## Stand

Planungsdokument des Forks; geht nicht upstream und liegt nur auf `xve-form-editing`. Ziel ist ein Upstream-PR mit
einem Commit je Phase.

Arbeitsweise: Die Git-Integration wird komplett im Fork umgesetzt, auf dem PR-Branch `git-integration` (Basis
`upstream/main`, Worktree `github\XamlVisualEditor-git`). Kein Abgleich mit dem TFVC-Arbeitsstand während der Arbeit;
erst nach dem Upstream-Merge wird der Stand in einem Schritt übernommen. Jede Phase endet mit grünem Build, grünen
Tests und einem Livetest per CDP. Testrepository: `xve-demo\GitDemo` mit lokalem Remote
`xve-demo\GitDemo-remote.git`.

| Phase | Stand |
|---|---|
| 0 CLI-Fundament | erledigt, live geprüft |
| 1 Session, Statusleiste, Kommandos | erledigt, live geprüft (inkl. Palette blendet Befehle mit falschem When aus, `IOutputChannel.Show()` holt den Kanal nach vorn) |
| 2 Branches und Sync | erledigt, live geprüft, gepusht (985d441) |
| 3 Verlauf, Commit-Details, Tags | erledigt, live geprüft, gepusht (3447b72) |
| 4 Explorer-API | erledigt, live geprüft, gepusht (62096e6), eine Review-Runde |
| 5 Editor-API, Vergleich, Blame | erledigt, live geprüft, gepusht (0ae2ec7), eine Review-Runde |
| 6 Feinere Commits, Stash | erledigt, live geprüft, gepusht (19a3662), Review + Datenverlust-Nachprüfung |
| 7 Merge und Konflikte | offen |
| 8 TFVC nutzt die gemeinsamen Teile | offen |

Vor jedem Push läuft ein codereview-roasted; alle Befunde werden behoben, danach Livetest per CDP, dann Push.
Alle Reviews bis Phase 2 (Stand 985d441) sind abgearbeitet, keine offenen Befunde. Festgehaltene Entscheidungen:

- Unstage nutzt die Liste der gestagten Änderungen statt `git reset`, weil `reset` einen laufenden Merge abbricht;
  Ordner-Discard verwirft nur die bestätigte Liste.
- Fetch ist schlicht `git fetch <remote>` je Remote: keine erzwungene Prune, keine eigenen Refspecs (sonst fehlen
  konfigurierte Notes-/Tag-Refspecs oder lokale Tags gehen verloren); `skipFetchAll` und die Prune-Einstellung des
  Benutzers gelten.
- Fetch und Push laufen ohne Schreibsperre; Pull = abbrechbarer Fetch plus kurzes `merge --ff-only` unter der Sperre.
- `--track` nur ohne Flag bei gleichem Namen (respektiert `branch.autoSetupMerge`), sonst `--no-track`.
- Unter Windows wird der Git-for-Windows-Launcher `cmd\git.exe` zum echten Binary aufgelöst, damit das Job-Object
  auch Hook-Kindprozesse beim Abbruch beendet.

- Pull wie `git pull`: Upstream aus `branch.<b>.remote` und dem ersten `branch.<b>.merge`-Wert, Fetch des ganzen
  Remotes, dann muss `FETCH_HEAD` den Merge-Eintrag genau dieses Werts enthalten (sonst „Upstream gelöscht“, auch ohne
  Prune; Sync pusht nach einem fehlgeschlagenen Pull nie). Danach `rev-parse @{upstream}` zur SHA,
  `merge --ff-only <sha>` mit Reflog „pull“, Zählung `ORIG_HEAD..<sha>` (null bei ungeborenem Branch).
- Der aufgelöste Git-Prozess bekommt die Umgebung des Launchers: `MSYSTEM`, `HOME`, PATH-Reihenfolge
  `<env>\bin`, `usr\bin`, `%HOME%\bin` (Record `GitForWindowsLayout`); `ExecutablePath` und Scoop-Shims werden
  ebenso aufgelöst.

Arbeitsregel ab Phase 4: Jeder Agent lädt `codereview-roasted` vor dem Codieren und prüft seinen Diff vor dem Commit
selbst; nur die einfachste Lösung, keine Workaround-Maschinerie. Vor dem Push werden Befunde behoben, die Nutzer merken
oder Ergebnisse verfälschen; Stil-Kleinkram kommt auf die Sammelliste und wird einmal vor dem Upstream-PR abgearbeitet.

Phase-4-Entwurf (ersetzt 4.1 und 4.2, freigegeben):

- Menüs: nur zwei neue Orte `ExtensionMenuLocations.ExplorerContext` und `DocumentTabContext`, Argumente immer
  `[fullPath]`; vorhandene `ExtensionMenuContribution`/`When` wiederverwenden; `ExtensionAsyncCommand` reicht den
  `CommandParameter` durch; `MenuItemsBehavior` auf `ItemsControl` verallgemeinert; keine Wrapper-ViewModels, kein
  `isDirectory`. Knoten ohne Pfad (Solution, Referenzen) blenden die Einträge aus; Projektknoten wirken auf den
  Projektordner.
- Dekorationen als Push-Layer (gleiches Muster wie die Zeilenmarker aus 4.3): `IExplorerDecorations.CreateLayer()`,
  `IExplorerDecorationLayer.Set(Pfad → ExplorerDecoration)`, `Dispose` entfernt; `ExplorerDecoration(Kind, Badge,
  ToolTip)` mit `Kind` nur als Farbklasse (Modified, Added, Deleted, Conflicted). Kein Pull-Provider, keine Priorität,
  keine eigene Drossel, keine Vererbung im Host: ein ungetrackter Ordner zeigt „U“, Dateien darin bleiben ohne Markierung.
- Git füttert aus `GitSession.Snapshots` ohne Git-Prozess je Element; jeder Vorfahrenordner einer Änderung bekommt einen
  Eintrag ohne Badge (Conflicted, wenn darunter ein Konflikt liegt, sonst Modified). Schalter `git.toggleExplorerDecorations`.
- Host: Dictionary mit `FileSystemPathComparison.Comparer`, atomar getauscht, ein Durchgang über die geladenen Knoten;
  vier Theme-Pinsel hell/dunkel, kein Opacity.

Phase-5-Entwurf (ersetzt 4.3 und 4.4, freigegeben):

- API nur als Default-Member auf vorhandenen Hosts: `ITextEditorViewHost.CreateLineMarkLayer(clickCommandId)` mit
  `ITextEditorLineMarkLayer.Set(Pfad, Marker)` (Push-Layer wie Phase 4; `LineCount 0` = entfernte Zeilen) und
  `IExtensionViewHost.ShowDocumentAsync(id, title, toolTip, createViewModel)` (vorhandener Tab wird aktiviert).
- Ein Diff-Weg für Gutter und Vergleich: eigener `LineDiff` (Präfix/Suffix abschneiden, Myers mit Obergrenze), Rx
  `Throttle(300 ms)` + `Switch`, Rechnen außerhalb des UI-Threads. Gutter-Basis nur der Stage-Stand; Cache-Schlüssel ist
  die Index-Blob-ID `hI` aus porcelain v2 (`GitFileChange.IndexObjectId`), also kein zusätzlicher git-Prozess.
- „Keine Basis, keine Marker“ für außerhalb des Repos, untracked, Konflikt, binär, LFS-Pointer, zu groß; Dekodierung
  beider Seiten über `SharedFileReader.Decode`, damit CRLF/BOM keine Scheinänderungen ergeben.
- Vergleichsdokument (HEAD/Stage) mit `UnifiedDiffEditor`, ein Hunk pro Änderungsblock, „ganze Datei“ ohne neuen Diff;
  Klick im Gutter springt zum Block. Blame mit `git blame --porcelain` auf der gespeicherten Datei als virtualisierte
  Liste; Doppelklick öffnet den Verlauf mit vorgewähltem Commit.
- Pinsel neutral benannt (`SourceControl…Brush`) und von Explorer-Dekorationen und Gutter gemeinsam genutzt.

Phase-6-Entwurf (freigegeben):

- Hunk und Zeile ein Weg: Auswahl = Zeilenbereich im angezeigten Diff; der Service liest denselben Diff neu, lehnt bei
  Abweichung ab (veralteter Diff), baut den Patch aus den Rohbytes und ruft `git apply [--cached] [--reverse] --recount
  --whitespace=nowarn` (Stage `--cached`, Unstage `--cached --reverse`, Verwerfen `--reverse`, Schreibsperre).
  Diff-Argumente immer mit `--no-ext-diff --no-textconv --src-prefix=a/ --dst-prefix=b/`.
- Builder-Regeln: unausgewählte `-`/`+` werden Kontext bzw. entfallen je Richtung; Auswahl, die eine Zeile an
  „No newline at end of file“ hängen würde, wird abgelehnt (git verklebt sonst still zwei Zeilen); Teilauswahl bei neuen
  oder gelöschten Dateien schreibt den Dateikopf um. Kein Teil-Staging für unversionierte Dateien.
- Registrierte Befehle `git.stageSelection`/`unstageSelection`/`discardSelection`; Verwerfen fragt immer nach.
  Changes-Panel nutzt `UnifiedDiffEditor`; `GitPanelViewModel.DiffLines` wird `IReadOnlyList<DiffLine>` (PR-Notiz).
- Amend: Checkbox in der Commit-Box, Nachricht aus HEAD vorbelegt; bei gepushtem HEAD Warnung mit Bestätigung.
- Stash-Reiter im Repository-Panel: Stash (optional mit unversionierten Dateien), Apply über Commit-ID, Pop/Drop mit
  Prüfung der Stash-ID, Drop fragt nach; kein „Show“ in Phase 6. Konflikte über den Snapshot erkannt.

Sammelliste für den Schliff vor dem PR:

- `HEAD@{n}` jenseits des Reflogs meldet nur „git rev-parse exited with code 128.“ statt gits Meldung.
- Die Warnung bei mehrdeutigen Namen bleibt durch `rev-parse --quiet` unterdrückt (nötig für die Exitcode-Trennung).
- Beim Wechsel von einem gekürzten zu einem vollständigen Diff wird der alte Text zweimal neu aufgebaut.
- Commit-Details: Die Kopfzeile ist eine Star-Zeile mit `MaxHeight=160`; bei kurzer Nachricht bleibt Leerraum.
- Commit-Details: Das Zurücksetzen des Kopfs hängt am Text der Zusammenfassung statt am Commit.
- Testlücken zu den beiden Layout-Befunden aus Review 0063f77.
- Phase 4 (Review `review_phase4`): Dekorations-Lookup normalisiert den Pfad nicht (nur beim Setzen); dunkles Theme mit
  orangem Akzent auf markierter Zeile unter der Maus: Deleted 2,6:1, Added 3,2:1; plus fünf weitere Kleinigkeiten.
- Phase 5 (Review `review_phase5`): elf Kleinigkeiten; tote Rückkehrbedingung in `BreakpointMarginBehavior`
  (`e.Handled && Source == this`) seit dem Tunnel-only-Fix. Test-Breakpoint in `xve-demo\GitShowcase\App.axaml.cs`
  Zeile 12 vor neuen Screenshots entfernen.
- Phase 6 (Reviews `review_phase6`, `review_19a3662`): zehn Kleinigkeiten, u. a. Verwerfen eines Submodul-Hunks meldet
  Erfolg ohne Wirkung; aus dem Diff kopierter Text enthält `␍` statt CR; ein fehlgeschlagener Status-Lauf hebt den
  Amend-Haken auf; Dialogtitel von „Stash … Including Untracked“ gleich dem normalen Stash.

Für die PR-Beschreibung: Die Git-Views überschreiben die Vordergrundfarbe gesperrter Buttons nicht mehr (der Stil stammt
aus Upstream-`GitPanelView`), gesperrte Buttons sehen deshalb jetzt gesperrt aus. Weitere Änderungen an
Upstream-Dateien (`GitPanelView.axaml`, `GitPanelViewModel.cs`, `GitDiffTextEditor.cs`): Statusspalte nach breitestem
Text bemessen und per Strg+C kopierbar, Diff-Farben per Ressourcenschlüssel überschreibbar. `MenuItemsBehavior` arbeitet
auf jedem `ItemsControl` und behält seine Einträge beim Schließen (Xaml.Behaviors löst bei jedem Menü-Schließen ab).
Bekannte Grenze: Ein über eine Junction geöffneter Workspace bekommt keine Git-Funktionen, weil git den Zielpfad meldet.
Mitbehobener Upstream-Fehler: Ein Klick auf eine Zeilennummer setzte keinen Breakpoint (Handler in Tunnel und Bubble,
schaltete doppelt); `EditorServicesAdapter.GetTextAsync` marshallt jetzt auf den UI-Thread (AvaloniaEdit `VerifyAccess`).
Material-Icon `unfold_more` mit Eintrag in THIRD-PARTY-NOTICES. Phase 6: `GitPanelViewModel.DiffLines` ändert den Typ
auf `IReadOnlyList<DiffLine>`; der Panel-Diff ignoriert `diff.external` und textconv (behebt Exit 128 mit `diff.external`). Demo-Screenshots als Rohbilder unter
`xve-demo\shots\git\` (Showcase-Repo `xve-demo\GitShowcase`), vor dem PR neu aufnehmen und annotieren.

UI-Vorgabe für alle Git-Panels: Jeder Button bekommt ein `PathIcon` nach der Icon-Konvention der übrigen Panels (vorhandene Geometrien wiederverwenden, Theme-Pinsel, keine festen Farben, kein Opacity) und behält Beschriftung oder Tooltip.

Entscheidung zu 1.4: Der `.git`-Watcher und das Aktualisieren nach eigenen Kommandos laufen immer; der rekursive
Arbeitsverzeichnis-Watcher bleibt ein Schalter.

Analysiert am Quellstand des Branches `xve-form-editing`. Nichts wurde geändert.

## 1. Kernentscheidungen

1. **Git-CLI bleibt, kein LibGit2Sharp.** Es gibt keinen starken Grund für die native Abhängigkeit; die CLI bringt Credential-Manager, Hooks, `.gitattributes`, LFS und fsmonitor mit. Der bestehende Prozessaufruf in `GitService.RunGitAsync` muss aber gehärtet werden (Phase 0).
2. **Schichtung wie heute:** Core = Interfaces, Modelle und reine Parser (wie `GitStatusParser`/`GitDiffParser`); Workspace = CLI-Implementierung (`GitService`); App = DI-Verdrahtung; Extension = UI, Session, Commands und Provider. `IGitService` bleibt unverändert; neue Fähigkeiten kommen in kleine, getrennte Interfaces (siehe 3.1). Das ist additiv und vermeidet laut AGENTS.md ein God-Interface.
3. **Keine gemeinsame `ISourceControlProvider`-Abstraktion für TFVC und Git.** Die Modelle unterscheiden sich zu stark: TFVC hat Server-Workspace, Check-out, Include/Exclude, Work Items, Offline-Modus und Changeset-Nummern; Git hat Index, Branches, Stash und SHAs. Eine gemeinsame Abstraktion wäre bei zwei Providern, von denen einer nur unter Windows läuft, Over-Engineering. Geteilt wird stattdessen:
   - **Host-Erweiterungspunkte** (generisch, auch von TFVC nutzbar): Explorer-Dekorationen, Explorer-/Tab-Kontextmenüs, Editor-Zeilenmarker, Extension-Dokument-Tabs.
   - **UI-Bausteine** im neuen Projekt `src/XamlVisualEditor.SourceControl.UI`, weil `extensions/XamlVisualEditor.GitExtension/Controls/GitDiffTextEditor.cs` und `extensions/XamlVisualEditor.TfvcExtension/Controls/TfvcDiffTextEditor.cs` heute zu etwa 95 % identisch sind.
4. **Live-Überwachung wird eine persistierte Einstellung `git.autoRefresh` (Vorgabe ein).** Statusleiste und Dekorationen brauchen einen Status. Der heutige Opt-in-Schalter (`GitPanelViewModel.IsGitMonitoringEnabled`, laut Commit 8545ffc aus Performance-Gründen eingeführt) bleibt als Schalter für den rekursiven Arbeitsverzeichnis-Watcher; der `.git`-Watcher (HEAD, index, refs) und das Aktualisieren nach eigenen Kommandos laufen immer. Diese Verhaltensänderung braucht deine Zustimmung, weil der Test `Git_remains_off_until_enabled_and_cancels_running_scan` angepasst werden muss.
   Abweichung in Phase 1: `git.autoRefresh` steht in der Vorgabe auf aus, wie der bisherige Opt-in-Schalter, weil der Schalter nur noch den rekursiven Watcher steuert, dessen Kosten der Grund für das Opt-in waren; Status, Statusleiste und Context-Keys sind ohne ihn aktuell.
5. **Merge passt in den ersten PR.** Konfliktlösung ist ohnehin gefordert, und `git merge --no-edit <branch>` ist billig. Rebase kommt später.
6. **3-Wege-Merge-Editor ist nicht billig, kommt später.** Im ersten PR: Konfliktliste, „eigene/fremde Version übernehmen“ pro Datei, „als gelöst markieren“, Merge abbrechen, Konfliktmarker im Gutter.
7. **Blame ist machbar:** `git blame --porcelain --contents -` mit dem Editorinhalt über stdin, damit auch ungespeicherte Zeilen korrekt als „noch nicht committet“ erscheinen. Anzeige als Dokument-Tab mit eigener Annotation-Margin.
8. **Visueller XAML-Diff im Designer kommt später.** Der Previewer rendert nur offene Designer-Dokumente im Projektkontext (`IEditorServices.ExportPreviewAsync`); für die HEAD-Fassung bräuchte es eine neue Previewer-API, die XAML-Text mit Projektkontext rendert. Billig im ersten PR ist nur: HEAD-Fassung schreibgeschützt als Text-Diff-Tab.

## 2. Befunde im Ist-Zustand

- `src/XamlVisualEditor.Workspace/GitService.cs`, `RunGitAsync`: kein `StandardOutputEncoding` (plattformabhängige Dekodierung, unter Windows kaputte Umlaute), stdin weder umgeleitet noch geschlossen, kein `GIT_TERMINAL_PROMPT=0`, kein `--no-optional-locks` (`git status` kann den Index schreiben und so den eigenen Watcher auslösen), Commit-Nachricht per `-m` auf der Kommandozeile statt `-F -` über stdin.
- `src/XamlVisualEditor.Core/GitStatusParser.cs`: porcelain v1 ohne `-z` und ohne Unquoting. Mit der Vorgabe `core.quotepath=true` kommen Pfade mit Umlauten als `"\303\244..."`; Stage, Unstage und Discard scheitern dann heute für solche Dateien. Für deutschsprachige Nutzer relevant.
- `GitPanelViewModel` vereint Status, Watcher und Refresh-Steuerung (Single-Flight `_refreshRunning`/`_refreshRequested`) im Panel-ViewModel. Statusleiste und Dekorationen brauchen einen gemeinsamen Zustand, wie TFVC mit `TfvcSession`. Der Watcher ignoriert `.git` vollständig, Branchwechsel im Terminal bleiben unbemerkt.
- Kontextmenüs: `ExtensionTreeView.axaml` und `SolutionExplorerView.axaml` haben fest verdrahtete Kontextmenüs; das Tab-Menü (`DocumentFileMenuItems` in `src/XamlVisualEditor.App/Styles/EditorTheme.axaml`, eingehängt über `DocumentTabContextMenus.Extend`) ist statisch; `ShellViewModels.CreateExtensionCommand` ruft Kommandos immer ohne Argumente auf (`ExecuteAsync(commandId, null, …)`).
- `ExtensionTreeViewModel.OnProviderChanged` lädt bei jedem `Changed` den ganzen Baum neu. Dekorationsänderungen dürfen deshalb nicht über `ITreeDataProvider.Changed` laufen.
- `src/XamlVisualEditor.App/ViewLocator.cs` bildet ViewModels fest auf Views ab (`Build` und `Match`); jedes neue Git-ViewModel muss dort eingetragen werden.
- `CommandPaletteCoverageTests` baut `new GitExtensionEntry(new GitService(), host.WorkspaceInfo, new InMemoryDialogHost())`. Dieser Konstruktor muss bleiben; alle Kommandos müssen beim Aktivieren synchron registriert werden, unabhängig davon, ob Git oder ein Repository vorhanden ist (Sperre über `When`).

## 3. Architektur

### 3.1 Core (`src/XamlVisualEditor.Core`)

Neue Interfaces in `Interfaces/`, jeweils mit `repoPath` pro Aufruf wie `IGitService`:

| Interface | Methoden (gekürzt) |
|---|---|
| `IGitHistoryService` | `GetLogAsync(repo, GitLogQuery)`, `GetCommitAsync(repo, sha)` (Details mit geänderten Dateien), `GetCommitFileDiffAsync(repo, sha, path)`, `GetTagsAsync`, `GetBlameAsync(repo, path, byte[]? contents)` |
| `IGitBranchService` | `GetBranchesAsync`, `SwitchAsync(name)`, `CreateAsync(name, startPoint, switchTo)`, `DeleteAsync(name, force)` |
| `IGitRemoteService` | `GetRemotesAsync`, `FetchAsync`, `PullAsync`, `PushAsync(setUpstream)` → `GitRemoteResult` (Success, IsAuthenticationFailure, IsRejected, Message) |
| `IGitStashService` | `ListAsync`, `PushAsync(message, includeUntracked)`, `ApplyAsync/PopAsync/DropAsync(index)`, `ShowAsync(index)` |
| `IGitPatchService` | `GetRawDiffAsync(repo, GitDiffRequest)` → `GitRawDiff` (Bytes plus Hunks mit Byte-Bereichen), `ApplyAsync(repo, byte[] patch, GitApplyTarget, reverse)`, `CommitAsync(repo, GitCommitOptions{Message, Amend})`, `GetHeadMessageAsync` |
| `IGitContentService` | `GetFileContentAsync(repo, path, GitRevision.Index/Head/Sha)` → `byte[]?`, `IsLfsTrackedAsync` |
| `IGitMergeService` | `MergeAsync(branch)` → `GitMergeResult`, `AbortAsync`, `TakeOursAsync/TakeTheirsAsync/MarkResolvedAsync(paths)` |
| `IGitCommandLog` | Ereignis `CommandCompleted(GitCommandLogEntry)`; die Extension schreibt es in den Output-Kanal „Git“ |
| `IGitRepositoryService` | `GetGitDirectoryAsync(repo)`, `GetRemoteNamesAsync(repo)`, `InitAsync(path)`; in Phase 1 ergänzt, weil Session (`.git`-Watcher, `git.hasRemote`) und `git.init` sie brauchen |

Modelle: neue Datei `GitHistoryModels.cs` (`GitCommitInfo`, `GitCommitDetails`, `GitCommitFileChange`, `GitLogQuery`, `GitBranch`, `GitStashEntry`, `GitTag`, `GitBlameLine`, `GitRawDiff`, `GitRemoteResult`, `GitMergeResult`, `GitServiceSet` als Parameterobjekt mit den Interfaces). `GitModels.cs` additiv: `GitRepositoryStatus.HeadSha`, `IsDetached`, `IsUnborn`, `Operation` (None/Merging/Rebasing/CherryPicking/Reverting), `GitFileChange.IsConflicted` und `ConflictKind`.

Parser (rein, ohne Prozess): `GitStatusParser.ParsePorcelainV2(repoPath, output)` (additiv, `-z`), `GitLogParser` (Format mit `%x1e`/`%x00`), `GitRefParser` (for-each-ref), `GitStashParser`, `GitBlameParser` (porcelain), `GitPatchBuilder` (Hunk- oder Zeilenauswahl → Patch-Bytes, berechnet die Hunk-Header neu), `GitRemoteErrorClassifier`.

`LineDiff.cs`: Myers-Zeilendiff O(ND) mit Obergrenzen, für Gutter-Marker und später Side-by-side. Alternative DiffPlex bringt eine Abhängigkeit und einen THIRD-PARTY-NOTICES-Eintrag; die eigene Fassung hat rund 200 Zeilen.

### 3.2 Workspace (`src/XamlVisualEditor.Workspace`)

- `GitProcessRunner.cs` (internal), herausgelöst aus `RunGitAsync`: UTF-8 für stdout/stderr, optional rohe Bytes; stdin als `byte[]` (UTF-8 ohne BOM) oder sofort geschlossen; immer `-c core.quotepath=off`; Umgebung `GIT_TERMINAL_PROMPT=0`, `GIT_LITERAL_PATHSPECS=1` (wie bisher), für Lesekommandos `GIT_OPTIONAL_LOCKS=0`, `GIT_EDITOR`/`GIT_SEQUENCE_EDITOR=true`, zusätzlich immer `--no-edit`; optionaler Timeout für Netzkommandos; Abbruch beendet den Prozessbaum wie bisher; schreibende Kommandos pro Repo über `SemaphoreSlim` serialisiert.
- `GitProcessOptions.cs`: Pfad zur Git-Executable und Umgebungs-Überlagerung, damit Tests `GIT_CONFIG_GLOBAL`/`GIT_CONFIG_NOSYSTEM` isolieren können.
- `GitExecutableLocator.cs`: PATH, unter Windows zusätzlich `%ProgramFiles%\Git\cmd\git.exe` (analog `TfExeLocator`); Mindestversion 2.23 (`switch`/`restore`), darunter eine Warnung.
- `GitService.cs` wird `partial` und implementiert alle neuen Interfaces: `GitService.History.cs`, `.Branches.cs`, `.Remote.cs`, `.Stash.cs`, `.Patch.cs`, `.Content.cs`, `.Merge.cs`. `GetStatusAsync` nutzt intern `--no-optional-locks status --porcelain=v2 -z --branch --untracked-files=normal`; das Rückgabemodell bleibt und wird nur erweitert.

### 3.3 App (`src/XamlVisualEditor.App`)

- `App.axaml.cs`, `ConfigureServices`: `AddSingleton<GitService>()`, jedes Interface als weiterleitendes Singleton, `AddSingleton(sp => new GitServiceSet(...))`, die neuen Host-Dienste (Abschnitt 4).
- `GitExtension` bekommt einen zweiten öffentlichen Konstruktor `(IGitService, GitServiceSet, IWorkspaceInfo, IDialogHost)`; MS.DI wählt den längeren. Der alte Konstruktor baut das Set über `GitServiceSet.FromService(gitService)` (Fähigkeitsprüfung per `as`), damit `CommandPaletteCoverageTests` unverändert grün bleibt.
- `ViewLocator.cs`: neue ViewModels in `Build` und `Match`, dazu `ExtensionDocument` (4.4).
- `Styles/EditorTheme.axaml`: Pinsel für Dekorationen und Zeilenmarker (`ExplorerDecorationModifiedBrush`, `…AddedBrush`, `…DeletedBrush`, `…UntrackedBrush`, `…ConflictBrush`, `LineMarkAddedBrush`, …) für helles und dunkles Theme; Untermenü-Platzhalter in `DocumentFileMenuItems`.

### 3.4 Neues Projekt `src/XamlVisualEditor.SourceControl.UI`

Referenziert Avalonia, AvaloniaEdit, ReactiveUI und Core; Zuschnitt nach Vorbild `Collaboration.UI`; Eintrag in `XamlVisualEditor.slnx`.

- `Controls/UnifiedDiffEditor.cs`: Zusammenführung der beiden Diff-Editoren, dazu Margin mit alten und neuen Zeilennummern, Eigenschaft `CurrentHunkIndex`, Auswahlbereich für Hunk- und Zeilen-Staging, Größenbegrenzung mit Hinweis.
- `Models/DiffLine.cs`, `DiffLineKind`, `DiffLineBuilder` (aus `GitDiff` und aus rohem Unified-Text, entspricht `TfvcDiffLine.Parse`).
- `Behaviors/`: Kopien von `RightClickSelectBehavior`, `DoubleTapCommandBehavior`, `ClipboardInteractionBehavior`, `ScrollSelectionIntoViewBehavior` aus `TfvcExtension/Views` (TFVC behält seine öffentlichen Typen; Umstellung in Phase 8) und `DiffPaneBehavior`, verallgemeinert aus `GitDiffPaneBehavior`.

### 3.5 Git-Extension (`extensions/XamlVisualEditor.GitExtension`)

Aufbau wie TFVC: `ActivateAsync` verdrahtet nur, die Logik steckt in Diensten.

- `GitExtension.cs`: Aktivierung, Lazy-ViewModels, `ActionDisposable` wie `TfvcExtension.cs`.
- `GitCommands.cs`: Konstanten aller Kommando-IDs sowie `internal static RegisterCommands/RegisterMenuAndPaletteItems` analog `TfvcExtension.RegisterViewCommands/RegisterMenuAndPaletteItems`, damit einzeln testbar.
- `Localization/GitStrings.resx` + `GitStrings.de.resx` wie TfvcStrings: neue Texte dorthin; die vorhandenen 23 `UiStrings.Git*`-Schlüssel bleiben. Host-Texte für die neuen Erweiterungspunkte gehören in `UiStrings.resx`/`.de.resx`.
- `Services/`: `GitSession` (Repo-Erkennung, `IObservable<GitSnapshot>` als einzige Wahrheit für Status, Branch, Ahead/Behind, Operation und Stash-Anzahl; `RefreshAsync` als Single-Flight; Context-Keys), `GitRepositoryWatcher` (Arbeitsverzeichnis plus `.git/HEAD`, `index`, `refs`, `MERGE_HEAD`; filtert `*.lock`, `objects`, `logs`, `bin`, `obj`, `.vs`, `artifacts`, `TestResults`), `GitSettingsStore` (`IExtensionStorage`, wie `TfvcSettingsStore`), `GitOutputLogger` (wie `TfvcOutputLogger`, maskiert Zugangsdaten in URLs), `GitCommandTarget` (Pfad aus `CommandContext.Arguments[0]` oder `Editor.ActiveDocument.FilePath`, umgerechnet in repo-relativ), `GitExplorerDecorationProvider`, `GitChangeMarkerService`, `GitAutoFetchService` (Vorgabe aus).
- `ViewModels/`: `GitPanelViewModel` wird weiterentwickelt (Typname bleibt) zum Änderungen-Panel mit Abschnitten Konflikte, Gestagt, Änderungen, Commit-Box mit Amend, Diff mit Hunk-Aktionen; `GitRepositoryPanelViewModel` (Reiter Branches, Stashes, Tags); `GitHistoryViewModel`; `GitCommitDetailsViewModel`; `GitDiffDocumentViewModel`; `GitBlameDocumentViewModel`; Zeilen-ViewModels.
- `Views/` entsprechend; `Controls/GitBlameMargin.cs` (`AbstractMargin`, nur im Blame-Dokument).
- `Controls/GitDiffTextEditor.cs` bleibt öffentlich erhalten (additiv), wird aber nicht mehr verwendet.

View-IDs: `git.panel` bleibt (gespeicherte Layouts passen weiter), Titel neu „Git-Änderungen“; `git.history` unten; `git.repository` links (Gegenstück zum TFVC-Quellcodeverwaltungs-Explorer).

## 4. Neue Erweiterungspunkte (`src/XamlVisualEditor.Extensions`)

Alle als nullable `init`-Eigenschaften an `ExtensionContext` nach dem Muster von `ContextKeys`/`BackgroundActivities`, dazu optionale Konstruktorparameter in `Hosting/BuiltInExtensionHost.cs` (additiv). Der Testhost in `ExtensionCommandMenuIntegrationTests.ExtensionMenuTestHost` reicht sie durch. Extensions müssen mit `null` umgehen.

### 4.1 Explorer- und Tab-Kontextmenüs mit Argumenten (`ExtensionContributions.cs`)

```csharp
public const string ExplorerContext = "menu.explorer.context";        // Args: [string fullPath, bool isDirectory]
public const string DocumentTabContext = "menu.documentTab.context";  // Args: [string filePath]
```

Bedeutung der Argumente in der XML-Doku; ein kleiner Helfer `ExtensionCommandArguments.TryGetPath(CommandContext, out string path, out bool isDirectory)` erspart Git und TFVC doppelten Code.

Host: `ShellViewModels.UpdateExtensionMenuItems` bekommt die Sammlungen `ExplorerContextMenuItems` und `DocumentTabContextMenuItems`; neue Hilfsmethode `ExecuteExtensionCommandAsync(commandId, IReadOnlyList<object?> args)`; neuer `ExtensionMenuItemViewModel`-Wrapper mit `Func<object?[]?>` als Argumentquelle. Views: `ExtensionTreeView.axaml` (nur Knoten mit `IExtensionTreeItemPathProvider`) und `SolutionExplorerView.axaml` bekommen ein eigenes `MenuItem` mit `MenuItemsBehavior`; Tab-Menü: `DocumentTabCommands` bekommt `ExtensionItems`; Untermenüs funktionieren über das vorhandene `Submenu = "Git"`.

Bewusste Einschränkung: Aktivierung nur über `CommandMetadata.When` und globale Context-Keys, keine Prüfung pro Element („nur geänderte Dateien“); das Kommando meldet in dem Fall „keine Änderungen“.

### 4.2 Explorer-Dekorationen (neu: `ExtensionExplorerDecorations.cs`)

```csharp
public enum ExplorerDecorationKind { None, Modified, Added, Deleted, Renamed, Untracked, Ignored, Conflicted }
public sealed record ExplorerDecoration(ExplorerDecorationKind Kind, string? Badge = null, string? ToolTip = null)
{ public bool IsInherited { get; init; } } // Ordner, die Änderungen enthalten: dezenter dargestellt
public interface IExplorerDecorationProvider
{
    // Threadsicher, ohne IO, O(1): wird bei jeder Knotenerzeugung aufgerufen (auch im Hintergrund-Thread).
    ExplorerDecoration? GetDecoration(string fullPath, bool isDirectory);
    event EventHandler<ExplorerDecorationsChangedEventArgs>? Changed; // Paths == null → alle
}
public interface IExplorerDecorations { IDisposable Register(IExplorerDecorationProvider provider, int priority = 0); }
```

Host: `Hosting/ExplorerDecorationRegistry.cs` (erster Treffer nach Priorität gewinnt; `Changed` gebündelt und 150 ms gedrosselt auf den UI-Thread). `ExtensionTreeNodeViewModel` und `SolutionExplorerNodeViewModel` bekommen `[Reactive] ExplorerDecoration? Decoration`, gesetzt beim Erzeugen (`ExtensionTreeViewModel.LoadAsync`, `ExtensionTreeNodeViewModel.EnsureChildrenAsync`, `SolutionExplorerNodeViewModel.FromWorkspace`). Bei `Changed` werden nur die geladenen Knoten durchlaufen, kein Neuladen des Baums. Projektknoten im Solution Explorer fragen mit dem Projektordner und `isDirectory: true`. View: im DataTemplate eine Badge-`TextBlock` plus Vordergrundfarbe des Labels über Style-Selektor pro Kind; mit Row-Virtualisierung billig.

Git-Provider: unveränderliches Snapshot-Dictionary, atomar getauscht; Ordner-Vorfahren aller Änderungen werden beim Snapshot einmal vorberechnet; Pfade unterhalb eines untracked Ordners gelten als Untracked. TFVC kann `TfvcSession`-Pending-Changes über denselben Vertrag anzeigen (Phase 8).

### 4.3 Editor-Zeilenmarker (neu: `ExtensionTextEditorLineMarks.cs`)

```csharp
public enum TextEditorLineMarkKind { Added, Modified, Deleted, Conflict }
public sealed record TextEditorLineMark(int StartLine, int LineCount, TextEditorLineMarkKind Kind, string? ToolTip = null);
public interface ITextEditorLineMarks { ITextEditorLineMarkLayer CreateLayer(string layerId, string? clickCommandId = null); }
public interface ITextEditorLineMarkLayer : IDisposable
{
    void Set(string filePath, IReadOnlyList<TextEditorLineMark> marks); // threadsicher; Host marshalt
    void Clear(string filePath);
}
```

Push-Modell: Die Extension liest den Text über `IEditorDocument.GetTextAsync` und `Changed`. Host: `src/XamlVisualEditor.App/Services/TextEditorLineMarkHost.cs`, `src/XamlVisualEditor.App/Views/LineMarkMargin.cs` (`AbstractMargin`, 4 px, nach den Zeilennummern). Eingehängt in `TextEditorViewHost.Register/Apply`, das bereits alle Editoren kennt (Vorbild: Einfügen der `BreakpointMargin` in `BreakpointMarginBehavior`). Dateipfad aus `editor.DataContext`: `IEditorDocumentViewModel.FilePath` oder `CodeEditorViewModel.FilePath`. Ein Klick führt `clickCommandId` mit `[filePath, line]` aus.

### 4.4 Extension-Dokument-Tabs (neu: `ExtensionDocuments.cs`)

```csharp
public sealed record ExtensionDocumentRequest(string DocumentId, string Title, object ViewModel) { public string? ToolTip { get; init; } }
public interface IExtensionDocuments
{
    Task ShowAsync(ExtensionDocumentRequest request, CancellationToken cancellationToken); // öffnet oder aktiviert
    Task<bool> CloseAsync(string documentId, CancellationToken cancellationToken);
}
```

Host: `ExtensionDocument : Document` in `XamlEditorDockFactory.cs`, Id-Präfix `ExtensionDocument:`, nach dem Muster `InfiniteCanvasDocument`/`AddCanvasDocument`; nicht im Layout persistiert; das ViewModel wird beim Schließen entsorgt, wenn es `IDisposable` ist. ViewLocator: `ExtensionDocument doc => new ContentControl { Content = doc.ViewModel }`. Verwendet für „Mit HEAD vergleichen“, Blame und später den TFVC-Vergleich im Tab.

### 4.5 Nicht nötig

Statusleiste (`IWindow.CreateStatusBarItem` mit `CommandId`), Quick Pick, InputBox, `IDialogHost` (`ConfirmationDialogRequest`, `ProgressDialogRequest` für Fortschritt und Abbruch bei Fetch/Pull/Push), `IBackgroundActivities`, `IExtensionStorage`, `IContextKeys`, Output-Kanäle reichen. Optional und nicht im ersten PR: Passwortfeld in der InputBox (für einen späteren Askpass-Helfer) und `IEditorServices.DocumentSaved`.

## 5. Kommandos

Kategorie immer `"Git"` (Eigenname, auch auf Deutsch). Jedes Kommando bekommt `CommandMetadata(Title, "Git", When, Priority)` und einen Palette-Eintrag. Kommandos mit Pfadargument verwenden ohne Argument das aktive Dokument, damit sie auch aus der Palette sinnvoll sind. Panel-lokale Aktionen (Hunk stagen usw.) bleiben `ReactiveCommand<RxVoid,RxVoid>` im ViewModel und werden nicht global registriert.

Context-Keys: `git.available`, `git.isRepository`, `git.hasRemote`, `git.hasUpstream`, `git.isMerging`, `git.hasConflicts`, `git.hasStashes`, `git.changeMarkers`, `git.explorerDecorations`, `git.autoRefresh`.

Menüorte: EX = Explorer-Kontext, ED = Editor-Kontext, TAB = Tab-Kontext, jeweils Untermenü „Git“; „Ext › Git“ = Menü Extensions mit Untermenü Git.

| ID | Titel (de) | Menüs | Phase |
|---|---|---|---|
| `git.changes.toggleView` | Git-Änderungen ein-/ausblenden | Ansicht › Panels (views.bottom) | 1 |
| `git.history.toggleView` | Git-Verlauf ein-/ausblenden | Ansicht › Panels (views.bottom) | 3 |
| `git.repository.toggleView` | Git-Repository ein-/ausblenden | Ansicht › Panels (views.left) | 2 |
| `git.changes.show` | Git-Änderungen öffnen (Strg+Umschalt+G) | Statusleiste, Ext › Git | 1 |
| `git.refresh` | Git-Status aktualisieren | Ext › Git | 1 |
| `git.showOutput` | Git-Ausgabe anzeigen | Ext › Git | 1 |
| `git.init` | Repository initialisieren (When `!git.isRepository`) | Ext › Git | 1 |
| `git.toggleAutoRefresh` | Arbeitsordner überwachen (CheckedWhen; Titel statt „Live-Überwachung“, weil er nur den Arbeitsordner-Watcher schaltet) | Ext › Git | 1 |
| `git.stage` / `git.unstage` | Datei stagen / Aus dem Stage nehmen | EX, ED, TAB | 1 |
| `git.discard` | Änderungen verwerfen … (mit Bestätigung) | EX, ED | 1 |
| `git.stageAll` / `git.unstageAll` | Alles stagen / Alles aus dem Stage nehmen | Ext › Git | 1 |
| `git.commit` / `git.commitAmend` | Committen … / Letzten Commit ändern … | Ext › Git | 1 / 6 |
| `git.checkout` | Branch wechseln … | Ext › Git | 2 |
| `git.createBranch` / `git.deleteBranch` | Branch erstellen … / Branch löschen … | Ext › Git | 2 |
| `git.fetch` / `git.pull` / `git.push` / `git.sync` | Abrufen / Pullen / Pushen / Synchronisieren | Ext › Git | 2 |
| `git.showHistory` | Repository-Verlauf anzeigen | Ext › Git | 3 |
| `git.showFileHistory` | Verlauf anzeigen (Datei/Ordner) | EX, ED, TAB | 3 |
| `git.toggleExplorerDecorations` | Git-Markierungen in Explorern (CheckedWhen) | Ext › Git | 4 |
| `git.compareWithHead` / `git.compareWithIndex` | Mit HEAD vergleichen / Mit Stage-Stand vergleichen | EX, ED, TAB | 5 |
| `git.showChangeAtLine` | Änderung an dieser Zeile anzeigen (Gutter-Klick) | ED | 5 |
| `git.blame` | Blame anzeigen | EX, ED, TAB | 5 |
| `git.toggleChangeMarkers` | Änderungsmarker im Editor (CheckedWhen) | Ext › Git | 5 |
| `git.stash` / `git.stashIncludeUntracked` | Änderungen stashen … / … inkl. unversionierter Dateien | Ext › Git | 6 |
| `git.stashApply` / `git.stashPop` / `git.stashDrop` | Stash anwenden … / Pop … / Verwerfen … | Ext › Git | 6 |
| `git.merge` / `git.mergeAbort` | Branch in aktuellen mergen … / Merge abbrechen | Ext › Git | 7 |
| `git.acceptOurs` / `git.acceptTheirs` / `git.markResolved` | Eigene / Fremde Version übernehmen / Als gelöst markieren | EX, ED | 7 |

Statusleiste: ein Element links, Priorität 30 (TFVC hat 40), Text wie „main ↑1 ↓2 · 5“, Tooltip mit Upstream; ein Klick führt `git.changes.show` aus.

Abgleich mit TFVC:

| TFVC | Git |
|---|---|
| Include/Exclude | Stage/Unstage |
| Einchecken mit Kommentar | Commit (+ Amend) |
| Undo | Discard (mit Bestätigung) |
| Shelve | Stash (zusätzlich Liste, Apply, Pop, Drop) |
| Vergleichen | Mit HEAD/Index vergleichen |
| Aktives Dokument hinzufügen | `git.stage` ohne Argument |
| Verlauf mit Kommentarsuche, Autorfilter (AutoComplete), „Mehr laden“ | dasselbe über `--author`, `--grep`, `--fixed-strings`, `--skip` |
| Labels | Tags |
| Changeset-Details mit Kommentarbearbeitung | Commit-Details; Nachricht bearbeitbar nur bei HEAD über Amend |
| Get latest | Pull |
| Statusleiste, Output-Kanal, Einstellungen | entsprechend vorhanden |

Ohne Gegenstück in Git: Auto-Check-out, Offline-Modus und Reconcile. Arbeitselemente optional als `#123`-Verweis in der Commit-Nachricht, sonst „später“.

## 6. Phasen

Jede Phase endet mit grünem Build und grünen Tests und lässt sich in der App ausprobieren. Größen grob geschätzt: Produktivcode / Testcode in Zeilen.

**Phase 0: CLI-Fundament** (M, ca. 700 / 600). `GitProcessRunner`, `GitProcessOptions`, `GitExecutableLocator`, Status über porcelain v2 `-z`, Modellerweiterungen, Commit über `-F -`/stdin.
Tests: `GitStatusV2ParserTests` (Umbenennung, Konflikt-Codes, Detached, unborn, `branch.ab`); `GitProcessRunnerTests` mit echtem Repo: Dateien `Größe ä.xaml` und `a b.txt` stagen, Commit mit Umlauten über stdin, Abbruch über `git cat-file --batch` (wartet auf stdin; Token abbrechen, prüfen, dass der Prozess beendet ist; kein Sleep); gemeinsamer Helfer `tests/Shared/GitTestRepository.cs` aus `GitServiceTests.InitRepository/RunGit` (`GIT_CONFIG_NOSYSTEM=1`, leeres `GIT_CONFIG_GLOBAL`, `init -b main`, Bare-Remote; die Runner-Instanz unter Test bekommt dieselbe Umgebungs-Überlagerung).
Live: Das bestehende Panel stagt eine Datei mit Umlaut.

**Phase 1: Session, Statusleiste, Kommandogerüst, Lokalisierung** (M, ca. 1.200 / 700). `GitSession` und `GitRepositoryWatcher` (aus `GitPanelViewModel` herausgezogen, das Panel nutzt die Session); Output-Kanal „Git“; Context-Keys; Statusleiste; `GitCommands` mit Kommandos der Phase 1; `GitStrings.resx`/`.de.resx`; `GitSettingsStore`; zweiter Konstruktor, `GitServiceSet`, DI in App.
Tests: `GitSessionTests` (echtes Repo, Refresh explizit über `RefreshAsync`, gewartet wird auf `Snapshots.FirstAsync(predicate)`); `GitCommandsTests` nach Vorbild `TfvcViewCommandsTests`; `GitStringsLocalizationTests` (Kopie von `TfvcStringsLocalizationTests`); Statusleisten-Test nach `StatusBarIntegrationTests`; `CommandPaletteCoverageTests` bleibt grün; `GitPanelViewTests` wird angepasst.

**Phase 2: Branches und Sync** (M, ca. 1.200 / 800). `IGitBranchService`, `IGitRemoteService`; Panel `git.repository`, Reiter Branches (lokal/remote, aktuell, Ahead/Behind; Kontextmenü Wechseln, Löschen, „Neu von hier“); Quick Pick für `git.checkout`; Fetch/Pull/Push mit `ProgressDialogRequest` und Abbruch; Klassifizierung von Auth-Fehlern; `push -u` nach Rückfrage; optionaler Auto-Fetch (Vorgabe aus).
Tests: `GitRefParserTests`, `GitRemoteErrorClassifierTests`; `GitRemoteServiceTests` mit lokalem Bare-Remote (Clone, Push, Fetch, Pull per Fast-Forward, abgelehnter Push, Push ohne Upstream); `GitRepositoryPanelViewTests` (headless).

**Phase 3: gemeinsames UI-Projekt, Verlauf, Commit-Details, Tags** (L, ca. 1.800 / 900). `XamlVisualEditor.SourceControl.UI` mit `UnifiedDiffEditor` und Behaviors; `IGitHistoryService`; Panel `git.history` für Repo, Datei oder Ordner (`--follow` nur bei Einzeldateien), Autor-AutoComplete aus geladenen Commits wie `TfvcHistoryViewModel._knownAuthors`, Nachrichtensuche, Paging mit 100; Details mit Dateiliste (`diff-tree -r -z --name-status -M --root`, bei Merges gegen den ersten Parent) und Diff pro Datei; Reiter Tags.
Tests: `GitLogParserTests`; `GitHistoryServiceTests` (zwei Autoren über `-c user.name`, Ordnerfilter, Root-Commit, Merge-Commit); `GitHistoryViewModelTests` (Paging, Filter, Abbruch der vorigen Abfrage); `GitHistoryViewTests` und `UnifiedDiffEditorTests` (headless).

**Phase 4: Explorer-API** (L, ca. 1.300 / 900). 4.1 und 4.2 samt Git-Provider und Kontextmenüeinträgen Stage, Unstage, Discard, Verlauf.
Tests: `ExplorerDecorationRegistryTests`; Erweiterung von `ExtensionCommandMenuIntegrationTests` (ein Eintrag in `ExplorerContext` erscheint in File Explorer und Solution Explorer und bekommt den Pfad als Argument; analog `DocumentTabContext`); headless: File Explorer über einem echten Repo zeigt das Badge, auch an Ordnern; `GitExplorerDecorationProviderTests`; Performance-Test in `tests/XamlVisualEditor.Tests.Performance` (10.000 geladene Knoten, ein Update unter einer großzügigen Grenze).

**Phase 5: Editor-API, Vergleich, Blame** (L, ca. 1.800 / 1.000). 4.3 und 4.4, `LineDiff`; `GitChangeMarkerService` (Basisinhalt aus `git show :path` oder `HEAD:path`, zwischengespeichert, verworfen bei Index-Änderung; Zeilenenden normalisiert; Diff 300 ms gedrosselt außerhalb des UI-Threads, abbrechbar); Diff-Dokument für HEAD/Index mit Umschalter „ganze Datei“ (`-U` mit großem Wert); Blame-Dokument, Klick auf einen Hunk öffnet die Commit-Details.
Tests: `LineDiffTests`; `TextEditorLineMarkHostTests` (headless: Margin zeichnet, Klick führt das Kommando aus); `ExtensionDocumentHostTests` (Öffnen, Aktivieren, Schließen mit Dispose, nicht im Layout); `GitChangeMarkerServiceTests` (echtes Repo, `InMemoryWorkspace`/`EditorServicesAdapter`); `GitBlameParserTests`, `GitBlameServiceTests` (`--contents` über stdin).

**Phase 6: feinere Commits** (M bis L, ca. 1.200 / 1.000). Hunk stagen (`apply --cached -`), aus dem Stage nehmen (`--cached --reverse`), verwerfen (`--reverse` auf dem Arbeitsverzeichnis, mit Bestätigung); Zeilenauswahl über `GitPatchBuilder`, für untracked Dateien vorher `add -N`; Amend mit Warnung, wenn HEAD schon im Upstream ist (`merge-base --is-ancestor HEAD @{u}`); Stash-Reiter mit Liste, Apply, Pop, Drop und Diff.
Tests: `GitPatchBuilderTests` (CRLF, „No newline at end of file“, Latin-1-Bytes, Teilauswahl); `GitPatchServiceTests` (echtes Repo, Prüfung über `git diff --cached`); `GitStashServiceTests`; Amend-Test; Hunk-Kommandos in `GitPanelViewModel`.

**Phase 7: Merge und Konflikte** (M, ca. 800 / 700). `git.merge`, `git.mergeAbort`; Konfliktabschnitt im Änderungen-Panel; eigene/fremde Version übernehmen, als gelöst markieren (bei Lösch-Konflikten `git rm`); Konfliktmarker als Zeilenmarker der Art `Conflict`; Merge über einen Commit abschließen.
Tests: `GitMergeServiceTests` (echter Konflikt aus zwei Branches), `GitConflictsViewTests` (headless).

**Phase 8 (optional im PR, sonst direkt danach): TFVC übernimmt die gemeinsamen Teile** (S bis M, ca. 400 / 300). `TfvcExplorerDecorationProvider`, TFVC-Vergleich im Dokument-Tab, Views auf `UnifiedDiffEditor` und die gemeinsamen Behaviors umgestellt; die alten öffentlichen Typen bleiben.
Tests: `TfvcExplorerDecorationProviderTests`; bestehende TFVC-Tests bleiben grün.

Summe: etwa 10.000 Zeilen Produktivcode und 7.000 Zeilen Tests. Sehr groß für einen PR; mindestens ein Commit pro Phase. Phasen 4 und 5 enthalten den riskanteren Host-Anteil und könnten vorab als eigener PR „Extension API: decorations, line marks, documents, context menus“ gehen. `docs/EXTENSION-API.md` und `CHANGELOG.md` werden pro Phase nachgetragen.

## 7. Risiken und Gegenmaßnahmen

**Performance in großen Repos.** `status` mit `--no-optional-locks` und `-unormal` (nicht `all`); fsmonitor und untracked-cache werden automatisch genutzt. Refresh mit 500 ms Drosselung und Single-Flight; Watcher-Lärm gefiltert. Unter Linux kann das inotify-Limit erreicht werden: Bei Watcher-Fehlern fällt die Session auf den `.git`-Watcher plus Refresh nach eigenen Kommandos zurück und schreibt einen Hinweis in den Output-Kanal. Dekorationen nur für geladene Knoten, O(1)-Lookups, Snapshot-Tausch ohne Lock. Log mit Paging; die vorige Abfrage wird bei Filteränderung abgebrochen. Obergrenzen für Blame und Diff mit Hinweis „Diff zu groß“. Gutter-Diff mit Obergrenzen für Zeilenzahl und D, abgebrochen bei der nächsten Eingabe.

**CRLF und Encoding.** Patches für Hunk-Staging nur aus den rohen Bytes von `git diff`; nie neu kodiert, `\r` bleibt, `--whitespace=nowarn`. stdin als UTF-8 ohne BOM, stdout als UTF-8, `core.quotepath=off` plus `-z`. Für den Gutter werden Zeilenenden normalisiert, damit `core.autocrlf` keine Scheinänderungen erzeugt. Blobs wie im Editor als UTF-8 mit BOM-Erkennung dekodiert (Editor-Pfad: `SharedFileReader`). LFS-Dateien: `git show` liefert nur den Pointer, daher Gutter aus (`check-attr filter`). Binärdateien erkannt, in der Liste, ohne Hunk-Aktionen. Leerer Diff bei Status „M“: Hinweis „nur Zeilenenden oder Dateimodus geändert“.

**Zugangsdaten ohne Terminal.** `GIT_TERMINAL_PROMPT=0` und geschlossenes stdin: nichts kann hängen. Git Credential Manager (bei Git for Windows dabei) und osxkeychain zeigen ihre eigene Oberfläche und funktionieren weiter. SSH: `GIT_SSH_COMMAND="ssh -o BatchMode=yes"`, aber nur, wenn weder `GIT_SSH`, `GIT_SSH_COMMAND` noch `core.sshCommand` gesetzt ist (Plink und TortoisePlink bleiben unberührt). Unbekannter Host-Key: klare Meldung statt automatischem Akzeptieren. Fehlerklassifizierung (`Authentication failed`, `could not read Username`, `Permission denied (publickey)`, `terminal prompts disabled`) mit Hinweis in Du-Form, z. B. „Anmeldung fehlgeschlagen. Richte einen Credential Helper ein (z. B. Git Credential Manager) oder lade deinen SSH-Schlüssel in den Agent.“ Netzkommandos mit Timeout, abbrechbar über den Fortschrittsdialog. Zugangsdaten in URLs werden in der Ausgabe maskiert. Eigener Askpass-Helfer (App-Modus `--git-askpass` über Named Pipe) später.

**Weitere Risiken.** Hooks: lange `pre-commit`-Hooks abbrechbar, Ausgabe im Output-Kanal. Fremder `index.lock`: klare Fehlermeldung. Parallelität: schreibende Kommandos pro Repo serialisiert, alles Git im Thread-Pool, Ergebnisse über `WitnessOn(RxSchedulers.MainThreadScheduler)`. Selbstauslösende Refresh-Schleifen: vermieden über `GIT_OPTIONAL_LOCKS` und den `*.lock`-Filter. Pfadvergleiche über `FileSystemPathComparison`. Umfang im ersten PR: ein Repo pro Workspace, keine Rekursion in Submodule; Worktrees über `rev-parse --git-dir`.

## Kritische Dateien für die Umsetzung

- `src/XamlVisualEditor.Workspace/GitService.cs`
- `extensions/XamlVisualEditor.GitExtension/GitPanelViewModel.cs`
- `src/XamlVisualEditor.Extensions/ExtensionContributions.cs` (dazu `ExtensionContext.cs` und `Hosting/BuiltInExtensionHost.cs`)
- `src/XamlVisualEditor.Shell.ViewModels/ExtensionUiModels.cs` (`ExtensionTreeViewModel`/`ExtensionTreeNodeViewModel`; dazu `ShellViewModels.cs` mit `UpdateExtensionMenuItems`, `CreateExtensionCommand` und `SolutionExplorerNodeViewModel`)
- `src/XamlVisualEditor.App/Services/TextEditorViewHost.cs` (dazu `ViewLocator.cs` und `App.axaml.cs`)
