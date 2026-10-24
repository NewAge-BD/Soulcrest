# Ingame-Overlays

Soulcrest enthält neun Overlayarten als native, transparente Top-Level-Fenster über dem Spiel. Das Spiel
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
  Monsternamen der Spielsprache, unabhängig von der Oberfläche. Unter Overlay → Gemeinsame Einstellungen →
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

Ein eigenes natives Boss-Panel zeigt 1–12 nächste Spawns mit Porträts/Countdowns. Größe und Position
sind im Overlay-Tab konfigurierbar; Schloss und Alt steuern die Klicksperre. Namen übernehmen die Overlay-/Spielsprache;
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
Erinnerungs-/Banner-/Tonwünsche. Der Eintrag Timer-Leiste im Overlay-Tab steuert Auswahl, Größe,
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
Spielbedienung oder zweite App nötig.

## Händler-Soulcounter (2026-10-13)

Ein eigenes stets durchklickbares Layered Window ergänzt aktuelle Pet-Soulcounter im Kartenfuß
unter den Preisen des Black Cloud Merchant. Special behält seine gemischten Slots; Skins bekommen
keine Ergänzung. „Owned“/„Sold Out“ beeinflussen die Anzeige nicht. Schalter unter Overlay →
Händler-Soulcounter, standardmäßig an. Frische Counter sind warmweiß, bestätigtes `MAX` hellgrün,
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
Nach einer erfolglosen vollständigen 3×3-Prüfung darf bei höchstens 1920 Pixeln Breite ein
zweites 5×5-Closing ausschließlich Kandidatenkonturen reparieren. Zusätzliche falsche Konturen
dürfen diesen Versuch nicht verhindern: ihre globale Anzahl beweist keine acht passenden Siegel.
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
bestehen. Die bekannte Geometrie sammelt dafür aktuelle Siegelbelege nur um ihre zehn erwarteten
Positionen, bevor die freie Suche ihre globale Kandidatenbegrenzung anwendet. Fremde Konturen
können diesen Abgleich nicht vorzeitig abbrechen. Jeder Morphologiepass muss für sich acht
verschiedene aktuelle Siegel und alle Rohkantenprüfungen erfüllen; Belege werden nicht addiert.
Echte Layout-, Fenster- und Quellenwechsel verlangen erneut zwei verschiedene Bilder.
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
Lichtlauf und Porträtpuls wiederholen sich während der gesamten Meldung bis zum Ausblenden
(Nutzerpräzisierung 2026-10-24). Die bestehende 30-Sekunden-Frist und das abschließende
Ausblenden bleiben erhalten. Beschriftungen sind DE/EN, die vorhandene Overlay-Skalierung bleibt gültig.

Alle fünf neuen Themes zeigen ebenfalls Lichtlauf, pulsierende Kontur und Porträthervorhebung
innerhalb ihrer vorhandenen rechteckigen Zeilen beziehungsweise MAX-Karte. Die Effekte folgen
der jeweiligen Gold-/Mintpalette und derselben Animationszeit; ein Themewechsel startet den
Hinweis nicht erneut. Soulcrest legacy verwendet weiterhin seinen ursprünglichen Zeichenpfad.

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


## Zentrale Einstellungen und Place Mode (2026-10-20)

Der Tab **Overlay** enthält die Darstellung, Position, Größe, Hotkeys und Kontextfreigaben
aller acht Overlayarten. Die bisherigen Fachansichten verlinken direkt auf den passenden
Eintrag. Routen, Erinnerungsdefinitionen und Töne bleiben dort; ebenso die gemeinsame
Drei-Spalten-Kartenlegende. Overlay-Sprache und Aufnahmesichtbarkeit gelten gemeinsam.

Der **Place Mode** zeigt die sechs frei platzierbaren Panels gemeinsam mit reinen
Vorschauinhalten auf einem gewählten Monitor. Spielmonitor automatisch, sonst App-Monitor;
auch ohne Spiel nutzbar. Ein eigenes aktivierbares Fenster dimmt den gesamten Monitor um
65 Prozent. Raster und Einrasten (16 physische Pixel) sind unabhängig schaltbar.
Verschieben und proportional skalieren über die Auswahlrahmen; Banner und Timeline haben
zusätzlich eine getrennte Breite. Jede abgeschlossene Geste wird atomar gespeichert;
Escape und Beenden beenden ohne Rücknahme. Nicht bearbeitete Vorschaupositionen werden
nicht gespeichert. Karte und Händler bleiben spielgebunden und haben separate Vorschauen.

