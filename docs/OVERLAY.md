# Ingame-Overlays

## Overlay-Verwaltung im Editor (04.11.2026)

Die frühere Overlay-Hauptnavigation ist in **Overlay Editor → Static Overlays**
aufgegangen. Dort liegen die neun statischen Familien einschließlich ihrer bisherigen
Einstellungen. Die echten Beispielbilder für Shop, Daevanion-Board und Minimap
stammen aus den am 04.11.2026 gelieferten Spielausschnitten. Pet Tracker und Timer
Strip zeigen Ausschnitte einer echten Aufnahme vom selben Tag; Boss Spawns verwendet
eine als älterer Darstellungsstand gekennzeichnete Aufnahme. Für Leveling,
Benachrichtigungen und Timeline fehlt noch ein echtes Bild. Loot und Abyss
Rank Tracker werden künftig als vom Nutzer konfigurierte
Editor-Vorlagen angelegt; Killfeed und Punkte-Stufenbalken stehen bereits als Module
bereit. Ältere Verweise auf den „Overlay-Tab“ in den folgenden historischen Abschnitten
beziehen sich für statische Familien auf diesen neuen Bereich.

Seit dem 05.11.2026 startet „Place Mode starten“ im Editor dieselbe gemeinsame
Bildschirmbearbeitung wie in Static Overlays. Dort lassen sich auch Editorfenster
verschieben und skalieren. Unter Anzeige & Verhalten aktivierte, nicht leere
Editorfenster erscheinen danach als durchklickbare Live-Overlays; Fensterbedingungen
und globale Vordergrundregel werden angewendet. Start/Stop aktiviert keine zuvor
ausgeschalteten Fenster. Neue Konfigurationen werden dort ausdrücklich freigegeben.
Positionen und Maßstäbe werden getrennt von Modulinhalten gespeichert. Bestehende
native Familien bleiben eigenständig, zusätzliche Hotkeys werden nicht angelegt.


## Overlay Presets und entfernte Alt-HUDs (05.11.2026)

„Overlay Presets“ vereint Fensterverwaltung und Vorlagen. Alle konfigurierten
Editorfenster zeigen eine kleine Vorschau mit Beispieldaten. Anzeige & Verhalten
ist kompakt gruppiert; zusätzliche Bedingungen bleiben pro Fenster einstellbar.

Die ursprünglichen nativen Loot- und Abyss-Rank-Fenster sind aus Laufzeit,
Verwaltung, Platzierung und Hotkeys entfernt. Die Datenquellen, Modulrenderer und
expliziten Audio-/Killfeedoptionen bleiben verfügbar. Alte Layout-/Aktivierungsfelder
werden nur zur Dateikompatibilität gelesen; sie erzeugen keine alten HUDs mehr.
Kina und AP verwenden in den Editormodulen ihre vorhandenen Ingame-Icons.
Details und die Korrektur des zyklischen Ausblendens stehen in OVERLAY_EDITOR_KIT.md.

Die folgenden datierten Abschnitte beschreiben frühere Entwicklungsstände.

## Zusätzlicher Overlay Editor (02.11.2026)

Der separate Tab „Overlay Editor“ übernimmt Oberfläche und Bedienung des
Grindcrest-Kits und bietet 19 frei kombinierbare Module: drei Dropansichten,
Sessiondauer und lokale Uhr, sechs Sessionwerte, sechs getrennte Stundenraten,
das Kina-Tagesziel von 1.000.000 und die neueste Rare-Drop-Meldung. Livewerte und
Beispieldaten sind im Canvas sowie in der App-Vollansicht verfügbar; die Bibliothek
zeigt immer Beispiele. Die neuen Fenster erscheinen noch nicht im Spiel.

Market Kina zeigt seit 10.11.2026 den Netto-Richtwert der Sessionitems aus dem
aktuellen niedrigsten Stückpreisen und verwendet nur für fehlende aktuelle Preise
den 24h-Durchschnitt als Ersatzwert, mit vorhandener Server-/Gebührenkonfiguration.
Preiszeit, Abdeckung und unbekannte
Werte bleiben erkennbar; eingefrorene historische Bewertungen werden nicht geändert.
Dropped Kina zählt ungebundene Zugänge. Sessiondauer einschließlich stiller Phasen
ist die gemeinsame Grundlage der Stundenraten. Die lokale Uhr verwendet HH:mm.

Fensterkonfigurationen und Vorlagen liegen getrennt unter `overlay-editor/overlay.json`
und `overlay-editor/overlay-templates.json` im Soulcrest-Datenordner. Bestehende
Beispiel-Layouts bleiben kompatibel; neue Fenster starten leer. Die bisherige
Overlayverwaltung, Fachfenster, Verknüpfungen und Hotkeys bleiben verfügbar.
Native Anzeigeoptionen und neue globale Hotkeys sind im Editor ausgeblendet.

Soulcrest enthält elf Overlayarten als native, transparente Top-Level-Fenster über dem Spiel. Das Spiel
muss im **randlosen Fenstermodus** laufen; über exklusivem Vollbild kann nicht gezeichnet werden.

## 1. Pet-Fortschritt (kompakt)

- **Nur bei aktueller Meldung** (Nutzerauftrag 2026-10-18): „Pet Progress“ bleibt
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

Eigener Tab statt Karten-Kachel. Nutzerentscheidung 2026-10-25 ersetzt die bisherige Zweiziel-
Wegführung: Alle bestätigten gespawnten Bosse erhalten grüne Verbindungen direkt vom Spieler.
Der erste kommende Boss erhält eine kräftige rote Verbindung direkt vom Spieler; weitere
kommende Bosse bilden ab dort eine blasse gestrichelte rote Kette. Die zusätzliche Anzahl ist
in der Wegführung separat gespeichert (0–23, Standard 1), unabhängig von der Spawnlistenlänge.

Alle Boss-Rush-Marker zeigen Name und Countdown beziehungsweise „Gespawnt“. Countdown null
wartet auf Bestätigung und beweist keinen Spawn. Gespeicherte/unbestätigte Ziele und Wege sind
vollständig verblasst grün, mit ausdrücklichem „Unbestätigt“ und weiterlaufenden bekannten Timern.
Die flüchtigen Ziele werden weder durch Ankunft noch durch manuelle Routen beendet. Fehlende
Koordinaten unterbrechen Kanten ohne Ersatzanker. Ohne passende Spielerposition entfallen nur
Spielerlinien. Dieselbe Projektion gilt für beide Ingame-Karten und die interaktive Appkarte.

Timerupdates verändern weder die stabile Zielliste noch die Kartenaufbereitung. Leaflet
aktualisiert bestehende Tooltips über den bisherigen separaten Timerfeed; das native Overlay
zeichnet aktuelle Counter erst am fertigen Label. Bei sichtbarem Navigationsmarker entfällt der
doppelte Timer am normalen Boss-Kategoriemarker. Verblasste Minimap-Marker bleiben ohne Fläche.

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

Der auswählbare Diagnosebeitrag `overlay-merchant` enthält anonymisierte Händlergründe
(z. B. keine Angebote, Layout nicht bestätigt, Capture-Aussetzer, altes Bild, ungültige Badge-
Flächen oder Darstellungsfehler), Workerzustand, Platzierungs-/Vordergrundsperren, Zähler und
höchstens 24 Zustandswechsel mit UTC-Zeit. `merchant` wird als benötigte Quelle mitgeprüft;
Angebotszahl und zugeordnete Pet-Zeilen bleiben getrennt. Keine Angebote, Paketdaten,
Kennungen, Ausnahmetexte oder Bilder werden protokolliert.

