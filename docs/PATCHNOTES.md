# Soulcrest – Patchnotes

Kurz und für Spieler geschrieben.

## 0.12.0 – 2026-10-22

- **Neu:** Daevanion-Overlay: Stat-Hervorhebung. Rechts neben dem Board wählst du einzelne Stats aus;
  passende Nodes bekommen einen umlaufenden Lichtzug, auch bei ausgeblendeten Ergänzungen. Die Auswahl
  bleibt gespeichert, „Alle Highlights aus“ setzt sie zurück.
- **Neu:** Ein Schalter „Overlay an/aus“ oben rechts am Board blendet die Node-Ergänzungen direkt aus
  und wieder ein, ohne Alt oder Entsperren. Die Auswahl bleibt gespeichert.
- **Neu:** Aktivierte Stat-Nodes erscheinen heller und leicht in ihrer Node-Farbe getönt.
- **Behoben:** Tooltips über dem Board lassen das Daevanion-Overlay nicht mehr flackern; nur die
  verdeckten Nodes werden ausgespart.
- **Behoben:** Die Icons auf der Ingame-Weltkarte flackern nicht mehr, wenn das Fenstersymbol der
  Weltkarte nicht erkannt wird.

## 0.11.1 – 2026-10-22

- **Geändert:** Die Option „Bei visueller Erkennung in Instanzen ruhen“ lässt sich jetzt auch bei
  eingeschaltetem Netzwerk-Tracking einstellen, solange keine Netzwerk-Ortserkennung verfügbar ist.
- **Behoben:** Beschädigte Kartenzwischenspeicher werden erkannt und automatisch neu aufgebaut.
- **Behoben:** Pet-Level und Soul-Fortschritt werden immer als zusammengehöriger Stand angezeigt.
- **Behoben:** Soulcrest beendet sich zuverlässiger, ohne dass die Hängererkennung nachläuft.

## 0.11.0 – 2026-10-21

- **Neu:** Mehrere Vorwarnzeiten für Bosse, Events, Resets und eigene Alerts: 2, 5, 10 und 15 Minuten
  frei kombinieren und um eigene Zeiten ergänzen. „Aus“ pausiert die Erinnerung und behält die Auswahl;
  bisherige Einstellungen werden übernommen.
- **Neu:** Die Karte merkt sich Zoom und Ausschnitt für jede Karte einzeln, auch über Neustarts.
- **Geändert:** Daevanion-Overlay: Stat-Nodes haben jetzt eine Fassung in der Farbe des Nodes (grün,
  blau, orange) mit größeren Zahlen; das Kürzel steht unter dem Rahmen, Doppelboni in zwei Spalten.
- **Geändert:** Der Netzwerkschalter heißt jetzt „Netzwerk-Tracking“.

## 0.10.2 – 2026-10-21

- **Neu:** Das Daevanion-Overlay unterstützt jetzt auch das Board **Ariel** in allen acht Klassen,
  einschließlich der PvE- und Boss-Boni und der Doppelboni.

## 0.10.1 – 2026-10-21

- **Behoben:** Das Daevanion-Overlay verschwand, solange levelbare Nodes blau leuchteten. Es bleibt
  jetzt auch während der Animation sichtbar.

## 0.10.0 – 2026-10-21

- **Neu:** Daevanion-Overlay: Ist ein Daevanion-Board im Spiel geöffnet, zeigt Soulcrest auf jedem Node
  direkt, was er bringt: Skill-Nodes mit ihrem Skillbild und „+1“, Stat-Nodes mit Kürzel und genauem
  Bonus (z. B. „Crit +5“). Für Nezekan, Zikel, Vaizel, Triniel und Azphel in allen acht Klassen, auf
  Deutsch und Englisch.
- **Neu:** Das Overlay richtet sich automatisch am Board aus und ist immer durchklickbar. Die Klasse
  erkennt Soulcrest selbst; ein- und ausschalten, Kürzellegende und Vorschau aller Boards gibt es im
  Overlays-Tab.

## 0.9.1 – 2026-10-20

- **Neu:** Tab **Overlays**: alle acht Overlays an einem Ort, mit Einstellungen, Vorschau, Sprache,
  Sichtbarkeit in Aufnahmen und Hotkeys. Die Fachansichten verlinken direkt dorthin.
- **Neu:** Platzierungsmodus: Alle Overlays erscheinen mit Beispieldaten auf dem Spielmonitor (auch ohne
  laufendes Spiel) und lassen sich verschieben und skalieren, wahlweise mit 16-Pixel-Raster und Einrasten.
  Escape übernimmt den letzten Stand.
- **Neu:** „Wann anzeigen“ für alle Overlays gemeinsam: nur wenn AION 2 im Vordergrund ist (Standard)
  oder immer.
- **Neu:** Progressionsmodus: Option „Höchste Mob-Dichte zuerst“. Sie wählt das Pet mit den meisten
  Spawns auf der aktuellen Karte und führt zum dichtesten Farmbereich (200 m Umkreis) statt zum
  nächstgelegenen Spawn.
- **Neu:** Overlay-Themes im Overlays-Tab: Aion2 (stilisiert oder neutral), Obsidian, Nordlicht, Runen
  und Soulcrest legacy. Das Händler-Overlay behält seinen bisherigen Stil.
- **Geändert:** Das Aion2-Theme ist der neue Standard. Wer bisher ausdrücklich das alte Aussehen gewählt
  hat, behält „Soulcrest legacy“.
- **Verbessert:** Verblasste Marker auf der Minimap haben keine Hintergrundflächen mehr und verdecken
  weniger.

## 0.8.3 – 2026-10-20

- **Verbessert:** Nach einer Instanz (z. B. Abyss → Altgard) erkennt Soulcrest die neue Karte schneller:
  Während der Ruhe in Instanzen schaut es alle 2 Sekunden nach und prüft dabei mehrere Karten auf einmal.
