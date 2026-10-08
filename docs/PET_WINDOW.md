# Startstand aus dem Ingame-Pet-Fenster

Stand: 2026-10-08. Grundlage sind Screenshots des Pet-Fensters vom Nutzer (englischer Client),
abgelegt unter `tests/fixtures/pet-window/en-2026-10-03-all.png` (2000×1125, Reiter **ALL**).

## Was das Fenster zeigt

| Bereich | Inhalt | Lesbar? |
| --- | --- | --- |
| Raster links (3 Spalten) | Karten mit Porträt, Stufenkreis unten links, unten rechts **`MAX`** (türkis) oder **`42/75`** (weiß) mit Fortschrittsbalken | **kein Name** auf der Karte |
| Rechter Bereich | Name des **ausgewählten** Pets („Swarm“), „Pet Insight **Lv. 3 (MAX)**“ | sehr gut (Windows OCR) |
| Unten links | „Collection Status **94/200**“ | gut |

Die Zahl hinter dem Schrägstrich verrät die Stufe, an der gearbeitet wird: `/5` heißt noch nicht
freigeschaltet, `/25` Stufe 1, `/75` Stufe 2, `MAX` Stufe 3.

Zweiter Screenshot `en-2026-10-03-locked.png`: gescrollte Liste mit Stufe-1-Karten (`0/25`) und
**gesperrten** Pets. Sie sind ausgegraut, haben **keinen Stufenkreis** und zeigen `x/5` mit Balken.
Die ausgewählte Karte („Predator Saraswati“) ist gesperrt, das Panel zeigt trotzdem „Lv. 1“. Die
Panel-Stufe ist für gesperrte Pets also **nicht** verlässlich und wird nur für MAX verwendet.

## Verfahren (`Soulcrest.Ocr.PetWindow`)

1. **Raster finden** (`PetWindowScanner`): Helligkeitsprofile im linken Viertel. Karten sind
   heller als der Fensterhintergrund und durch dunkle Fugen getrennt. Gesucht werden drei
   gleich breite Spalten nebeneinander, darin Karten mit einem Seitenverhältnis von etwa 1,36.
   Angeschnittene Karten beim Scrollen fallen weg. Die ausgewählte Karte leuchtet und ist daher
   höher als der Median.
2. **Fortschritt lesen:**
   - **Stufenkreis als Gegenprobe:** Seine Zahl bedeutet laut Nutzerbestätigung vom 2026-10-08
     `3 = MAX`, `2 = /75`, `1 = /25`. Gesperrte, ausgegraute Karten ohne Zahl gehören zu `/5`.
     Die schmale 1 wird geometrisch erkannt, breitere Ziffern per OCR (dreifach nebeneinander,
     weil Windows OCR einzelne Ziffern oft auslässt). Eine erkannte 3 ergibt MAX; Brüche, deren
     Stufe dem erkannten Kreis widerspricht, werden verworfen. Ohne lesbare Kreiszahl braucht
     MAX exakten Textkonsens. Türkise Pixel allein reichen nicht.
   - **Brüche** per OCR auf einer im Bild gefundenen Textgruppe (ersatzweise der üblichen Textstelle
     der übrigen Karten). Ansichten: Ziffernfarbmaske, saubere Textkomponenten, hellster Kanal,
     Weiß-Maske und bei Bedarf dunkle Kontur, jeweils 4× und 6×. Mehrere gleiche Ansichten und der
     Balken sichern die Lesung ab. Der Parser kennt den Lesefehler
     „Schrägstrich als 1“ (`42175` = 42/75, `415` = 4/5).
   - **Gesperrt oder freigeschaltet** über den Stufenkreis unten links: Navy-Blau, Farbton
     106–125, freigeschaltete Karten 299–486 Pixel, gesperrte 0. Der Auswahlschimmer hat Farbton
     ~100 und zählt nicht. Gesperrt heißt immer `x/5`.
   - **Balken** (beige, Spur 8–90,5 % der Kartenbreite): Bei gesperrten Pets ist der Füllstand
     exakt genug für Fünftel (gemessen 0,73–0,75 bei 4/5, 0,58 bei 3/5, 0,41 bei 2/5). Er dient
     als Rückfall, wenn die Zahl unlesbar ist, und gewinnt bei Widerspruch. Bei `/25` ist ein
     Rückfall nur bei eindeutigem Füllstand und zum OCR-Text passender Zahl erlaubt; bei `/75`
     dient der Balken nur als Gegenprobe. Kleine /75-Werte liegen oft unter dem Stufenkreis.
3. **Pet erkennen** (`PortraitMatcher`): SIFT-Merkmalsabgleich gegen die Pet-Porträts des
   Datenpakets (dieselbe Spielgrafik, rund ausgeschnitten) und gegen gelernte Porträts.
   Sicher ab 20 Treffern mit dreifachem Abstand zum Zweiten, plausibel ab 9 mit doppeltem Abstand. Fixture: 14 von 14
   bekannten Pets richtig, das unbekannte Pet (12 Treffer) bleibt unbekannt.