Ein separater Präsentationszustand verbirgt währenddessen echte Fenster und Schlösser.
Er erzeugt keine Fachereignisse und verlängert keine laufenden Erinnerungs-/Aufstiegsfristen.
Bei Rückkehr zählt der aktuelle Inhalt, die normalen Kontextregeln und gesperrte Interaktion.

Vier überlappende Freigaben je HUD: Weltkarte, Menü, Instanzen, fehlende Minimap.
Nur laufendes Tracking darf Kontexte feststellen. Menü umfasst ESC und Minimapverlust
außerhalb einer Instanz; fehlende Minimap gilt auch innerhalb einer Instanz, außer bei
geöffneter Weltkarte. Alle aktiven Bedingungen benötigen ihre Freigabe. Boss-Spawns und
Benachrichtigungen sind initial überall freigegeben. Loot/Leveling initial nirgends;
Timer und Timeline übernehmen die bisherigen individuellen Regeln. Inhaltsvoraussetzungen
bleiben bestehen. Die früheren festen Boss-Ausblendregeln sind damit ersetzt.


### Wann anzeigen / When to Show

Die gemeinsame Einstellung gilt für sämtliche acht Overlayarten. Standard ist
**When Aion2 is in the foreground** / **Wenn Aion2 im Vordergrund ist**; **Always** / **Immer**
hebt ausschließlich diese Fokusbedingung auf. Die übrigen Kontext- und Inhaltsregeln bleiben
wirksam. Auch der Händler verwendet bei Immer weiterhin ausschließlich frische, bestätigte
Anker im sichtbaren Spielfenster. Der Place Mode bleibt unabhängig davon nutzbar.


## Gemeinsame Themes (2026-10-20)

Overlay → Gemeinsame Einstellungen → Theme bietet Aion2 - stilisierte (Standard, auch bei alten
Einstellungsdateien), Aion2 - neutral, Obsidian, Nordlicht, Runen und Soulcrest legacy.
`AppSettings.OverlayTheme` speichert `aion2-stylized`, `aion2-neutral`, `obsidian`,
`aurora`, `runic` oder `soulcrest-legacy`. Die alte ID `aion2` sowie fehlende/ungültige
Werte ergeben `aion2-stylized`; ausdrücklich gespeicherte Themeauswahlen bleiben erhalten. Es gibt ein gemeinsames Layout, keine Themeprofile.
Die Auswahl gilt für sieben Overlayarten. Das Händler-/Shop-Overlay ist seit der
Nutzerpräzisierung vom 2026-10-20 ausdrücklich ausgenommen: Liveanzeige und Vorschau
verwenden immer den festen bisherigen Soulcrest-Stil.

`OverlayThemeDefinition` bündelt unveränderliche Farben, Schriftrollen und Abstände.
Die Produktionspainter erhalten das Theme ausdrücklich; die bisherigen optionalen
Painter-Aufrufe ohne Theme bleiben für Legacy-Renderfälle kompatibel. Themeabhängige
Messgeometrie liefert Fenstergrößen, Zeilen, Lock- und Buttonflächen. Livefenster,
PNG-Vorschauen und Place Mode teilen diese Zeichenfunktionen. Die HTML-Timervorschau
bezieht ihre Themefarben aus derselben Definition.

Die beiden Aion2-Varianten zeichnen Flächen in #171B1F mit Alpha 204: neutral mit
ungefärbtem Außenrand, stilisierte mit abgeschrägten Ecken und doppelter Metallkontur.
Ihr Innenlayout und ihre Farben bleiben gleich. Obsidian verwendet kontraststarkes
Schwarz/Grauweiß mit Segoe-UI-Überschriften, Nordlicht Blau/Mint mit Bahnschrift und
leicht gerundeten Konturen, Runen Braun/Bronze mit Palatino-Linotype-Überschriften und
Rautenornamenten. Aion2 verwendet Georgia Bold. Beschriftungen bleiben kräftig in
Segoe UI Semibold, Countdowns in der lokalen Noto Sans Bold. Loot bleibt nach Fokus/Beute gegliedert, Leveling
folgt der Questhierarchie, Boss-Spawns behalten Warnfarben. Timer bleiben hintergrundlos.
Karte und Händler behalten Projektion, native Anker, Preise und semantische Farben.

