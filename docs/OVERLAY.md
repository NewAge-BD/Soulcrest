# Ingame-Overlays

Zwei Overlay-Arten, beide als native, transparente Top-Level-Fenster über dem Spiel. Das Spiel
muss im **randlosen Fenstermodus** laufen; über exklusivem Vollbild kann nicht gezeichnet werden.

## 1. Pet-Fortschritt (kompakt)

- **Nur bei aktueller Meldung** (Nutzerauftrag 2026-10-18): „Loot & Pet Progress“ bleibt
  ohne neue Pet-Souls ausgeblendet. Ein echter angezeigter Soul-Gewinn öffnet das Panel für
  die bestehende Minute; weitere Gewinne verlängern ihre jeweilige Einblendung. Ein ausgewähltes
  Fokus-Pet oder der laufende Tracker allein hält das Fenster nicht offen. Bestätigte
  Aufstiegsanimationen bleiben für ihre bestehenden 30 Sekunden sichtbar, einschließlich MAX.
  Sobald weder aktuelle Soul-Einblendung noch Aufstiegsanimation vorliegt, verschwindet das
  ganze Panel samt Schloss. Die Overlay-Einstellung bleibt dabei eingeschaltet.
- Liste der **Fokus-Ziele** (vom Nutzer ausgewählte Pets), je Zeile: Gattungsfarbe, Pet-Name,
  Quellmonster, `Souls x / y` bis zur nächsten Stufe, Fortschrittsbalken, Stufe.
- Darunter: letzter erkannter Soul-Drop, Souls/h der laufenden Erfassung, Erfassungsstatus
  (läuft / pausiert / Fehler).
- **Toast** bei jedem bestätigten Soul-Drop: `+2 Magic Gravi → 17/25`, eine Minute, nach Pet gruppiert.
  Unbekanntes Monster: neutraler Toast `+2 ??? (Magic Gravi) – zuordnen`.
- Kein Ton (Interview: nur Toast + Hotkeys).
- **Namen im Loot-Overlay** (2026-10-08): Fokuszeilen und Loot-Einblendungen verwenden die
  Monsternamen der Spielsprache, unabhängig von der Oberfläche. Unter Optionen → Overlays →
  „Overlay-Sprache“ sind Automatisch (Spielsprache), Deutsch und Englisch wählbar; Standard ist
  Automatisch. Eine feste Overlay-Auswahl hat Vorrang, danach die explizite Scan-Sprache, danach
  die zuletzt sicher erkannte Sprache aus Pet- oder Erkundungsscan. Vor der ersten Erkennung gilt
  die bisherige Namenssprache. Erkannte Sprache und Auswahl bleiben nach Neustart erhalten;
  leere oder unklare OCR-Texte löschen die Erkennung nicht. Fehlende deutsche Monsternamen bleiben
  Englisch; fehlt ein Quellmonster, gilt dieselbe Sprachwahl für den Pet-Namen. Menütexte und
  Statusbeschriftungen behalten die Oberflächensprache.
- **Soul-Namen und eindeutige Zeilen** (Nutzerwunsch 2026-10-11): Unter dem hellen Monsternamen
  steht eine eigene kleinere graue Zeile `Soul: <Pet-Name>` aus dem Pet-Katalog, in derselben
  Overlay-/Spielsprache. Fehlende Soul-Namen werden nicht geraten; lange Namen enden mit Ellipse.
  Die Zeilenhöhe enthält zusätzlich Stufe, Zähler und Fortschrittsbalken. Die Zusatzzeile wird bei
  alten Beuteeinträgen zusammen mit der Karte ausgeblendet.
  Das aktuelle Fokus-Pet erscheint ausschließlich im Fokusbereich; seine Soul-Drops der letzten
  Minute stehen dort als `+N`. Historische Aliase werden vor dem Gruppieren auf dieselbe Pet-ID
  aufgelöst. Unter „Letzte Beute“ bleiben bis zu fünf andere Pets, neueste zuerst. Beim Fokuswechsel
  erscheint das vorherige Fokus-Pet wieder in der Beuteliste, solange sein Drop noch aktuell ist.
  Die Aufteilung verändert nur die Anzeige, nicht Loot-Ereignisse oder gespeicherten Fortschritt.

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

## Schloss und hängende Alt-Zustände (2026-10-11)

- Nutzerdiagnose an der laufenden Instanz: geschlossenes Schloss, aber verschiebbares Panel.
  Im Eingabe-Log blieb der Modifier-Observer auf Alt gedrückt, während Raw Input bereits
  losgelassen meldete. Ein späterer Release im Observer stellte die Sperre wieder her.
