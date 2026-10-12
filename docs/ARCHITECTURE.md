# Architektur

## Lösung

```text
Soulcrest.slnx
├─ src/
│  ├─ Soulcrest.Core          net10.0        Pet-DB-Modelle, Fortschrittslogik, Chat-Parser,
│  │                                         Scroll-Reconciler, Fuzzy-Matching (plattformneutral)
│  ├─ Soulcrest.Ocr           net10.0-windows Windows OCR (Pet-Fenster, Listen), Kartenabgleich,
│  │                                         Farbklassifikation, Tonemapping, OpenCvSharp
│  ├─ Soulcrest.Vision        net10.0-windows Kartenerkennung: Features, Homographie, opt. Fluss
│  ├─ Soulcrest.App           net10.0-windows WinForms-Host + Blazor Hybrid (WebView2), Capture,
│  │                                         native Overlays, Persistenz, Lokalisierung
│  └─ Soulcrest.BrowserPreview net10.0       UI + Karte mit Beispieldaten im Browser
├─ tests/
│  ├─ Soulcrest.Core.Tests     Parser, Reconciler, Fortschritt, Pet-DB-Validierung
│  ├─ Soulcrest.Ocr.Tests      Pet-Fenster- und Karten-Fixtures
│  ├─ Soulcrest.Vision.Tests   Homographie gegen synthetisch verzerrte Referenzausschnitte
│  └─ Soulcrest.App.Tests      Razor-Komponenten, Services, Stores
├─ data/                       Pet-DB, OCR-Muster, Kartenmanifeste (Kacheln generiert, nicht im Git)
├─ scripts/                    Import-OpenMapData.ps1, Test-Ui.ps1
└─ docs/
```

Abhängigkeiten nur von oben nach unten: `App → Ocr, Vision, Core`; `Ocr → Core`;
`Vision → Core`. `Core` kennt weder Windows noch UI.

## Laufzeitfluss

```text
             ┌──────────────── Soulcrest.App ────────────────────────────────┐
WGC (HWND) → │ CaptureService ─┬─> LootFeedPipeline + ChatPipeline (Ocr)  │
             │                 │      └─> CrossCheck ─> SoulEvents ──────┐    │
             │                 │                                         ▼    │
             │                 │                          ProgressService (Core)
             │                 │                          Profil/Server, Ledger
             │                 │                                         │    │
             │                 └─> MapOverlayEngine (Vision) ─ Homographie   │
             │                                                   │       │    │
             │   NativeOverlayHost: PetProgressOverlay ◄─────────┼───────┤    │
             │                      MapOverlay ◄─────────────────┘       │    │
             │   Blazor UI (WebView2): Karte (Leaflet), Pet-Liste, ◄─────┘    │
             │                         Kalibrierung, Einstellungen            │
             └────────────────────────────────────────────────────────────────┘
```

- **Eine** Capture-Quelle für OCR und Kartenerkennung (WGC auf das Spielfenster), mehrere
  Verbraucher mit eigenen Raten: Loot-Feed schnell bei Änderung (≤ 150 ms, sonst sparsamer
  Bereitschaftsmodus), Chat ≤ 500 ms, Gebietsname selten (~5 s oder bei Soul-Drop), Karte nur bei
  aktivem Karten-Overlay (~15–30 Hz Fluss, Detektion ~2 Hz im Hintergrund).
- Service-Muster wie Grindcrest: Zustands-Snapshots + `Changed`-Event, UI liest nur Snapshots.
  Befehle sind asynchron. Keine Controls im Service.

## Hauptfenster (Blazor)

1. **Karte:** Leaflet (lokal in `wwwroot/lib/leaflet`, kein CDN), `CRS.Simple`, Kacheln aus dem
   Datenpaket. Ebenen:
   - Pet-Gebiete (Polygone, Gattungsfarbe, Füllgrad = Fortschritt), Klick → Pet-Liste gefiltert
   - alle offenen Marker nach Kategorie ein-/ausblendbar (Teleporter, Dörfer, Siegel, Hidden
     Cubes, Empyrean Traces, Ressourcen …), Suche, Zähler pro Kategorie
   - eigene Pins/Notizen (Klick setzen, Text, Farbe, verschieben, löschen, Export/Import JSON)
2. **Pets:** Tabelle/Kacheln aller Pets mit Filter (Gattung, Gebiet, Status: fehlt / in Arbeit /
   max), Fokus-Stern, manuelle Eingabe von Stufe + Souls, „aus Sammlungsfenster lesen“.
3. **Erfassung:** Start/Pause, Status, Live-Feed der erkannten Zeilen mit Konfidenz,
   offene/unzugeordnete Ereignisse, Kalibrierung (Rahmen + Autosuche), Prüfansicht.
4. **Overlays:** Ein/aus, Position, Größe, Inhalt, Hotkeys, Vorschau.
5. **Einstellungen:** Profil/Server, Sprache UI (DE/EN), Spielsprache OCR (DE/EN), Theme,
   Diagnose (Aufzeichnung, Debug-Logs mit Aufbewahrung).