4. **Lernen über den rechten Bereich:** Ist eine Karte ausgewählt, liest Soulcrest Name und Stufe
   rechts. Ist der Name im Katalog, wird das Pet bestätigt und das Porträt gespeichert. Sonst
   wird ein **neues Pet gelernt** (`%LOCALAPPDATA%\Soulcrest\learned\`). Etwa 50 der 200 Pets
   kommen auf keiner Karte vor (Dungeons, Shop, Events). Gelernte Pets erkennt danach auch der
   Loot-Tracker.
5. **Zusammenführen** (`PetScanService`): Während der Nutzer scrollt, wird alle ~0,3 s gelesen.
   Pro Pet werden verschiedene Kartenbilder gesammelt; identische Bilder zählen nur einmal.
   Verschiedene gelesene Werte bleiben bis zur Panel-Bestätigung ein Konflikt und werden nicht
   übernommen. Exakte Panel-Werte haben Vorrang vor Kartenlesungen. Unbekannte Porträts werden
   über einen 8×8-Hash wiedererkannt. **Angeklickte Karte:**
   Der Schimmer verändert Porträt und Text. Deshalb wird sie über die Position der Karte im
   vorherigen Bild (gleiche Spalte, gleiche Höhe) mit ihrem sauber gelesenen Eintrag verknüpft. So
   bekommt das per Name bestätigte Pet dessen Werte. (Fehlerbericht 2026-10-03: „Daten werden
   nicht übernommen“. Der Text der ausgewählten gesperrten Karte war unlesbar, ohne Wert wurde
   nichts übernommen.)
6. **Prüfen und übernehmen:** Die Tabelle zeigt pro Pet Porträt, Zuordnung (änderbar), den Wert
   im Spiel und den Wert in Soulcrest. Übernommen werden nur markierte Einträge, unbekannte sind
   nicht vorausgewählt.

## Überarbeitung nach dem Live-Test (2026-10-03)

Die Live-Aufnahme (GDI, 2560×1440, `en-2026-10-03-live-2560.png`) las mit festen Bandpositionen
nur 3 von 14 Karten: Die Zahl sitzt dort tiefer auf der Karte. Seitdem wird alles im Bild selbst
gesucht:

- **Reihen** aus dem gemittelten Profil aller drei Spalten. Ein dunkles Porträt hatte zuvor eine
  ganze Karte verschluckt. **Auswahl** am türkisen Leuchtrahmen (Ring um die Karte).
- **Text** als Gruppe gleich hoher weißer bzw. türkiser Zeichen, rechtsbündig unten
  (Connected Components). Die OCR bekommt **nur diese Zeichen**, schwarz auf weiß, ohne Fell
  oder Haut daneben.
- **Balken** als mindestens 3 aufeinanderfolgende Zeilen mit gleichem rechtem Ende, von unten
  gesucht. Fell hatte vorher mehr beige Pixel als ein kurzer Balken. Genauigkeit ±0,01.
- **Gegenprobe:** Ein OCR-Wert zählt nur, wenn er zum Balken passt. Ausnahme: 3 Durchgänge lesen
  exakt dasselbe. Teilwerte wie `6/2Ä` werden zu x/25 ergänzt und ebenfalls gegen den Balken
  geprüft. Lieber „unlesbar“ als falsch.
- **Ohne lesbaren Text:** Eine schmale „1“ im Stufenkreis bedeutet x/25, dann gilt der Balken × 25.
  Bei x/75 wird nicht geraten.
- **Panel:** „Lv. 1 (5/25)“ ist der exakte Stand des ausgewählten Pets und zählt am stärksten.

Ergebnis: alle drei Testbilder vollständig richtig (15/15, 12/12, live 15/15). Der Test
`PetWindowLiveCaptureTests` verlangt: kein falscher Wert und mindestens 14 von 15 gelesen.

**Vierter Screenshot** (`en-2026-10-03-locked2.png`, Fehlerbericht „wird nicht richtig erkannt“):

- Eine Eiskristall-Karte mit `0/25` galt als MAX: türkises Porträt und leerer Balken. MAX gilt jetzt
  nur, wenn der Stufenkreis **keine** „1“ zeigt. Die Türkis-Pixelprobe ohne Textgruppe greift nur
  noch, wenn keine weiße Textgruppe gefunden wurde.
- Die Auswahl wurde nicht erkannt: Der Leuchtrahmen war dunkler (Helligkeit ~105 statt ≥140,
  Ringanteil 0,12 statt ~0,3). Jetzt zählt Türkis ab Helligkeit 90, und ausgewählt ist die Karte,
  deren Ringanteil mindestens 0,06 und doppelt so hoch wie bei jeder anderen Karte ist (übrige
  Karten ≤0,085).
- Ergebnis: 15/15 richtig, „Cadaver Insectoid“ per Panel bestätigt mit 3/5 (`PetScanLockedPets2Tests`).

**Fünftes Bild** (Live-Aufnahme `en-2026-10-03-nocounter-live.png`, „viele nicht erkannt, aber alle bis
auf 1. Reihe links haben keine Counter“):

- Gesperrte Pets **ohne Souls zeigen weder Zahl noch Balken** = 0/5. Ihre Karten sind unten dunkel
  (Profilwerte 8–20). Mit der Schwelle 20 wurden sie zu kurz erkannt (170 statt 198 px), eine Reihe
  fiel ganz weg. Jetzt werden die Reihen mit Schwelle 5 gesucht. Wo ein Leuchten oder helle Grafik
  zwei Reihen verbindet, greifen die strengen Abschnitte (Schwelle 20).
- Ein 0/5-Pet ohne Zuordnung muss nicht angeklickt werden. Es entspricht dem Standard, also gibt es
  nichts zu übernehmen. Das Overlay listet es nicht als fehlend.
- **Porträtabgleich mit Geometrieprüfung:** Die ringförmige Kugel bekam 63 rohe SIFT-Treffer gegen
  die Spinne (über den runden Rahmen der Icons). Jetzt zählen nur Treffer, die eine gemeinsame Lage
  ergeben (RANSAC, Ähnlichkeitstransformation, Maßstab 0,3–6). Fixtures: falsche Pets ≤7, echte
  9–99 (ausgegraute 9–24). Plausibel ab 9 und doppelt so viel wie der Zweite, sicher ab 20 und dreifach.
- Stufenkreis der ausgewählten Karte: Die Ziffer ist dort türkis statt weiß, und der Leuchtrahmen
  reichte in den Suchbereich. Jetzt gelten nur der Kreis (größte Navy-Fläche) und eine Ziffer in
  seiner Mitte.
- **Namen im Panel:** Windows OCR liest „i“ als „j“ („Red Spark Jgnus“, „Dracunj Herbalist“), lange
  Namen sind mit „...“ gekürzt. `PanelPetName` vergleicht mit j = i, Präfix bei gekürzten Namen und
  höchstens einem Lesefehler (zwei ab 12 Zeichen), immer eindeutig. Falsch gelernte Pets werden beim
  Start auf das richtige Pet umgezogen (Fortschritt und Porträt).

**Bewegungserkennung:** „Bild bewegt sich“ hing an den Pet-Zuordnungen je Bild. Bei unsicheren
Porträts wechseln die, und eine große Idle-Animation in der Mitte hielt den Status dauerhaft auf
„bewegt sich“. Jetzt vergleicht `GridMotion` nur die Pixel des Kartenrasters (Graustufen, 120 px
breit, mittlere Abweichung < 6) und die Rasterlage (±3 px).

**Overlay-Scanmodus:** Während des Scans zeigt das Overlay neben dem Raster (nicht darüber)
„x/y erkannt“ für die sichtbare Seite, „Bild bewegt sich“ beim Scrollen und grün „✓ Alle erkannt –
weiterscrollen“, wenn alle sichtbaren Karten Wert und Pet haben und zwei Bilder gleich waren.
Fehlende Karten stehen mit Position darin („Reihe 2 links (Wert unlesbar)“) zum Anklicken.

## Bedienung (Tab „Pet-Fenster“)

Pet-Fenster im Spiel öffnen (Reiter ALL), dann **Scan starten** und langsam durchscrollen.
Unbekannte Pets einmal anklicken, danach Scan stoppen, prüfen und **Markierte übernehmen**.
**Bild einlesen …** liest einen Screenshot ein. Der Erfassungsbereich ist standardmäßig der
Hauptbildschirm, andernfalls wählt man ihn per Rahmen um das Spielfenster.

## Tests

- `PetWindowTextTests` (Core): Kartentexte inklusive OCR-Varianten, Panel-Stufe, Sammlungsstand.
- `PetWindowScannerTests` (Ocr): Fixture vollständig. 15 Karten, 12× MAX und 42/75, 20/75, 17/75,
  Porträts, Auswahl, Panel „Swarm / 3“, Sammlung 94/200.
- `PetScanServiceTests` (App): Bild einlesen, Zusammenführen, Bestätigung über das Panel,
  Übernahme von 14 Pets, Lernen eines unbekannten Pets. Läuft in einem temporären Datenordner
  (`SOULCREST_DATA`).
- Selbsttest (`--smoke-test`) prüft, dass OpenCV/SIFT im Single-File-Build lädt.

## Offen

- Fünf Testbilder liegen vor (EN). Live-Scrollen ist ungetestet, ebenso gesperrte Pets mit 0/5.
- Porträts gesperrter (ausgegrauter) Pets passen schlecht zu den Datenpaket-Porträts. Sie werden
  erst durch Anklicken (Panel-Name) sicher zugeordnet.
- Deutscher Client: Panel-Texte („Pet Insight“, „Collection Status“) sind unbekannt.
- Gattung gelernter Pets ist unbekannt. Sie ließe sich aus dem gewählten Gattungsreiter links
  ableiten.

## Platzhalter-Namen und falsch gebundene Porträts (2026-10-03)

- 39 Pets der Kartendaten heißen nur nach ihrem Icon („Crestlich 01“, „Bee 01“), weil die Quelle für
  sie nur chinesische Namen hat. Der Loot-Feed zeigt den echten Namen („Soul: Crestlich (Bound)“),
  der deshalb nie passte (Bericht „der loot wurde nicht erkannt“). Abhilfe:
  - belegte Namen in `data/pets/name-overrides.json` (in Core eingebettet). Bisher nur
    `crestlich-01` = Crestlich (chinesisch 魁鸟, 02 ist 公魁鸟 = Männchen; Loot-Feed und Chat des Nutzers);
  - **Lernen im Pet-Fenster:** Passt das Porträt der angeklickten Karte sicher zu einem Platzhalter
    und kennt der Katalog den Panel-Namen nicht, bekommt der Platzhalter diesen Namen
    (`learned-names.json`, Fortschritt bleibt, die ID ändert sich nicht);
  - früher gelernte Pets, deren Porträt sicher ein Platzhalter ist, werden beim nächsten Scan
    zusammengeführt (z. B. „Enhanced Krall Com...“ → `krallwar-01-v01`). Gekürzte Namen zählen im
    Loot-Feed als Namensanfang.
- **Fehler bei der Bindung der angeklickten Karte:** Sie wurde über ihre Position im vorherigen Bild
  zugeordnet, auch während gescrollt wurde. Bei genau einer Reihe Versatz stand dort ein anderes Pet.
  Die gespeicherten Porträts von „Airon“ und „Kalgolem“ zeigten Papis und Ashen Spider. Jetzt gilt:
  - Gebunden wird nur noch bei stillstehender Liste (`GridMotion`).
  - Widerspricht das sicher erkannte Porträt dem Panel-Namen, wird nichts gelernt.
  - Gelernte Porträts, die sicher ein anderes Pet zeigen, werden beim Start des Scans nach
    `learned\rejected\` verschoben.

## Stufe-2-Karten mit kleinen Werten (2026-10-03)

Fensteraufnahme `en-2026-10-03-level2-live.png` (WGC, ohne Overlay). Bericht: „Reihe 3 Mitte wird oft
nicht richtig erkannt“.

- Bei kleinen x/75-Werten liegt der Balken ganz unter dem Stufenkreis (Balken = 0). Dann „passte“
  jeder kleine Wert zur Gegenprobe, und „1/73“ wurde für 2/75 als 1/75 übernommen. Jetzt bestätigt der
  Balken nur, wenn er sichtbar ist (`BarCanTell`). Sonst braucht es zwei gleiche, exakte Lesungen ohne
  abweichende dritte. Geratene Teilwerte zählen dort nicht.
- Findet der Scanner keine Textgruppe (weiße Ziffern auf hellen Schuppen, 9/75), liest er die übliche
  Textstelle dieser Aufnahme (Median der übrigen Karten). Er nutzt dafür zusätzlich eine Otsu-Ansicht
  der dunklen Ziffernkontur.
- Ergebnis: 11 von 15 gelesen, **kein falscher Wert** (`PetWindowLevel2Tests`). 9/75 bleibt unlesbar
  („975“, „91/-75“). Nur den Zähler zu lesen (Nenner 75 aus dem Stufenkreis) hätte 2/75 als 1/75
  übernommen und bleibt deshalb aus. Unlesbare Karten listet das Overlay, ein Klick liefert den Wert
  über das Info-Panel.
- Alle Tracker (Pet-Fenster, Loot-Feed, Karte) nehmen über `GameCaptureService` nur das Spielfenster auf
  (WGC). Soulcrests Overlays, die z. B. über der ersten Kartenreihe liegen, stören dadurch nicht mehr.

## UI-Skalierung, Bildbereich und frische Bilder (2026-10-03)

Bericht: „Der Pet-Scan funktioniert jetzt schlechter“, dazu drei UI-Skalierungen (`uiscale-small`,
`-medium`, `-large`).

- **Frisches Bild:** Die Pipeline der Fensteraufnahme gab die Kopie des vorherigen Aufrufs zurück. Beim
  Pet-Scan (alle 0,3 s) war das Bild 0,3 s alt, und das Overlay zeigte die Liste einer alten
  Scrollposition. Kopien älter als 50 ms werden jetzt verworfen. Nur der 60-fps-Kartenloop nutzt die
  Pipeline.
- **„1/73“:** Bei x/75 löst der Balken erst ab 15 % genau genug auf. Unvollständig gelesene Werte
  ergänzt der Balken nur bei /25, /5 oder ab 15 % Füllung. Ein Balkenrest von 0,05 hatte 1/75 für
  2/75 bestätigt.
- **Zu schmale Textgruppe** („1/7“) wird auf die übliche Textbreite der Aufnahme erweitert.
- **Raster:** Suchbereich bis 34 % der Breite (große Skalierung: Raster bis ~29 %). Spalten werden als
  linkes statt rechtes Dreierpaar gesucht, denn bei mittlerer Skalierung lag sonst der Text neben dem
  Raster im Ergebnis. Reihen unter 90 % der mittleren Reihenhöhe gelten als angeschnitten und
  entfallen. Bei großer Skalierung wurde eine solche Reihe als „gesperrt 0/5“ gelesen.
- **Info-Panel** ab 70 % der Breite (große Skalierung: „Pet Insight“ bei ~73 %).
- **Mitte ausgeblendet:** Der Pet-Scan erfasst nur Raster (links) und Panel (rechts). Die Mitte mit dem
  animierten Pet (36–69 %) bleibt schwarz.
- Ergebnis: Alle drei Skalierungen haben die richtige Kartenzahl (15/12/9) und keinen falschen Wert
  (`PetWindowUiScaleTests`). Die übrigen sechs Aufnahmen bleiben unverändert.

## Kailin / Young Kailin und Balkenwerte (2026-10-03)

Bericht: „Wenn ich auf 2. Reihe links gehe, möchte er wieder 2. Reihe rechts haben und umgekehrt.“
Aufnahme `en-2026-10-03-row2-live.png`.

- Kailin und Young Kailin haben fast gleiche Porträts (Abgleich 56:39 bzw. 46:26, nicht eindeutig).
  Über den Bild-Hash landeten beide Karten im **selben** unbekannten Eintrag. Ein Klick benannte ihn
  nach der einen Karte, der nächste nach der anderen. Jetzt gilt: Gleichzeitig sichtbare Karten sind nie
  dasselbe Pet. Jede bekommt einen eigenen Eintrag, und ein doppelt passendes Pet geht nur an die
  stärkere Karte.
- Dabei fielen falsche Werte auf:
  - 21/25 kam nur aus dem Balken als 22 (0,86 = 21,5). Kerubar galt dort als 18/25, zeigt aber 16/25 (Balken 0,65 stimmt; Korrektur der Vorlage am selben Abend).
    Ein reiner Balkenwert gilt jetzt nur noch, wenn er klar auf einer Zahl liegt (±0,3 Souls) und der
    OCR-Text dieselbe Zahl in einer bekannten Schreibweise enthält (`TextShowsSouls`).
  - 11/25 wurde sechsmal als „1/25“ gelesen; die Mehrheit setzte sich gegen den Balken (0,45) durch.
    Fehlt einer Lesung nur die führende Ziffer des Balkenwerts, wird sie verworfen
    (`DropsLeadingDigit`).
- Ergebnis: 11 von 15 gelesen, alle richtig, 15 getrennte Einträge (`PetScanRow2Tests`). In
  `locked2` bleibt eine 0/25-Karte mit leerem Text unlesbar statt aus dem Balken gelesen.

## Ziffernfarbe, Seitenbindung, Tempo und Overlay (2026-10-03, Abend)

- **Ziffernfarbmaske** (Idee des Nutzers): Die Zahlen sind fast reines Weiß mit dunklem Rand. Der
  Scanner nimmt den genauen Farbwert aus dem Textband selbst (hellste, am wenigsten gesättigte Pixel)
  und behält nur Flächen in diesem Farbton (Lab-Abstand < 22), die vom dunklen Rand umschlossen sind
  (berühren den Bandrand nicht), so hoch wie die Schrift sind und auf der Textzeile liegen. Links wird
  großzügig mitgenommen, damit eine mit hellem Hintergrund verschmolzene erste Ziffer nicht fehlt.
  Diese Ansicht wird zuerst gelesen. Ergebnis: „4/25“ auf pinker Blüte, „2/25“ auf hellblauem Schleim,
  „11/25“ und „21/25“ auf hellen Porträts – vorher unlesbar oder falsch – jetzt richtig (30 von 30
  Karten auf zwei Live-Bildern).
- **Balken bei /25**: Zwei gleiche Lesungen ohne dritte Bestätigung zählen bei sichtbarem Balken nur,
  wenn sie höchstens 1 Soul vom Balken abweichen (zweimal „14/25“ bei Kerubar 16/25, Balken 0,65).
  Die Fixture `row2-live` war falsch beschriftet: Kerubar zeigt dort 16/25, nicht 18/25.
- **Seitenbindung**: Solange dieselbe Seite zu sehen ist (die meisten nicht ausgewählten Karten zeigen
  dasselbe Porträt an derselben Stelle), behält jede Karte das über das Infofenster bestätigte Pet.
  Nötig für fast gleiche Porträts (Kailin / Young Kailin): ohne Auswahl fielen sie auf „unbekannt“ zurück.
- **Veraltetes Infofenster**: Steht dort noch das vorher gewählte Pet und ist dieses Pet auf einer
  anderen Karte zu sehen (oder die Karte ist schon als anderes Pet bestätigt), wird nichts gelernt.
- **Doppelt gelernte Porträts**: Zwei Pets mit byte-gleichem gelerntem Porträt werden beim Start
  beiseitegelegt (`learned/rejected/<id>-doppelt.png`) und beim nächsten Anklicken neu gelernt.
- **Gesperrt vs. freigeschaltet**: Eine gesperrte Karte (x/5) wird nie einem Pet zugeordnet, das schon
  als freigeschaltet gelesen wurde, und umgekehrt (live: „2/5“ landete bei Kuru Overseer 0/25).
- **Tempo**: Ein Scan dauerte 4,5 s, davon 3,8 s Porträtvergleich. Jetzt parallel und mit Cache je
  Porträt-Hash; gelesene Karten werden über einen Fingerabdruck des unteren Kartenbereichs nicht erneut
  gelesen. Ruhiges Bild / Klick: ~0,15 s; neue Seite: ~3,4 s.
- **Bedienung**: „Scan starten“ zeigt sofort „Scan startet …“ (Porträts laden im Hintergrund),
  laufend „Scan läuft · stoppen“. „Markierte übernehmen“ geht auch während des Scans und wird kurz
  grün („✓ N übernommen“).
- **Overlay**: Karten, die noch angeklickt werden müssen, bekommen im Spiel einen Rahmen (orange =
  Wert unlesbar, gelb = Pet unbekannt), nur bei ruhender Seite, klick-durchlässig und nie in Aufnahmen.
- **Nachtrag**: Kartenwerte zählen nur aus Bildern einer ruhenden Liste (Zwischenbilder beim Scrollen
  gaben Sylphen 12/25 eine Stimme für 11, Rafflesia 4/25 eine für MAX). Bestätigt das Infofenster ein
  Pet, das einem anderen Eintrag mit widersprüchlichem Stand (freigeschaltet statt gesperrt) zugeordnet
  war, verliert jener Eintrag die Zuordnung (Kuru Overseer ist gesperrt 2/5; eine 0/25-Karte war ihm
  per Porträt zugeordnet worden).

## Sichtbare Zahlen und strengere Prüfung (2026-10-08)

Live beobachtet: Zelophi 7/75, Young Ursus 6/75 und Crestlich 4/75 erschienen als MAX;
Odyle Stone Spirit 5/25 wurde als 3/25 gelesen. Türkise Bildteile und der bei kleinen /75-Werten
verdeckte Balken erklären die falsche MAX-Regel. Die erste grob zum Balken passende OCR-Lesung
wurde bisher sofort akzeptiert.

- Alle OCR-Ansichten werden vor der Entscheidung gesammelt. Ein dominierender exakter Wert
  braucht mindestens drei Lesungen aus mindestens zwei Vorverarbeitungen und mindestens doppelt
  so viele Treffer wie sämtliche anderen Werte zusammen. Bei nur zwei Lesungen oder einem
  einzelnen Kandidaten ist zusätzlich eine engere Balken-Gegenprobe nötig.
- Der Lese-Cache verwendet exakte Pixel des unteren Kartenbereichs (SHA-256), damit kleine
  Ziffernänderungen keinen früheren Wert aus einem grob verkleinerten Fingerabdruck zurückgeben.
- Bei „Scan starten“ werden alte Wertlesungen und Panel-Werte zurückgesetzt; Pet-Zuordnungen
  bleiben erhalten. Das Detailpanel wird nur aus ruhigen Bildern gelernt.
- Erste Fassung (durch den Nachtrag unten ersetzt): Jede sichtbare Karte erhielt oben ein Zahlenlabel: **Scan**, **Panel** oder **Bisher** mit
  beispielsweise `7/75`, `0/5`, `MAX` oder `?`. Die Zahl im Spiel unten bleibt frei.
  Türkis bedeutet gelesen, Orange unlesbarer aktueller Wert, Gelb unbekanntes Pet und Rot
  widersprüchliche Werte. Bei Konflikten steht die andere Lesung darunter. Bei abweichender
  Kartenlesung zeigt ein Panel-Label zusätzlich den Kartenwert.
- Ein alter gespeicherter Kartenwert macht eine aktuell unlesbare Karte nicht mehr fertig.
  Konflikte blockieren die Übernahme und werden auch in der Tabelle angezeigt. Ein Klick im Spiel
  kann sie über den exakten Panel-Wert auflösen. Bei Bewegung verschwinden die alten Labels schon
  vor der OCR der nächsten Seite.
- Regressionstests prüfen türkise Bildteile, doppelte Bilder, Konflikte, Panel-Vorrang, Neustart der
  Werte und die freie Zahlenposition. Die vorhandenen Originalaufnahmen bleiben Teil der Prüfung.
  Die konkrete neue Live-Situation mit 5/25 muss nach Neustart der aktualisierten App erneut geprüft
  werden; die Offline-Tests ersetzen diesen Vergleich nicht.

## Nachtest: Zahl über dem Spielwert und manuelle Korrektur (2026-10-08)

- Der Name steht oben auf der Karte (Katalogname nach Porträt-/Panel-Abgleich; unsichere Vorschläge
  mit `?`). Der Wert mit Quelle **Scan**, **Panel**, **Bisher** oder **Manuell** steht rechts direkt
  über den lokalisierten Spielziffern. MAX wird für die Labelposition separat im unteren türkisen
  Textbereich gesucht. Originalziffern und Balken bleiben frei; Konfliktdetails stehen unter dem Namen.
- **Strg gedrückt halten und mit links auf den Overlay-Wert klicken** öffnet einen Soulcrest-Dialog.
  Er akzeptiert ausschließlich `0–4/5`, `0–24/25`, `0–74/75` oder `MAX`; Speichern korrigiert den
  Scan-Eintrag. Erst **Markierte übernehmen** schreibt den Fortschritt ins Profil. Abbrechen ändert nichts.
  Der Editor hält den angeklickten Eintrag fest, folgt dessen Zusammenführung nach Panel-Bestätigung
  und meldet einen inzwischen geleerten Eintrag. Kennungen werden während der Service-Laufzeit nicht wiederverwendet.
- Manuelle Werte haben Vorrang vor Karten- und Panel-OCR, bleiben in Snapshots/Zusammenführungen und
  beim erneuten Scanstart erhalten. Werden zwei korrigierte Einträge zusammengeführt, gilt die zuletzt
  manuell gespeicherte Korrektur. **Liste leeren** oder App beenden verwirft die Scan-Liste; bereits
  ins Profil übernommene Korrekturen bleiben dort gespeichert.
- Die visuelle Ebene bleibt klickdurchlässig. Ein separates kleines Eingabefenster erscheint nur über
  dem Wert unter dem Mauszeiger, bei gehaltenem Strg, ruhiger aktueller Seite und AION2 im Vordergrund.
  Prüfung alle 25 ms; kein Maus-/Tastatur-Hook, keine Weiterleitung oder Erzeugung von Spieleingaben.
- Sicher gelesene Kreisstufe 1/2 liefert den Nenner. Der Zähler braucht Zustimmung aus unterschiedlichen
  Vorverarbeitungen; bei nur einer Maske und zwei Skalen zusätzlich einen sichtbaren Balken innerhalb
  eines halben Souls. Beobachtete beschädigte Nenner (`7/715`, `6/7`) und Leerzeichen (`33 75`)
  werden nur bei passender Kreisstufe ausgewertet. `331/75`, ausdrücklich andere Nenner und uneinige
  Zähler werden nicht repariert. Der Schutz gegen fehlende führende Ziffern bleibt erhalten; bei /25
  müssen die neuen Konsenslesungen innerhalb eines Souls zum sichtbaren Balken liegen.
- Bei Stufe 1/2 kann ein türkiser Porträtbereich den üblichen weißen Zahlenausschnitt nicht mehr ersetzen.
  Die Panel-Namenssuche umfasst nur die Zeile unmittelbar über Insight (maximal 7,5 % der Bildhöhe),
  schließt Reiterköpfe aus und lernt neue Namen live erst nach zwei aufeinanderfolgenden ruhigen
  Bestätigungen. Einzelbildimporte bleiben gezielte Bestätigungen. Vorhandene OCR-Namensnormalisierung
  übernimmt z. B. `Crestljch`/`Kerubjan` weiter über den Katalogabgleich.
- Grundlage: Nachtest 17:14–17:19, insbesondere Dratona 33/75, Sylphen 8/75, Fossa 7/75,
  Young Ursus 6/75, Crestlich 4/75, Papis 3/75 und Odyle 5/25. Die protokollierten Texte sind
  Regressionseingaben für die Entscheidung; es gibt keine neuen Bild-Fixtures aus diesem Lauf und
  keine daraus gemessene Erfolgsquote. Ursprüngliche Bild-Fixtures bleiben verpflichtend.
- **Offen:** 175 erkannte freigeschaltete Einträge gegenüber 174 laut Spiel. Die Ursache ist nicht belegt;
  keine automatische Bereinigung oder Begrenzung der Ergebnisse. Das Log enthält jetzt eindeutige
  Eintragskennungen, Porträt-Hashes und Sammlungssummen zur Prüfung beim nächsten Live-Scan.
  Die Strg-Klick-Bedienung und Erkennung müssen nach Neustart des Testers im Spiel nachgeprüft werden.

## Native Farbmaske für Ziffern (2026-10-08)

- Die zusätzliche Ansicht `CounterColourMask` bestimmt die Textfarbe aus den hellsten neutralen
  Kernpixeln der rechten Hälfte des Zahlenausschnitts (Nenner). Sie arbeitet auf den Originalpixeln,
  bevor eine Vergrößerung Schrift und Porträt vermischt. Die ältere Farbmaske bleibt eine weitere Ansicht.
- Statt eines festen RGB-Werts wird pro Ausschnitt ein Referenzton gemessen. In den vorhandenen
  Live-Fixtures sind Ziffernkerne häufig RGB 255/255/255; ältere verkleinerte Aufnahmen und
  Kantenglättung enthalten andere Werte. Ein toleranter Lab-Abstand erhält die Randpixel.
- Ein dunkler Rand im 5×5-Umfeld, passende Komponentengröße, gemeinsame Grundlinie und enge
  Abstände entfernen helle Fell-/Kristallteile. Die rechts liegenden Nennerziffern verankern die Zeile.
  Ergebnis: schwarze Ziffern auf Weiß, erst danach OCR bei 4×/6×. Zwei Skalen dieser Maske zählen
  weiter als dieselbe Vorverarbeitung; Kreisstufe, Konsens und Schutz vor fehlenden Ziffern bleiben erhalten.
- Offline-Vergleich über elf vorhandene Originalaufnahmen, zusammen 153 Kartenpositionen:
  vorher 144 lesbar, nachher 150. Zusätzliche korrekte Ergebnisse: 9/75 und 2/75 in zwei /75-Aufnahmen,
  21/25 in `row2-live`, ausgewählte 4/75 bei großer UI-Skalierung. Keine Änderung eines zuvor
  akzeptierten Werts. Die Kartenpositionen enthalten Wiederholungen derselben Pets und sind keine
  unabhängige Live-Erfolgsquote für den Scan vom 8. Oktober.
- Rafflesia 3/75 wird von der neuen Maske sauber gelesen, bleibt wegen widersprüchlicher anderer
  Ansichten im Gesamtscan unlesbar. Die Entscheidungsregeln werden dafür nicht gelockert.
  Der konkrete neue Live-Scan, 174/175 und der leere Scroll-Eintrag sind damit nicht als gelöst belegt.

## Startzeit und Bewegung während OCR (2026-10-08)

- Die Prüfung gelernter Porträts gegen die Kartenicons wird lokal in
  `cache/pet-portrait-validation.json` gespeichert. Schlüssel: Pet-Kennung und SHA-256 des Porträts;
  Kontext: Kennungen, Dateinamen und Inhalte aller Referenzicons sowie Revision der Matcher-Assembly.
  Änderungen erzwingen einen erneuten Abgleich. Defekte oder nicht beschreibbare Cache-Dateien
  verhindern den Scan nicht. Die erste Prüfung neuer Bilder läuft mit höchstens vier Arbeitern.
- Gespeichert wird das vollständige Matchergebnis einschließlich Alternativtreffer, keine dauerhafte
  Freigabe: die Reparaturregeln werden weiterhin gegen den aktuellen Katalog ausgewertet.
  Identische Porträts verschiedener Pets werden vor jeder Cache-Abfrage wie bisher ausgesondert.
- Ein Live-Bild, dessen Raster noch nicht stillsteht, darf weder neue Einträge erzeugen noch bestehende
  Kennungen, Werte oder die Bindungen der letzten ruhigen Seite verändern. Erst der nächste ruhige
  Vergleich übernimmt Karten. Einzelbildimporte bleiben ausdrückliche Bestätigungen.
- Während OCR läuft, wird etwa alle 100 ms nur das linke Raster erneut aufgenommen. Erkannte Bewegung
  oder ein nicht prüfbares GPU-Bild blendet Labels aus, bricht die alte OCR ab und verwirft ihr Ergebnis.
  Auch unmittelbar vor der Übernahme wird nochmals geprüft. Vor dem nächsten Scan wird die alte
  Aufgabe vollständig abgewartet; sie kann keine Labels nachträglich zurückbringen.
- `pet-scan.log` protokolliert `START requested`, `icons-ready`, `validation-ready` mit Cache-Treffern,
  `scanner-ready`, `capture-ready` und `first-frame`, jeweils mit Zeit seit dem Startauftrag. Damit lässt
  sich beim nächsten Live-Lauf die tatsächliche Startverzögerung bestimmen.
- Lokale Messung mit denselben 215 Icons und 75 gelernten Porträts wie im Nachtest:
  bisherige serielle Porträtprüfung 16,18 s; neue erste Prüfung inklusive Hashes und Speicherung 9,49 s;
  erneute Prüfung mit Cache 0,085 s (75 Treffer). Icons laden weiterhin etwa 2,49 s, gelernte
  Referenzen 0,54 s und die erste Original-Fixture etwa 2,24 s. Das ist eine isolierte Messung,
  keine gemessene Klick-bis-Overlay-Zeit im laufenden Spiel. Beim ersten Start wird der Cache erst gefüllt.
- Die beiden einmaligen leeren Scroll-Einträge aus Nachtest 3 motivieren den Schutz vor bewegten
  Bildern. Bereits vorhandene Einträge werden nicht automatisch gelöscht. Die Abweichung 175/174
  und einzelne unlesbare Zähler bleiben offen; die neue Version braucht eine erneute Live-Prüfung.

## Deutschsprachiger Spielclient (2026-10-08)

- **Sprache im Spiel (Scan)** wählt für Pet- und Erkundungsscans automatisch Deutsch/Englisch oder
  ausdrücklich Deutsch bzw. Englisch. Die Wahl wird gespeichert, unabhängig von Oberfläche und Namen.
  Beim nächsten Pet-Scan wird ein zuvor erstellter Scanner mit anderer Sprache neu aufgebaut.
- Nutzerbestätigte Paneltexte: **Pet-Kenntnis** und **Sammlungsfortschritt**. Der Namensanker erkennt
  auch von OCR entfernte oder durch Leerzeichen ersetzte Bindestriche. Der exakte Bruch liefert die
  Verständnisstufe, falls kein lesbares `Lv.` vorhanden ist. Ohne bestätigten Textanker wird kein
  beliebiger Zahlenbereich zum Pet-Panel erklärt. Im festen Sammlungsbereich wird ein eindeutiger
  plausibler Sammlungszähler gelesen; 5/25/75-Nenner und widersprüchliche Summen werden verworfen.
- Die Automatik erkennt die bestätigten DE-/EN-Panelbeschriftungen auch einzeln. Reine Zahlen und
  MAX ändern weiterhin nicht die gewählte Sprache; gemischte widersprüchliche Sprachhinweise bleiben
  unentschieden. Die Porträt-, Stufen-, Farbmasken- und Konsensregeln gelten in beiden Sprachen.
- Katalognamen werden schon auf Englisch und Deutsch abgeglichen. Für ein zuvor auf Englisch gelerntes
  Pet mit sicherem Porträt wird ein zweimal bestätigter deutscher Name als `De` am bestehenden Pet
  gespeichert. Seine ID, der englische Name und der Fortschritt bleiben erhalten; bestehende deutsche
  Namen werden nicht ersetzt. Neue nur auf Deutsch gelernte Pets behalten den gelesenen Namen auch
  als Fallback, solange kein belegter englischer Name verfügbar ist.
- Validierung: bestätigte Texte als Parser-Regression und als synthetische Typografie mit echtem
  Windows OCR (de-DE/Automatik), persistierte Sprachauswahl, deutsche Namenszuordnung ohne neue ID
  und weiterhin alle englischen Original-Fixtures. Synthetische Typografie ersetzt keinen deutschen
  Spiel-Screenshot; die native Live-Prüfung folgt nach Neustart des Desktop-Testers.

## Nachtest 4: Randkarten, Zähler und Wiedererkennung (2026-10-08)

Der deutsche Live-Lauf bestätigte Pet-Kenntnis, Sammlungsfortschritt 56/200 und deutsche
Panelnamen. Zum Ende standen 101 gelesene Einträge und 58 erkannte freigeschaltete Pets gegen
56 im Spiel. Es war kein vollständiger Durchlauf aller 200 Karten. Die Nutzerübernahme von
96 Ergebnissen wird durch diese Änderung nicht nachträglich verändert.

- Reihen müssen jetzt mindestens 97 % der mittleren vollständigen Reihenhöhe haben und innerhalb
  des Suchbereichs liegen. Zuvor konnten 92 % sichtbare Randkarten ohne unteren Zähler/Balken als
  0/5 in die Stimmen eingehen (Sylphen, Steingeist, Rotflamme Ignus). Solche Reihen werden vor
  Porträtabgleich und OCR ausgeschlossen. Vollständige graue Karten ohne Zähler bleiben gültig 0/5.
- Für /75 sperrt ein sichtbarer Balken ab 15 % OCR-Werte mit über 20 Prozentpunkten Abweichung.
  Das gilt für Stufen-Konsens und die übrigen OCR-Kandidaten. So wird 1/75 bei einem Balken von
  0,423 nicht mehr als Ersatz für 31/75 übernommen. Kleine verdeckte Balken bleiben ausgenommen;
  der Balken liefert keine exakte Zahl. Die bisherigen Regeln für /25 und MAX bleiben erhalten.
- Ein späterer Porträttreffer kann einen einzigen unbekannten Eintrag mit demselben gelesenen
  Wert und höchstens zwei unterschiedlichen Hash-Bits übernehmen. Er behält Schlüssel, Stimmen
  und manuelle Korrekturen. Mehrdeutige Kandidaten, andere Werte, anderer Freischaltungszustand
  und bereits auf einer anderen sichtbaren Karte verwendete Einträge werden nicht zusammengeführt.
  Eine vorher ausgeschaltete Übernahmeauswahl bleibt erhalten.
- Der deutsche Leveltext `St. 1` wird im Panel gelesen. Bei grauen Karten bezeichnet er die
  Vorschau; ohne echten Zähler wird daraus kein freigeschalteter Fortschritt erzeugt.
- Regressionen verwenden die protokollierten Momo-Werte und Kuru-Porträthashes, synthetische
  Randgeometrie sowie eine kontrolliert angeschnittene bestehende Original-Fixture. Keine neuen
  Spielbilder gespeichert. Die Wirkung auf die Gesamtzählung braucht einen erneuten Live-Nachtest;
  die ältere Abweichung 175/174 ist damit nicht als gelöst erklärt.