- **Verbessert:** „My Routes“ ist kompakter: Wiederholen ist direkt erreichbar, lange Ziellisten zeigen
  die ersten drei Einträge und den Rest zusammengefasst.
- **Behoben:** Die Wiederholen-Einstellung einer Route bleibt jetzt dauerhaft gespeichert.
- **Behoben:** Das Händler-Overlay flackerte bei manchen Shop-Ansichten.

## 0.8.2 – 2026-10-18

- **Entfernt:** Die DPS-Vorschau ist vorerst wieder entfernt.

## 0.8.1 – 2026-10-18

- **Neu:** Tab **DPS** als frühe Vorschau: lokal gespeicherte Kämpfe mit Auswahl nach Lauf, Versuch
  und Teilnehmer, Skill- und Buff-Details und Boss-HP; auch ehemalige Gruppenmitglieder bleiben
  sichtbar. Unbekannte oder unvollständige Werte werden als solche gekennzeichnet, nicht als 0. Die
  Messung ist noch nicht für alle Kämpfe bestätigt.
- **Geändert:** Boss-Erinnerungen verschwinden, sobald ihr Countdown abläuft, auch ohne
  Spawn-Bestätigung.

## 0.8.0 – 2026-10-18

- **Neu:** Timer-Leiste im Spiel: eine schlanke Symbolleiste mit Countdowns für Events und Bosse,
  ohne Hintergrund, Größe 60–200 %, Bereiche wählbar, mit Vorschau im Timer-Menü.
- **Neu:** Boss-Erinnerungen erscheinen als eigene Hinweise, auch wenn das Timer-HUD aus ist, und
  bleiben bis zum bestätigten Spawn stehen. Die Anzeigedauer stellst du einmal zentral ein.
- **Neu:** Der Patchnotes-Tab ist ein durchsuchbarer Versionsleser.
- **Neu:** Das Händler-Overlay zeigt die Pet-Kategorie über dem Namen und die Zahl der Spawnpunkte je
  Farmkarte.
- **Verbessert:** Die Bossliste sortiert bestätigte Spawns zuerst; Bosszeiten bleiben dauerhaft
  erhalten. Fehlende Zeiten der Lower-Reshanta-Bosse werden aus dem Spielkalender ergänzt.
- **Verbessert:** Events in Alerts sind aufklappbar; die Overlay-Bereiche sind wählbar. Die
  Boss-Suche findet Reshanta-Karten auch unter „Abyss“. Neues Symbol für Dimensional Invasion.
- **Verbessert:** Die interaktive Karte lädt kompaktere Daten und zeichnet ihre Symbole flüssiger.
- **Geändert:** „Loot & Pet Progress“ blendet sich aus, solange nichts anzuzeigen ist.
- **Entfernt:** Der Boss-Lootpool im Boss-Rush-Tab.

## 0.7.6 – 2026-10-17

- **Geändert:** Cube Groups zeigen jetzt die echten Spawngruppen der Hidden Cubes statt Gebieten:
  Altgard 59, Verteron 95 Gruppen. Die Linien verbinden die möglichen Positionen derselben Gruppe;
  Spawnfolge, Belegung und Respawnzeiten bleiben unbekannt.

## 0.7.5 – 2026-10-16

- **Neu:** Ebene **Cube Groups** (unter Hidden Cube, getrennt für Karte, Minimap und Weltkarte):
  Auf Altgard und Verteron sind die Hidden Cubes eines Gebiets zu einer farbigen Gruppe verbunden,
  zum bequemen Abfliegen. Jeder Cube gehört zum nächstgelegenen Dorf oder Jagdgebiet; die Linien
  verbinden die echten Spawnpositionen. Das „?“ in der Legende erklärt die Ebene.

## 0.7.4 – 2026-10-16

- **Neu:** Pet-Aufstiege werden im Fortschritts-Overlay gefeiert: Stufe 1 und 2 leuchten 30 Sekunden
  lang direkt in der Fokus- oder „Letzte Beute“-Zeile auf, MAX bekommt eine eigene grüne Erfolgskarte.
  Pets auf MAX erscheinen nicht mehr unter „Letzte Beute“.
- **Behoben:** Das Händler-Overlay blinkte, fiel nach einer Weile aus oder sprang um ein Pixel.

## 0.7.2 – 2026-10-15

- **Verbessert:** Das Overlay auf der geöffneten Weltkarte zieht beim Verschieben und Zoomen spürbar
  direkter nach: frischere Bilder, ein kürzerer Weg bei HDR (im Mittel etwa 12 ms weniger
  Verzögerung, live gemessen).
- **Verbessert:** Das Diagnosepaket schlüsselt die Weltkarten-Verzögerung nach Aufnahme, Zeichnen und
  Anzeige auf.

## 0.7.1 – 2026-10-14

- **Neu:** Die Monstersuche im Map-Tab zeigt jedes Monster einmal, über alle Karten (DE/EN). Die Auswahl
  hebt alle Spawns hervor und zoomt passend; eine kompakte Infokarte zeigt Stufe und Soul-Stand des
  Pets und wechselt auf Wunsch zur Farmkarte.
- **Verbessert:** Die Stufenauswahl der Pets-Ebene ist eindeutiger: Haken bei „Stufen anzeigen“ für
  Stufe 1, Stufe 2 und MAX.
- **Verbessert:** Weniger CPU-Last: Die interaktive Karte zeichnet Spieler und Ziele sparsamer, das
  Overlay auf Minimap und Weltkarte bereitet Bilder ohne Rückstau vor (gemessen etwa 10–20 % weniger
  Last, kürzere Overlay-Verzögerung).

## 0.7.0 – 2026-10-14

- **Neu:** Beim Black Cloud Merchant steht unter jedem Pet-Angebot dein Soul-Stand (z. B. 0/5, MAX) und
  wo das Pet gefarmt wird (Abyss, Altgard, Verteron oder „Special“ ohne Farmort auf diesen Karten).
  MAX ist grün, Special bernsteinfarben, fehlende Pets (0/5) hellgold hervorgehoben. Ein- und
  ausschaltbar unter Optionen → Overlays; das Overlay ist immer durchklickbar.
