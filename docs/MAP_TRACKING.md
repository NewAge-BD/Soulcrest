# Spielerposition aus der Ingame-Karte

## Aktuelle Arbeitsweisen (Nutzerpräzisierung 2026-10-21)

Öffentliche Builds enthalten keinen Instanzkatalog (Nutzerentscheidung 2026-10-21). Ohne Ortsfakten
kann der Netzwerkstrom kein Gebiet bestätigen; der Kartentracker und die HUD-Kontexte arbeiten dann wie
bei ausgeschaltetem Netzwerk-Tracking, und die Ortsanzeige im Map-Tab entfällt.

Bei eingeschaltetem Netzwerk-Tracking bestimmt ausschließlich der bestätigte Weltkontext die
Aufenthaltskarte und Instanzart. Ein unterstütztes offenes Gebiet wird nur gegen seine eigene
Minimapreferenz geprüft. Bildverlust startet dort weder eine Suche nach anderen Aufenthaltskarten
noch eine Instanzvermutung. Bildschirmgeometrie, Position und Zoom bleiben visuelle Fakten.

In bestätigten Dungeons und Eventinstanzen ruht der Kartentracker vollständig ohne Bildanforderungen:
keine Zwei-Sekunden-Recovery, ESC-/Weltkartenbeobachtung, SIFT-Suche oder Optical Flow. Das gilt auch
für Shugo Festival und Dimensional Invasion. Netzwerkänderungen und Stop wecken die wartende
Schleife; bereits eingeplante Ergebnisse unterliegen weiterhin der aktuellen Quell-/Kartengeneration.
Unterstützte offene Abyss-Karten behalten ihre vorhandene Ausnahme und Positionsfunktionen.

Auch bei Start ohne Own, Übergang, gestörtem Netzwerk oder unbekanntem Gebiet wird kein visueller
Instanz-Fallback aktiviert, solange der Netzwerkschalter eingeschaltet ist. Erst ausdrücklich
ausgeschaltetes Netzwerk-Tracking erlaubt die bisherigen unbestätigten Verfahren einschließlich
Mehrkartensuche und Instanzvermutung nach Kartenverlust. Die gespeicherte Ruheoption gilt für diesen
Fallback; sie wird nicht durch die effektive Netzwerkentscheidung verändert.

Die aktuelle Location erscheint kompakt in der Netzwerk-Tracking-Kachel des Map-Tabs mit
Bestätigungsstatus sowie belegter Kategorie/Modus. Direkte Weltkontext-Ereignisse aktualisieren sie
auch bei gestopptem Player-Tracker. Sie ist keine visuelle Kartenbeobachtung.

Die folgenden datierten Abschnitte dokumentieren die Entwicklung des visuellen Verfahrens.

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

## 2026-10-12: Quelle und Alter der Aufnahme qualifizieren

Der tatsächliche synchron gelieferte Frame trägt neben seinem Aufnahmezeitpunkt die Information,
ob er ausschließlich aus dem Spielfenster stammt. Monitor- und GDI-Fallbacks gelten nur mit AION2
im Vordergrund; isolierte WGC-Fensterbilder bleiben im Hintergrund nutzbar. Aufnahmealter maximal
zwei Sekunden. Verwerfen invalidiert auch ausstehende erste Matches, ohne daraus einen Dungeon
abzuleiten. Zentrieren/Follow verwenden ausschließlich FreshPosition; historische Positionen
bleiben grau und schalten keine Karte um.

## 2026-10-12: Unabhängige Legende pro Oberfläche