Eine frische Beobachtung des nativen Fensters beweist noch keine frische Händleranalyse.
`Healthy` setzt eine sichtbare, ungesperrte Anzeige mit laufendem Worker und aktuell gültigem
Layout-/Analysebild voraus. Eine verborgene Anzeige wartet; fehlende Werte bleiben unbekannt,
veraltete Analyse und aktuelle Fehler werden gesondert ausgewiesen. Das Bildalter wird beim
Lesen aus dem monotonen Originalzeitstempel neu berechnet, ohne Frische zu verlängern.
Ein alter Fenstertransfer allein ist kein Fehler: unveränderte Badges dürfen mit neuen
bestätigten Bildern weiter sichtbar bleiben. Ein fehlgeschlagener nativer Fenstertransfer
wird als Fehler behandelt, nicht als sichtbare Darstellung verbucht.

Die Funktionsdiagnose vom 2026-11-09 enthielt diese vorhandenen Händlergründe vorübergehend
nicht mehr. Der Nachfix vom 2026-11-10 stellt sie strukturiert wieder bereit und ändert
weder Locator-Schwellen noch Capture-, Fokus-, Kaufdialog- oder Click-through-Verhalten.

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

Nutzerentscheidung vom 2026-11-12: Der ausgewählte Entwurf 1 „Native Prägung“ ersetzt die
äußere Fassung vom 21.10. Zahlen und Skillbilder bleiben vollständig in der runden Nutzermaske
(etwa 65% des Node-Durchmessers); originaler Aktivierungsring, quadratischer Rahmen und farbige
Ecken bleiben frei. Stat-Nodes zeigen das lokale originale Statmaterial mit kurzem Kürzel,
großem exaktem Wert und einem kleinen Dekopunkt. Doppelboni stehen gleichzeitig in zwei
getrennten Zeilen mit jeweils eigenem Kürzel und Wert. Palatino Linotype Bold mit dunkler
Kontur wird anhand der tatsächlichen Glyphgrenzen passend skaliert. Skillbilder tragen eine
kleine dunkle +1-Plakette. Alle Farben erhalten ihre ausdrücklichen Bonusbezeichnungen.

An/Aus-Schalter und Stat-Leiste verwenden dieselben eingebetteten Schieferbuttons und
Checkboxen. Maße, Platzierung, Durchlässigkeit der Zwischenräume, NoActivate-Bedienung und
getrennte Anzeigeschalter bleiben erhalten. Die umlaufende Lichtspur folgt symmetrisch dem
nativen Außenrahmen ohne verlängerten Sockel; native Rahmen und Nachbarn sind ausgeschlossen.
Nicht bestätigte Nodes bleiben frei. Die konservative stabile Fenster- und Prüfhülle umfasst
weiterhin das vollständige Board, unabhängig von Verdeckungen. Kein Laufzeitzugriff auf
Spieldateien oder einen Datenkatalog außerhalb des Apppakets.

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

Aktivierte Stat-Nodes zeigen ein helleres Statmaterial mit dezenter Tönung ihrer Node-Farbe.
Die Erscheinung wird pro sichtbarem Node aus den aktiven und abgedunkelten Originalmotiven des
aktuellen Bildes verglichen. Unbekannt bleibt dunkel; blaues Level-Leuchten allein bedeutet keine
Aktivierung. Masken, Schrift, Rahmenkontur und Original-Skillbilder bleiben dabei unverändert.

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

## Eigenes Loot-Overlay (2026-10-26)

„Loot-Overlay“ ist unabhängig vom bestehenden Pet-HUD schaltbar, platzierbar und
skalierbar. Es verwendet den gemeinsamen Overlaystil, Vordergrundfilter und die
Alt-Interaktion. Monsterloot-Aufzeichnung und Overlayanzeige haben getrennte,
gespeicherte Schalter im Loot-Tracker. Der gemeinsame Netzwerk-Schalter bleibt
maßgeblich; die Anzeige startet keine eigene Erfassung.

Oben zeigt ein blauer Balken die absoluten täglichen ungebundenen Monster-Kina
des sicher gebundenen Charakters bis 1.000.000. Jeder Charakter behält seinen
eigenen gespeicherten Tagesstand. Ohne frischen Beleg trägt er den Hinweis
„Gespeicherter Tagesstand“; ohne bekannten Stand bleibt er unbekannt. Ein lokaler
Datumswechsel setzt ihn nicht zurück. Neue Netzwerkstände ersetzen gespeicherte
Werte einschließlich niedrigerer Stände nach einem serverseitigen Reset.

Im Tagesbalken wächst der Beitrag der laufenden Session proportional als grüner
Abschnitt mit einer von links nach rechts pulsierenden Animation. Der ältere
Tagesanteil bleibt blau. Darunter stehen die grünen Session-Kina und alle anderen
erfassten Drops mit Mengen; es gibt keinen separaten grünen Fortschrittsbalken.
Ein frisch bestätigter Übergang zum Limit desselben Charakters löst eine
kurze Erfolgsanimation aus; der Tagesbalken bleibt anschließend gold. Ein bereits
voller Cache oder Charakterwechsel erzeugt keine nachträgliche Erfolgsanimation.
Ausgeblendete Fenster stoppen ihren Animationstimer. Vorschau und PNG-Abnahme
verwenden denselben Produktionsrenderer wie das tatsächliche Overlay.

Kina und Droplisten verwenden die über Item-ID bzw. Petnummer exakt zugeordneten
lokalen Grafiken. Das gilt für das native HUD und die Sessionbeute im Loot-Tracker.
Kristalle verwenden die ausdrückliche native Typzuordnung. Die eingebauten
Thumbnails werden begrenzt zwischengespeichert; Zeichnen und Freigabe teilen einen
Cachebesitzer. Fehlende oder beschädigte Grafiken beeinflussen keine Zähler.

## Boss Rush: Spawn-Dauer und Sichtfenster (2026-10-26)

Boss-Rush-Marker gespawnter Bosse zeigen zusätzlich die aufwärtszählende Dauer seit dem
serverseitigen Spawnzeitpunkt (MM:SS). Standardmäßig sind sie nur bei bekanntem Alter von
0 bis einschließlich 60 Sekunden sichtbar. „Gespawnte Bosse älter als 1 Min. anzeigen“
im Map-Tab hebt nur die obere Altersgrenze auf; die Wahl bleibt gespeichert und gilt
für Worldmap, Minimap und Appkarte. Unbekannte oder zukünftige Spawnzeiten erzeugen
keinen aktiven Boss-Rush-Marker. Gespeicherte Stände bleiben verblasst grün/unbestätigt.

Sekundenticks ändern innerhalb des Fensters nur den Counter. Erst Grenzübertritt oder
Schalterwechsel ändern die Navigationsszene. Leaflet kann die Altersgrenze auch ohne
neuen Zielcallback anhand der monoton fortgeschriebenen Serverdauer anwenden. Normale
Katalogsymbole, Quellstatus und Alerts werden durch diese Darstellungsregel nicht gelöscht.

## Besondere Lootdrops (2026-10-26)