- **Geändert:** Pets bilden eine gemeinsame Sammlung ohne Trennung nach Server. Der bisherige Stand
  wird übernommen (mit Sicherung vorher), manuelle Korrekturen bleiben.
- **Verbessert:** Souls zählen jetzt auch, wenn Soulcrest mitten im Spiel gestartet wird.

## 0.6.3 – 2026-10-13

- **Behoben:** Im Rift (und bei anderen neuen Server-Verbindungen) stoppte die Soul-Zählung nach etwa
  13 Sekunden. Neue Verbindungen werden jetzt zusätzlich erfasst, ohne die laufende Erfassung
  zurückzusetzen.
- **Verbessert:** Das Diagnosepaket unterscheidet die Ursachen von Erfassungslücken genauer.

## 0.6.2 – 2026-10-13

- **Behoben:** Pet-Stufen und Erkundung wurden nach dem Login manchmal nicht übernommen (weitere
  Login-Varianten, verlorene erste Pakete einer neuen Verbindung). Jetzt live nach mehreren direkten
  Charakterwechseln bestätigt.
- **Behoben:** Die Soul-Zählung konnte zeitweise stoppen; Pet-Aufstiege und der erste Erwerb eines Pets
  werden zuverlässig übernommen, auch auf Maximalstufe.
- **Behoben:** Eine fehlerhafte Teilliste beim Login verwirft nur noch sich selbst; Pets, Quests und
  Kibelisken werden unabhängig voneinander bestätigt.
- **Behoben:** Nach einer Instanz (z. B. Abyss → Altgard) wurde die neue Karte manchmal nicht erkannt.

## 0.6.1 – 2026-10-13

- **Neu:** Abgeschlossene Sealed Dungeons und Strongholds werden aus dem Spiel übernommen: direkt beim
  Abschluss und beim Login aus dem vollständigen Erkundungsstand. Kibelisken werden beim Binden und
  beim Login abgehakt. Erkundung zeigt nur noch „abgeschlossen“ oder „ohne Abschluss“.
- **Behoben:** Die Soul-Zählung stoppte manchmal nach wenigen Souls, wenn das Spiel eine zusätzliche
  Verbindung öffnete. Unbekannte Datenformate halten die Zählung nicht mehr an.
- **Behoben:** Boss-Timer wurden kurz nach dem Empfang als unbestätigt angezeigt; sie bleiben jetzt
  für die ganze Sitzung gültig, bis die Verbindung abbricht.
- **Verbessert:** Das Setup erklärt, dass für Pets und Erkundung einmal die Charakterauswahl (erneuter
  Login) nötig ist, und zeigt je Bereich, ob die Daten schon da sind.

## 0.6.0 – 2026-10-12

- **Neu:** Pet-Stufen, verfügbare Souls und der Erkundungsfortschritt des eingeloggten Charakters
  werden passiv aus dem eigenen Spielverkehr erkannt, ohne Scans. Eine Statuskarte zeigt, ob die
  Erkennung aktiv ist. Bisherige Stände bleiben erhalten (vor der Umstellung wird eine Sicherung
  angelegt), manuelle Korrekturen haben Vorrang.
- **Neu:** Boss-Timer stehen direkt an den Boss-Symbolen der interaktiven Karte.
- **Neu:** Die Kartenlegende hat drei Spalten: Minimap, interaktive Karte und Weltkarte lassen sich
  getrennt einstellen, auch für Orte, NPCs und Monster.
- **Neu:** Sealed-Dungeon-Stationen der Leveling-Routen tragen ihren Namen statt „Stop“.
- **Verbessert:** Boss Rush, Erkundungsfortschritt und Alerts sind übersichtlicher für 16:9
  aufgebaut; die Legende ist kompakter.
- **Entfernt:** Pet-Scan und Exploration-Scan per Texterkennung sowie die Windows-OCR-Sprachpakete im
  Installer. Dungeons und Strongholds werden nicht mehr durch Annäherung oder Betreten abgehakt; der
  Status kommt aus dem Spiel und lässt sich in der Erkundungsübersicht korrigieren.

## 0.5.0 – 2026-10-12

- **Neu:** Tab **Alerts** mit Event-Timern für EU-Server (deutsche Zeit): Shugo Festival, Dimensional
  Invasion, Spacetime Rift (mit Portalphase), Abyss Siege sowie täglicher und wöchentlicher Reset.
  Zeitachse über 12, 24 oder 48 Stunden, laufende und nächste Events, Erinnerungen mit Vorwarnung
  zum Einschalten.
- **Neu:** Eigene Erinnerungen: stündlich, täglich, wöchentlich, alle N Tage, im Minutenabstand oder
  einmalig, je mit Overlay und/oder Ton.
- **Neu:** Boss-Erinnerungen aus den erfassten Boss-Timern, gemeinsam in Alerts und Boss Rush: pro Boss
  Vorwarnung aus, 2, 5, 10, 15 Minuten oder eigene Zeit.
- **Neu:** Töne: kurzer Standard-Doppelton oder eigene MP3-Dateien aus einer gemeinsamen Bibliothek,
  direkt an jeder Event- und Bosskarte wählbar und testbar.
- **Neu:** Overlays für Timer, Kurzbenachrichtigungen (auch bei ausgeblendeter Minimap sichtbar) und
  eine Event-Zeitachse (standardmäßig aus); alle kompakt, frei skalierbar per Ziehgriff, mit Schloss,
  Alt-Freigabe und eigenen Hotkeys.
- **Neu:** Der Karten-Tab zeigt eine kompakte Timer-Liste aller Events und ausgewählten Bosse.
- **Behoben:** Die interaktive Karte sprang nach dem Wegteleportieren wiederholt zum Abyss zurück
  (veraltete Position oder Aufnahme).

