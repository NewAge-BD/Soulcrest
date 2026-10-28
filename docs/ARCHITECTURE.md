# Architektur

## Lösung

- `Soulcrest.Core` (`net10.0`): Pet-Daten, Fortschrittsmodelle, begrenzte Netzwerkparser,
  Karten-/Textmodelle, Boss-/Alertregeln; unabhängig von Windows und UI.
- `Soulcrest.Ocr` (`net10.0-windows`): bestehender Assemblyname für OpenCV-Kartenabgleich,
  Bildkonvertierung und Pixeltracking. Seit 2026-10-12 keine Windows-OCR oder Pet-/Listen-Scans.
- `Soulcrest.App` (`net10.0-windows`): WinForms + Blazor Hybrid/WebView2, WGC, passive Npcap-
  Erfassung, native Overlays, lokale Persistenz und DE/EN-Lokalisierung.
- Entsprechende Core-/Ocr-/App-Testprojekte. `App → Ocr, Core`; `Ocr → Core`.

## Laufzeitfluss seit 2026-10-12

```text
Npcap (eigener Serverstrom)
  → TcpReassembler → GameMessageStream (Frames/LZ4)
  → begrenzte Core-Decoder → NetworkLootService (Identität/Generation/Sequenz)
  → ProgressService / ExplorationService / BossRushService
  → UI, Overlays, Farmplanung

WGC (Spielfenster) → OpenCV-Kartenabgleich / MapTrackingService
  → bestätigte Kartenposition → Navigation / Kartenoverlays
```

Pet-/Exploration-OCR, Scantimer, Scanfenster/-Hotkeys und Sprachpaketinstallation sind entfernt.
Der vorhandene passive Mitschnitt ist die einzige Fortschrittsquelle neben manuellen Korrekturen.
Nur ein durch eigenen Welteintritt bestätigter Strom darf Charakterfortschritt speichern.
Ein früher unbestätigter Login-Kandidat suspendiert die alte Zuordnung; streng geprüfte volle
Startlisten werden nur bis genau einer Prepare und der passenden Own-Identität vorgemerkt.
Charakter-ID und Entity-Key müssen übereinstimmen, Server/Name kommen ausschließlich aus Own.
Unverankerte Exploration-Prefixe und vor Own empfangene Erkundungsdeltas werden verworfen. TCP-Lücken,
Formatfehler, beschädigte Bundles und Verbindungswechsel entwerten den Kontext; Altstände bleiben.
Netzwerk-Speicherfehler werden geloggt und unterbrechen den restlichen Capture-Batch nicht.
`NavigationArrivalService` verarbeitet nur Wegpunkte und Leveling-Stationen. Annäherungsring,
Dungeon-/Stronghold-Clear-Aktion und frühere OCR-Erkundungshelfer sind entfernt. Der kompatibel
übernommene `TargetArrivalRadius` hat keine Verbindung zum Erkundungsfortschritt.

Services liefern Snapshots und Changed-Events. UI liest deren Zustände; WGC/OpenCV für Navigation
ist unabhängig von Netzwerkfortschritt. Leaflet und Kartenkacheln bleiben lokal.

## Fortschritt und Profile

Pet-Stufe und verfügbare Souls sind getrennt; ausreichende Balance behauptet keinen erworbenen
Levelaufstieg. Farm-Gesamtwerte bleiben für Planung erhalten. Nur echte Gewinne erzeugen
Ledger-/Sitzungsereignisse; Snapshots, Erwerb und Verbrauch zählen keine neuen Drops.
Nutzerentscheidung 2026-10-13: eine globale Pet-Sammlung ohne Server-/Charakterbindung.
Eigene Pet-Vollstände, Gewinne, Verbrauch und absolute Aufstiege werden direkt verarbeitet;
9009 benötigt wegen Broadcast weiterhin den eigenen Entity-Key. Pet-Generation/Sequenz sind
von Exploration getrennt. Reentrante Meldungen werden begrenzt in Empfangsreihenfolge verarbeitet.
Echte Lücken machen gespeicherte Werte unbestätigt; neue vollständige Meldungen zählen weiter.
Schema3 übernimmt die aktive bisherige Sammlung ohne Addition. Vollständige Altserverdaten und
unbekannte Felder bleiben als LegacyServerSnapshot und bytegenaues Migrationsbackup erhalten.