Im Karten-Tab hat jede Gruppe, Kategorie und Ressourcenart drei Checkboxen: Minimap,
interaktive Karte und Worldmap. Gruppen schalten nur ihre jeweilige Spalte; Unterarten werden
je Oberfläche getrennt gespeichert. Die Auswahl gilt pro Karte und bleibt nach Neustart erhalten.
Interaktiv bleiben MapCategories/MapHiddenKinds bestehen. Native Auswahlen liegen getrennt in
MapMinimapLayers/MapWorldmapLayers (Categories/HiddenKinds); MapPlacement.WorldMap wählt die
passende native Auswahl. Ein explizites leeres Array blendet alle regulären Symbole dieser
Oberfläche aus. Explizite Ziele/markierte Pets und separat aktivierte Guard-Zonen behalten ihre
bisherigen Regeln.

Vor der ersten Legendenänderung werden beide bisherigen nativen Ansichten aus ShowPetsInGame,
ShowResourcesInGame und den bisherigen Kategorien/Unterarten übernommen. Erst danach wird
die gewünschte Spalte geändert. Damit verändert ein interaktiver Klick keine noch unmigrierte
native Ansicht. Neue Auswahlen haben Vorrang vor den alten globalen Schaltern.

Die interaktive Karte verbindet erfasste Boss-Timer ausschließlich über die eindeutige
Named-Spawn-ID. Ohne Navigationsziel muss zusätzlich der exakte Locations/Field-Boss-Katalogpunkt
eindeutig sein. Seit dem Boss-Rush-Rework vom 2026-10-25 übernimmt ein sichtbarer Boss-Rush-Pin
den kombinierten Namen-/Timertooltip; der normale Kategoriemarker zeigt dann keinen zweiten
Timer. Ohne Pin gehört der Timer weiterhin zur Boss-Ebene. Ein Pin behält seinen Countdown
auch bei ausgeblendeter Kategorie. Ein einmaliger Markerindex vermeidet wiederholte Scans des
Kartenpakets; lokale monotone Countdowns aktualisieren ausschließlich bestehende Tooltiptexte.

Nur gespeicherte oder ausdrücklich entwertete Listen sind unbestätigt; das Informationsalter
allein entwertet keine bestätigte Liste. Unbestätigte Navigationsmarker und Wege erscheinen
verblasst grün mit Hinweis. Null bedeutet „Warte auf Bestätigung“, bis eine Servernachricht einen
Spawn bestätigt. Boss-Spielerlinien benötigen einen frischen passenden Positionsfix der aktuellen
freigegebenen Kontextgeneration; beim Entzug bleiben nur bekannte Boss-zu-Boss-Kanten bestehen.


## Schnellere Wiedererkennung nach Instanzen und Kartenwechseln (2026-10-19)

Nutzermeldung: Abyss -> Altgard wird sehr spät erkannt; bis dahin bleibt der Instanzmodus aktiv.
Die bisherige Suche prüfte im Ruhezustand nur eine Karte je zwei bzw. fünf Sekunden und schob
zwischen andere Karten wieder die alte Karte ein. Nun bleibt der Ruheabstand bei zwei Sekunden.
Ein Probe-Bild prüft mehrere vorbereitete Referenzen in einem auf 1,2 Sekunden begrenzten Lauf.
Zuletzt besuchte mögliche Zielkarten kommen zuerst, die alte Karte bleibt erreichbar. Nach einem
abgebrochenen Suchlauf geht es beim nächsten Kandidaten weiter statt immer von vorne anzufangen.
Das gilt auch nach mehreren Fehlversuchen außerhalb des pausierten Modus.

Eine schwache grobe Übereinstimmung bereitet die nötige Detailreferenz im Hintergrund vor; sie
blockiert die weiteren Kandidaten nicht. Nach Fertigstellung wird das Tracking geweckt und diese
Karte mit einer neuen Aufnahme bevorzugt geprüft. Je Karte läuft höchstens eine solche Aufbereitung;
beim Beenden werden Erkennung und Aufbereitung beendet bzw. abgewartet, bevor native Referenzen
freigegeben werden. Keine Vorbereitung aller Detailkarten auf Vorrat.

