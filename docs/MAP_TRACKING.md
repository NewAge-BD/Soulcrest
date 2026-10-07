# Spielerposition aus der Ingame-Karte

Stand: 2026-10-03. Wunsch des Nutzers: Die Funktion des Map Overlays übernehmen. Man markiert die
Karte im Spiel, Soulcrest verfolgt darüber den Spieler und zeigt ihn auf der interaktiven Karte.

## Vorbild und Lizenz

`problem-xyz/aion2-map-overlay` (Version 1.3.0, Python,
source-available). Die Lizenz erlaubt Lesen und Änderungen für den eigenen Gebrauch, aber keine
Weitergabe. Regel des Projekts: **kein Code kopiert**, nur das Verfahren in C# neu umgesetzt.
Die Kartenbilder stammen aus dem eigenen Datenpaket (`docs/MAP_DATA.md`), nicht aus dem Map Overlay.

## Verfahren (`Soulcrest.Ocr.MapTracking`)

1. **Referenz** (`MapReference`): Die Kacheln einer Zoomstufe werden zu einem Graustufenbild von
   etwa 4096 px zusammengesetzt (Zoom = `refZoom − log2(size/4096)`, bei allen 10 Karten z4).
   SIFT-Merkmale werden **kachelweise** gesucht (512 px, 48 px Rand), damit sie die ganze Karte
   abdecken. Ohne Kacheln behält SIFT nur die stärksten Punkte, und ganze Gebiete bleiben leer.
   Schwarze Randkacheln werden übersprungen. Ohne Merkmalslimit (siehe Überarbeitung unten). Cache unter
   `%LOCALAPPDATA%\Soulcrest\cache\map-references\<karte>-z<zoom>-<datenversion>.bin`. Behalten
   werden die aktuelle Datenversion und die neueste andere (Tester und installierte App nebeneinander);
   ältere löscht Soulcrest beim Laden einer Karte.
2. **Suche** (`MapLocator`): SIFT auf dem markierten Bereich (2.500 Merkmale), Lowe-Ratio 0,75,
   RANSAC-Fit einer Ähnlichkeitstransformation (Verschiebung, Zoom, Drehung), Schwelle 4 px,
   mindestens 12 übereinstimmende Paare (Kartenwechsel 30), Maßstab 0,05–20.
   - **Global:** gegen alle Merkmale (FLANN, kd-Baum). Gilt für das erste Bild und nach Verlust.
   - **Lokal:** nur Merkmale um die letzte Position (Ausschnitt plus halbe Größe je Seite), Brute
     Force. Schneller und mit weniger Fehltreffern. Findet sie nichts, folgt die globale Suche.
3. **Spieler** = fester **Ankerpunkt** im markierten Bereich (Standard: Mitte). Die Spielkarte hält
   die Figur dort fest. Die inverse Transformation bildet ihn auf die Referenz ab, × Welt/px ergibt
   die Markerkoordinaten des Datenpakets (Altgard 2, sonst 1).
4. **Kartenerkennung:** Erst die zuletzt gezeigte Karte. Nach 3 Fehlschlägen wird je Bild eine
   andere Karte probiert, bis eine passt.

Nicht übernommen: optischer Fluss zwischen den Erkennungen und Glättung. Für einen Positionspunkt
reichen 4 Erkennungen pro Sekunde (100–160 ms je Bild).

## Bedienung (Tab „Karte“, Kasten oben links)

1. Karte im Spiel öffnen (Minimap oder Karte, die dem Spieler folgt).
2. **Kartenbereich markieren**: Rahmen nur um den Kartenausschnitt ziehen, ohne Rahmen und Knöpfe.
3. **Spieler verfolgen**. Das Vorschaubild zeigt ein rotes Kreuz. Liegt es nicht auf der
   Spielfigur, klickt man im Vorschaubild auf die Figur (Anker wird gespeichert).
4. „Karte folgt dem Spieler“ verschiebt die Ansicht und wechselt bei Bedarf die Karte. „Zum
   Spieler“ zentriert. Der Marker ist orange und pulsiert, grau heißt „gerade nicht erkannt“.