- Für Alt und Shift zählt jeweils die zuletzt eingegangene Modifier-Beobachtung beider Quellen.
  Eine neuere Loslassmeldung kann einen alten Gedrückt-Zustand entkräften, auch wenn die meldende
  Quelle selbst zuvor schon losgelassen annahm. Der positive Windows-Tastenzustand bleibt als
  Fallback für noch nicht eingegangene Press-Meldungen erhalten. Unbekannte Quellen haben keinen
  Vorrang. Es werden nur der letzte Modifier-Zustand und eine Reihenfolgezahl gehalten, keine
  anderen Tasten oder Eingabetexte.
- Loot-, Boss- und Leveling-Schlösser zeigen den tatsächlichen Interaktionszustand, einschließlich
  temporärer Alt-Freigabe. Ein ausdrücklicher Klick auf Sperren beendet Drag/Resize bzw. eine
  ausstehende Button-Aktion sofort. Ein bereits aktives Alt-Signal bleibt für dieses Panel bis
  zu seiner nächsten Loslassmeldung blockiert; erst erneutes Alt-Drücken gibt es wieder frei.
  Manuelles Entsperren bleibt unabhängig davon möglich. Ein Hotkey mit Alt schaltet die manuelle
  Wahl um, damit die temporäre Alt-Freigabe das dauerhafte Entsperren nicht umkehrt. Die Wahl ist weiterhin flüchtig;
  Ausblenden beendet die manuelle Interaktion. Keine Weltkarten-Interaktion wird aktiviert.

## Boss Rush (2026-10-09)

Eigener Tab statt Karten-Kachel. Das rote Ziel führt zum ältesten bestätigten Boss, der nächste
Spawn hängt als blasse, gestrichelte Kette daran. Ohne aktiven Boss beginnt die Vorschau beim
Spieler. Beide Ziele sind flüchtig und werden nicht durch Ankunft oder manuelle Routen beendet.

Ein eigenes natives Boss-Panel zeigt 1–12 nächste Spawns mit Porträts/Countdowns. Größe, Position
und Klicksperre sind im Boss-Rush-Tab konfigurierbar. Namen übernehmen die Overlay-/Spielsprache;
Aufnahme-Sichtbarkeit entspricht den gemeinsamen Overlay-Settings. Die bisherige Pet-Overlay-
Sperre und ihre Hotkeys bleiben unabhängig.

Ausgewählte aktive Boss-Erinnerungen erscheinen als eigenständige Overlay-Benachrichtigung,
unabhängig von Weltkarte, Menü, Instanzen und Sichtbarkeit oder Auswahl der Timer-Leiste.
Die Vorwarnzeit je Boss legt den Beginn fest. Global gilt eine feste Sekundendauer oder das
vollständige Vorwarnfenster bis zum Timerablauf. Bei Countdown 0 verschwindet die Vorwarnung
ohne zusätzliche Wartemeldung; ein Spawn wird dadurch nicht bestätigt.
Es gibt kein zusätzliches Bannerfenster. Ton bleibt optional (standardmäßig aus; keine Änderung
am Soul-Drop-Feedback). Bereits ausgelöste feste Meldungen behalten bei einem Kartenwechsel
ihre Restdauer. Veraltete Listen lösen keine neuen Warnungen aus. Der Test-Button zeigt eine
Vorschau ohne Spiel-Eingaben oder Aufnahme.

## Kompakte Alert-Oberflächen (2026-10-12)

Nutzerentscheidung: HUD, Benachrichtigungen und Timeline haben eigene persistierte Breite,
Skalierung und Position. Größenregler in Alerts und unterer rechter Ziehgriff bei Schloss-/Alt-
Freigabe. Auf Abschluss/Capture-Verlust/Relock/Alt-Release wird eine Änderung beendet; Refresh
überschreibt sie nicht. Bannersichtbarkeit bleibt unabhängig von Menü/Minimap.
Die Timeline verzichtet auf Titelkopf, Zeitraumbuttons und Fußzeile. Resetlinien statt Resetspuren,
Siege in der Invasionsspur; ausgewählte Bosse als lokale Icons in einer optionalen Einzelspur.
Zeitpunkte und kollidierende Inhalte bleiben per Tooltip zugänglich. Siehe ALERTS.md.

## Native Timer-Leiste (2026-10-18)