Exploration gilt pro Charakter. Bestehende GUIDs, Labels, Namen, Orts-IDs, Haken und Leveling-
Routen bleiben bestehen; stabile eigene Charakter-ID und Server-ID ergänzen die Zuordnung.
Die Oberfläche zeigt für Dungeons/Strongholds Abschluss-Haken (Nutzerpräzisierung 2026-10-13).
Die bisherige Dreistufen-Enum bleibt speicherkompatibel; fehlende Belege bleiben intern unbekannt.
Eigene direkte Erfolge sowie das belegte positive Muster einer neuen eigenen Questliste
(Status4, Stage1, [1,0,0,0,0,0]) bestätigen Abschlüsse. Positive eigene Kibelisk-Listen bestätigen
Bindungen. Absenz setzt keine Haken zurück; Beobachtungsaktualität ist ein separates Feld.
Radius/Instanzbetreten setzt keine Haken.
Manuelle Korrekturen bleiben geschützt. Details: [EXPLORATION.md](EXPLORATION.md).

## Lokale Speicherung

`%LOCALAPPDATA%\Soulcrest`: `settings.json`, `progress.json` (gemeinsame Pets), `exploration.json`
(Charakterprofile), `events.jsonl` (append-only), `targets.json`, `routes.soulroute`,
`leveling-progress.json`, `alerts.json`, `cache/`, `learned/`, `captures/`, `webview2/`.
Schreiben ist atomar. Vor erstem Schema-2-Schreiben entsteht eine unveränderte Altdatei-Sicherung.
Tests leiten Nutzerdaten in temporäre Ordner um und laufen wegen gemeinsamem Prozesszustand
(Sprache, Env, Datenpfad) seriell. Die echte Nutzer-App wird für Entwicklung nicht parallel gestartet.

## Alerts (2026-10-12)

`Core.Alerts.AlertSchedules` berechnet aus einer expliziten Zeitzone die nächste UTC-Instanz
stündlicher, täglicher, wöchentlicher, N-Kalendertage-, Intervall- und Einmalregeln. Der
`AlertsService` wird im Host unabhängig vom aktiven Tab gestartet, liefert Snapshots und
dedupliziert Auslösungen in einem atomaren Schema-1-Store `alerts.json`. Die Uhr ist injizierbar.
Boss-Hinweise konsumieren die vorhandenen `BossRushService.Alerts` einschließlich stabiler
Karte/Spawn-ID; die vorhandene BossAlertPolicy bleibt die einzige Spawn-Triggerquelle.

`AlertsView` verwaltet Zeitpläne; die gemeinsame `LiveBossTimers`-Liste integriert die individuellen
Boss-Erinnerungen und Anzeigedauern. Die separate Boss-Alerts-Kachel ist entfernt. `AlertsOverlayForm` zeichnet die sichtbaren Countdowns ohne Fokuswechsel und übernimmt
Schloss, Alt und Capture-Sichtbarkeit. Die gemeinsame `OverlayVisibilityPolicy` wertet für diese
Leiste (Panel `alerts`) die positiven AppSettings-Freigaben für Weltkarte, Menü und Instanzen aus den vorhandenen
Trackingzuständen aus. Ohne laufendes Tracking gibt es keine Kontextunterdrückung. Ton läuft im MainForm über
`NoticeRaised` unabhängig von Rendern und Sichtbarkeit. Das Boss-Rush-Overlay zeichnet im Host
nur noch seine Liste; es erzeugt keine zweite Alarmanzeige. Overlay-Einstellungen und Vorschau stehen im Overlay-Tab; Erinnerungsregeln bleiben unter Alerts.

`EventCatalog` hält sechs lokale Nichtboss-EU-Zeitpläne nach der gewählten Community-Referenz.
`Core.Alerts.EventWindows` trennt Kalenderstart, Eventlaufzeit und Portalphase und liefert begrenzte
Vorkommen für die Zeitachse. `AlertsService.EventTimers` ist unabhängig von aktivierten Erinnerungen.
Stabile `catalog:<id>`-Erinnerungen nutzen denselben Scheduler; optionale `EventPresentation`-Werte
in Schema 1 steuern die native Anzeige getrennt. Katalogpläne bleiben fest, eigene Alerts editierbar.
Der native Countdown kann mittels `CountdownLabel` auf Portal-/Eventende zeigen; bestätigte
Bossmeldungen behalten ihre bisherige Darstellung. Dashboard und erfasste Bosskarten sind getrennt. Im Workspace stehen die vier Events als
unabhängig aufklappbare Zeilen mit gemeinsamen lokalen Icons und eingebetteten `EventTimerDetails`.