## 0.4.7 – 2026-10-11

- **Verbessert:** Der Installer zeigt beide OCR-Sprachen (Englisch und Deutsch) mit ihrem Status;
  bereits installierte Pakete sind als installiert markiert. Deutsch ist optional und hilft, wenn das
  Spiel auf Deutsch läuft oder du die Spielsprache wechselst. Maßgeblich ist die Sprache des Spiels,
  nicht die der Soulcrest-Oberfläche.

## 0.4.6 – 2026-10-11

- **Neu:** Das Loot-Overlay zeigt unter dem Monsternamen klein und grau den Soul-Namen
  (`Soul: <Pet>`). Das Fokus-Pet erscheint nicht mehr zusätzlich unter „Letzte Beute“; seine Drops der
  letzten Minute stehen als +N auf der Fokuskarte.
- **Neu:** Der Pet-Scan läuft direkt im Setup; markierte Ergebnisse werden bei „Weiter“ übernommen.
- **Verbessert:** Der Exploration-Scan erkennt Karte und geöffnete Liste selbst und folgt ihnen, auch
  beim Wechsel. Der Start sitzt jetzt oben im Exploration-Tab.
- **Verbessert:** Gekürzte Ortsnamen („Versiegelter Dunge…“, lange Dungeon-Namen) werden mit den
  vollständigen deutschen und englischen Namen abgeglichen; Kartenbeschriftungen daneben stören nicht
  mehr. Der Abschluss-Haken wird auch bei sehr kleiner und sehr großer UI-Skalierung gefunden.
- **Behoben:** Ein Overlay ließ sich manchmal trotz losgelassener Alt-Taste weiter verschieben. Das
  Schloss zeigt jetzt den echten Zustand und sperrt sofort.

## 0.4.5 – 2026-10-11

- **Neu:** Erkundungsübersicht im Exploration-Tab: Zähler je Kategorie, Suche (DE/EN), Filter
  Offen/Erledigt/Gesperrt und eine Checkliste. Sie folgt automatisch der Karte, auf der du im Spiel
  bist (lässt sich fixieren), schlägt den nächsten offenen Ort vor und nennt die Voraussetzungen
  gesperrter Dungeons.
- **Verbessert:** Der Progressionsmodus folgt einem Kartenwechsel im Spiel (z. B. Altgard → Abyss),
  auch wenn „Spieler verfolgen“ aus ist.
- **Verbessert:** Pet-Scan-Tab überarbeitet: drei Schritte, Suche und Filter, Prüfbedürftiges zuerst,
  Korrekturen direkt in der Zeile, Sammelübernahme markierter Ergebnisse.
- **Behoben:** Die Pet-Liste zeigt nur noch Pets mit belegter Soul-ID; doppelte Einträge (z. B.
  Modified Karnif) sind zusammengeführt, „Gatekeeper“ ist kein eigenes Pet mehr. Abgeschnittene Namen
  wie „Aultrossts Doppelga…“ werden richtig zugeordnet.
- **Geändert:** Spieler-Tracking und „Spieler verfolgen“ sind neu standardmäßig an; gespeicherte
  Einstellungen bleiben.

## 0.4.4 – 2026-10-11

- **Behoben:** Beim Wechsel der Karte konnte die interaktive Karte mit einem Fehler hängen bleiben
  (veraltete Zielmarker der vorherigen Karte).
- **Verbessert:** Der Exploration-Scan liest auch ausgegraute, kontrastarme Erkundungslisten und
  Ultrawide-Auflösungen zuverlässiger; ein Abschluss braucht weiterhin zwei eindeutige Lesungen.
- **Verbessert:** Neue Scan-Kachel mit drei Schritten, Start/Stopp und getrennten Zählern; die
  Scanhilfe im Spiel hat eine klare Legende (Grün = Name zweimal gelesen).

## 0.4.3 – 2026-10-11

- **Neu:** Sealed Dungeons werden beim Betreten abgehakt, auch ohne Progressionsmodus: Nach einer Position
  im Abschlussradius muss eine Instanz folgen. Vorbeigehen hakt nichts mehr ab. Strongholds genügt
  weiterhin der Radius.
- **Verbessert:** Zielmarker für Sealed Dungeons, Kibelisken und Quests sehen jetzt aus wie die der
  Ressourcen: Ring um das Spielsymbol, Linie ab dem Rand, Icon und Entfernung neben dem Ring. Das gilt
  in allen Modi (markiert, Progression, Leveling-Routen).

## 0.4.2 – 2026-10-10

- **Behoben:** Im Ring markierter Ressourcen und Versteckter Kuben liegt kein zusätzliches Symbol mehr,
  wenn die Kategorie auf der Karte eingeblendet ist; das Spielsymbol bleibt sichtbar.

## 0.4.1 – 2026-10-10

- **Verbessert:** Zielmarker im Spiel verdecken die Ressourcensymbole nicht mehr: Der Ring liegt um das
  Symbol, Pfeile beginnen und enden am Ringrand. Markierte Ressourcen und Versteckte Kuben zeigen ihr
  Icon neben dem Ring statt des Namens, die Entfernung bleibt.

## 0.4.0 – 2026-10-10

- **Neu:** Leveling-Routen für Asmodier und Elyos, Episode 2–6 mit Levelbereich. Die Route führt Station
  für Station über die Karte, merkt sich den Fortschritt pro Charakter und setzt nach einem Neustart fort.
  Monsteraufgaben zweigen zum nächsten passenden Spawn ab.
- **Neu:** Leveling-Overlay im Spiel mit aktueller Station, Fortschritt und Weiter/Zurück.
- **Neu:** Der Progressionsmodus verfolgt mehrere Ziele zugleich: Pets, Sealed Dungeons und Strongholds
  beliebig kombiniert, das nächstgelegene offene Ziel zuerst. „Am nächsten an der vollen Stufe“ ist jetzt
  eine Option unter „Ziel je Pet“.