## Datenmodell (Kurzfassung)

```text
PetDefinition    id, names{en,de}, genus, monsters[{names{en,de}, aliases[], regionIds[]}],
                 sources[], thresholds (aus Datei), confirmed, sourceRefs[]
Profile          id, server, displayName, gameLanguage
PetProgress      profileId, petId, level, soulsInLevel, manualOverride?, updatedAt
SoulEvent        id, profileId, capturedAt, source(feed|chat), chatMinute "HH:MM"?,
                 monsterText, petId?, amount, regionIdSeen?, confidence, iconMatch?,
                 status(confirmed|open|unmatched|rejected), frameRefs[]
UserPin          id, map, x, y, title, note, color
```

Speicherorte: `%LOCALAPPDATA%\Soulcrest\` → `settings.json`, `profiles/<id>/progress.json`,
`profiles/<id>/events.jsonl` (append-only), `pins.json`, `calibration.json`,
`diagnostics/`, `webview2/`. Schreiben immer atomar (`AtomicFile`).

## Fortschrittslogik

- Fortschritt ist **ereignisbasiert**: `progress = Startstand (manuell/OCR) + Σ bestätigte
  SoulEvents seit Startstand`. Manuelle Korrektur setzt einen neuen Startstand mit Zeitpunkt.
- Schwellen kommen aus der Pet-DB (`thresholds`), Standard aus `data/pets/defaults.json`.
- Beim Erreichen einer Schwelle: Stufe +1, Rest-Souls übertragen (falls die Ingame-Regel das so
  macht; offen, siehe `PET_SYSTEM.md`).


## Alerts (2026-10-12)

`Core.Alerts.AlertSchedules` berechnet aus einer expliziten Zeitzone die nächste UTC-Instanz
stündlicher, täglicher, wöchentlicher, N-Kalendertage-, Intervall- und Einmalregeln. Der
`AlertsService` wird im Host unabhängig vom aktiven Tab gestartet, liefert Snapshots und
dedupliziert Auslösungen in einem atomaren Schema-1-Store `alerts.json`. Die Uhr ist injizierbar.
Boss-Hinweise konsumieren die vorhandenen `BossRushService.Alerts` einschließlich stabiler
Karte/Spawn-ID; die vorhandene BossAlertPolicy bleibt die einzige Spawn-Triggerquelle.

`AlertsView` und wiederverwendete `BossAlertsSettings` verwalten Zeitplan und individuelle
Ausgaben. `AlertsOverlayForm` zeichnet die sichtbaren Countdowns ohne Fokuswechsel und übernimmt
Schloss, Alt, Capture-Sichtbarkeit und Menü-/Minimap-Unterdrückung. Ton läuft im MainForm über
`NoticeRaised` unabhängig von Rendern und Sichtbarkeit. Das Boss-Rush-Overlay zeichnet im Host
nur noch seine Liste; es erzeugt keine zweite Alarmanzeige. Einstellungen und Vorschau stehen im Alerts-Tab.

`EventCatalog` hält sechs lokale Nichtboss-EU-Zeitpläne nach der gewählten Community-Referenz.
`Core.Alerts.EventWindows` trennt Kalenderstart, Eventlaufzeit und Portalphase und liefert begrenzte
Vorkommen für die Zeitachse. `AlertsService.EventTimers` ist unabhängig von aktivierten Erinnerungen.
Stabile `catalog:<id>`-Erinnerungen nutzen denselben Scheduler; optionale `EventPresentation`-Werte
in Schema 1 steuern die native Anzeige getrennt. Katalogpläne bleiben fest, eigene Alerts editierbar.
Der native Countdown kann mittels `CountdownLabel` auf Portal-/Eventende zeigen; bestätigte
Bossmeldungen behalten ihre bisherige Darstellung. Dashboard und erfasste Bosskarten sind getrennt.

`CountdownRows` enthält ausschließlich HUD-Zeilen. `NotificationRows` enthält zeitlich aktive
Banner unabhängig von Minimap-/Menü-Unterdrückung. `AlertNotificationsForm` zeichnet diese in einem
separaten, immer klickdurchlässigen Fenster an der Timer-Position. `AlertsOverlayForm` bleibt das
ausblendbare, interaktive Timer-HUD. `AlertSoundPlayer` liefert asynchrone lokale MP3-Wiedergabe über
Windows MediaPlayer, mit Dateiauswahl, Abbruch bei neuer Wiedergabe/Dispose und Systemton-Fallback.
`SoundFile` ist ein optionales Schema-1-Feld; neue Laufzeit-Netzwerkzugriffe entstehen nicht.


Boss-Timer verwenden in Alerts und Boss Rush dieselbe `LiveBossTimers`-Komponente. Individuelle
`BossReminderPreference`-Overrides in AppSettings erben ohne Eintrag die globale Vorwarnzeit.
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