`TimerOverlayRows` ist seit 2026-10-18 die unabhängige Projektion für die neue native Timer-Leiste.
`TimerOverlayChoices` liefert stabile Schlüssel, Auswahl, Icon-ID und Verfügbarkeit. Eine explizite
`TimerOverlayTimerIds`-Liste in AppSettings ersetzt die alte Widget-Auswahl ausschließlich für
die Leiste; `null` übernimmt alte Präferenzen, `[]` bleibt leer. `TimerOverlayPresentation` formatiert
den gemeinsamen nativen/Razor-Countdown. `EventTimerDisplay` projiziert Rift ausschließlich auf
die zehnminütige Öffnung und anschließend den nächsten Start. Tab, Kartenliste und native Leiste
verwenden dieselbe Projektion; `EventWindows`, Kalenderdauer und Reminder bleiben unverändert.
Der optionale `AlertDisplayRow.InlineLabel` reserviert während der Öffnung einen eigenen Namensslot
neben dem Icon, unabhängig vom festen Countdownslot. Die frühere globale Vorfensteroption wird
beim Laden auf 0 zurückgesetzt. `TimerStripRenderer` zeichnet 28-px-Originalicons und
18-px-Noto-Sans-Bold-Text in 32-px-Zeilen auf eine echte Alpha-Fläche ohne Hintergrund.
`TimerOverlayIcons` löst gemeinsame Symbol-IDs auf gebündelte Dateien oder validierte lokale
Kartenpfade auf. `TimerIconCache` lädt abgelöste Bilder, gibt sie mit dem Fenster frei und merkt sich
fehlende Bilder nicht dauerhaft. Die Menüvorschau nutzt den bestehenden lokalen Karten-VirtualHost.
`TimerStripTypeface` lädt die mitgelieferte Schrift privat; installierte Fonts sind nicht nötig. Automatischer Umbruch statt fester Breite;
die Nutzerskalierung wirkt genau einmal. `CountdownRows` bleibt als Kompatibilitätsprojektion.

`NotificationRows` enthält zeitlich aktive
Banner unabhängig von Minimap-/Menü-Unterdrückung. `AlertNotificationsForm` zeichnet diese in einem
separaten Fenster mit eigener gespeicherter Position; gesperrt ist es klickdurchlässig. `AlertsOverlayForm` bleibt das
ausblendbare, interaktive Timer-HUD. `AlertSoundPlayer` liefert asynchrone lokale MP3-Wiedergabe über
Windows MediaPlayer, mit Dateiauswahl, Abbruch bei neuer Wiedergabe/Dispose und Systemton-Fallback.
`SoundFile` ist ein optionales Schema-1-Feld; neue Laufzeit-Netzwerkzugriffe entstehen nicht.