- **Neu:** Overlays lassen sich direkt bedienen: Schloss-Knopf zum Entsperren, Verschieben mit gehaltener
  Alt-Taste. Alle Overlay-Tastenkürzel sind unter Optionen frei belegbar.
- **Neu:** Bei geöffnetem ESC-Menü blendet Soulcrest seine Overlays aus.
- **Neu:** Routen werden im eigenen Format `.soulroute` gespeichert. Vorhandene Routen übernimmt
  Soulcrest automatisch, die Leveling-Routen kommen dazu.
- **Verbessert:** Das Pet-Scan-Overlay markiert alle Karten, die die Ergebnisliste zum Prüfen anzeigt,
  auch unsicher erkannte und unbekannte gesperrte Pets.
- **Verbessert:** Aufgeräumter Karten-Tab: Erklärungen stehen hinter einem ?-Symbol.
- **Verbessert:** Boss-Rush-Loot nach Seltenheit sortiert und farbig markiert.
- **Behoben:** Das Pet-Scan-Overlay erscheint auch, wenn das Pet-Fenster die Minimap verdeckt.

## 0.2.8 – 2026-10-09

- **Neu:** Boss Rush mit eigenem Tab. Soulcrest liest die Field-Boss-Spawns passiv aus dem Spielverkehr
  (Bossliste im Spiel einmal öffnen), führt dich in Spawn-Reihenfolge von Boss zu Boss und hängt den
  nächsten Spawn als gestrichelte Linie an. Auch rechts im Kartenmenü schaltbar.
- **Neu:** Boss-Overlay mit den nächsten Spawns, Porträts und Countdowns; Anzahl, Größe und Position
  einstellbar.
- **Neu:** Alerts für ausgewählte Bosse mit Vorwarnzeit, Anzeigedauer und optionalem Signalton.
- **Neu:** Aufklappbare Lootpools pro Boss mit Item-Icons, deutschen/englischen Namen und Suche.
- **Verbessert:** Boss-Timer bleiben nach Teleports und einem Neustart erhalten.
- **Verbessert:** Modernisierte Ingame-Overlays. Loot-Kacheln zeigen ihre Seltenheit in Farbe; der
  Loot-Fokus folgt dem markierten Pet oder dem Fortschrittsziel.
- **Verbessert:** Strg+Alt+L entsperrt Loot- und Boss-Overlay gemeinsam zum Verschieben.
- **Verbessert:** Das Setup zeigt während der Installation des Windows-OCR-Pakets einen laufenden Balken
  und den Hinweis, dass das einige Minuten dauern kann.
- **Geändert:** Der Tab „Pet-Fenster“ heißt jetzt „Pet Scan“.

## 0.2.7 – 2026-10-08

- **Neu:** Alle Fortschrittsscans (Pets, Kibelisken, Dungeons, Garnisonen) funktionieren mit dem deutschen
  Spielclient.
- **Neu:** Der Pet-Scan zeigt auf jeder Karte den erkannten Namen und Wert. Mit Strg + Linksklick auf den
  Wert lässt er sich direkt korrigieren.
- **Neu:** Die Sprache der Namen im Loot-Overlay ist unter Optionen wählbar; standardmäßig folgt sie dem
  Spiel.
- **Verbessert:** Der Pet-Scan liest Kartenwerte deutlich sicherer: Er prüft die Stufe im Kreis gegen den
  Zähler, verwirft Bilder während des Scrollens und markiert widersprüchliche Werte zum Prüfen.
- **Verbessert:** Der Pet-Scan startet schneller.
- **Verbessert:** Zeigt die Dungeon- oder Garnisonenliste 100 %, übernimmt der Erkundungs-Scan die ganze
  Kategorie; Durchscrollen entfällt. Unter 100 % zählen nur Orte mit Abschluss-Haken in derselben Zeile.
- **Verbessert:** Gekürzte deutsche Ortsnamen und „Eingang zu …“ werden sicherer zugeordnet.
- **Geändert:** Neues Soulcrest-Logo.

## 0.2.5 – 2026-10-07

- **Neu:** Das Setup führt jetzt auch zum Pet-Scan und fragt nach der Erkundung: Wer mit dem Charakter
  schon alles erkundet hat, hakt mit einem Klick alles ab (mit Rückgängig), sonst geht es zum
  Erkundungs-Scan.
- **Geändert:** Das Loot-Tracker-Overlay zeigt den Namen des Monsters, das du jagst, statt des Pet-Namens
  (z. B. „Soft Breeze Spirit“ statt „Lesser Wind Spirit“).
- **Verbessert:** Kleine, stark gezoomte Minimaps über strukturarmem Gelände (Küste, Sand) verlieren die
  Position nicht mehr ständig; solange die Karte sauber mitläuft, hält Soulcrest die Position.
- **Verbessert:** Die Hinweise der Scans sind in Aufnahmen sichtbar, wenn „Overlays in Aufnahmen sichtbar“
  gewählt ist.
- **Behoben:** Der Pet-Scan verwechselt Pets nicht mehr, die sich nur in der Farbe unterscheiden (z. B.
  Stone Spirit und Odyle Stone Spirit).
- **Behoben:** Ein unbekanntes Pet zeigte beim Scan kurz den Stand eines anderen.
- **Verbessert:** Das Diagnosepaket enthält das letzte Bild des Pet-Scans.

## 0.2.4 – 2026-10-07

- **Neu:** Gefahrenzonen der Wachen. Die Wachen an den Kibelisken töten Spieler der anderen Fraktion mit
  einem Schlag; Soulcrest zeigt um sie eine rote, halbtransparente Zone auf Minimap und Vollbildkarte.
  Im Setup wählst du deine Fraktion: Elyos sehen die Zonen auf Altgard, Asmodier auf Verteron. Radius
  (Standard 40 m) und Karten unter Optionen → Overlays.
