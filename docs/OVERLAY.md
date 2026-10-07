# Ingame-Overlays

Zwei Overlay-Arten, beide als native, transparente Top-Level-Fenster über dem Spiel. Das Spiel
muss im **randlosen Fenstermodus** laufen; über exklusivem Vollbild kann nicht gezeichnet werden.

## 1. Pet-Fortschritt (kompakt)

- Liste der **Fokus-Ziele** (vom Nutzer ausgewählte Pets), je Zeile: Gattungsfarbe, Pet-Name,
  Quellmonster, `Souls x / y` bis zur nächsten Stufe, Fortschrittsbalken, Stufe.
- Darunter: letzter erkannter Soul-Drop, Souls/h der laufenden Erfassung, Erfassungsstatus
  (läuft / pausiert / Fehler).
- **Toast** bei jedem bestätigten Soul-Drop: `+2 Magic Gravi → 17/25`, ca. 3 s, stapelbar.
  Unbekanntes Monster: neutraler Toast `+2 ??? (Magic Gravi) – zuordnen`.
- Kein Ton (Interview: nur Toast + Hotkeys).

Technik aus Grindcrest übernehmen (siehe `GRINDCREST_REUSE.md`): `NativeOverlayForm`,
`NativeOverlayHost`, `NativeOverlayRenderer` (GDI+ in ein Layered Window per
`UpdateLayeredWindow`, ohne WebView; `SetWindowDisplayAffinity(0x11)`, `RegisterHotKey`),
`OverlaySettingsStore`, Positionierung relativ zum Spielfenster (`NativeOverlayGameWindow`),
Klickdurchlässigkeit, Vorschau am Desktop, Layout in Blazor bearbeitbar. Rotations-, Buff- und
Session-Module werden nicht übernommen.

## 2. Karten-Overlay (Ingame-Karte)

Zeichnet über die **geöffnete Ingame-Karte** (großes Kartenfenster oder Mini-Kartenfenster unten
rechts, siehe Video):

- **Gebiete fehlender Pets:** Polygone aus `regions.json`, gefüllt halbtransparent in
  Gattungsfarbe (Cogni blau, Fera rot, Natura grün, Varian gelb, Special hellblau).
- **Labels mit Rest-Souls:** am Polygon-Schwerpunkt `Pet-Name · noch N`.
- **Fokus-Ziele:** Standardmäßig nur Fokus-Pets. Umschaltbar auf „alle fehlenden“.

### Erkennungstechnik (nach Vorbild Map Overlay, neu in C#)

Map Overlay (`problem-xyz/aion2-map-overlay`, Python) zeigt das bewährte
Verfahren. Wir **portieren die Technik, nicht den Code**: Lizenz „source-available“, Änderungen
nur zur Eigennutzung erlaubt. Das passt zu „nur privat“, trotzdem sauber neu schreiben.

1. **Bereich wählen:** Nutzer zieht einmal ein Rechteck um die Ingame-Karte (Region-Selector).
2. **Referenz:** Vorab berechnete Features (ORB/AKAZE via OpenCvSharp) auf dem Referenzbild der
   Karte (`data/maps/altgard/reference.webp`, ~4096²), gecacht auf Platte.
3. **Detektion** (eigener Thread): Frame-Ausschnitt → Features → Matching → RANSAC-Homographie
   Referenz → Bildschirm. Gütemaß: Inlier-Anzahl, Reprojektionsfehler.
4. **Tracking zwischen Detektionen:** optischer Fluss (Lucas-Kanade) auf dem Ausschnitt korrigiert
   Verschieben/Zoomen der Karte bei hoher Frequenz. Die Detektion korrigiert nur Drift.
5. **Fehlertoleranz:** Einzelne Fehldetektionen sind Misses. Nach N Misses wird neu aufgebaut,
   danach wird das Overlay ausgeblendet (nicht falsch zeichnen). Werte wie Map Overlay
   (`REBUILD_AFTER_FAILURES = 3`, `GIVE_UP_AFTER_FAILURES = 8`) als Startpunkt.
6. **Änderungsprüfung:** Frames mit unverändertem Stichproben-Hash überspringen (CPU sparen).
7. **Zeichnen:** Polygone über die Homographie transformieren. Ein transparentes, klickdurchlässiges
   Fenster exakt über dem gewählten Bereich, beschnitten auf diesen Bereich.

### HDR

Dieselbe Capture-Quelle wie die OCR (WGC, FP16 → feste Kennlinie). Für das Feature-Matching
zusätzlich Graustufen + CLAHE, damit HDR/SDR dieselben Features liefern.

## Gemeinsames Verhalten

- **Globale Hotkeys** (konfigurierbar, Standard-Vorschläge):
  - `Strg+Alt+P` Pet-Overlay ein/aus
  - `Strg+Alt+M` Karten-Overlay ein/aus
  - `Strg+Alt+L` Overlays entsperren/sperren (Klickdurchlässigkeit)
  - `Strg+Alt+N` nächstes Fokus-Ziel
- Overlays sind standardmäßig **aus Bildschirmaufnahmen ausgeblendet**
  (`SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)`). Option „In Aufnahmen sichtbar“.
  Wichtig: Soulcrest selbst nimmt per WGC das **Spielfenster** auf. Die eigenen Overlays dürfen die
  OCR nicht stören. Das wird durch die Fensteraufnahme plus Display-Affinity sichergestellt und
  muss getestet werden.
- Overlays folgen dem Spielfenster (Bewegen, Monitorwechsel, DPI) und verstecken sich, wenn das
  Spiel minimiert oder nicht im Vordergrund ist (`NativeGameForegroundMonitor`).
- Overlays lesen und schreiben **keinen** Spielspeicher, senden keine Eingaben an das Spiel.