Boss-Timer verwenden in Alerts und Boss Rush dieselbe durchsuchbare `LiveBossTimers`-Komponente
mit vollständigem Katalog. Die reine UI-Sortierung trennt bestätigte Spawns, positive Countdowns
und wartende/unbestätigte Einträge; Navigation und Quellenauswahl bleiben unverändert. Individuelle
`BossReminderPreference`-Overrides in AppSettings steuern Enabled und die optionale LeadSecondsList;
EventAlert besitzt analog LeadMinutesList. Fehlende Listen nutzen die bisherigen Einzelwerte,
ausdrücklich leere Listen erzeugen keine zusätzliche Vorwarnung. Snapshots kopieren die Arrays.
Bosse ohne Override erben die globale Vorwarnzeitenliste beziehungsweise den alten Einzelwert. `BossAlertDurationSeconds` und
`BossAlertUntilSpawn` gelten global für alle Boss-Erinnerungen, bedienbar über der Suche.
Jeder ausgewählte aktive Reminder erzeugt eine sichtbare Notification ohne zusätzliche
Banner-/Minutenfenster-Gates. Legacy-ShowOverlay/AlwaysShowOverlay/OverlayLeadMinutes bleiben
für Store- und Timer-Leisten-Kompatibilität erhalten, beeinflussen jedoch echte Bossnotices und
Tests nicht. Die UI bietet daher mehrere Vorwarnzeiten und optionalen Ton. Bereits zulässig erzeugte
feste Notices überstehen Map-/Streamwechsel bis Expires; Metadatenänderungen löschen sie nicht.
Neue Cache-Warnungen bleiben durch die unveränderte Triggerpolicy ausgeschlossen.
Der Vorwarnfenstermodus verwendet eine endliche Frist und einen monotonen Restzeitanker.
Gültige frische Uhrkorrekturen passen nur den ursprünglichen Zyklus an. Bei Restzeit 0
entfernt BossRushService die Quelle; UntilSpawnNotice liefert null und AlertsService entfernt
gebundene Bannerkopien auch beim direkten Lesen ohne weiteren Tick. Ein Karten-/Quellenwechsel
beendet ein positives Fenster nicht vorzeitig; ein neuer anderer Zyklus verlängert es nicht.
Die frühere AwaitingConfirmation-Sonderanzeige entfällt. Ein abgelaufener Timer erzeugt weiterhin
keinen Spawnbeleg; normale Bossliste und Kartenprojektion dürfen den Quellenzustand unbekannt
anzeigen. Manuelles Deaktivieren/Abwählen bleibt möglich. Tests, feste Anzeigedauer und tatsächlich
bestätigte Spawnmeldungen behalten ihre Sekundendauer. Stille Korrekturen erzeugen keinen neuen
Ton. Die native Begrenzung auf sechs Zeilen mit Überlaufhinweis bleibt; Fensterquellen blockieren
nicht die feste Queue. Alte per-Boss-Dauerfelder werden beim JSON-Lesen ignoriert, ohne daraus
einen globalen Modus zu erraten.
`AdvanceWarningPicker` wird von Bosskarten, Eventdetails, Resets, eigenen Alerts und den
Boss-Standardeinstellungen verwendet. Die Bosskarten beider Tabs aktivieren außerdem die vorhandene
unabhängige Timer-Leisten-Auswahl. `AdvanceWarnings` normalisiert positive, eindeutige Offsets.
`BossAlertPolicy.EvaluateMany` trägt die Vorwarnstufe über BossAlertTrigger und BossNotice bis in
die persistenten Auslieferungsbelege. Bei mehreren überschrittenen Stufen wird nur die zuletzt
fällige ausgeliefert; ältere Stufen werden konsumiert. Alte Boss-lead-Belege ohne Stufe verhindern
eine erneute Sofortwarnung, erlauben aber spätere neue Stufen. AlertsService behandelt Kalender-
Vorwarnungen ebenso und holt nur den nächsten zukünftigen Termin nach. Die bisherige 30-Sekunden-
Toleranz für Startmeldungen bleibt. Weitere Stufen ersetzen die Banner-Identität desselben Bosses,
behalten jedoch den monotonen Fensteranker und dessen Ablauf. Die Tonausgabe erfolgt je Stufe.
`BossAlertPolicy` löst weiterhin allein aus; ein per-key Resolver kann einen Boss pausieren,
ohne die Timerauswahl zu verändern. Queue- und AlertsService-Prüfungen verwerfen deaktivierte
echte Hinweise. Bestehende Zyklusidentität und Cache-/Freshness-Grenzen bleiben maßgeblich.

`EventTimelinePresentation` berechnet eine reine Timeline-Projektion aus dem lokalen Katalog
und erfassten Bossdaten; keine erfundenen Bosszyklen. `EventTimelineOverlayForm` ist ein eigenes
natives, per Schloss/Alt bedienbares Fenster mit 12/24/48 Stunden. AppSettings speichert Position,
Skalierung, Zeitraum, separaten Sichtbarkeitsschalter und optionale Kopplung an die Minimap.
Der Host wendet die HUD-Unterdrückung nur bei aktivierter Kopplung an; kurze Banner bleiben separat.

### Kompakte Alert-Overlay-Größen (2026-10-12)

AppSettings hält separate logische Breite/Skalierung/Position für Timer-HUD, Kurzbanner und
Timeline. Revision 4 übernimmt beim ersten Laden die frühere gemeinsame HUD-Position/Skalierung
für Banner; spätere Anpassungen bleiben unabhängig. Größenregler verwenden die physische Breite;
Monitoranpassung ist vorübergehend. Native Resize/Drag speichert beim Abschluss. Details stehen
in ALERTS.md; die Scheduler-/Deduplizierungslogik bleibt unverändert.

## Verworfenes Feature: DPS-Meter (2026-10-19)

Der Nutzer hat den DPS-Meter vollständig verworfen. Die zugehörigen Komponenten,
Datenverträge, Erfassungsabnehmer, Historie und Katalogressourcen wurden entfernt.
Die bestehende Pet-, Fortschritts- und Boss-Timer-Erfassung bleibt eigenständig erhalten.



## Overlay-Verwaltung und Platzierung (2026-10-20)