Der Nutzerauftrag ersetzt die bisherige Timer-Kartenliste vollständig durch eine Currency-artige
Leiste: vier lokale Event-Icons und kräftige helle Zeitwerte aus der lokal mitgelieferten
Noto Sans Bold (18 px, klare dunkle Kontur), 28-px-Iconboxen in 32-px-Zeilen und wählbare Zwischenräume.
Der nachfolgende Nutzerauftrag vom selben Tag entfernt Hintergrund und Fade vollständig, einschließlich
der Einstellung; auch ein früher gespeicherter Hintergrundwunsch bleibt wirkungslos.
Keine Überschrift und keine Einzelkarten. Bosse verwenden ihre belegten lokalen Kartenporträts in einer
ruhigen runden Kontur. Täglicher Reset, wöchentlicher Reset, eigene Timer und fehlende Bossporträts haben
unterscheidbare eigene Symbole in warmer Goldpalette (SVG-Quellen und transparente PNGs). Auswahl und aktive Phasen ändern keine
Erinnerungs-/Banner-/Tonwünsche. Die neue sichtbare Sektion im Timer-Menü steuert Auswahl, Größe,
Abstand, Sekunden und Overlay-Einstellungen. Dort lassen sich Weltkarte, Menü und Instanzen
unabhängig freigeben. Die offene Spielwelt bleibt sichtbar; ohne laufendes Spielertracking
wird kein Bereich angenommen. Das frühere gemeinsame Anzeigefenster entfällt. Viele ausgewählte
Timer umbrechen innerhalb des Monitors; Reihenfolge und Countdownbreite bleiben bei Zeitwechseln
stabil. Nur während der zehnminütigen Rift-Öffnung ergänzt ein eigener Namensbereich
„Spacetime Rift offen“ bzw. „Spacetime Rift Open“ in Gold. Danach zählt Rift bis zum nächsten Start.
Die gemeinsame reine Anzeigeprojektion verändert keine Kalender- oder Erinnerungsregeln.

`AlertsOverlayForm` präsentiert die Leiste über `UpdateLayeredWindow`; die bisherigen Schutzregeln
für Fokus, Alt/Schloss, Click-through und Capture-Sichtbarkeit bleiben bestehen. Die Timer-HUD-
Ausblendung verwendet die neuen Bereichsfreigaben über das vorhandene Tracking.
Der Schlossknopf sitzt neben der Leiste und verdeckt keine Zeit. Die alte zweizeilige Darstellung
existiert nur noch für separate Erinnerungsbanner. Timeline und Banner bleiben unabhängig.
Produktionsrenderer und Offline-Vorschau verwenden dieselben Layout-/Zeichenmethoden; keine
Spielbedienung oder zweite App nötig. Herkunft der Originalgrafiken: `wwwroot/icons/events/SOURCE.md`.

## Händler-Soulcounter (2026-10-13)

Ein eigenes stets durchklickbares Layered Window ergänzt aktuelle Pet-Soulcounter im Kartenfuß
unter den Preisen des Black Cloud Merchant. Special behält seine gemischten Slots; Skins bekommen
keine Ergänzung. „Owned“/„Sold Out“ beeinflussen die Anzeige nicht. Schalter unter Optionen →
Overlays, standardmäßig an. Frische Counter sind warmweiß, bestätigtes `MAX` hellgrün,
gespeicherte/unbekannte Stände gedämpft graugold; `?` ersetzt eine unbekannte Balance.

Gestaltung auf Nutzerwunsch 2026-10-13: Der Kartenfuß setzt die vorhandenen Seiten mit einer
offenen goldgrauen Haarkontur und kleiner Spitze fort, statt ein rundes Status-Badge anzuhängen.
Ein fast deckender warmgrauer Verlauf verdeckt die alte schräge Unterkante und deren Ornament.
Keine obere Querlinie; Preis, Name und Auswahlkasten bleiben frei. Die Fläche beginnt auf Höhe
der Preisoberkante und spart die native Preis-Pille mit einem erweiterten Capsule-Clip aus
(2 skalierte plus 1 physischer Pixel Sicherheitszugabe). So verdeckt sie die alten schrägen Ränder ohne sichtbare angehängte Oberkante.
Bei Referenzskala 1 ist der Fuß 212 px breit und reicht 52 px unter den Preis (36 px ohne Farmzeile).
Segoe UI in physischen Pixeln: normale Counter 16 px, Farmregionen 12 px, schwarzer Schatten
um 1 px nach unten. Lange Texte werden anhand ihrer gemessenen Breite eingepasst. Bestätigtes
`0/5` bleibt ein kräftiger goldener Counter; gespeichertes `0/5` gedämpft. Spätere `0/25` oder
`0/75` erhalten keine Freischaltbetonung.