Unique, Epic, Mythic und Special erhalten in der nativen Beuteliste und dem
Loot-Tab fließende farbige Lichtbänder und dezente Partikel; Unique verwendet
die goldene Itemkarten-Anmutung. Text, Originalicon und Menge bleiben darüber
lesbar. Die gespeicherte Animationsoption und reduzierte Browserbewegung stoppen
die Effekte. Unsichtbare native Fenster unterhalten keine Animationsschleife.

Neu persistierte eigene Drops erzeugen vier Sekunden lange Fundhinweise. Bis zu
drei gleichzeitige Hinweise sind begrenzt sichtbar; Verbergen verwirft sie, ohne
späteren Rückstau. Die Laufzeit ist monoton. Der deterministisch erzeugte lokale
Dropklang besitzt einen eigenen Windows-Audiokanal und ersetzt keine Boss-/
Eventtöne. Fundhinweise, Ton, Lautstärke und Animation sind getrennt einstellbar.


Nutzerergänzung 26.10.: Alle bekannten Itemqualitäten färben auch den Namen in
der Dropliste, unabhängig von einem besonderen Hintergrund. Common weiß, Rare
grün, Legend blau und Unique gelb sind durch die vier Veredelungssteinbeispiele
bestätigt. Razor und nativer Renderer teilen dieselbe Namenpalette; unbekannte
Itemqualität bleibt neutral. Raritätsalarme und Zählung werden dadurch nicht erweitert.

Loot-Overlay-Ergänzung 26.10.: Hintergrundtransparenz skaliert ausschließlich die
vorhandene Theme-Fläche (0 % ursprüngliche Stärke, 100 % ohne Flächenfüllung).
Text, Icons, Rahmen und Fortschritt behalten ihre Deckkraft. Keine zweite Bitmap
pro Animationsframe. Die gespeicherte Option „Bei Sessionstart einblenden“ ist
zunächst aus; Aktivierung alleine blendet nichts ein. Erst ein neuer dauerhaft
verbuchter PvE-Sessionstart setzt den eigenen Overlay-Schalter. Ein rein flüchtiger
Alert-Vorbereitungsaufruf hält den ersten Rare-Drop bis zum UI-Callback sichtbar,
respektiert aber zuletzt bestätigte Unterdrückung und beendet sich bei Dispose.

Rechts neben Session-Kina steht deren Live-Stundenrate. Common Gear ist eine reine
Overlay-Projektion anhand exakter nativer Equip-Klassifikation und Common-Grade,
keine Gruppierung weißer Materialien. Unbekannte Itemtypen bleiben einzelne Zeilen;
Sessionjournal und Loot-Tab behalten ihre ursprünglichen Items.

Das bisherige „Loot & Pet-Fortschritt“ heißt in Overlayauswahl und nativem HUD
„Pet-Fortschritt“ / „Pet progress“; das separate Loot-Overlay bleibt eigenständig.

Unique, Epic, Mythic und Special besitzen unabhängig gespeicherte Alertprofile:
Fundhinweis aktivieren und Signalton ausschalten, Standardklang oder lokale MP3
wählen. Der gemeinsame Fundhinweis-/Tonschalter und die Lautstärke bleiben die
übergeordneten Einstellungen. Die MP3-Bibliothek und Dateiauswahl werden mit den
bestehenden Alerts geteilt; Hörprobe und echte Dropwiedergabe verwenden ausschließlich
den Drop-Audiokanal. Fehlende oder unlesbare MP3-Dateien melden den Rückfall auf den
Standardklang. Vorschau erzeugt keine Beute, Session oder gespeicherte Fundmeldung.

Der Standard-Dropklang ist ein eigener, synthetisierter Item-Pickup-„Plop“
(`scripts/sound/generate_loot_drop.py`, Nutzerauftrag 2026-10-28), sehr ähnlich dem
zuvor bereitgestellten Effekt, aber ohne fremde Aufnahme. Die 0,21 Sekunden lange
PCM16-WAV mit 48 kHz Stereo ist als Appressource eingebettet. Hörprobe und neue Drops nutzen dieselbe Ressource;
Lautstärke, Stummschaltung und eigene MP3-Auswahl bleiben einstellbar.

## Abyss Rank Tracker (2026-10-26)

Das frühere Punkte-Stufen-Overlay heißt in Auswahl und HUD „Abyss Rank Tracker“.
Die Fläche ohne Killfeed beträgt 480×178; gespeicherter Registrykey
abyss-rank und Layout-/Aktivierungseinstellungen bleiben unverändert.

Zusätzlich zum absoluten Rangstand und Schwellenfortschritt zeigt es „Rank Points
this Session“ und „Rank Points per hour“: beide ausdrücklich netto, Gewinne minus
Verluste. Die großen Zahlen übernehmen den Session-Kina-Stil des Loot-Overlays,
positive Werte grün, negative rot; kein zusätzlicher Popup-Painter. Unbekannte
Kanäle bleiben „—“, bei null Aufenthaltsdauer bleibt nur die Rate unbekannt.
Signierte RP-Raten benutzen eigene Berechnung/Formatierung, da Kina-Raten negative
Werte absichtlich abweisen. Nur eine passende laufende eigene Abyss-Session mit
aktueller Profil-/Welt-/Ownbindung und verfügbarer Erfassung liefert diese Metriken.
Die Punkte-Stufe bleibt von einem tatsächlichen Klassenrangplatz getrennt.

Gesamt-Rangpunkte im Balken verwenden 18 Pixel, verbleibende Punkte darüber
13,5 Pixel, jeweils fett und weiß mit dunkler umlaufender Kontur. So bleiben die
Werte auf dem goldenen Füllbereich und im leeren Balken lesbar. Große Zahlen
werden innerhalb ihrer vorhandenen Grenzen passend verkleinert; Breite,
Rangiconpositionen und Sessionmetriken bleiben erhalten.


Seit 27.10.2026 enthält der Rang-Snapshot einen ausdrücklichen
WorldContextVisible-Status. Das echte Fenster zeigt sich ausschließlich bei
bestätigtem Abyss-Kontext mit passender aktueller eigener Profil-/Generationbindung.
Laden, unbekannter/veralteter Kontext, Kartenwechsel nach außerhalb und fehlende
Erfassung blenden es aus. Ein gecachter Rangstand reicht nicht. Eine laufende
Session ist für diese Weltentscheidung nicht erforderlich. Position, Skalierung
und gespeicherte Einstellungen bleiben erhalten; ein ausgeblendetes Fenster
beendet Drag/Interaktion und erfordert vor erneutem Alt-Entsperren ein Loslassen.

Seit 27.10.2026 entwickeln sich die Originalicons bei bestätigten Stufenwechseln
im selben eigenen Charakterkontext zusammenhängend weiter beziehungsweise zurück.
Der Rangcontroller bewegt nur ein Symbol zur Balkenmitte und zurück; rechts erfolgt
der Austausch unsichtbar. Mehrstufensprünge durchlaufen alle benachbarten Originale
in einer gemeinsamen Reise. Neueste Ziele ersetzen die Restfolge am nächsten
eindeutigen Icon. Erstbestätigung, gespeicherte Werte und gleiche Stufe bleiben
statisch; Kontextverlust und Ausblenden brechen ohne spätere Nachholung ab.