`OverlayRegistry` inventarisiert acht Overlayarten und adaptiert die bestehenden absoluten
Layoutfelder. `OverlayVisibilityPolicy` entscheidet je freiem Panel über vier überlappende
Kontextfreigaben; Settingsrevision 5 übernimmt die bisherigen Präferenzen und gibt Boss-Spawns
auf Nutzerwunsch in allen Kontexten frei. Fehlende Minimap und Menü bleiben getrennte Bedingungen.

`OverlayPlacementService` liefert Monitor-/Moduszustand und Änderungen. Der native Host
verbirgt die echten Fenster mit `SetPlacementHidden`, statt deren Fachzustand zurückzusetzen.
`OverlayPlacementForm` zeichnet Dimmung, Raster, Auswahl und reine Vorschauinhalte in einem
aktivierbaren Layered Window. `OverlayPreviewRenderer` nutzt die Produktions-Zeichenmethoden;
keine Ereignisse werden zur Vorschau ausgelöst. Persistenz ausschließlich über bestehende
Settingsfelder und atomare Updates am Geste-Ende. Physische Desktopkoordinaten einschließlich
negativer Monitorursprünge; ein Layout, keine Monitorprofile. Monitoränderungen beenden sicher.


Die gemeinsame Einstellung `OverlayShowMode` ergänzt die Kontextpolicies um ein globales
Vordergrundgate (foreground standardmäßig, optional always). Der Host verteilt dafür einen
eigenen Präsentationszustand `SetForegroundSuppressed` an alle acht Fenster; Placement und
Vordergrund sind unabhängige Bedingungen. Händlerprüfungen wählen bei always die sichtbare
Spielgeometrie ohne Vordergrundzwang, behalten aber Fensterbindung, Frische und Ankerprüfung.


## Daevanion (2026-10-20)

`Core.Daevanion` enthält den lokal gebündelten Darstellungskatalog mit sechs Boardfamilien für acht
Klassen. Statboni und Skillstufenboni bleiben unterschiedliche Effekte; Nodes können zwei Statboni
enthalten. Die optionale Klasse der eigenen Netzwerkidentität und ihre Generation bilden den
aktuellen Daevanion-Kontext, ohne die Gültigkeit einer Own-Identität an Klassenunterstützung zu binden.

`Ocr.Daevanion.DaevanionBoardLocator` erkennt native Rahmen, Startkontur und Rastermaske.
`DaevanionService` nutzt die gemeinsame WGC-Spielaufnahme, bestätigt zwei unterschiedliche Frames
und verbindet Klasse, Board, Geometrie, Sprache und Frische in einem Snapshot. Unmittelbar vor
Darstellung prüft `DaevanionOverlayForm` den Kontext erneut. Der gemeinsame Produktionsrenderer
zeichnet rund maskierte Inhalte und eine qualitätsfarbene Stat-Fassung außerhalb nativer Rahmen.
Die Fassung passt ihre Erweiterung an den erkannten Rasterabstand an; Menüvorschau und
Offline-Bildprüfung verwenden denselben Painter.

Frische Node-Anker tragen zusätzlich ihre beobachtete aktive, inaktive oder unbekannte
Erscheinung. Sie beeinflusst den runden Stat-Hintergrund, keine gespeicherten Fortschrittsdaten.
Node-Grafiken werden begrenzt nach Inhalt, Größe, Sprache, Erscheinung und Rasterabstand
zwischengespeichert. Während des Zeichnens eintreffende Tooltipmasken entfernen nur nicht mehr
belegte Grafiken und ergänzen neue; die verbleibende bestätigte Teilfläche wird atomar präsentiert.

`DaevanionNodeContentVisible` ist eine dauerhafte reine Anzeigepräferenz, getrennt von
`DaevanionOverlayEnabled`. Ein unabhängiges, nicht aktivierendes Toggle-Fenster bleibt bei
verborgener Node-Fläche erreichbar. Seine Freigabe ist an aktuelle Board-/Charakterevidenz gebunden;
der 100-ms-Sichtbarkeitstick prüft sie auch ohne gezeichnete Nodes. Umschalten invalidiert weder
Raster noch Snapshot und leert keinen Grafikcache. Hauptschalter und Hotkey behalten ihre vollständige
Abschaltfunktion. Der gemeinsame Toggle-Painter und die vollständige Rastergeometrie erzeugen auch
die Offline-Vorschauen beider Zustände.

