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
Schloss, Alt und Capture-Sichtbarkeit. `TimerOverlayVisibility` wertet ausschließlich für diese
Leiste die positiven AppSettings-Freigaben für Weltkarte, Menü und Instanzen aus den vorhandenen
Trackingzuständen aus. Ohne laufendes Tracking gibt es keine Kontextunterdrückung. Ton läuft im MainForm über
`NoticeRaised` unabhängig von Rendern und Sichtbarkeit. Das Boss-Rush-Overlay zeichnet im Host
nur noch seine Liste; es erzeugt keine zweite Alarmanzeige. Einstellungen und Vorschau stehen im Alerts-Tab.

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
`BossReminderPreference`-Overrides in AppSettings steuern ausschließlich Enabled/LeadSeconds
und erben ohne Eintrag die globale Vorwarnzeit. `BossAlertDurationSeconds` und
`BossAlertUntilSpawn` gelten global für alle Boss-Erinnerungen, bedienbar über der Suche.
Jeder ausgewählte aktive Reminder erzeugt eine sichtbare Notification ohne zusätzliche
Banner-/Minutenfenster-Gates. Legacy-ShowOverlay/AlwaysShowOverlay/OverlayLeadMinutes bleiben
für Store- und Timer-Leisten-Kompatibilität erhalten, beeinflussen jedoch echte Bossnotices und
Tests nicht. Die UI bietet daher nur Vorwarnzeit und optionalen Ton. Bereits zulässig erzeugte
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

## Combat-Datenfundament (2026-10-17, offline implementiert)

Der DPS-Meter erhält eigene versionierte Verträge und deterministische Offlineprojektionen in
`Soulcrest.Core/Combat`. Run → Boss/Versuch → historische Teilnehmer bildet die Struktur;
Ereignisse erhalten monotone Kampfzeit, Capture-UTC, Sequenz und Quellgeneration. Rosteränderungen,
Skills, Schaden, Heilung, Effekte, HP, Tode und Datenlücken bleiben als lokale Primärdaten erhalten.
Aktuelle Fünferplätze werden nicht mit historischer Charakteridentität gleichgesetzt.

`CombatHistoryStore` legt Metadaten, append-only Journale und einen rekonstruierbaren Index
unter `AppPaths.DataDirectory/combat/` ab. Vergleiche und Zeitachsen werden daraus berechnet;
keine zweite Statistikablage, keine Kampfdaten-Uploads. Schemafehler und Korruption werden
ausdrücklich gemeldet. Aufbewahrung/Löschen arbeitet auf ganzen Runs, Favoriten und aktive Runs
bleiben geschützt. Die ersten Bausteine werden offline mit synthetischen Ereignissen geprüft.

Belegte Global/EU-Teildecoder in `Core/Network` lesen neutrale Treffer-/HP-/Party-/CP-/Effektformen
über `CombatProtocolDecoder`. Trefferbeträge werden nicht ohne Schadens-/Heilungsnachweis in
Domänenereignisse umgewandelt. Vorhandene Boss-Rush- oder Dungeonabschlussmeldungen behaupten
weder einzelne Dungeonboss-Kills noch Schadens-/Buffsemantik.

`NetworkLootService` stellt `INetworkCombatSource` aus derselben nativen Erfassung bereit.
Capture-UTC (Freigabepaket), monotone Empfangszeit, Sequenz, opaker Stromtoken und Transportgeneration
gelangen über eine begrenzte `NetworkCombatSubscription` an `CombatNetworkService`. Capturecallbacks
kopieren/enqueuen nur; Decoder, Verbraucher und späterer Persistenzarbeiter laufen außerhalb der
Capture-Sperre. Überlauf liefert auch ohne Folgepaket eine globale Grenze. Dateireplay verwendet
denselben Transport; Datei- und Lesefehler dürfen keine scheinbar vollständige Messung ergeben.

Der Worker veröffentlicht nur nach frischem Own plus bestätigter Gruppenmitgliedschaft und filtert
Entity-Updates auf eigene Gruppe beziehungsweise explizit belegte Bossziele. Jede Autoritätsgrenze
verwirft Roster/Bossziele; logische Kontextrevision schützt zusätzlich gegen alte Encounteradapter
bei Gruppenwechsel innerhalb derselben TCP-Generation. `NetworkCaptureCoordinator` trennt Loot-
und Combatbedarf. Pet-Stopp deaktiviert Fortschrittsanwendung, ohne eine aktive Combatquelle zu
beenden. Quellaufrufe erfolgen außerhalb der Coordinator-Sperre, reentrante Bedarfwechsel werden
erneut abgeglichen. Shutdown weckt Leser und verhindert einen verspäteten nativen Neustart.

Noch offen: vollständige Schaden-/Heilungs-/Boss-/Instanz-/Kampfendsemantik und DPS-Oberflächen.
Konservativer Runcontroller/Persistenzanschluss siehe unten; Beleggrenzen in `DPS_METER.md`.