„Ranganimation“ in den eigenen Overlayeinstellungen bietet reduzierte Bewegung
(lokale Iconentwicklung ohne Reise/Skalierung/Licht), einen unabhängigen orchestralen
Aufstiegsklang, Lautstärke und Klangvorschau. Ein Klang pro Aufstiegsfolge, ruhiges
Sustain für große Sprünge; Abstieg bleibt stumm. Neue Zielstufen beenden den bisherigen
Klang, dessen Landungszeit sonst veraltet wäre. Charakter-/Trackingwechsel stoppen
auch einen noch ausklingenden Akkord. Keine Abhängigkeit von Drop-/Boss-Soundoptionen.
Technische Übergabe und reproduzierbare synthetische Vorschauen: ABYSS_RANK_MOTION.md.
Nach Nutzerfeedback dauert ein Aufstieg 2,64 s, ein Abstieg 2,78 s; der Morph
in der Mitte erhält 1,4 beziehungsweise 1,5 s. Reduzierte Bewegung dauert 720 ms.
Der Produktionsklang ist unabhängig von gespeicherter Nutzerlautstärke um insgesamt 11 dB
abgesenkt; Tonakzente folgen weiterhin Morphbeginn und Landung der jeweiligen Route.
Die aktuelle Klangfarbe bleibt nach weiterem Nutzerfeedback zurückhaltend: weiche
Harfe und Kammerstreicher, leises Horn im Hintergrund; Chor, Schlagwerk und heller
Metallglanz entfallen. Die Ankunft klingt nach einer Sekunde aus. Der feste Master
und gespeicherte Lautstärkewerte bleiben erhalten.

## Killfeed: letzte zehn und Gegnericons (27.10.2026)

Die letzte Nutzerauswahl gilt: maximal zehn gewählte eigene Kampfergebnisse
insgesamt, keine zeitliche Ausblendung. Die bisherigen Kills-/Tode-/Assist-Schalter
bleiben separat; Abwahl entfernt Einträge ohne Wiederherstellung. Abschalten,
ein anderer Charakter/Server und Empfangsende leeren den rein flüchtigen Feed.
Ladeübergänge desselben Charakters behalten die letzten Einträge im RAM.

Jede Killzeile zeichnet direkt vor dem Gegnernamen ein originales Klassenicon
und dahinter sein tatsächliches Rangicon, jeweils 22×22 und vertikal zentriert.
Der explizite Gegnergrad 1–19 wählt dieselbe `rank-01`–`rank-19`-Originalgrafik;
es erfolgt keine Berechnung aus Punkten oder aus dem eigenen Overlayrang.
Unbekannte oder unlesbare Icons belegen keinen Ersatzplatz. Nicht zugeordnetes
RP-Delta bleibt vollständig ausgeblendet; der Name nutzt dann die zusätzliche Breite.

Der vorhandene Renderer bleibt 480 Pixel breit, bestätigte Feedzeilen 27 Pixel hoch;
mit zehn solchen Zeilen ist das HUD 487 Pixel hoch. Geschätzte Assistzeilen erhalten
die unten beschriebene zusätzliche Beschriftungszeile. Klassen- und Rangmetadaten werden
beim Ereignis eingefroren und ändern keine historische Zeile. Original-Classicons
sind lokal gebündelt; Netzwerkpositionen und neue Laufzeitquellen sind ausgeschlossen.

## Eigene Tode und Punktespalten (27.10.2026)

Auch die eigene Todeszeile zeigt bei passender bestätigter Spielerzuordnung die
Klasse und den tatsächlichen Rang des Angreifers vor seinem Namen. Unbekannte
Metadaten bleiben ohne Ersatzicon. Der Rang ist weiter der explizite native Grad,
keine aus eigenen Punkten oder gegnerischer Klasse geschätzte Stufe.

Die rechte Spalte hält für Kill/Assist AP und RP getrennt, für Tod nur die RP-Kosten.
Beträge benötigen eine direkte Ergebniszuordnung; — bedeutet unbekannt, nicht null
Punkte. Die aktuell qualifizierten Wallet-/Gesamtstandsquellen enthalten diesen
Kampfbezug noch nicht. Sie aktualisieren weiterhin die unabhängigen Sessionwerte.
Die zehn gehaltenen Einträge, Kategorien und Kontextgrenzen bleiben bestehen.

## Geschätzte eigene Assists (Nutzerentscheidung 29.10.2026)

Der Nutzer erlaubt ausdrücklich die eigene Schadensbeteiligung als sichtbar geschätzten
Assist. Ausschließlich ein vollständig validierter eigener positiver Schadenseffekt an
einem unabhängig bestätigten Spieler setzt einen kurzlebigen RAM-Beleg. Nur positiv
belegte Schadenseffektformen sind freigegeben; unbekannte Varianten, Heilung und
Nullschaden zählen nicht. Ein vollständiger Tod desselben bestätigten Spielerlebens
durch einen anderen expliziten PC-Caster muss innerhalb von 30 Sekunden folgen.
Aktuelle eigene Identität, Lebenszyklus-, Transport- und Capturegrenzen gelten weiter;
ein eigener Todesstoß zählt allein als Kill. Die Frist ist eine Heuristik, kein Server-Credit.

Die blaue Assistzeile zeigt „Assist“ und darunter ausdrücklich „geschätzt“ beziehungsweise
„estimated“. Sie ist 44 Pixel hoch; normale Kill-/Todeszeilen bleiben 27 Pixel hoch.
Alle gewählten Arten teilen weiterhin zehn Plätze ohne Zeitablauf. Der bestehende
Assist-Schalter ist voreingestellt an. Klasse-/Rangicons stammen aus derselben eingefrorenen
bestätigten Opferzuordnung; unbekannte Kategorien bleiben unbekannt. AP/RP werden nicht aus
Schaden oder nahen Punktedeltas ergänzt; ohne direkten Ergebnisbezug steht weiterhin —.

Die unabhängige Sessionaufzeichnung speichert anonyme Assist-Ereignisse mit `IsEstimated`.
Zusammenfassung und Statistik führen Assists getrennt von Kills/Toden und nennen den
geschätzten Anteil. Die Timeline hat eine eigene Assistspur; Route und zeitliche Ereignisliste
zeigen „Assist · geschätzt“. Ein eigener Assistfilter ist standardmäßig sichtbar; der blaue
Plusmarker mit gestricheltem Rand benötigt eine frische eigene Position. Ohne diese bleibt
allein der zeitliche Eintrag. Marker erzeugen keine Laufwegpunkte oder Verbindungslinien.
Replay und Ansichtsfilter erhalten die Kennzeichnung. Keine Gegnernamen oder nativen
Kennungen werden in Sessions gespeichert; keine Schadensbeträge, DPS- oder Gruppenanalyse.
Der direkte Serververtrag für einen als bestätigt bezeichneten Assist bleibt offen.

## Loot-Reihenfolge und rangabhängiger Killfeed-Hintergrund (27.10.2026)

Die neuere Nutzerentscheidung erweitert die bisherige Common-Gear-Projektion:
Common/weiß, Rare/grün und Legend/blau werden im Overlay jeweils zu einer eigenen
Ausrüstungszeile mit Gesamtmenge zusammengefasst. Nur exakte Katalog-Equip-Items
dieser Stufen zählen dazu. Materialien, unbekannte Items und Unique+-Ausrüstung
bleiben einzeln. Deutsche Gruppen heißen „Gewöhnliche“, „Seltene“ und „Legendäre
Ausrüstung“, englische Common/Rare/Legend Gear. Originalitems im Journal und
Loot-Tab bleiben erhalten; auch dort ist die Darstellung jetzt sortiert.