## Tests

- `MapLocatorTests`: synthetische Spielkarte (Ausschnitt der nächstfeineren Zoomstufe, abgedunkelt,
  mit Titelleiste und Spielerpunkt). Position auf ±0,2 Kartenpixel genau, Maßstab 2,00, zweites Bild
  per lokaler Suche. Ein Pet-Fenster-Screenshot wird nicht platziert.
- `MapReferenceAllMapsTests`: Ein Ausschnitt jeder der 10 Karten wird nur auf der eigenen Karte
  gefunden (51–367 Treffer), auf allen anderen nicht.

## Offen

- Live vom Nutzer bestätigt („funktioniert“, 2026-10-03).
- Eine Karte, auf der sich die Figur bewegt (statt der Karte), braucht eine Erkennung des
  Spielerpfeils statt des festen Ankers.
- Blickrichtung des Spielers (Pfeil) wird nicht ausgewertet.

## Überarbeitung nach dem ersten Live-Fehler (2026-10-03)

Bericht: „wird gerade nicht erkannt“, nachdem der Nutzer eine Markierung auf der Spielkarte gesetzt
hatte. Aufnahme: `tests/fixtures/map-tracking/live-2026-10-03-region.png` (Verteron, halbtransparente
Karte, gelbe Auto-Pfad-Linien, Spielerpfeil bei 266/237 von 518×469).

- Mit der alten Referenz (z4, Budget 100.000) gab es nur **30** stimmige Paare, knapp über der
  Grenze 20. Falsche Karten hatten höchstens 3.
- Das Budget je Kachel war der Engpass: z4 **ohne Limit** 54 Paare. Die Spielkarte zeigt etwa 3,2×
  das Detail von z4, daher **z5 ohne Limit**: 242 Paare (Maßstab 1,62).
- Die gelben Pfadlinien stören kaum, sie fehlen in der Referenz. Eine Farbmaske kostete sogar
  Paare (227 statt 242) und bleibt aus (`MaskPathLines`).
- **Neu:** Ratio 0,8, Mindestens 12 Paare auf der aktuellen Karte. Ein **Kartenwechsel braucht 30**:
  Karten teilen Zierelemente (Kompass, Wappen), ein Abyss-Ausschnitt erreichte auf einer anderen
  Abyss-Karte 20.
- **Zwei Referenzen:** z4 („grob“, alle Karten, für die Kartensuche, 1.400–60.000 Merkmale, ~4 s
  Aufbau) und z5 („fein“, nur die aktuelle Karte, ~58.000 Merkmale bei Verteron, ~15 s). Beide
  werden beim Start im Hintergrund vorbereitet und zwischengespeichert. Die Kartensuche überspringt
  noch nicht fertige Karten und wartet nie.
- Test: `MapLiveCaptureTests` (z4 ≥ 40, z5 ≥ 150 Paare, Spieler bei Verteron 2073/2053 ± 7).

## Wiederfinden nach Verlust (2026-10-03)

Bericht: „wenn die Position einmal verloren ist, wird sie nie wiedergefunden“. Ursache: Nach 3
Fehlschlägen wurden nur noch die **anderen** Karten probiert, die aktuelle nie wieder. Jetzt gilt:

- Im Wechsel: jedes zweite Bild die aktuelle Karte (abwechselnd feine und grobe Referenz), dazwischen
  eine andere Karte (`NextCandidate`).
- **Halten:** Bis zu 4 Fehlschläge (~1 s) bleibt die letzte Lage stehen. Zoomen, Scrollen und
  Ausblenden der Spielkarte ließen Marker und Linien sonst flackern.
- Tracking läuft nach einem Neustart weiter, wenn es vorher aktiv war (`MapTrackingActive`).
- Protokoll: `%LOCALAPPDATA%\Soulcrest\logs\map-tracking.log` (Wechsel gefunden/verloren und alle 10 s
  eine Zeile mit Merkmalen, Treffern, stimmigen Paaren, Maßstab). Ab 1 MB wird es zu
  `map-tracking.old.log`, die Generation davor fällt weg (ebenso `pet-scan.log`).