### Empirische Betragsemantik und Encounter-Wirebelege

`CombatAmountClassifier` liefert Damage/Healing nur für explizit ausgewähltes Evidenzprofil und
belegten Form-/Effektschlüssel, sonst Unknown. Die 15 eingebauten selbst gemessenen V3-Fakten sind
ein unvollständiger empirischer Korpus mit unbekanntem Spielbuild; keine automatische Livefreigabe.
Ein zukünftiger Normalisierer muss Qualität/fehlende Semantik bis zur Vergleichsfreigabe erhalten.

`CombatEncounterMessage` ergänzt NPC-Appear/Gone, Entity-Death und MiddleBossReward. Der Worker
veröffentlicht nach Own+Roster neutrale NPC-Entdeckung sowie gruppen-/zielgebundene Todesmeldungen
und DungeonState/Result. Erscheinung/Verschwinden entwertet alte Bossbindungen. `TryParseWorld`
erhält rohe Mapfelder aus dem vollständig geprüften Own; Zugriff benötigt aktuelle Kontextrevision.
Keine Instanzidentität aus MapKey, kein Bossrang aus HP und kein Sieg aus Verschwinden/HP null.
DungeonState-Zeitfenster sind keine Bosskampfzeit. Der folgende Controller verwendet explizite Belege.

### Konservativer Runcontroller und lokale Schreibstrecke

`CombatRunController` ist reine begrenzte Fachlogik mit expliziten Dungeon-/Roster-/Boss-/Endverträgen.
Gemeinsame monotone Kampfzeit, separate Präsentationsuhr und UTC-Metadatenanker; Capture-UTC bleibt
am Originalereignis. Resultate bleiben standardmäßig 60 s, ein neuer Pull ersetzt sie.
Bekannte Instanz kann nach frischer Autorisierung fortgesetzt werden; unbekannte wird über Lücken
getrennt. Wipe/Reset brauchen einen eigenen Beleg. Kontextverlust und Speichergrenzen unterbrechen.

`CombatRunAdapter` verlangt einen geprüften Map-/Dungeon-/NPC-Join und getrennten Endbeleg.
Vier konservative Auldor-/Krao-Zielregeln stammen aus dem eingebetteten geprüften Faktkatalog;
Rollen und Endbelege bleiben von AmountScope, Entity-/Gruppenautorität und Instanzkennung getrennt.
`CombatUnclassifiedAmountEvent` hält neutrale Beträge in der lokalen Zeitachse; empirische Teilprofile
liefern keine vollständigen DPS-/HPS-Fähigkeiten oder Vergleichsfreigaben. Keine geratenen Besitzer.
Entitylebenszeiten und Party-/Bosskonflikte werden in beiden Richtungen geprüft.

`CombatRunService` konsumiert den bestehenden Worker, taktet Checkpoints und entkoppelt sie über
`CombatHistoryWriter` vom seriellen Disk-I/O. Kompatible wartende Stände desselben Runs werden
zusammengefasst, nicht begrenzt viele fremde Runs still verworfen. Faults sind explizit; mögliche
Teilappends werden nie blind wiederholt. Store versiegelt abgeschlossene Journale gegen neue Events.
Regulärer Shutdown wartet asynchron auf die angenommenen Schreibaufträge. Automatisches Recovery
alter aktiver Runs/Teiljournale und echte Lastabnahme sind noch offen; Konstruktion startet keinen
Mitschnitt und öffnet keine Historie. Alle Daten bleiben unter dem lokalen Combatdatenordner.

### Versionierter Bossfaktkatalog

`CombatEncounterCatalog` lädt `Soulcrest.CombatEncounters.json` ohne Datei-/Netzwerkfallback oder
Öffnen der Quellenreferenzen. Begrenztes strenges Schema, eindeutige IDs/Quellen, gehashte Belege,
defensive Sammlungen; unbekannte Namen/Rollen/Enden bleiben getrennt darstellbar.
`GlobalEuCombatBossCatalog` leitet nur die vier freigegebenen Auldor-/Krao-Zielregeln ab. Die fünfte
bekannte NPC-Zuordnung (Berk) ist weiterhin keine Laufzeitrolle. Keine fremde Variantenkonversion.

Der separate Offline-Aufbereiter überprüft den aktiven CDN-Cache, exakte Variantendatensätze und
Mapgegenlisten gegen ein eingefrorenes Rezept. Das Ergebnis enthält keine Positionen/Rohpakete.
Ein einmaliger gezielter öffentlicher Daten-Nachimport ist Entwicklerarbeit; die App ruft nichts ab.
Konstruktion gewährt kein Amountprofil. Runmetadaten entstehen erst beim bestätigten Pull, behalten
aber den früher beobachteten Dungeonzeitanker. Kontext-/Ersatzentity-Grenzen widerrufen alte Belege.