Reihenfolge: Special, Mythic, Epic, Unique, Legend, Rare, Common, unbekannt;
innerhalb derselben Stufe Menge absteigend, bei Gleichstand Name und Item-ID.
Die Sortierung erfolgt nach der Overlay-Gruppierung und verändert keine
Beutebuchung oder Drop-Alerts.

Jede Killfeed-Zeile nutzt denselben Lichtband-/Partikeleffekt wie Unique+-Loot.
Der explizite Gegnergrad steuert die Palette: Grad 19–10 Gold, 9–5 Violett,
4–2 Rosé, 1 Cyan. Das sind Gestaltungsfarben, keine Item- oder Rangklassifikation.
Farbintensität und Bewegung nehmen für jeden einzelnen Grad Richtung 1 zu.
Unbekannter Grad bleibt ohne Rang-Effekt. Der eigene Punktestand beeinflusst
diesen Hintergrund nicht.

Grüner/roter Seitenstreifen, Punkt und Kill-/Todeslabel bleiben vor einer dunklen
Ergebnisfläche sichtbar. Original-Class-/Rangicons erhalten bei erfolgreich
geladenem Bild eine dunkle Unterlage; fehlende Assets erzeugen keine Platzhalter.
Die bereits vorhandenen zehn Plätze und Kategorie-Schalter bleiben erhalten.
Animation läuft nur bei tatsächlich sichtbaren ausgewählten Rangzeilen; reduzierte
Bewegung hält den Effekt statisch. Verbergen, Handleverlust und Dispose stoppen
den separaten 33-ms-Feedtimer. Der Standard-Loot-Effekt bleibt unverändert.

## Rangpunkte als Quotient (27.10.2026)

Auf Nutzerwunsch ersetzt `Gesamtstand/nächste absolute Punkte-Schwelle` den
Text „Rangpunkte gesamt“ im Balken, zum Beispiel `999572/1000000`. Beide Zahlen
stehen ohne Tausendertrennzeichen. Auch während einer Ranganimation bestimmt
der tatsächlich angezeigte Gesamtstand die nächste Schwelle und die Restpunkte.
Bei höchster Punkte-Stufe bleibt der Nenner unbekannt (`Gesamtstand/—`);
fehlende Rangpunkte behalten den bisherigen Unbekannt-Hinweis.

## Gemeinsames Loot-Overlay mit Lifeskill (27.10.2026)

Das Live-HUD verwendet ausschließlich die gemischte aktuelle gemeinsame Session.
Die historische Auswahl Gemischt/PvE/Lifeskill/Abyss beeinflusst es nicht. Der
produktive Host verwendet `UnifiedLootOverlaySource`; alte einzelne Recorder
werden dort nicht mehr parallel gestartet. Neue bestätigte Beute und erfolgreiche
oder fehlgeschlagene Sammelversuche starten außerhalb des Abyss denselben Timer.

Alle Varianten sind 480 Pixel breit. Schrift, konturierte Zahlen, Trennlinien und
Abstände folgen dem Abyss Rank Tracker. Nur belegte Aktivitätsblöcke erscheinen:
PvE mit Kina und Kina/h, Abyss mit AP-Cap und AP-Rate sowie Kina, Lifeskill mit
erfolgreichen Ernten und Ernten/h, ergänzend Fehlversuchen und Itemmenge. Alle
Raten verwenden die volle gemeinsame Sessiondauer. Eine reine Lifeskill-Session
enthält keine leeren Kina-/AP-Blöcke; unbekannte Werte bleiben ausdrücklich unbekannt.

Die 27 Pixel hohen Itemzeilen enthalten Originalicon, Seltenheitsfarbe, Namen und
rechtsbündige Gesamtmenge. Materialien bleiben einzeln. Common-, Rare- und
Legend-Ausrüstung werden weiterhin getrennt gruppiert; erst danach wird nach
Seltenheit und Menge sortiert. Das Journal behält sämtliche Originalitems.

Alle Seltenheiten nutzen denselben Lichtband-/Partikelrenderer wie der Killfeed:
Common statisch, Rare/Legend dezente Bänder, Unique bis Special zunehmend kräftige
Farbe und Bewegung. Dunkle Iconunterlagen und kontrastreiche Mengen bleiben davor.
Fundhinweise und Töne sind weiterhin ausschließlich Unique und höher vorbehalten.
Abgeschaltete Animation oder reduzierte Windows-Bewegung hält die Effekte statisch;
ausgeblendete Fenster stoppen den Timer vollständig. Ohne Animation aktualisieren
sichtbare Fenster nur Uhr und Raten im Sekundentakt.

Automatisches Einblenden und Fundhinweise hängen an derselben erfolgreichen
Sessionbuchung: erst Journalcommit, dann SessionStarted/Einblenden, danach die
DropAccepted-Empfänger. Damit bleibt auch der erste Lifeskill-Fund erhalten.
Wiederhergestellte Sessions lösen weder neue Fundhinweise noch Sounds aus.
Position, Skalierung, Transparenz, Sperre und Alt-Bedienung bleiben gespeichert.

## Killfeed nach Wiederbelebung und Teleportation (27.10.2026)

Ein vorübergehend fehlendes Own und ein frischer Welteintritt desselben Charakters
auf demselben Server leeren die bereits bestätigten letzten zehn Einträge nicht.
Die ursprüngliche Ereignisgeneration, Sequenz, Klasse, der tatsächliche Rang und
eventuell explizit zugeordnete Beträge bleiben unverändert eingefroren.

Der Feed veröffentlicht seine aktuelle Anzeigebindung atomar mit diesen historischen
Zeilen. Nur wenn sie zum bestätigten aktuellen Abyss-/Profilkontext passt, zeigt
der Rank Tracker die Historie wieder. Laden, unbekannter Kontext und außerhalb
von Abyss bleiben wie bisher ausgeblendet. Ein neues Entity oder eine neue eigene
Generation desselben Charakters ist allein kein Grund, die Historie zu löschen.

Neue Kampfergebnisse benötigen weiterhin die aktuelle eigene Referenz, Generation,
Sequenz und gültige Faktzeit. Die Historie überträgt keine Netzwerkqualifikation,
keine Actor-/Lebenszustände, keine Einkommensbasis und keine Transportkontinuität.
Charakter-/Serverwechsel, explizites Empfangsende, Abschalten und Dispose leeren
weiterhin den Feed; Kategorieabwahl entfernt ihre Zeilen. Kein Speichern auf Disk.

## Loot-Overlay nur bei laufender Session (27.10.2026)

Unter Overlay → Loot-Overlay begrenzt die neue Option „Nur bei laufender Session
anzeigen“ die Sichtbarkeit auf die aktuelle laufende Session. Ohne Session oder
nach deren Ende wird das Fenster ausgeblendet und sein Timer gestoppt. Die Option
ist standardmäßig aus; sie ändert weder die Aufzeichnung noch den gespeicherten
Ein-/Aus-Schalter, die Position oder andere Darstellungseinstellungen.

„Bei Sessionstart einblenden“ bleibt davon unabhängig: Diese vorhandene Option
kann das ausgeschaltete Overlay bei einer neuen bestätigten Session einschalten.
Beide Optionen zusammen zeigen es automatisch während der Session und blenden es
nach deren Ende wieder aus. Vordergrund- und andere Sichtbarkeitsregeln gelten
weiterhin für alle Sessionarten, einschließlich reiner Lifeskill-Sessions.