`LayeredOverlayPresenter` präsentiert die freien Panels aller neuen Themes mit Alpha pro Pixel;
Schrift bleibt deckend, während der Hintergrund durchscheint. Legacy verwendet seinen
bisherigen Paint-/Opacity-Pfad. Beim Wechsel der Layered-Betriebsart bleiben Capture-
Sichtbarkeit und Klickdurchlässigkeit erhalten; Schlossfenster folgen derselben Theme-
und Geometriewahl. Reine Darstellung ändert keine Fortschritts- oder Ereignisfristen.
Kartenframes und Sprite-Caches tragen die Theme-ID, veraltete Frames werden verworfen.
Der Händler-Painter erhält keinen Themeparameter; ein Themewechsel verändert weder
seine festen Fonts/Materialien noch seine Vorschau und löst keine Theme-Neuzeichnung aus.

Während einer aktiven Geste bleibt die bisherige Geometrie gültig. Nach Geste-Ende
wird das aktuelle Theme angewendet, auch bei einer wirkungslosen Place-Mode-Geste.
Der Wechsel selbst speichert weder Positionen noch Skalierungen oder Breiten neu.


## Daevanion-Board (2026-10-20)

- Automatisch am geöffneten Spielbrett ausgerichtete Ergänzung mit klickdurchlässigen Node-Flächen
  für Nezekan, Zikel, Vaizel, Triniel, Ariel und Azphel in allen acht unterstützten Klassen.
- Skill-Nodes zeigen ihr originales Skillbild und +1 für den Skillstufenbonus. Stat-Nodes
  zeigen ein lokalisiertes Kürzel und den exakten Bonus; Ariel-/Azphel-Doppelboni stehen in zwei
  zugeordneten Spalten. Ariels PvE- und Boss-Boni behalten ihren sichtbaren Kontext.
- Native Rahmen, Auswahlmarkierung und Start-Node bleiben erhalten. Abgedunkelte Nodes bleiben
  lesbar; der sichtbare Aktivierungszustand steuert nur die Darstellung und speichert keinen Fortschritt.
  Die ergänzte Innenfläche verwendet immer
  einen festen Spielstil, unabhängig vom globalen Theme.
- Die Klasse stammt ausschließlich aus der vollständig bestätigten eigenen Netzwerkidentität.
  Ohne bestätigte Klasse wartet die Anzeige. Charakter-/Verbindungswechsel entwerten den Kontext;
  eine unbekannte Klasse beeinflusst weder Loot- noch Erkundungsfortschritt.
- Gemeinsame WGC-Fensteraufnahme ohne Monitor-Fallback. Sechs unterschiedliche Rastermasken,
  originale Rahmen und Startkontur bestätigen das Board. Zwei verschiedene frische Frames müssen
  die Geometrie bestätigen; unklare Ansichten und verdeckte Nodes werden nicht überzeichnet.
- Der Overlay-Tab enthält Schalter, Erkennungsstatus, Kürzellegende und eine reine Vorschau aller
  Klassen/Boards. Vorschauwahl ist keine manuelle Live-Zuordnung. Kein freies Layout und kein
  Entsperren; der optionale Ein-/Ausblend-Hotkey ist zunächst unbelegt.
- Sprache, Vordergrundmodus, Aufnahmesichtbarkeit und Place-Mode-Unterdrückung gelten gemeinsam
  mit den anderen Overlays. Die Erkennung benötigt kein laufendes Karten-Tracking.

Nutzerentscheidung vom 2026-10-22: Oberhalb rechts am vollständigen Board sitzt ein eigener,
direkt anklickbarer Schalter „Overlay an/aus“ (DE/EN). Er blendet nur die Node-Ergänzungen aus:
Skillbilder, Zahlen, Hintergründe und Fassungen. Seine eigene Klickfläche bleibt sichtbar;
außerhalb des Schalters bleibt das Spiel bedienbar. Alt oder Entsperren sind nicht erforderlich.
Die Auswahl `DaevanionNodeContentVisible` wird gespeichert und überlebt Boardwechsel und Neustarts;
bei alten Einstellungen ist sie eingeschaltet. Der Overlay-Tab zeigt ihren Zustand an.

Die Erkennung läuft bei ausgeblendeten Node-Inhalten weiter. Schalterposition und Frische werden
auch dann geprüft; ein geschlossenes oder unklar erkanntes Board, fehlende eigene Klasse, Ablauf
der Quellenfrist, Vordergrund- oder Platzierungsunterdrückung entfernen auch den Schalter.
Der bestehende Hauptschalter und Hotkey deaktivieren weiterhin alles einschließlich Erkennung
und Schalter, ohne die gespeicherte Auswahl für die Node-Inhalte zu ändern.