`DaevanionHighlightedStatIds` ergänzt eine globale, persistente Stat-Auswahl in Katalogreihenfolge.
Das unabhängige Stat-Rail-Fenster bindet NoActivate-Klicks an den aktuellen Boardkontext und nutzt
vollständige Geometrie für Position/Paging. Platzmangel ist reines Präsentationsfeedback im
`DaevanionService`, ohne Quellen- oder Revisionsänderung. Der Highlight-Produktionsrenderer zeichnet
auf einer separaten klickdurchlässigen Fläche einen umlaufenden Lichtzug. Ein Timer mit 33–66 ms Abstand läuft nur
bei sichtbaren Treffern; jeder Tick prüft aktuelle Auswahl und bestätigte Quelle. Reconcile entfernt
auch bei kleinen Nodes das vollständige Highlight-Envelope. Node-Grafikcache und optionale
Node-Fläche bleiben davon unabhängig; Preview und Live teilen Layout und beide Renderer.
Die animierte Kontur wird je Größe/Abstand/Phase einmal pro Renderaufruf gerastert und auf alle
Treffer kopiert, weiterhin unter individuellen Welt-/Nachbarclips. Temporäre Stamps werden entsorgt.


## Ereignisgesteuerte Kartenruhe (2026-10-21)

Der Kartentracker leitet seine Arbeitsweise direkt aus Netzwerkschalter und Weltkontext ab.
Mit aktivierter Netzwerkquelle erlaubt nur eine bestätigte unterstützte offene Karte visuelle
Minimaparbeit gegen genau diese Kartenreferenz. Dungeons/Eventinstanzen, Übergänge und noch
unbestätigte Zustände warten ohne Map-Capture, UI-Probes oder Recovery auf eine Änderung.
Der visuelle Instanz-Fallback ist ausschließlich bei ausgeschaltetem Netzwerkschalter aktiv.
Die vorhandenen Quellen-/Kartengenerationen schützen weiterhin Positionen und Zielmutationen.
Öffentliche Builds enthalten keinen Instanzkatalog (Nutzerentscheidung 2026-10-21). Ohne Ortsfakten
kann der Netzwerkstrom kein Gebiet bestätigen; der Kartentracker und die HUD-Kontexte arbeiten dann wie
bei ausgeschaltetem Netzwerk-Tracking, und die Ortsanzeige im Map-Tab entfällt.
Eine flüchtige Revision des Netzwerkschalters entwertet alte Bildarbeit auch nach einem schnellen
Aus-/Ein-Rundlauf mit demselben Endwert; sie wird nicht als neue Profileinstellung gespeichert.

HUD-Kontextfreigaben werden bei Karten-, Weltkontext- und Einstellungsänderungen angewendet.
Der Interaktionstimer behält Tastatur-/Vordergrundaufgaben. Bei ausgesetzter visueller Beobachtung
wirkt die bestätigte Instanzfreigabe ohne zusätzliche Menü-/Minimapannahme. Die gemeinsame
Bildaufnahme wird nicht global beendet; Händler- und Daevanionverbraucher bleiben unabhängig.

## Gespeicherte Kartenansichten (2026-10-21)

AppSettings.MapViewports hält native X/Y-Mitte und Leaflet-Zoom je Karte; alte Einstellungen
starten mit leerem Bestand ohne Migration. MapViewport validiert finite Werte. Die interaktive
Karte puffert Bewegungen und meldet ausschließlich endgültige Ansichten mit einer kartenbezogenen
Präsentationsrevision zurück. MapView akzeptiert nur bekannte, von ihm angeforderte Karten und
die jeweils neueste Revision. Der Zustand bleibt von Netzwerk- und Positionsepochen getrennt:
Eine Ansicht ist kein Aufenthalts- oder Spielerbeleg.

MapView wählt anhand des bestätigten Netzwerkorts die unterstützte Karte vor dem ersten visuellen
Spielerfund, unabhängig von Follow-/Progressionsschaltern. Diese steuern weiterhin Zentrierung
und Wegführung. Manuelles Browsen bleibt bis zum nächsten tatsächlichen Netzwerk-Kartenwechsel erhalten.
Erneutes Own derselben nativen Karte sowie reine Inhaltsverfeinerungen erzwingen keinen Wechsel.

## Loot-Sessions: Statistik und synchroner Replay (2026-10-27)

`UnifiedLootTrackerService` ist der einzige aktive Writer für neue Loot-Sessions.
Er serialisiert die bestehenden qualifizierten Farm-, Kina-, Sammel- und Abyss-Fakten
unter einer Session-ID und einer monotonen Sessionuhr. Die bisherigen Farm- und
Lifeskill-Sessionwriter sowie die alte Abyss-Sessionengine sind entfernt.
`AbyssProgressService` pflegt ausschließlich den unabhängigen absoluten Punktecache
und dessen Rangfortschritt. Er abonniert keine Einkommensereignisse, kennt keine
Sessionjournale oder Routen und erwirbt keine Recorder-Lease. Der Rankadapter
`AbyssRankOverlaySource` projiziert Punktecache, aktuelle gemeinsame Session und
flüchtigen Killfeed; das Loot-HUD verwendet allein `UnifiedLootOverlaySource`.