Der committed Sessionstand wird vor dem Sessionstart-Callback veröffentlicht.
Die Fundhinweisfreigabe liest aktuelle Optionen und Sessionstatus gemeinsam unter
dem bestehenden Präsentations-Gate. So bleibt auch der erste seltene Drop beim
Start einer zuvor ausgeblendeten Session erhalten; alte UI-Callbacks können ihn
nicht löschen. Wiederherstellung erzeugt weiterhin keine neuen Fundhinweise.

## Achievement Tracker (07.11.2026)

Das native Achievement-Overlay verwendet das Theme und die Bedienung des Pet
Progress Trackers, mit eigener Position und Skalierung. Es ist standardmäßig
aktiviert und startet rechts oben mit 24 Pixel Rand und 360 logischen Pixeln
Breite. Einstellungen, statische Overlays und Platzierungsmodus führen es als
„Achievement Tracker“. Globale Sichtbarkeit und Aufnahmeausschluss gelten mit.

Karten zeigen Titel, bekannte aktuelle Kettenstufe, Originalaufgabe,
Fortschrittsbalken und Belohnungsicons mit Mengen. Bei unbekanntem Ziel bleiben
Balken und gemeldeter Wert neutral. Nur bestätigte Ziele erhalten Prozente;
Rohänderungen allein behaupten keinen Abschluss. Favoriten stehen zuerst, offene
Abschlüsse danach und kurzlebige Änderungen zuletzt. Pro Familie gibt es eine
Karte. Es gelten keine Pet-Soul-, Max-Level- oder Fünf-Karten-Regeln.

Nicht favorisierte Karten bleiben standardmäßig 20 Sekunden nach der letzten
echten Änderung sichtbar. Die Meldungsdauer ist in den Einstellungen des
Achievement Trackers zwischen 5 und 300 Sekunden einstellbar und gilt auch für
offene Abschlüsse. Fortschritt und offene Belohnungen bleiben gespeichert.
Favoriten bleiben dauerhaft und sind durch einen kleinen goldenen Stern sowie
eine dezente Akzentkante erkennbar. Ein neuer bestätigter Abschluss animiert im
Pet-Stil; bei Favoriten höchstens 60 Sekunden, sonst höchstens die eingestellte
Meldungsdauer. Favorisierte offene Abschlüsse bleiben anschließend ruhig sichtbar.
Ein Claim beendet die Animation sofort: ohne Favorit verschwindet die Karte,
mit Favorit folgt die nächste noch nicht abgeholte explizite Kettenstufe.
Bereits beim Login offene Abschlüsse werden ohne Animation angezeigt.

Alt entsperrt die Interaktion; Schloss und bestehende Overlay-Hotkey-Auswahl sind
angebunden. Alt und Mausrad scrollen über weitere Karten, wenn die Liste 80 Prozent
der Monitor-Arbeitsfläche erreicht. Position und Eckenskalierung sind unabhängig
vom Pet Tracker gespeichert. Timer laufen auch bei verdecktem Overlay weiter.
Ohne Favoriten oder noch laufende Meldungen ist das Fenster ganz
ausgeblendet. Es gibt keine zusätzlichen Sounds oder Spielaktionen.

### Fortschrittsketten und automatische Auswahl (08.11.2026)

Normale Erfolge erscheinen automatisch bei Fortschritt. Battle-Pass-, Saison-,
Event- und weitere Sonderaufgaben erscheinen nur als ausdrücklich gesetzte
Favoriten. Ihre Daten und Bereiche im Achievement-Tab bleiben verfügbar.

Bei bekannten Zielen folgt die Hauptkarte dem aktuellen Fortschritt: Sie zeigt
das nächste noch nicht erreichte Ziel, bei vollständiger Kette die letzte Stufe.
Ältere offene Belohnungen halten die Karte nicht mehr auf einer früheren Stufe.
Eine Abschlussanimation legt während ihrer Anzeigedauer einen Goldpuls über die
aktuelle Karte; ein Haken beim Zähler signalisiert den erreichten Meilenstein.
Titel, Aufgabe, Balken, Werte und Rewards bleiben dabei live auf der aktuellen
Stufe. Frühere offene Belohnungen bleiben separat erhalten.

Claims beenden die Animation sofort. Ohne Favorit verschwindet die Karte; mit
Favorit bleibt die aktuelle Fortschrittsstufe sichtbar. Unbekannte Zielketten
behalten ihre neutrale Anzeige und die belegte Belohnungszuordnung. Gleichlautende
Aufgaben verschiedener Spielbereiche werden nicht zu einer Kette zusammengelegt.
Eine Verlängerung der Meldungsdauer spielt bereits ausgeblendete Karten nicht
erneut ab. Erst neuer Fortschritt oder ein neuer Abschluss startet ihren Timer neu.

### Titelziele im Achievement Tracker (08.11.2026)

Titelkarten im Combat Score Calculator besitzen einen eigenen Favoritenstern.
Grau bedeutet inaktiv, Gelb bedeutet aktiv. Die Markierung wird je Charakter
gespeichert und bleibt von direkt favorisierten Achievement-Reihen unabhängig.
Ein Linksklick auf die Karte zeigt rechts den Titel und seine Freischaltbedingungen.
Die verknüpfte Achievement-Karte öffnet genau die erforderliche Stufe im
Achievement-Tab; dessen Titelbelohnung führt zurück zur Titelansicht.

Ein Titel-Favorit verfolgt die belegte Achievement-Reihe anhand des aktuell
erkannten eigenen Fortschritts bis zur letzten abgeholten Stufe. Mehrere Pins
derselben Familie erzeugen keine doppelten Fortschrittskarten. Der Titelname
erscheint als Ziel auf der Overlay-Karte. Ohne beobachtete passende Reihe bleibt
das Titelziel mit unbekanntem Fortschritt sichtbar. Ein bestätigter eigener
Titelbesitz kann einen solchen ungeklärten Platzhalter beenden. Eine abgeholte
Achievement-Belohnung beweist für sich allein keinen Titelbesitz.

### Genus Insight (09.11.2026)

Seit dem Nutzerauftrag vom 12.11.2026 gibt es zusätzlich die freigegebene schmale
Seitenleiste als eigene Familie **Overlay Editor → Static Overlays → Genus Insight**.
Sie ist bei neuen und bestehenden Einstellungen ohne bisherige Genus-Präferenz
standardmäßig aktiviert. Eine gespeicherte Abschaltung bleibt erhalten.
Die 340×510-Pixel-Ansicht zeigt neun nummerierte Slots mit Stat und Wert oben,
Wertebereich und Rollmarker darunter sowie echten Wunschstat-Sternen und exakten
MAX-Rolls. Sie verwendet die gemeinsamen Overlay-Themes und die Overlay-Sprache.

Das Schloss oder Alt gibt das Fenster zum Verschieben und proportionalen Skalieren
frei; auch der gemeinsame Place Mode unterstützt es. Position und Skalierung werden
nach der Geste gespeichert. Startposition links neben der nativen Statliste,
Skalierung 60–200 Prozent. Standardmäßig erlauben die Kontextregeln das Genus-Menü;
die eigenständige Prüfung auf frisches Menü, Preset und eigene Charakterbindung
bleibt zwingend. Menüschluss oder Quellenverlust blendet das Fenster aus.

