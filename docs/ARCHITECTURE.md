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