- **Neu:** Das Pet-Overlay lässt sich im entsperrten Modus (Strg+Alt+L) an jeder Ecke größer oder kleiner
  ziehen; alles skaliert mit.
- **Geändert:** Markierte Pets tragen im Overlay den Namen des Monsters, das ihre Soul fallen lässt
  (z. B. „Drana Mutant“ statt „Drana Mutant Brute“).

## 0.2.3 – 2026-10-07

- **Verbessert:** Das Overlay folgt der Karte jetzt fast ohne Verzögerung (rund 20 statt 150 ms). Die
  Aufnahme hat neue Spielbilder oft verworfen und mit veralteten gearbeitet, besonders mit HDR.
- **Verbessert:** Beim Ziehen der Weltkarte bleibt das Overlay auf der Karte; vorher hielten Liste und
  Leisten am Rand es fest.
- **Verbessert:** Wechsel zwischen Weltkarte und kleiner Karte in rund 0,2 statt 2–3 Sekunden.
- **Verbessert:** Deutlich weniger Prozessorlast bei offener Weltkarte.

## 0.2.2 – 2026-10-07

- **Verbessert:** Bei offener Weltkarte folgt das Ingame-Overlay der Karte flüssig und hängt beim
  Verschieben nicht mehr nach.

## 0.2.1 – 2026-10-07

- **Neu:** Patchnotes gibt es jetzt auch auf Englisch; Soulcrest zeigt sie in der Sprache der Oberfläche,
  auch im Update-Hinweis.
- **Verbessert:** Die Windows-Titelleiste ist dunkel wie Soulcrest; unter Windows 11 hat sie die Farbe der
  Kopfzeile.
- **Behoben:** Wird eine laufende Route mit „Alle entfernen“ beendet oder durch eine andere ersetzt, ist
  ihr Wiederholen danach aus.
- **Behoben:** Ein Fehler, der sich ständig wiederholt, öffnet nicht mehr Hinweisfenster über
  Hinweisfenster: höchstens eines gleichzeitig, derselbe Fehler höchstens einmal pro Minute.

## 0.2.0 – 2026-10-07

- **Neu:** Soulcrest 0.2 – der erste öffentliche Abschnitt: frei auf GitHub, mit Updates aus der App heraus.
- **Neu:** Der Progressionsmodus beachtet die Freischalt-Reihenfolge der Sealed Dungeons. Auf Verteron
  öffnen Distorted Cave → Fissure Cave → Rift Cave nacheinander, auf Altgard Lost Ruin → Twisted Pit →
  Rift Fissure; die Ruinen der Uralten Stadt Roah erst nach allen anderen Sealed Dungeons der Karte.
  Gesperrte Dungeons überspringt er, das Popup auf der Karte nennt, was noch fehlt.
- **Geändert:** Rechtsklick auf Kibelisks, Sealed Dungeons und Strongholds markiert sie wieder als Ziel.
  Binden bzw. Abhaken steht jetzt im Popup beim Linksklick.
- **Behoben:** Der Schalter „Gebiete“ ist weg; er blendete nichts ein. Die Gebietsnamen schaltet die
  Ebene „Regions“.

## 0.1.31 – 2026-10-07

- **Neu:** HDR-Aufnahme. Läuft der Monitor in HDR, nimmt Soulcrest das Spiel in voller HDR-Tiefe auf und
  rechnet es für die Erkennung um. Bisher kam das Bild auf HDR-Monitoren stark überstrahlt an.
- **Neu:** Routen haben eine einheitliche Farbe. Ein Klick auf den Farbpunkt vor der Route wählt sie;
  Stopps und Linien auf der Karte und im Spiel nehmen sie an. Neue Routen bekommen die nächste freie Farbe.
- **Verbessert:** Updates laden nur noch rund 100 MB statt 400 MB, solange sich die Kartendaten nicht
  ändern. Das Update läuft still durch und startet Soulcrest danach neu.
- **Verbessert:** Markierte Ziele und Routenstopps zeigen ihr Symbol, auch wenn ihre Ebene ausgeblendet ist.
- **Geändert:** Kartenseite aufgeräumt: Tracking sowie Ziele und Routen stehen je in einer Kachel,
  Erklärungen hinter „Hilfe“. Die Pet-Filter (Stufen, nur kartenexklusive) stehen jetzt unter „Pets“ in
  den Ebenen, „Gebiete“ in deren Kopfzeile.

## 0.1.30 – 2026-10-07

- **Neu:** Soulcrest ist öffentlich auf GitHub: <https://github.com/NewAge-BD/Soulcrest>. Jede Version
  gibt es dort als Windows-Installer unter „Releases“.
- **Neu:** Update-Suche. Nach dem Start sucht Soulcrest nach einer neuen Version und zeigt ihre
  Patchnotes; „Jetzt aktualisieren“ lädt den Installer, prüft ihn und startet ihn. Unter Info gibt es
  „Nach Updates suchen“ und den Schalter für die Suche beim Start.
- **Neu:** Unter Info lässt sich Soulcrest freiwillig über Ko-fi unterstützen.
- **Geändert:** Lizenz PolyForm Noncommercial 1.0.0 (nicht kommerziell nutzbar).
- **Entfernt:** Das alte Loot-Tracking per Texterkennung; Souls zählt nur noch der Netzwerk-Tracker.

## 0.1.29 – 2026-10-07

- **Neu:** Reiter „Patchnotes“ zeigt, was sich in jeder Version geändert hat. Die installierte Version
  ist markiert.

## 0.1.28 – 2026-10-07

- **Verbessert:** Das Spieler-Tracking braucht etwa halb so viel CPU. Unterwegs sank die Last im Test
  von 1,4 auf 0,7 Prozessorkerne, im Stand auf unter einen halben.