Die native Familie teilt die vorhandene Genus-Beobachtung. Konstruktion und
Selbsttest starten keine Aufnahme; erst der reguläre Fensterhost aktiviert die
Beobachtungslease. Ausschalten, Platzierung, Unterdrückung und Beenden geben sie
wieder frei. Die Diagnose `overlay-genus-insight` liest nur vorhandene anonyme
Quell-/Menü- und Fensterzustände und meldet versteckte oder fehlende Quellen nicht
als gesunde Liveanzeige. Ein bestehendes frei konfiguriertes Genus-Widget bleibt
unabhängig erhalten.

Korrektur vom 12.11.2026: Die bereits global akzeptierte frische Petliste versorgt
Genus zusätzlich über eine eigene flüchtige RAM-Quelle. Sie darf vor der vollen
Charakterbindung oder auf einer anderen eigenen Spielverbindung eintreffen und
geht dadurch nicht mehr an der engeren Combat-Zuordnung verloren. Eine aktuelle
eigene Charakterbindung sowie ein frisch bestätigtes Genus-Menü mit Preset bleiben
separate Pflichtbedingungen. Das aktive Preset wird nicht aus der gemeinsamen
Liste auf einen Charakter übertragen. Quellenlücken, Stop und ungültige Änderungen
entziehen die Liveautorität; gespeicherte Rolls bleiben ausschließlich historisch.

Das Widget „Genus Insight“ zeigt neun Rolls der im Spiel geöffneten Kategorie
und des dort betrachteten Presets. Die Auswahl im Combat Score Calculator
verändert das Overlay nicht. Links stehen Wertebereich und Rollmarker, daneben
Stat und aktueller Wert in der Seltenheitsfarbe. Sterne markieren die im
Genus-Tab je Kategorie und Slot gespeicherten Wunschstats. Ein bestätigter
exakter Höchstwert zeigt „MAX ROLL!“.

Nutzerauftrag vom 12.11.2026: Im Genus-Insight-Tab steht zusätzlich die zentrale
Tabelle „Favorit Stats“ mit allen89 Katalogstats, lokalisierter Suche und Anzahl
der passenden Slotpools. Ein dort gewählter Stat wird in sämtlichen zulässigen
Slots aller Kategorien als Favorit übernommen. Die Auswahl ist unabhängig vom
Charakter, Preset, Spielmenü und einer laufenden Netzwerkquelle editierbar.
Manuelle Zu- und Abwahlen je Kategorie/Slot/Stat haben Vorrang und bleiben auch
bei späteren globalen Änderungen erhalten. Die Tabellen verwenden ausschließlich
die Favoritensterne; ein zusätzlicher Resetlink entfällt auf Nutzerwunsch.
Beide Overlayvarianten übernehmen dieselben
effektiven Sterne; beobachtete Rolls, Werte, Bereiche und Boni ändern sich nicht.
Die vorhandene Favoritdatei wird erst bei einer erfolgreichen Änderung auf
Schema2 mit globalStats und expliziten booleschen slotOverrides geschrieben.
Alte positive Slotfavoriten bleiben als lokale Ausnahmen erhalten. Der separat
wählbare Diagnosebeitrag „Genus Insight: Favorit Stats“ liest ausschließlich
anonyme Zähler und Speicherstatus, ohne Spiel- oder Aufnahmeabhängigkeit.

Nutzerauftrag vom 12.11.2026: Gemeinsame Statnamen-Erkennung mit dem Stats-Tab;
87 der89 Katalognamen sind durch beobachtete Anker, begrenzte numerische Namen
und Abgleich mit den aktuellen Slot-Rolltabellen zugeordnet. Jeder angezeigte Roll
muss weiterhin zu Slot, Rang und exaktem skalierten Wert passen. Die beiden noch
unbelegten numerischen IDs für AmplifyFrontAttack und FrontAttackCriticalResist
bleiben offen. Ein explizites Nulltripel aus Stat-ID, Rang und Wert erscheint als
„Nicht analysiert“; fehlende oder ungültige Daten bleiben unbekannt.

Die Kategorie- und Preseterkennung nutzt ausschließlich vorhandene Fensterbilder.
Zwei unabhängige frische Bilder müssen die Menügeometrie und Auswahl bestätigen.
Bei fehlendem Menü, unklarer Auswahl, alten Bildern oder verlorener eigener
Charakterbindung werden alte Rolls nicht als aktuelle Anzeige weitergeführt.
Der eigene Beobachter ist vom Kartenstatus unabhängig und läuft nur, solange
der Genus-Tab oder ein entsprechendes Live-Widget benötigt wird. Er nutzt dieselbe WGC-Sitzung wie
die übrigen passiven Anzeigen. Das Widget führt keine Spielaktionen aus.

Der Klick auf eine Presetnummer aktiviert dessen Boni unmittelbar (Nutzerbestätigung
vom 09.11.2026). Die Übersicht „Alle angewendeten Effekte“ folgt deshalb ebenfalls
der bestätigten Spielauswahl. Sie wird nur für die eigene laufende Charakterbindung
gemerkt und bei Autoritätsverlust oder einer neuen vollständigen Liste verworfen.
Ohne beobachtete Spielauswahl dient die bestätigte vollständige Liste als Stand. Unvollständige oder noch nicht zugeordnete Stats bleiben
sichtbar als Teilstand beziehungsweise unbekannter Roll gekennzeichnet.

Seit Nutzerpräzisierung vom 09.11.2026 bleiben die gemeinsamen Genus-Rolls
persistent erhalten; nur das aktive Preset ist charakterbezogen. Der Calculator
zeigt bei fehlender aktueller Auswahl den gespeicherten Stand mit Zeitangabe.
Schließen des Spielmenüs, Fokusverlust und Capturepausen löschen diese Anzeige
nicht. Das Live-Overlay benötigt weiterhin ein frisch bestätigtes geöffnetes
Menü und den eigenen aktuellen Charakterkontext.

## Boss-Rush-Filter (2026-11-09)

Im Map-Tab unter Boss Rush stehen zwei unabhängige gespeicherte Checkboxen, beide
default aus: nur Bosse mit eigener Unique-Ausrüstung sowie nur Bosse mit eingerichtetem
Alert. Gemeinsam müssen beide Kriterien erfüllt sein. Der Alertfilter verlangt die
Bossauswahl plus aktivierte Einzelpräferenz; der globale Alert-Master ist davon
unabhängig. Auch Erinnerungen ohne Vorwarnung, nur bei bestätigtem Spawn, zählen.

NavigationSchedule projiziert ausschließlich die aktuelle native Liste. Filterung liegt
vor Spawn-/Vorschauanzahl und Kettenbildung und gilt identisch für alle Karten, das
Spawn-Panel und die Boss-Rush-Zielübersicht. Altersfenster, Cachekennzeichnung und
fehlende Koordinaten behalten ihre Regeln. Schedule, vollständige Timer-/Bossauswahl
und Alertquellen werden nicht gefiltert. Ein leeres Filterergebnis verweist auf die
Map-Filter und behauptet keine fehlende Bossliste.

## Pflicht-Sperre im Overlay Editor (2026-11-09)

Jedes Editorfenster enthält zusätzlich zu seinen höchstens 24 Inhaltswidgets
genau eine 28×28 Pixel große Overlay-Sperre. Sie liegt über dem Inhalt und lässt
sich im Canvas per Maus, Pfeiltasten oder X/Y-Feldern frei positionieren. Entfernen,
Duplizieren und separate Skalierung sind ausgeschlossen. Eine kleinere Canvasfläche
klemmt die Sperre erreichbar an den Rand; herausragende Inhalte bleiben gespeichert.

