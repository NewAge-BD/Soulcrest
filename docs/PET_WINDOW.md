# Startstand aus dem Ingame-Pet-Fenster

Stand: 2026-10-03. Grundlage ist ein Screenshot des Pet-Fensters vom Nutzer (englischer Client),
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
   - **MAX** über die Pixelsignatur: Türkis ist nur der MAX-Text. Fixture: MAX-Karten haben
     109–144 türkise Pixel im Band, Karten in Arbeit 0–2 (auch türkise Porträts wie Skyray). Die
     OCR war bei dem kurzen Wort unzuverlässig (5 von 12).
   - **Brüche** per OCR auf zwei festen, schmalen Bändern ab 25 % der Kartenbreite (bei 35 %
     fehlte die „1“ von `17/75`). Reihenfolge: hellster Kanal 4×, dann 6×, dann Weiß-Maske. Kurze
     Texte wie `4/5` liest Windows OCR unzuverlässig, die Kombination x4+x6 deckt sie fast
     vollständig ab (`CardTextVariantsExploration`). Der Parser kennt den Lesefehler
     „Schrägstrich als 1“ (`42175` = 42/75, `415` = 4/5).
   - **Gesperrt oder freigeschaltet** über den Stufenkreis unten links: Navy-Blau, Farbton
     106–125, freigeschaltete Karten 299–486 Pixel, gesperrte 0. Der Auswahlschimmer hat Farbton
     ~100 und zählt nicht. Gesperrt heißt immer `x/5`.
   - **Balken** (beige, Spur 8–90,5 % der Kartenbreite): Bei gesperrten Pets ist der Füllstand
     exakt genug für Fünftel (gemessen 0,73–0,75 bei 4/5, 0,58 bei 3/5, 0,41 bei 2/5). Er dient
     als Rückfall, wenn die Zahl unlesbar ist, und gewinnt bei Widerspruch. Bei `/25` und `/75`
     ist er zu grob (±1–2) und wird nicht verwendet.
3. **Pet erkennen** (`PortraitMatcher`): SIFT-Merkmalsabgleich gegen die Pet-Porträts des
   Datenpakets (dieselbe Spielgrafik, rund ausgeschnitten) und gegen gelernte Porträts.
   Sicher ab 30 Treffern mit doppeltem Abstand zum Zweiten, plausibel ab 15. Fixture: 14 von 14
   bekannten Pets richtig, das unbekannte Pet (12 Treffer) bleibt unbekannt.
4. **Lernen über den rechten Bereich:** Ist eine Karte ausgewählt, liest Soulcrest Name und Stufe
   rechts. Ist der Name im Katalog, wird das Pet bestätigt und das Porträt gespeichert. Sonst
   wird ein **neues Pet gelernt** (`%LOCALAPPDATA%\Soulcrest\learned\`). Etwa 50 der 200 Pets
   kommen auf keiner Karte vor (Dungeons, Shop, Events). Gelernte Pets erkennt danach auch der
   Loot-Tracker.
5. **Zusammenführen** (`PetScanService`): Während der Nutzer scrollt, wird alle ~0,3 s gelesen.
   Pro Pet zählt die Mehrheit der Lesungen, wobei Lesungen durch den Auswahlschimmer halb so viel
   zählen. Unbekannte Porträts werden über einen 8×8-Hash wiedererkannt. **Angeklickte Karte:**
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