Nutzerpräzisierung 2026-10-13: Special und MAX müssen direkt auffallen, farbige Hervorhebung ist
erwünscht. MAX steht allein in 19 px Bold mit hellem Grün und erhält ohne Special einen grünen
Kartenfuß sowie kräftigere salbeigrüne Kontur. Gespeichertes MAX bleibt weniger gesättigt.
Special steht in 14 px Bold in hellem Bernstein, flankiert von kleinen Rauten entsprechend den
Shop-Ornamenten. Dazu ein warmbraun getönter Fuß und eine deutlichere Goldkontur. Bei Special
plus MAX bleiben Bernsteinfläche/Special-Zeile und grünes MAX gemeinsam sichtbar. Der Verlauf
beginnt am Preis neutral und färbt erst den Fuß stärker ein; Preis-Pille und Geometrie bleiben
frei. `Special` bezieht sich weiterhin ausschließlich auf die Farmzuordnung, nicht Gattung
oder Shop-Tab. Alle Maße folgen ausschließlich der bereits
zurückgerechneten Ankerskala; weder Fensterauflösung noch Monitor-DPI skalieren zusätzlich.
Die fünf Referenzen und zusätzliche 1920×1080-/1280×720-Varianten werden offline mit dem echten Renderer
geprüft; das Werkzeug unter `scripts/merchant-preview/` startet keine App/Erfassung. Es prüft auch
den auf 1920 px verkleinerten Analysepfad mit Rückprojektion und identische Pixel bei 96/144/192 DPI.
Reine Geometrie-Regressionen decken zusätzlich 120 DPI ab.

Die vorhandene gemeinsame WGC-Sitzung liefert ausschließlich Spielfensterpixel: dieser Pfad hat
keinen Monitor-/GDI-Fallback. MerchantCardLocator erkennt Siegel, Preis-Pillen und Kartenränder,
ohne OCR oder Spielbild-Templates. Erst zwei verschiedene frische Frames mit zehn bestätigten
Kartenplätzen erlauben die Anzeige. Alle fünf gelieferten UI-Skalen sind offline im produktiven
C#-Locator geprüft, zusätzlich in der auf 1920 Pixel Breite reduzierten Analyseauflösung.
Bei Siegelmangel darf dort ein zweites 5×5-Closing ausschließlich Kandidatenkonturen reparieren.
Ein gültiges gemeinsames Layout darf bei fehlgeschlagenen Ränderprüfungen um höchstens einen
Analysepixel gemeinsam verschoben werden. Acht Siegel sowie alle zehn Preise und Kartenränder
bleiben mit denselben Schwellen Pflicht. Scheitert ein kleineres Bild (960–1919 px Breite),
wird einmal linear auf 1920 px normalisiert und anschließend genau einmal zurückgerechnet;
bereits akzeptierte Originalanker bleiben unverändert. Keine Capture- oder Quellenänderung.
Physische Fensterkoordinaten werden genau einmal umgerechnet, ohne zusätzlichen DPI-Faktor.

Die Händleranzeige ist unabhängig von Minimap-Fund, Instanzruhe und Kartenrouting. Sie verschwindet
bei fehlendem Layout, Kontext-/Seitenwechsel, Fensterbewegung, Minimierung, Vordergrundverlust oder
veraltetem Bild. Ein vorübergehender Kaufdialog sperrt die Angebotsseite nicht: nach zwei neuen
stabilen Bildern erscheinen die Counter wieder. Eine echte Transportlücke entfernt die
Netzwerkseite auch ohne Charakterbindung. Wartende UI-Updates verwenden nur die neueste Beobachtung; ein verworfenes Altbild
kann keine Counter erneut einblenden. Ausschließlich vorhandener Fortschritt wird gelesen.

Die kleinere zweite Zeile nennt Farmregionen: Abyss, Altgard, Verteron, bei mehreren Regionen alle
in dieser Reihenfolge. „Special“ bezeichnet Pets ohne Fundort auf diesen Karten im vollständigen
aktuellen Quellenabgleich, unabhängig von Pet-Gattung und Händler-Tab. Fehlende Zuordnung bleibt
`?`. Die Offline-Tabelle verknüpft Kartenmarker und explizite Pet-NPC-Orte; so werden auch Pets
ohne platzierten Kartenmarker korrekt eingeordnet (Elite Klaw und Elite Guardite: Verteron).