Die Mindestbestätigung für einen Kartenwechsel (30 geometrisch stimmige Paare), Weltkartenprüfung,
Zoomplausibilität und Generation des Aufnahmekontexts bleiben erhalten. Ein schwacher Treffer,
Ladebildschirm oder bloßes Wecken beendet den Instanzmodus nicht. Der gesamte Suchlauf berücksichtigt
eine Altersgrenze für seine Ergebnisse; veraltete Ergebnisse werden nicht als neue Position gemeldet.
Die Positionsquelle bleibt die bisherige Aufnahme und Bildauswertung, ohne Speicherzugriff.

## Interaktive Kartenansichten und Netzwerkauswahl (2026-10-21)

Der Map-Tab speichert Mitte (native Kartenpixel) und Leaflet-Zoom getrennt je Karte in den
AppSettings.MapViewports. Die bestehende LastMap-Auswahl bleibt separat. Beim Wiederbesuch wird
der gespeicherte eigene Ausschnitt geladen. Explizites Zentrieren auf einen frisch erkannten
Spieler übernimmt dessen Mitte und erhält bevorzugt den gespeicherten Zoom der Zielkarte.
Karten ohne gespeicherte Ansicht nutzen die bisherige Übersicht beziehungsweise die belegte
Maßstabsanpassung beim Spielerwechsel. Die optische Icon-Basisskalierung bleibt an der Übersicht.

Bestätigtes eigenes Netzwerk-Own wählt eine unterstützte Kartenbrücke auch ohne visuelle Position
und bei gestopptem Player-Tracker. Die Kartenauswahl erfolgt auch bei FollowPlayer=false und
ProgressionEnabled=false. Diese Schalter steuern weiterhin Spielerzentrierung und Wegführung;
bei beiden ausgeschalteten Schaltern bleibt der gespeicherte Zielkartenausschnitt maßgeblich.
Wiederholte eigene Meldungen und Dungeon-Verfeinerungen derselben Karte
setzen manuelles Browsen nicht zurück. Auch native IDs nicht unterstützter Instanzen werden als
Beobachtung gemerkt, damit die Rückkehr auf eine bekannte Karte als Wechsel erkannt wird.
Übergänge, Lücken, ausgeschaltetes Netzwerk und fehlende Kartenbrücken erfinden keine neue Auswahl.
Die Kartenansicht liefert weiterhin keinen Beleg für eine Spielerposition oder Zielankunft.

Viewport-Rückmeldungen tragen die konkrete Präsentationsrevision je Karte. Die letzte ausgehende
Ansicht darf noch auf ihre eigene Karte gespeichert werden; verspätete Meldungen eines früheren
Besuchs dürfen keinen neueren Stand überschreiben. Zwischenzustände aus Fit/Restore und verworfene
asynchrone Ladevorgänge werden nicht gespeichert. Bewegungen werden gebündelt statt pro Frame
in Einstellungen geschrieben.

## Weltkarten-Icons bei unerkanntem Fenstersymbol (2026-10-22)

Die schnelle Symbolprüfung darf die Weltkarte erst nach zwei aufeinanderfolgenden Fehlbildern
schließen, wenn das Symbol seit dem aktuellen Öffnen mindestens einmal positiv erkannt wurde.
Eine per Kartenabgleich bestätigte Weltkarte ohne erkanntes Symbol bleibt damit sichtbar;
der bestehende Rückfall über zwei fehlgeschlagene Weltkartenabgleiche erkennt das Schließen.
Jedes erneute Öffnen beginnt mit unbestätigtem Symbol. Beobachtungen alter Kartenkontexte und
nicht akzeptierte Aufnahmen dürfen die Prüfung weder aktivieren noch die Weltkarte schließen.
Erkennungstakt, Optical Flow, Projektion und Renderer bleiben unverändert.

## PvE-Laufspur und temporäre Sessionroute (2026-10-26)