- **Behoben:** Eine kurz gesperrte Datei (Virenscanner, Sicherung) lässt Soulcrest nicht mehr abstürzen
  und beendet nicht mehr das Tracking. Soulcrest versucht das Speichern erneut und zählt die Souls
  weiter.
- **Behoben:** Der Loot-Tracker verpasst keine Souls mehr, wenn sich gerade eine Netzwerkverbindung
  öffnet.
- **Neu:** Unerwartete Fehler landen in `logs\errors.log` (auch im Diagnosepaket). Bei Fehlern im
  Fenster erscheint ein Hinweis, Soulcrest läuft weiter.

## 0.1.27 – 2026-10-07

- **Neu:** Das Setup prüft Npcap und das Windows-OCR-Paket. Fehlt etwas, lädt es auf Wunsch den
  offiziellen Npcap-Installer von npcap.com und lässt Windows das OCR-Paket installieren. Dafür fragt
  Windows einmal nach Administratorrechten.
- **Neu:** Kurzes Einrichtungs-Tutorial beim ersten Start. Ein Video zeigt, wie man im Spiel die Minimap
  vergrößert, daneben markierst du den Minimap-Bereich. „▶ Setup starten“ oben in den Optionen öffnet
  es jederzeit wieder.
- **Geändert:** Der Questrunner ist vorerst wieder entfernt und archiviert. Er kommt überarbeitet
  zurück.

## 0.1.26 – 2026-10-07

- **Neu:** Questrunner-Modus. Auf der Karte unter Tracking mit ▶ starten. Karte und Overlay im Spiel
  zeigen die letzten 3 Schritte grau und die nächsten 3 farbig, dazu eine Linie mit Entfernung zum
  aktuellen Schritt. Die Anzahl stellst du im Popup eines Schritts mit − / + ein.
- **Neu:** Die Questlines liegen bei. Asmodier und Elyos haben je alle Episodenquests von Stufe 1–45,
  mit Questtexten und Zielorten aus den gaming.tools-Questdaten. Der Fortschritt wird je Charakter
  gespeichert.
- **Neu:** Questlog lesen. Unter Optionen → Questlog den Quest-Tracker markieren. Im Questrunner-Modus
  springt Soulcrest dann von selbst zum offenen Ziel. Bisher nur der englische Client.
- **Neu:** Weiter geht es auch bei Ankunft am Ziel (10 m) oder mit ◀ / ▶ in der Kachel.
- **Neu:** Der Questrunner-Reiter zeigt rechts eine Karte. Ein Klick auf die Schrittnummer zeigt den
  Schritt dort.
- **Verbessert:** Das Spieler-Tracking ruht in Instanzen ohne Karte und spart so Leistung. Nach dem
  Ladebildschirm läuft es von selbst wieder an.
- **Behoben:** Soulcrest löscht veraltete Karten-Vorlagen fürs Tracking. Bisher sammelten sich bei
  jedem Kartendaten-Update mehrere hundert MB an. Die Protokolle bleiben unter 2 MB.

## 0.1.25 – 2026-10-06

- **Neu:** Questrunner, Teil 1 (Aufnahme und Editor). Im Reiter „Questrunner“ eine Aufnahme starten
  und an jedem Questziel mit Strg+Alt+S oder „Schritt setzen“ im Panel über dem Spiel einen Schritt
  setzen. Danach öffnet sich ein Notizfeld.
- **Neu:** Schritte rasten auf einen Kibelisk, Sealed Dungeon, Stronghold oder NPC im Umkreis von
  12 m ein. „Ohne Ort“ ist für Instanzen und Kartenwechsel, „Neue Quest“ beginnt die nächste Quest.
- **Neu:** Der Editor benennt um, sortiert, ordnet zu und löscht. Auf der Karte stehen nummerierte
  Schritte und die gelaufene Wegspur; Schritte lassen sich dort mit der Maus verschieben.
- **Behoben:** Der Kibelisk „Shulak Street Stall“ wird erkannt (bisher zweimal als „Steel Hammer
  Temporary Trading Post“ geführt). Bisherige Haken ziehen mit um.
- **Verbessert:** Der Kibelisk-Scan verzeiht einen falsch gelesenen Buchstaben, z. B. „Idun's Lake“.
- **Geändert:** Die Kartendaten stammen mit Erlaubnis von aion2.gaming.tools; Karte und Info
  verlinken die Seite jetzt.

## 0.1.24 – 2026-10-06

- **Neu:** Kartenseite neu aufgeteilt. Ebenen und Suche links, Tracking, Wegführung, Ziele, Routen und
  Anzeige rechts; beide Seiten lassen sich am Kartenrand einklappen.
- **Neu:** Kibelisks auf der Karte: gebundene in Farbe, nicht gebundene grau, ohne grünen Haken.
- **Verbessert:** Wechselt das Tracking die Karte (Teleport), bleibt die Zoomstufe erhalten und die
  Karte zentriert auf dich.
- **Behoben:** Bei vielen Suchtreffern wurde die Trefferliste zu Punkten zusammengequetscht.
- **Behoben:** Ein Klick auf einen Monster-Treffer blendete die ganze Monster-Ebene dauerhaft ein.
  Jetzt werden nur die Fundorte dieses Namens hervorgehoben, „Aufheben“ nimmt sie wieder weg.

## 0.1.23 – 2026-10-06

- **Neu:** Reiter „Scan Exploration Progress“: Sealed Dungeons, Strongholds und Kibelisks werden in
  einem Scan eingelesen. Soulcrest erkennt selbst, welche Liste auf der Weltkarte offen ist. Er
  ersetzt die drei Einzelreiter.
- **Neu:** Kibelisk-Liste scannen. „Bind Complete“ hakt ab, „Binding Incomplete“ nimmt den Haken zurück,
  nicht entdeckte Kibelisks gelten als nicht gebunden. Gekürzte Namen werden zugeordnet.
- **Behoben:** Unter der Kartensuche erschien statt der Treffer ein Kasten mit Punkten. Das war die
  Vorschlagsliste des eingebauten Browsers; sie ist jetzt abgeschaltet.