Der Schalter verwendet ein eigenes nicht aktivierendes Fenster ohne Bindung an die Sichtbarkeit
des Node-Fensters. Er folgt dem vollständigen Raster statt der aktuell sichtbaren Node-Teilmenge.
Falls oberhalb nicht genug Platz ist, weicht er rechts neben den oberen Boardrand aus. Beide
Plätze werden auf den Spielclient begrenzt; fehlt auch rechts sicherer Platz, entsteht keine
Klickfläche über Nodes. Die Größe folgt dem Zielmonitor-DPI, die Rasterkoordinaten bleiben physisch.
Eine vor Board-/Kontextwechsel, Verschieben, Ausblenden oder Verlust der Maus-Erfassung begonnene Klickgeste
kann keine verspätete Umschaltung auslösen. Bei einem Speicherfehler bleibt die vorherige Auswahl
erhalten und der Schalter zeigt einen kurzen Fehlerhinweis.

Die Stat-Highlighter (Nutzerauftrag 2026-10-22) stehen unabhängig davon rechts neben dem
vollständigen Board: pro Familie 8, 14 (Ariel) oder 12 (Azphel) getrennte Stat-Schalter.
Qualifizierte Kürzel und vollständige Tooltips unterscheiden PvE/PvP/Boss. Mehrfachauswahl
bildet die Vereinigung passender Stat-Nodes; Doppelboni erhalten genau eine Markierung.
`DaevanionHighlightedStatIds` speichert bekannte, eindeutige IDs in Katalogreihenfolge.
Standard leer; „Alle Highlights aus“ setzt auch auf anderen Boards gespeicherte Stats zurück.
Der zentrale Overlay-Tab zeigt die Auswahl des frischen Boards und bietet denselben Reset.

Auf spätere Nutzerpräzisierung läuft ein ruhiger elfenbeinfarbener Lichtzug im Uhrzeigersinn
um die ausgewählte Fassung (2,4 Sekunden pro Umlauf, bis etwa 30 Bilder/s, bei Last reduziert), statt statischer
Hervorhebung. Die getrennte klickdurchlässige Zeichenfläche bleibt auch bei „Overlay aus“
aktiv. Jedes Animationsbild liest die aktuelle Auswahl und prüft aktuelle Quelle, Kontext
und Verdeckung erneut. Animationszeit ersetzt niemals Aufnahmezeit oder Quellenfrist.
Native eigene und benachbarte Rahmen, Aktivierungsringe und Start bleiben ausgeschlossen.
Bei großen Boards reduziert ein Zeichenbudget den Takt auf 50–66 ms; die Umlaufgeschwindigkeit
bleibt an der monotonen Uhr gebunden, ohne Nachholschleifen oder verlängerte Quellenfrist.

Die Schalterleiste verwendet ein eigenes NoActivate-Fenster; nur die abgeschrägten einzelnen
Schaltflächen nehmen Klicks entgegen, Zwischenräume bleiben frei. Breite 176 DIP, Zeilen
28 DIP mit 4 DIP Abstand; zwölf DIP Abstand rechts neben der vollständigen Fassung.
Die bestehende rechte Ausweichposition des An/Aus-Schalters bleibt ausgespart. Bei wenig
Höhe erlauben feste Blätterpfeile alle Stats; bei fehlender Breite entsteht kein Panel über
Nodes und der Overlay-Tab meldet Platzmangel. DPI und Spielclientgrenzen gelten gemeinsam.
Board-/Kontextwechsel, Bewegung und Ausblenden verwerfen begonnene Klickgesten. Aufnahme-
ausblendung und Sichtbarkeitsregeln gelten auch für Leiste und Animation. Beide ändern
weder Snapshot, Erkennungsrevision noch Node-Grafikcache; Speicherfehler nehmen die Auswahl
zurück und zeigen eine Rückmeldung. Hauptschalter/Hotkey verbergen sämtliche Fenster.

Die runde Nutzermaske begrenzt Zahlen und Skillbilder auf etwa 65% des Node-Durchmessers.
Originaler Aktivierungsring, quadratischer Außenrahmen und farbige Ecken bleiben frei. Statwerte
liegen auf dem gelieferten Stein-Hintergrund, mit Palatino Linotype Bold und dunkler Kontur;
Skillbilder verwenden dieselbe runde Alphaform.

Nutzerentscheidung vom 2026-10-21: Stat-Nodes verwenden die umlaufende Fassung (Entwurf 4).
Das Kürzel sitzt unter dem nativen Rahmen; der runde Innenraum bietet größere Zahlen und bei
Doppelboni zwei passende Spalten. Common/None-Fassungen sind schieferfarben, Rare grün,
Legend blau und Unique orange. Die Kategorie bestimmt nur das Material, niemals Bonus oder
Node-Typ. Die Beschriftung nennt auch grüne, blaue und orange Boni ausdrücklich.