- **Kartensuche nach Aktualität:** Zuletzt gefundene Karten zuerst. Nach einem Blick auf die große
  Karte einer anderen Zone ist der Spieler meist wieder auf der vorherigen.
- **Plausibilität:** Treffer mit weniger als 30 stimmigen Paaren zählen nur, wenn der Zoom (Bildpunkte je
  Karteneinheit) höchstens 25 % vom letzten guten abweicht. Beim Ausblenden der Karte kamen 14–15 Paare
  bei Zoom 0,55 statt 0,89 zustande, das wären Positionssprünge gewesen.
- Die große Vollbildkarte wird bewusst nicht unterstützt. Sie hält die Figur nicht am Ankerpunkt.

Live-Protokoll danach: Nach dem Ladebildschirm wurde Altgard per Kartensuche gefunden (90 Paare).
Verluste gab es nur bei geschlossener Karte (8–58 Merkmale im Bereich), jedes Mal wiedergefunden. Mit
feiner Referenz: 273–624 Paare, lokale Suche, Fehler 0,3 px.

## Ziele mit Pfeillinien (2026-10-03)

- **Rechtsklick** auf ein Symbol der interaktiven Karte (Pet, Ort, Monster, Ressource …) markiert es
  als Ziel. Ein zweiter Rechtsklick hebt die Markierung auf. Mehrere Ziele sind möglich, jedes hat
  seine Farbe (Reihenfolge des Markierens). Gespeichert in `%LOCALAPPDATA%\Soulcrest\targets.json`
  (`MapTargetsService`). Die Liste im Kasten „Ziele“ springt per Klick zum Ziel, ✕ entfernt eins.
- **Interaktive Karte:** farbiger Ring über dem Symbol (eigene Ebene, wächst mit dem Zoom). Ist der
  Spieler auf derselben Karte, führt eine Linie mit Pfeilen alle 70 px vom Spieler zum Ziel.
- **Spielkarte:** `RouteOverlayForm` liegt deckungsgleich über dem markierten Kartenbereich.
  Pixelgenaue Transparenz (UpdateLayeredWindow), klick-durchlässig und von der Bildschirmaufnahme
  ausgenommen, damit das Tracking es nie sieht. Ziele werden über die aktuelle Lage der Karte
  (`MapPlacement`: Kartenkoordinaten → Referenz → Bildschirm) eingezeichnet. Die Linie wird am Rand
  des Kartenbereichs abgeschnitten (Liang–Barsky, `RouteGeometry`). Liegt das Ziel außerhalb, zeigt
  ein großer Pfeil am Rand mit Namen die Richtung. Ist die Karte nicht erkannt, verschwindet das
  Overlay. Schalter: „Linien zu Zielen auf der Spielkarte“.

## Pet-Stand auf der Karte

Neben jedem Pet-Symbol steht der Stand aus dem Profil: „St. 1 · 6/25“ (erreichte Stufe, Souls zur
nächsten Stufe) oder „St. 3 · max“ (ausgegraut). Die Beschriftung erscheint ab einer Zoomstufe über
der Übersicht, sonst überdecken sich die Schilder. Sie steht auch im Popup.

## Ausbau 2026-10-03 (Nutzerwünsche)

- **60 fps im Ingame-Overlay:** Eine schnelle Schleife (höchstens 60 Bilder/s) erfasst den Kartenbereich
  und trägt die Lage per **optischem Fluss** weiter (`MapFlow`: bis 200 Ecken, Lucas-Kanade, RANSAC,
  wenige ms). Alle 250 ms geht ein Bild an die Erkennung im Hintergrund. Ihr Ergebnis wird um die
  seither gemessene Verschiebung (Drift) korrigiert, wie im Map Overlay. Timerauflösung 1 ms
  (`timeBeginPeriod`), sonst wartet Windows in 15,6-ms-Schritten. Live gemessen: 51–57 fps.