Eine aktive bestätigte Open-World-PvE-Session bezieht dieselbe Recorder-Lease wie
Lifeskill. Die gespeicherte manuelle Karten-/Trackingwahl wird nicht verändert.
Frische gültige Minimap-Positionen werden maximal einmal je Sekunde und bei Loot
übernommen. Map-, World-/Own-Bindungsgeneration und Erkennungslücken trennen die
Strecke. Nachlauf nach dem letzten Loot ist nur vorläufig; Abschluss und Recovery
schneiden ihn ab. Das Journal speichert nur neue Routenpunkte/Segmente, die kleine
Zusammenfassung enthält keine wachsende Laufspur.

„Auf Karte anzeigen“ kopiert die gewählte aufgezeichnete Karte in eine temporäre
Leaflet-Ebene mit separaten Linien je Abschnitt. Fit-Bounds richtet die Ansicht
aus; Follow-Zentrierung ist während der Vorschau nur vorübergehend ausgesetzt.
Navigation und Routenbibliothek bleiben erhalten. Kartenwechsel oder Aufheben
räumen die Vorschau auf. Die kleine Loot-Tab-Vorschau verwendet dieselben Pixel
und die bestehenden passenden Offlinekacheln.

Die Ortsbenennung verwendet auf ausdrückliche Nutzerentscheidung den nächsten
benannten Regions-Mittelpunkt aus dem aktiven Kartenpaket. Sie bleibt ausdrücklich
angenähert: Labelzentren sind keine Gebietsgrenzen. Der Resolver lädt die lokalen
validierten JSON-Kartenpayloads einmal, führt kein JavaScript aus und verwirft
Abstandsgleichstände unterschiedlicher Ortslabels.


## Gemeinsame Minimapspur für Session-Replays (2026-10-27)

Die Replayaufzeichnung nutzt dieselbe Capture-/SIFT-/Optical-Flow-Pipeline wie das
Minimap-Overlay. Ein vollständiger Bildabgleich liefert bei Bewegung nominell alle
250 ms einen Positionsanker, bei Stillstand seltener. Die schnelle Schleife liefert
bis 60 Hz Bildfluss. Die tatsächliche Auflösung hängt von frischen verfügbaren Bildern
ab; die frühere 1-Hz-Sessionroute bleibt als kompatible Zusammenfassung erhalten.

Die inverse Overlay-Abbildung am Spieleranker ergibt Kartenkoordinaten für die
Replay-Zwischenposition. Jede Probe trägt die ursprüngliche monotone Bildzeit,
eine lokale Sequenz, einen Kontinuitätsabschnitt und Herkunft Bildabgleich/Bildfluss.
Die öffentliche bestätigte PlayerPosition und automatische Ankunft bleiben getrennt.
Nur die Minimap darf Bewegung liefern. Weltkarte, ESC, veraltete Aufnahme,
Kontextverlust und gebrochener Bildfluss trennen die Spur. Bildflussproben bleiben
bis zum nächsten bestätigten Minimapfix vorläufig.

Der Positionsstrom wird im gemeinsamen Sessionordner in versionierten binären
Sekundenblöcken mit Sequenz und Prüfsumme gespeichert. Ein begrenzter Hintergrund-
puffer entkoppelt I/O von Capture und Lootwriter. Überlast und Speicherfehler erzeugen
eine sichtbare Positionslücke und ändern keine Beutesumme. Recovery verwendet den
letzten intakten Block und die endgültige monotone Dauer der Originalsession;
unproduktiver Nachlauf verlängert die Session nicht.

Erst „Replay rendern“ bereitet beendete und unterbrochene Sessions auf. Ausschließlich
die Replayfassung darf unmittelbar benachbarte Gleichkartenabschnitte als geschätzte
Gerade verbinden. Bekannte Kartenwechsel und eigene Abyss-Tode verhindern Verbindungen.
Originalfakten und Farmroutenexport bleiben erhalten. Der Replaycache wird aus
Originalsession und Positionsstrom abgeleitet, nicht aus einem Ansichtsfilter.