Die Außenfassung wird aus dem nativen Rahmen und dem tatsächlich erkannten Rasterabstand
berechnet. Engere Zwischenräume verkürzen nur die zusätzlichen Außenflächen; native Rahmen
werden nicht verschoben oder übermalt. Unter fünf Pixeln nutzbarer Beschriftungshöhe bleibt
die kompakte Innenbeschriftung erhalten. Nicht bestätigte Nodes erhalten keine Ergänzung;
Nachbarrahmen einschließlich Start bleiben auch von Außenflächen ausgenommen. Die stabile
Fensterfläche umfasst die Fassungen des vollständigen Boards, unabhängig von Verdeckungen.

Wie beim Shop wird ein bestätigtes Raster anschließend als bevorzugte Suchhypothese verwendet.
Jedes neue Bild prüft Rahmen, Startkontur und belegte/leere Felder erneut; alte Geometrie ist
kein Bildnachweis. Identische Inhalte benötigen kein erneutes Layered-Window-Zeichnen. Ein frischer
Snapshotwechsel während des Zeichnens oder Sichtbarkeitsticks erzeugt keine unnötige Ausblendung.
Die Fensterfläche bleibt bei veränderten sichtbaren Nodes am ganzen Board ausgerichtet.
Fehlende oder kurz blockierte WGC-Lesezugriffe behalten nur die ursprüngliche Ein-Sekunden-Frist;
widersprechende Bilder und Kontextwechsel verwerfen die Darstellung weiterhin.

Nutzerkorrektur vom 2026-10-22: Ändert ein Tooltip die sichtbare Node-Teilmenge während des
Zeichnens mehrfach, wird die fertige Fläche mit den jeweils frisch bestätigten Nodes abgeglichen.
Verdeckte oder inhaltlich geänderte Nodes werden daraus entfernt, zurückgekehrte Nodes ergänzt;
die gemeinsame sichtbare Teilmenge bleibt ohne Ausblenden des gesamten Fensters erhalten.
Ein begrenzter Grafikcache verwendet dieselben Node-Grafiken auch bei wechselnden Tooltipmasken.

Aktivierte Stat-Nodes zeigen einen helleren Stein mit dezenter Tönung ihrer Node-Farbe.
Die Erscheinung wird pro sichtbarem Node aus den aktiven und abgedunkelten Originalmotiven des
aktuellen Bildes verglichen. Unbekannt bleibt dunkel; blaues Level-Leuchten allein bedeutet keine
Aktivierung. Masken, Schrift, Außenfassung und Original-Skillbilder bleiben dabei unverändert.

Die blaue Animation levelbarer Nodes wird als Darstellungsvariante erkannt. Jeder Node kann
sein Originalmotiv im aktuellen Grauwertbild oder im aktuellen Rotkanal nachweisen; für beide
gelten dieselben Korrelationsschwellen und jeweils passende Originalreferenzen. Vergrößerte
Leuchtsäume liefern nur Rasterhypothesen, nicht die Größe der gezeichneten Innenflächen.
Begrenzte native Rahmengrößen werden erneut gegen Motiv, Startkontur und ganze Boardmaske geprüft.
Ein Leuchten bestimmt weder den Node-Typ noch einen Fortschrittswert. Verdeckungen, Menüwechsel
und die ursprüngliche Capture-Frist bleiben weiterhin maßgeblich.

### Bestätigte Instanz ohne Kartenbeobachtung (2026-10-21)

In netzwerkbestätigten Dungeons und Eventinstanzen fordert der Kartentracker keine Bilder an,
auch nicht zur ESC-/Weltkartenprüfung. Für die frei platzierbaren HUDs gilt deshalb ausschließlich
die gespeicherte Instanzfreigabe als Kontextfilter. Unbeobachtete Menü-, Weltkarten- und Minimap-
Flags werden nicht als zusätzlicher negativer Nachweis verwendet. Vordergrund, manuelle
Sichtbarkeit und Funktionsschalter bleiben unabhängig. Fortschritt und Timer laufen weiter.

Bei aktiver visueller Kartenbeobachtung gelten die bisherigen überlappenden Kontextfilter
weiter. Auch Übergänge und ein noch unbestätigter aktiver Netzwerkkontext setzen die visuelle
Beobachtung aus. Die unterstützten offenen Abyss-Karten behalten ihre Karten-/HUD-Ausnahme. Eigenständige
Händler-/Daevanionaufnahme und ihre Darstellung werden durch die Kartenruhe nicht gestoppt.