- **Pet-Symbole im Ingame-Overlay:** Porträt der Pet-Spawns (aus `data.js`, `MapPetMarkers`) mit
  „St. 1 · 6/25“ daneben, fertige Pets ausgegraut. Schalter unter Optionen.
- **Overlays in Aufnahmen sichtbar** (Optionen): schaltet `WDA_EXCLUDEFROMCAPTURE` ab. Soulcrests eigene
  GDI-Aufnahme sieht die Overlays trotzdem nicht, denn ohne `CAPTUREBLT` werden geschichtete Fenster
  nicht kopiert.
- **Optionen-Tab:** Kartenbereich, Vorschau (Spielerposition prüfen), Folgen, Linien, Pet-Symbole,
  Aufnahmen. Auf der Karte bleiben ▶/■ (Tracking), ⌖ (Folgen) und eine Statuspille mit Karte und fps.
- **Folgen:** Der Spieler bleibt in der Mitte, die Karte zieht mit, der Zoom bleibt. Nach dem Ziehen der
  Karte pausiert das Folgen 6 s.
- **Progressionsmodus** (Seitenleiste der Karte): weiße gestrichelte Linie zum nächsten Spawn (Pet oder
  Quellmonster) eines Pets unter dem gewählten Ziel (5 / 30 / 105 Souls). Gemessen vom Spieler, ohne
  Tracking von der Kartenmitte. Auch im Ingame-Overlay.
- **Interaktive Karte flüssiger:** Symbole werden beim Zoomen nicht mehr neu erzeugt, die Größe folgt
  den CSS-Variablen `--pet-scale`/`--place-scale`. Die Kachelebene lädt 2 Reihen über den Rand hinaus.

## Weltkarten-Modus (2026-10-03)

### Korrektur 2026-10-04 (0.1.1)

Die unten beschriebene Prüfung nur bei Zoomwechsel oder fehlendem Treffer reichte nicht:
Ein Vollbildausschnitt kann mit demselben Zoom und vielen Merkmalen passen. Zudem veröffentlichte
der optische Fluss neue Spielerpositionen, bevor die Vollbildprüfung abgeschlossen war.

- Vor jeder neuen Spielerposition wird jetzt der Vollbild-Kontext geprüft, unabhängig von Zoom
  und Trefferzahl. Minimap-Ausschnitt und verkleinerte Vollbildansicht stammen aus derselben
  Aufnahme, damit ein Öffnen/Schließen zwischen zwei Aufnahmen keine falsche Freigabe erzeugt.
- Optischer Fluss bewegt nur noch die Overlay-Geometrie. Spielerposition und automatische
  Zielauswahl verwenden ausschließlich geprüfte Erkennungen (höchstens 4/s statt zuvor 10/s).
- Erkannte Vollbildkarten verändern weder letzte Spielerposition noch gespeicherten Minimap-Zoom.
  Nach zwei ausbleibenden Vollbildtreffern wird die Minimap erneut geprüft.
- Regressionstests mit echten Aufnahmen prüfen Vollbild bei identischem gespeicherten Zoom,
  Öffnen und Verschieben sowie Schließen und Wiederaufnahme. Live-Abnahme im Spiel steht aus.

Die folgende Beschreibung dokumentiert den ursprünglichen Stand vor dieser Korrektur.

Bericht: „Wenn ich ingame die Worldmap öffne, kommt das Overlay durcheinander.“ Der markierte Bereich
zeigt dann einen Ausschnitt der Weltkarte. Das ist dieselbe Karte in anderem Zoom, und der feste
Ankerpunkt ist nicht mehr die Spielfigur.