`OverlaySettings.LockWidget` speichert ausschließlich ihre logische Position.
Vorlagen und Fensterduplikate übernehmen sie; ältere Layouts erhalten beim Lesen
die Position rechts oben, ohne dafür die Datei zu schreiben. Die Sperre gehört
nicht zur optionalen Widgetliste. Parallele Inhalts- und Bildschirmänderungen
bewahren eine inzwischen geänderte Sperrposition.

Das native Fenster verwendet denselben separaten `OverlayLockButton` und Painter
wie die statischen Overlays. Canvas, App-Vorschau und Presetbilder zeigen dessen
Produktionsglyph. Der Live-DOM-Renderer zeichnet keinen zweiten Glyph darunter.
Ein Klick entsperrt das gesamte Fenster zum Verschieben und gleichmäßigen Skalieren
an den Ecken; erneutes Klicken sperrt es. Der Place Mode bleibt zusätzlich verfügbar.
Das Entsperren ist flüchtig; Ausblenden oder Quellenentzug sperrt wieder.

Abgeschlossene native Gesten speichern einmalig über den vorhandenen atomaren
Positionspfad. Während einer Geste oder laufenden Speicherung setzen neue Frames
die Geometrie nicht zurück. Captureverlust, Rechtsklick und Quellenentzug verwerfen
eine laufende Geste. Aktuelle Quellenautorität und Einstellungen werden vor dem
Speichern abgeglichen, gespeicherte Geometrie vor einer Folgegeste übernommen.
Speicherfehler bleiben sichtbar und verhindern weitere unbestätigte Positionsänderungen.
Es entstehen keine neuen Tracker, Netzwerkzugriffe, Sounds oder Spieleingaben.

Im Loot-Tracker verweist ein direkter Link auf den Editor. Die weiterhin gemeinsamen
Drop-Meldungs-, Sound- und Animationsoptionen bleiben im Loot-Tracker; ihre Rückmeldung
spricht ausschließlich von Drop-Einstellungen, nicht von einem alten Loot-HUD.

## Aufnahmefreigabe je Editorfenster (2026-11-09)

Unter „Anzeige & Verhalten“ steuert „In Aufnahmen anzeigen“ ausschließlich das
ausgewählte Editorfenster einschließlich seines Schlosses. Die Auswahl wird
atomar gespeichert; Vorlagen verändern sie nicht. Bestehende und neue Fenster
bleiben ohne ausdrückliche Freigabe aus Aufnahmen ausgeschlossen.

Der globale Schalter der statischen Overlays ist davon unabhängig. Die vorhandene
Absicherung gegen Selbstaufnahme bleibt erhalten: Live-Editorfenster werden nur
bei reiner Spielfensteraufnahme durch Soulcrest für externe Aufnahmen freigegeben.
Die anonyme Editor-Diagnose enthält `CaptureExcluded` als konfigurierte Vorgabe,
ohne daraus einen erfolgreichen nativen Aufnahmeausschluss abzuleiten.

Der Place Mode zeichnet eine gemeinsame temporäre Fläche. Enthält sie ein nicht
freigegebenes Editorfenster oder nicht freigegebene statische Overlays, bleibt
die gesamte Fläche aus Aufnahmen ausgeschlossen. Ausschließlich freigegebene
Editorfenster benötigen dort keine zusätzliche globale Static-Freigabe.

## Fehlender Kartenkontext nach dem Start (2026-11-10)

Der Overlay Editor zeigt in seiner Liveansicht einen Hinweis zum einmaligen
Teleportieren, solange eine laufende freigegebene Lootquelle und der eigene
gebundene Charakter vorhanden sind, der aktuelle Kartenkontext aber noch nicht
bestätigt wurde. Er verschwindet bei bestätigtem Kontext automatisch. Eine leere
oder pausierte Session, abgeschaltetes Tracking und bestätigte normale Instanzen
lösen ihn nicht aus. Bibliothek, Beispieldaten, Presets und statische Overlays
bleiben davon unabhängig. Der Hinweis startet keine Erfassung und holt keine
zuvor unbelegten Drops nach.

Im Loot-Tracker folgt die Exchange-Konfiguration dem tatsächlich erkannten
Spielcharakter. Die Option „Marktserver automatisch erkennen“ ordnet dessen
Serverkennung anhand des ausdrücklichen Spielschlüssels der Exchange-Liste zu;
die separate Markt-ID wird für Preise verwendet. Alte Katalogcaches werden dafür
einmalig regulär erneuert, vorhandene Preisbelege bleiben erhalten.
Bestehende Profile werden ebenfalls
automatisch zugeordnet. Onlinepreise werden beim Start aktiviert; der separate
Schalter entfällt. Region, Fraktion und Server lassen sich nach
Abschalten der Automatik weiterhin selbst wählen; Gebühren sind unabhängig davon.
„Serverliste laden“ wiederholt auch eine fehlgeschlagene automatische Zuordnung.

## Dungeon-Souls im Shop (2026-11-11)

Das Black-Cloud-Merchant-Overlay verwendet denselben Dungeonquellen-Katalog wie
der Pet-Katalog. Ein belegter Dungeonbezug färbt Kartenfuß, Kontur und Quellenzeile
violett. Die Quellenzeile zeigt „Dungeon“, bei ausschließlich zukünftigen Quellen
„Dungeon (zukünftig)“ und bei ungeklärter Global-Verfügbarkeit „Dungeon (?)“.
Bestehende Farmkarten mit ihren bekannten Monsterplatzierungen bleiben zusätzlich
erhalten. „Special“ beschreibt weiterhin nur das Fehlen von Quellen auf Abyss,
Altgard und Verteron; bei einem Dungeonbeleg ersetzt die genauere Kennzeichnung
diesen Text im Shop. Für Dungeons werden keine Spawnzahlen erfunden.

Das violette Material hat Vorrang vor dem goldenen Special- bzw. grünen MAX-Fuß.
Der MAX-Counter bleibt grün, der 0/5-Counter behält seinen warmen Akzent. Petgattung
über dem Namen und Soul-Counter unter dem Preis behalten ihre Position. Bei
Dungeon plus Weltkarten stehen Dungeonlabel und Kartenanzahlen in zwei kurzen
Zeilen; nur dieser Quellenfuß wächst um sechzehn logische Pixel. Die einmalige
Skalierung bleibt erhalten. Die Offline-Vorschau prüft alle fünf UI-Skalierungen
und 96/144/192 DPI. Netzwerkbestätigung, Zählung, Capture, Fokus,
Click-through und Wiederanzeige nach Kaufdialogen werden nicht geändert.

Die bestehende auswählbare Händlerdiagnose ergänzt die Zahl aktuell projizierter
Dungeonzeilen. Die Pet-Diagnose enthält die anonymen Bestandszahlen des gemeinsamen
Quellenkatalogs.

Sind die ursprünglichen Temp-Referenzen nicht mehr vorhanden, erlaubt das
Offline-Werkzeug ausdrücklich gekennzeichnete archivierte Vorschauen. Manifest
und Einzelberichte speichern Eingabehash und vorhandene Overlaypixel; das sind
keine unveränderten Originalscreenshots. Zusätzlich wird das aktuelle transparente
Overlay separat ausgegeben. Dieser Modus startet weder App noch Capture.