Neue Open-World-Sessions tragen `Kind=Mixed`, Abyss-Aufenthalte `Kind=Abyss` und
alle neuen Sessions `UnifiedVersion=1`. Das Feld `Kind` hält den Weltkontext fest. Jeder
Timelinefakt trägt `Origin=Pve`, `Lifeskill` oder `Abyss`; auch im Abyss behalten
Sammelversuche `Lifeskill` als Herkunft. Gemeinsame Items werden über `Item:{id}`
zusammengeführt, nicht über quellenspezifische Anzeige-Keys.

Unveränderliche Snapshots tragen Sessiongesamtwerte, exakte Ereignisse und getrennte
Bewegungssegmente. `LootTrackerSessionProjection` bildet Gesamt-, PvE-, Lifeskill-
und Abyss-Ansichten aus demselben Parent: ID, Weltkontext und gemeinsame Dauer bleiben
erhalten, nur passende Fakten und daraus berechnete Werte wechseln. Projektionen
schreiben keine Journale. Ältere unvollständig trennbare Ansichten werden kenntlich
gemacht; fehlende Belege bleiben unbekannt. Sortierung, Gruppierung, Minutenwerte
und Scrubben verändern weder Speicherung noch Beute- oder Sessionzählung.

`TimelineVersion`/`TimelineEvents` ergänzen den Sessionvertrag. Jede bestätigte AP-/RP-/Kina-
Änderung und jeder vollständige Drop beziehungsweise Sammelreward erzeugt genau ein Ereignis
an derselben bereits deduplizierten Mutation wie seine Mengen. Eigene Kampfmarker bleiben
ein separater Ereignisstrom; anonyme validierte Klasse und tatsächlicher Gegner-Ranggrad
werden darin eingefroren, niemals Namen oder native Kennungen. Ein Punkte-Stufenmarker
entsteht ausschließlich aus kontinuierlichen absoluten eigenen Rangfakten und bucht keine
weiteren RP. Erste Bestände, Wiederanbindung und Quellenlücken erzeugen kein Einkommen.
Verfügbarkeitswechsel unterscheiden unqualifizierte Kanäle von bestätigten Nullwerten.

`UnifiedLootSessionStore` schreibt ein separates Journal unter
`loot-tracker/unified-sessions/<id>/`: Schema 1 mit absoluten Sessionwerten und
angehängten Timeline-, Routen-, Kampf- und Sammeldeltas in `events-0001.jsonl`,
dazu `summary-v1.json` als atomarer Checkpoint. Der letzte vollständige Journalrecord
ist maßgeblich; Checkpointtotals werden nie dazuaddiert. Der Validator prüft Mengen
und bekannte Kanal-/Versuchszähler gegen die exakten Fakten. Recovery erhält die
committete monotone Dauer und Fakten auch bei kleinen Abweichungen zur UTC-Spanne;
fehlende Daten werden nicht ergänzt. Ein unklarer Schreibzustand unterbricht die
Aufzeichnung, statt halbe Rewards oder weitere Fundhinweise zu veröffentlichen.

`UnifiedLootLegacyHistory` lädt die bisherigen Farm-/Abyss-Journale bis Schema 5 und
Lifeskill-Journale bis Schema 4 im Hintergrund als Historie. Es gibt keine Migration
oder neuen Appends in diese Journale. Historische echte Erntezeilen bleiben nutzbar,
alte Minutenbins ausdrücklich aggregiert; fehlende Wege, Einzelereignisse und
Gegnerkategorien werden nicht rekonstruiert. Ein ausdrückliches Löschen verwendet
den zuständigen alten Store. Tages- und absolute Charaktercaches bleiben getrennt
von der Sessionlöschung und liefern keine nachgetragenen Sessionereignisse.

Frische Positionsfakten werden nach Karten-/Bindungs-/Beobachtungslücken segmentiert.
Open-World-Bewegung seit der letzten Aktivität bleibt vorläufig; Ende und Recovery
verwerfen unproduktiven Nachlauf. Bewegung verlängert keinen Timeout. Der Replay
zeigt den beobachteten Weg; die Kartenübernahme verwendet ausschließlich die aus
bestätigten Lifeskill-Sammelstellen gebildete optimierte Route, keine Laufspur,
Kampfpositionen oder Monsterdropstellen.