## Pet-Kategorie und Spawnzahlen im Händler (2026-10-18)

Nach Nutzerpräzisierung stehen Cogni, Fera, Natura oder Varian mittig oberhalb des nativen
Pet-Namens, im freien Streifen unter dem Auswahlkästchen. Der Soulcounter steht wieder
mittig unter dem Preis und nutzt die volle Zeilenbreite. Dezente Blau-/Rot-/Grün-/Goldtöne
ordnen die Kategorien ein; Quelle
ist die numerische Pet-ID-Tabelle. Andere oder fehlende Kategorien bleiben `?`. Dies ist
unabhängig von der Farmklassifikation Special und dem Special-Tab des Shops.

Die Farmzeile ergänzt je Region die Anzahl belegter Soul-Quellmonster-Platzierungen,
beispielsweise `Abyss (784) / Altgard (123)`. Gezählt wird `PetDefinition.Monsters` im
kanonischen lokalen Katalog, nicht die geclusterten Pet-Symbole aus `Spawns` und keine
aktuelle Population. Abyss summiert die Maps der gleichnamigen Manifestgruppe; ohne Manifest
werden ausschließlich die drei etablierten Reshanta-IDs berücksichtigt. Fehlende positive
Zahlen bleiben `?`: Elite Klaw und Elite Guardite behalten beispielsweise `Verteron (?)`,
da die Ortsbelege die Map bestätigen, aber keine exakten Platzierungen liefern. Special
bleibt ohne Zahl. Mehrere Maps behalten ihre Reihenfolge, Aliasse werden nicht doppelt gezählt.

Die Kategorie wird ausschließlich als 13-Pixel-Text mit dezentem Schatten auf transparenter
Fläche gezeichnet. Ihr 96×20-Pixel-Rechteck liegt zentriert auf der Preis-/Kartenachse und
beginnt 228 skalierte Pixel über der Preisoberkante; alle Maße folgen nur der Ankerskala.
Die fünf Referenzen bestätigen Abstand zu Auswahlkästchen, Rabattfähnchen und Pet-Name.
Header und Fuß haben getrennte erlaubte Zeichenbereiche. Das Layered Window umschließt
beide; Name und Artwork dazwischen bleiben vollständig transparent. Die Surface wird vor
jedem Rendern explizit geleert. Außerhalb liegende Kategorieflächen verwerfen das ganze Set.

Kategorie und Count-Text sind additive Wertfelder, damit unveränderte Angebotszeilen auch
weiterhin gleiche Darstellungszustände ergeben. Fußhöhe/-breite, Preis-Aussparung, MAX- und
Special-Hervorhebung sowie die Capture-/Stabilitäts-/Fokus-/Durchklicklogik bleiben erhalten.
Die Metadatenfüße haben eine um 6 skalierte Pixel tiefere Schulter vor der kurzen Spitze;
lange Farmzeilen sitzen zusätzlich 3 skalierte Pixel höher. So bleiben Glyphen und Schatten
innerhalb der Goldkontur, mit Abstand zur ersten Zeile.

## Händlerstabilität und Diagnose (2026-10-16)

Nach zwei bestätigenden Bildern wird die erste verifizierte Ankergeometrie einschließlich
Skala festgehalten. Kleine Messschwankungen verschieben das Fenster und die Preis-Aussparungen
nicht mehr. Die vorherigen Anker sind im Locator nur eine Hypothese: acht aktuelle unabhängige
Siegel und alle zehn Preise/Kartenränder müssen weiterhin die bestehenden Rohkantenprüfungen
bestehen. Echte Layout-, Fenster- und Quellenwechsel verlangen erneut zwei verschiedene Bilder.
Eine zur Fenstergröße unpassende Capturefläche wird verworfen; ein Pixel Rundungsdifferenz des
HDR-Verkleinerungspfads bleibt zulässig. Keine zweite DPI-Skalierung und kein Capture-Fallback.

Ein fehlendes oder kurz GPU-bedingt blockiertes Bild darf eine bereits bestätigte Darstellung
nur innerhalb ihrer ursprünglichen Einsekunden-Frischefrist erhalten. Dabei wird kein
Zeitstempel erneuert. Ein frisches Bild ohne gültiges Shoplayout, ein anderer Shopkontext,
Vordergrundverlust und Fensterbewegung blenden weiterhin sofort aus. Kaufdialog-Rückkehr
bleibt ohne neue Netzwerkliste möglich, sobald zwei gültige neue Bilder vorliegen.