## 0.1.22 – 2026-10-06

- **Neu:** Entfernung in Spielmetern an Rechtsklick- und Progressionszielen im Overlay, z. B. „Ninir ·
  258 m“. Im Spiel geprüft: 259/997/907 m gegen 258/998/906 m.
- **Neu:** Soulcrest erkennt nach jedem Ladebildschirm den eingeloggten Charakter und wechselt das
  Erkundungsprofil von selbst. Unbekannte Charaktere bekommen ein Profil; ein unbenanntes
  „Character 1“ wird dabei übernommen, die Haken bleiben (bei laufendem Loot-Tracking).
- **Neu:** Kibelisks werden je Charakter abgehakt, sobald du sie erreichst. Der Haken ist auf der Karte
  sichtbar und per Rechtsklick rücknehmbar.

## 0.1.21 – 2026-10-06

- **Neu:** Markierst du ein Pet (Rechtsklick oder Progressionsmodus), zeigen Karte und Ingame-Karte
  alle seine Spawn-Stellen und die Monster, die seine Soul droppen, als rote Punkte. Das gilt auch,
  wenn Filter das Pet sonst ausblenden.
- **Geändert:** Per Rechtsklick markierte Pets verschwinden nicht mehr bei Ankunft, sondern erst, wenn
  das Pet die nächste Stufe erreicht. Pets in gespeicherten Routen verschwinden weiter bei Ankunft.
- **Neu:** Pet-Ziele im Overlay zeigen den Soul-Stand der Stufe, z. B. „Superior Water Spirit 24/25“.
- **Behoben:** Zielnamen im Overlay werden am Kartenrand nicht mehr abgeschnitten.

## 0.1.20 – 2026-10-05

- **Neu:** Ressourcen-Unterarten in der Legende, z. B. Edelsteine → Saphir, Diamant, Rubin. Jede mit
  eigenem Icon und einzeln ein- und ausblendbar.
- **Neu:** Auf der Karte und der Ingame-Karte zeigt jede Ressource das Icon ihrer Sorte, Diamanten und
  Rubine sind also direkt unterscheidbar.
- **Verbessert:** Die Auswahl der Unterarten wird je Karte gespeichert; die Suche blendet eine
  ausgeblendete Unterart automatisch ein.

## 0.1.19 – 2026-10-05

- **Neu:** Routen speichern. Eine mit Shift+Rechtsklick markierte Folge (ab 2 Zielen) speicherst du in
  der neuen Kachel „Routen“ unter einem eigenen Namen.
- **Neu:** ▶ ruft eine gespeicherte Route jederzeit wieder auf und ersetzt die markierten Ziele.
- **Neu:** Wiederholmodus ⟳ je Route: Nach dem letzten Ziel beginnt die Runde von vorn, auch nach
  einem Neustart von Soulcrest.
- **Geändert:** Erreichst du in einer Folge ein späteres Ziel, verschwinden übersprungene Ziele davor
  gleich mit.

## 0.1.18 – 2026-10-05

- **Verbessert:** Das Spieler-Tracking braucht pro Suche etwa 60 statt 300 ms.
  - Ob die Weltkarte offen ist, erkennt Soulcrest am Symbol oben links (Idee aus der Community 😉).
  - Die Minimap wird beim Mitverfolgen verkleinert gelesen.
- **Verbessert:** Stehst du still, sucht das Tracking seltener, bis alle 2 s. Bei Bewegung sofort wieder
  normal.
- **Neu:** Der Diagnosebericht zeigt den aktuellen Suchtakt.

## 0.1.17 – 2026-10-05

- **Neu:** Mit Rechtsklick oder Shift+Rechtsklick gesetzte Ziele verschwinden, sobald du sie erreichst,
  wie im Fortschrittsmodus. Bei einer Kette führt die Linie danach vom Spieler zum nächsten Ziel.
- **Neu:** Den Abschlussradius kannst du jetzt auch bei „Ziele“ einstellen; 0 schaltet das
  Verschwinden ab.
- **Geändert:** Standard-Abschlussradius 15 statt 40 Kartenpixel. Ein unveränderter alter Wert wird
  automatisch umgestellt.

## 0.1.16 – 2026-10-05

- **Behoben:** Soulcrest hing sekundenlang, teils über 40 s, solange im Spiel die große Weltkarte offen
  war. Das Ingame-Overlay wird jetzt im Hintergrund gezeichnet und ist rund 7-mal schneller.
- **Verbessert:** Pet- und Ressourcensymbole auf der Ingame-Karte werden einmal vorgezeichnet und
  danach nur noch kopiert.

## 0.1.15 – 2026-10-05

- **Neu:** Fortschrittsmodus „Pets · Closest to completion“ führt zum Pet, dem die wenigsten Souls zur
  vollen Stufe fehlen. Bei Gleichstand gewinnt das nähere.
- **Neu:** Ressourcen und Versteckte Kuben, die in der Kartenlegende eingeblendet sind, erscheinen auch
  auf Minimap und Vollbildkarte.
- **Neu:** Rechtsklick auf Sealed Dungeon oder Garnison: Ziel setzen oder entfernen, Haken setzen oder
  entfernen.
- **Neu:** Hänger-Wächter im Diagnosebericht (Abschnitt „Hänger“).
- **Verbessert:** Ressourcen, Kuben und Orte zeigen ihr Legenden-Icon und wachsen beim Zoomen wie die
  Pet-Symbole.
- **Verbessert:** Die Legendenauswahl wird pro Karte gespeichert.
- **Verbessert:** Exploration-Tabs reagieren schneller.

## 0.1.14 – 2026-10-04

- **Neu:** Eigenes Programmsymbol.

## 0.1.13 – 2026-10-04

- **Behoben:** Der gelbe Windows-Aufnahmerand lässt sich wieder abschalten, wie in Grindcrest.