Die gemeinsame Replayachse ist die gespeicherte monotone Dauer seit Sessionbeginn;
ursprüngliche Faktzeiten bleiben für Anzeige und Nachvollziehbarkeit erhalten. Zukunft
wird am Playhead entfernt, fehlende Zeiten liefern keine synchrone Position. Karte und
Spielerpunkt folgen nur bestätigten Positionen; nach einer Beobachtungslücke bleibt die
Kamera am letzten Ort und der frische Spielerpunkt erlischt. Punkte-Stufen, Gegnergrade,
Walletbestand, Farm-Cap und Sessioneinkommen bleiben getrennte Größen.

Nutzerergänzung 2026-10-27: Gelaufene Route und Session-Timeline bilden ein
zusammenhängendes Replay-Modul mit gemeinsamer Mediensteuerung: Abspielen/Pause,
Vorwärts-/Rückwärtswiedergabe, Zehn-Sekunden-Sprünge sowie Anfang/aktuelles Ende.
Geschwindigkeiten 0,25x, 0,5x, 1x, 2x, 4x und 8x; Start mit 1x. Wiedergabezustand
und Tempo bleiben lokal im geöffneten Modul, Sessionwechsel pausiert und setzt
zurück. Die monotone Wiedergabeuhr verändert ausschließlich den Playhead,
keine Statistik, gespeicherten Ereignisse oder Sessiontimer.

Nutzerergänzung 2026-10-27: Routenmarker zeigen die Originalicons ihrer tatsächlich
zugeordneten Beute mit der Katalog-Seltenheitsfarbe und bestätigten Mengen. Ernten
werden nur über ihre exakte Sammelstellenidentität zusammengefasst, Drops nur an
identischen gespeicherten Kartenkoordinaten. Der Playhead begrenzt Icons, Mengen
und Besuchszeiten gemeinsam. Neue Drop-Fakten speichern die bereits qualifizierte
frische Minimap-Position optional atomar mit der Beute; historische oder unplatzierte
Beute erhält keine nachträglich geschätzte Position. Killmarker zeigen wahlweise
das eingefrorene Gegner-Rang- oder Klassenicon, ausgegraut mit Totenkopf. Die globale
Auswahl bleibt gespeichert, unabhängig von Replayartefakt und Playhead; fehlende
Gegnermetadaten verwenden einen neutralen Platzhalter mit Totenkopf. Das Laden
alter Journale und Replayartefakte ohne Drop-Position bleibt kompatibel.

### Gemeinsame Lebensdauer und Overlay-Publikation

Außerhalb von Abyss starten qualifizierte Beute und bestätigte Sammelversuche
(einschließlich Fehlversuchen) die gemeinsame Session und erneuern denselben
konfigurierten Timeout. Das Ende liegt auf der letzten Aktivität, nicht auf dem
späteren Timertick. Der 07:00-UTC-Farmtagwechsel trennt Open World. Das Kina-Cap
markiert lediglich dieselbe Session als Limited Kina; es startet keine zweite
Session und setzt weder Dauer noch Mengen zurück. Abyss beginnt beim bestätigten
Eintritt, endet beim Austritt und hat keinen Aktivitätstimeout. Normale Übergänge,
Tod sowie Daily-/Weekly-Reset teilen den Aufenthalt nicht; fehlende aktuelle
Own-/Profilbindung außerhalb erlaubter Übergänge und echte Quellenlücken unterbrechen.

Jeder Kanal behält seine eigene Own-/Generations-/Sequenzdeduplikation. Quelle,
Welt und Faktzeit werden beim Empfang und vor der seriellen Mutation erneut
geprüft. Eine alte Beobachtung kann keinen neueren Weltkontext zurückrollen.
Zähler werden nur bei qualifizierter Verfügbarkeit auf bekannte Null gesetzt.
Keine Sessionlogik ersetzt die bestehenden Producer- oder Transportqualifikationen.

`UnifiedLootOverlayState` veröffentlicht aktive qualifizierte Session, Verfügbarkeit,
eigenen Tagesfakt, profilgebundenen gespeicherten Tagesstand und Bestätigungsstatus
als ein unveränderliches Objekt. Der Overlayadapter liest diese Instanz atomar;
Charakterwechsel kombinieren daher keine Session mit einem fremden Tagescache.
Ein gespeicherter Stand ersetzt keine aktuelle Netzbestätigung. Quellenverlust
blendet die aktive Sessionprojektion aus, ohne historische Werte zu löschen.