Eine vollständig geprüfte identische Angebotswiederholung derselben Quelle ist unverändert,
auch bei anderer Paket-Reihenfolge (Slots bleiben nach GoodsId sortiert). Sie kann keine Seite
erstmalig bestätigen oder nach Reset/Malformed wiederbeleben. Ein Status blendet weiterhin
sofort aus; ausschließlich sein vollständiges passendes Zweierpaar darf die Referenz einer
identischen vorherigen Seite wiederverwenden. Generation, Server-/Streamkontext und die
Zwei-Sekunden-Grenze bleiben strikt, globale Pet-Zählung und Quellenbestätigung unverändert.

Der Diagnosebericht enthält nun anonymisierte Händlergründe (z. B. keine Angebote, Layout
nicht bestätigt, Capture-Aussetzer, altes Bild, Darstellungsfehler), Zähler und höchstens 24
Zustandswechsel mit UTC-Zeit. Keine Angebote, Paketdaten oder Identitäten werden protokolliert.
Ein fehlgeschlagener nativer Fenstertransfer wird als Fehler behandelt, nicht als sichtbare
Darstellung verbucht.

## Pet-Aufstiegsanimation (2026-10-16)

Nur das kompakte Pet-Fortschrittsoverlay animiert bestätigte Live-Aufstiege für 30 Sekunden.
Stufe 1 und 2 werden direkt in der passenden Fokuszeile hervorgehoben; andernfalls in Recent
Loot („Letzte Beute“). Ein Goldakzent, Lichtlauf, Porträtring und „Stufe … erreicht“ gehören
zur vorhandenen Zeile. Es erscheint keine zusätzliche normale Erfolgskarte. Hat die Planung
bereits das nächste Fokusziel gewählt, bleibt der Hinweis beim aufgestiegenen Pet in Recent
Loot. Ohne aktuellen Drop wird dort vorübergehend eine reine Anzeigezeile ohne +Menge ergänzt;
beim Ablauf verschwindet sie wieder. Ein aktiver Aufstieg wird vor der Fünfergrenze einsortiert,
damit echte Dropmengen auch für ein sonst sechstes Pet erhalten bleiben.

MAX erhält weiterhin eine eigene grüne Erfolgskarte mit Porträt und „Pet vervollständigt!“.
Pets mit Maximalstufe erscheinen unabhängig von diesem Hinweis nicht mehr in Recent Loot.
Der Filter greift vor der Fünfergrenze, andere Pets rücken nach. Fokuszeilen bleiben davon
unabhängig. Zeilen werden über kanonische Pet-IDs zugeordnet, nie über sichtbare Namen.
Lichtlauf und Ringimpuls markieren den Beginn, anschließend bleibt die Hervorhebung ruhig
bis zum Ausblenden. Beschriftungen sind DE/EN, die vorhandene Overlay-Skalierung bleibt gültig.

ProgressService veröffentlicht ein rein flüchtiges Ereignis ausschließlich für akzeptierte
explizite Erwerbs-/Upgrade-Meldungen. Souls allein, Verbrauch, Login-Snapshots und manuelle
Korrekturen lösen es nicht aus. Geladene beobachtete Stufen und Baselines setzen eine stille
monotone Untergrenze; wiederholte Bestätigungen derselben Stufe, beide Bestätigungspfade und
Quellenwechsel spielen den Erfolg nicht erneut ab. Manuelle Overrides bleiben geschützt.
Das Ereignis wird außerhalb des Fortschrittslocks vor Changed veröffentlicht, ohne neue
Loot-Ereignisse, zusätzliche Persistenz oder Änderungen an Zählung/Quellenprüfung.

Die Animation verwendet monotone Stopwatch-Zeit, eine aktive Meldung und höchstens acht
wartende Pets. Höhere Stufen desselben Pets werden zusammengefasst; eine volle Warteschlange
verwirft die älteste wartende Meldung. Ein 33-ms-Zeichentimer läuft nur während des Hinweises,
der bisherige Datenrefresh bleibt bei 250 ms. Ausdrückliches Ausblenden, Unterdrückung, Abschalten
und Dispose leeren den flüchtigen Zustand. Automatisches Verbergen des leeren Panels hält die
Annahme neuer Aufstiege aktiv; es zeigt dabei keine alten Meldungen erneut. Es gibt keinen späteren
Nachholalarm und keine Fokusaktivierung.
Die bestehende Klicksperre, Aufnahme-Sichtbarkeit und Capturelogik bleiben erhalten.