- **Erkennen:** Fehlt die kleine Karte oder ändert sich ihr Zoom um mehr als 15 % (Altgard-Weltkarte 0,55 gegenüber 0,89, nur 38 % Abstand), oder ist noch kein Zoom bekannt (`MiniMapZoom`), wird höchstens bei
  jeder zweiten Erkennung der ganze Bildschirm geprüft (Viertelgröße, grobe Referenz, eigener Locator).
  „Weltkarte offen“ gilt bei ≥ 30 stimmigen Paaren, wenn diese deutlich über den markierten Bereich
  hinausreichen (`MapFix.InlierBounds`, `SpreadsBeyond`). Echte Aufnahmen: offene Weltkarte 194 Paare
  über den halben Bildschirm, normale Spielszene 6 Paare (`WorldMapDetectionTests`).
- **Overlay:** Es liegt über dem ganzen Bildschirm, zeigt Pets, Ziele und Linien auf der Weltkarte,
  folgt Verschieben und Zoomen per optischem Fluss (Viertelgröße, per StretchBlt direkt so erfasst;
  vorher 15 fps mit Vollbildkopie) und zeichnet höchstens 30-mal pro
  Sekunde. Die Spielerposition bleibt eingefroren: Linien starten an der letzten Position der kleinen
  Karte. Zeigt die Weltkarte eine andere Zone, gibt es nur Ringe ohne Linien.
- **Zurück:** Zwei Erkennungen ohne großflächige Karte bedeuten „Weltkarte zu“. Dann wird wieder die
  kleine Karte gesucht.

## Aufnahme per Grafikkarte (Windows Graphics Capture, 2026-10-03)

Der Wunsch war eine schnellere Aufnahme der Weltkarte („in Viertelgröße“). Messung auf dem Rechner des
Nutzers bei laufendem Spiel (`CaptureTimingExploration`):

| Weg | Vollbild 2560×1440 | Viertel | Kleinkarte 518×469 |
| --- | --- | --- | --- |
| GDI CopyFromScreen | 66–85 ms | – | – |
| GDI StretchBlt (Viertel) | – | 57–89 ms | – |
| WGC, auf ein Bild warten | 33 ms | 47 ms | 31 ms |
| WGC mit Pipeline, ohne `Sleep(1)` | 12 ms | **5,6 ms** | **0,5 ms** |

- GDI liest bei jedem Bild den ganzen Desktop aus der Grafikkarte zurück. Deshalb sparte die
  Viertelgröße nichts.
- `MonitorCapture` (nach Grindcrests `GraphicsWindowFrameSource`, angepasst auf einen Monitor):
  WGC-Sitzung auf den Monitor, Zuschnitt auf der Grafikkarte (CopySubresourceRegion), Ring aus drei
  Staging-Texturen. Gelesen wird die Kopie des vorherigen Aufrufs, damit es kein Warten hinter der
  GPU-Arbeit des Spiels gibt. Verkleinert wird direkt aus dem abgebildeten Speicher (OpenCV Area).
- `Thread.Sleep(1)` schläft ohne erhöhte Timerauflösung 15,6 ms (zwei Runden = 31 ms). Daher
  `Thread.Yield`.
- Rahmen: Unter Windows 11 fragt Soulcrest einmal nach Aufnahme ohne gelben Rahmen
  (`GraphicsCaptureAccess`). Ohne Erlaubnis zeigt Windows den Rahmen um den Monitor.
- **Spielfenster statt Monitor:** Mit „Overlays in Aufnahmen sichtbar“ sah die Monitor-Aufnahme
  Soulcrests eigene Symbole. Der optische Fluss verfolgte sie mit, und weil sie selbst nachhinken,
  zogen sie auf der Weltkarte nach und wanderten (Bericht 2026-10-03, Screenshot mit Symbolen). Jetzt
  wird das Fenster von `AION2.exe` aufgenommen (`CreateForWindow`, Lage über
  `DWMWA_EXTENDED_FRAME_BOUNDS`). Darin kommen Overlays nie vor (`WindowCaptureExploration`: Fenster
  ohne, Desktop mit Symbolen). Ohne Spielfenster: Monitor, sonst GDI.
- Rückfall auf GDI, wenn WGC nicht verfügbar ist. Protokoll und Status zeigen `aufnahme=`.
