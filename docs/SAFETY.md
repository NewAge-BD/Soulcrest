# Technische Sicherheitsgrenze

Aion 2 hat einen Anti-Cheat. Soulcrest bleibt deshalb strikt **passiv** und verhält sich wie
eine Bildschirmaufnahme- plus Overlay-Software.

## Soulcrest darf

- Pixel des Aion-2-Fensters per Windows Graphics Capture lesen (Fenster per HWND und
  Prozess-ID gebunden; kein Rückfall auf eine Desktopaufnahme ohne ausdrückliche Wahl);
- Fenstergeometrie, Monitor, DPI, Vordergrund- und Minimierungsstatus des Spielfensters lesen;
- eigene transparente Fenster zeichnen und globale Hotkeys für **eigene** Funktionen registrieren;
- den Zustand von **Alt und Shift** beobachten, damit sich die Overlays mit gehaltener Taste bedienen
  lassen (Overlay-Interaktion, 2026-10-10): per Raw Input an das eigene Fenster und einen Low-Level-
  Tastaturhook (`WH_KEYBOARD_LL`) im eigenen Prozess. Jede Eingabe wird unverändert weitergereicht,
  nichts wird abgefangen, gesendet oder protokolliert; andere Tasten werden nicht ausgewertet;
- lokale Dateien im eigenen Datenordner `%LOCALAPPDATA%\Soulcrest\` und im App-Verzeichnis
  lesen und schreiben.

## Soulcrest darf nie

- Spielprozess-Speicher lesen oder schreiben, Module laden/injizieren, DLLs in das Spiel bringen;
- Netzwerkverkehr des Spiels verändern oder senden; mitschneiden nur in der Ausnahme unten;
- Tastatur- oder Mauseingaben an das Spiel senden oder Spielaktionen automatisieren;
- Spieldateien verändern;
- zur Laufzeit Daten von Drittseiten abrufen. Keine Telemetrie. Einzige Ausnahme ist die Update-Suche
  (Nutzerentscheidung 2026-10-07): nach dem Start (abschaltbar unter Info) und auf Knopfdruck eine Anfrage
  an die Releases von `NewAge-BD/Soulcrest` auf GitHub; der Installer wird nur auf ausdrücklichen Klick
  geladen, gegen die veröffentlichte SHA-256 geprüft und dann gestartet. Gesendet wird nur die Anfrage.
  Der einmalige Kartendatenimport ist ein Entwicklerskript, kein App-Pfad;
- bisheriger Kartenanbieter abrufen, scrapen oder deren Token nachbauen (siehe `MAP_DATA.md`).


## Ausnahme: passiver Netzwerk-Mitschnitt (Nutzerentscheidungen 2026-10-03/12)

Der Nutzer hat entschieden, dass das Loot-Tracking den eigenen Spielverkehr als Alternative zur OCR
auswerten darf. Dafür gilt:

- **standardmäßig aktiv auf ausdrücklichen Nutzerwunsch vom 2026-10-04**, dauerhaft ausschaltbar; nur per Npcap/`dumpcap`, nur **lesend**;
- nur der Verkehr des Spielprozesses (Filter auf dessen TCP-Gegenstellen und UDP-Ports), kein
  sonstiger Rechnerverkehr;
- **keine Entschlüsselung**, kein Auslesen von Schlüsseln aus Client, Speicher oder Dateien, kein
  Senden oder Verändern von Paketen. Ist der Verkehr verschlüsselt, ist dieser Weg zu Ende;
- Mitschnitte liegen nur lokal unter `%LOCALAPPDATA%\Soulcrest\captures`.
- **Aufenthaltskontext (Nutzerauftrag 2026-10-21):** Dieselbe gemeinsame Quelle liest aus dem
  vollständig geprüften eigenen Welteintritt ausschließlich die zusätzliche Kartenkennung.
  Eigene passende Dungeon-Zustände können den Inhalt und den Modus verfeinern. Keine
  Netzwerk-Spielerkoordinaten und keine fremden Spieler auswerten. Der Kontext bleibt flüchtig;
  Verbindungslücken und Welteintrittsgrenzen entwerten ihn. Der gemeinsame Schalter heißt
  „Netzwerk-Tracking“; Ausschalten beendet weiterhin die gesamte gemeinsame Erfassung.
- **Charakter- und Fortschrittserkennung (Nutzerauftrag 2026-10-12):** Derselbe Serverstrom
  liefert die eigene Charakter-ID, Server-ID, den eigenen Namen und den Entity-Key beim Welteintritt.
  Nur dieser bestätigte Verbindungs-/Charakterkontext darf Quest-/Teleportfortschritt zuordnen.
  Nutzerentscheidung 2026-10-13: Petdaten bilden eine gemeinsame Sammlung ohne Servertracking;
  eigene Pet-Vollstände, Gewinne, Verbrauch und absolute Aufstiegsergebnisse dürfen direkt aus
  den exakt nativ zugeordneten Spielverbindungen übernommen werden, auch vor Own. 9009 ist ein
  Broadcast und benötigt weiterhin den bestätigten eigenen Entity-Key. Exploration gilt pro
  Charakter. Die bisherigen Fortschrittsparser werten andere Spieler, deren Namen und
  Positionen nicht aus. Die Parser
  überspringen fremde/ungenutzte Felder der eigenen Welteintrittsnachricht ohne sie zu speichern.
  Echte Charakternamen kommen nicht ins Repository. Die bisherigen OCR-Scans entfallen.
  Formatfehler und echte Transportlücken entwerten die betroffene Bestätigung; gespeicherte Stände
  bleiben erhalten. Ladebildschirme entwerten die Charakterzuordnung, nicht die globale Pet-Liste.
- **Boss Rush Mode (Nutzerauftrag 2026-10-09):** Derselbe passive Serverstrom liefert die
  Boss-Spawn-IDs, Status und Zeitstempel (`0191`) sowie die Serverzeit (`0036`). Optionale
  Zusatzblöcke werden übersprungen; es werden keine Spielerpositionen ausgewertet. Keine
  zusätzlichen Anfragen an das Spiel, keine zweite Erfassung, keine Entschlüsselung.
  Auf Nutzerwunsch bleibt die letzte Bossliste lokal in `cache/boss-timers.json` erhalten;
  keine IPs/Verbindungsdaten. Nach Loads/Neustart ist sie bis zur frischen Liste vorläufig.
- **Lifeskill-Sammelbeute (Nutzerauftrag 2026-10-25):** Dieselbe Quelle zählt nur
  vollständige Primärbeute mit passendem eigenen Actor-/Targetbeleg in derselben
  frischen Charakter-/Transportgeneration. Fremde Actorbelege werden verworfen,
  keine fremden Aktionen gespeichert. Instanz-/Spawnkeys bleiben flüchtige
  RAM-Belege; Sessionjournale enthalten nur lokale Profilzuordnung, Items und
  bestätigte Minimap-Positionen. Kein Inventarecho wird zusätzlich gezählt,
  kein Netzwerk-Spielerpositionsparser. Nutzerpräzisierung 2026-10-25: bestätigte
  erfolgreiche und fehlgeschlagene Versuche erzeugen mit frischer Minimap-Position
  eine Sammelstelle. Die nächste passende Katalog-Node derselben Karte innerhalb
  15 Spielmetern ersetzt diese Position; exakte Abstandsgleichstände behalten
  die Sammelposition. Diese ausdrücklich gewünschte Nähezuordnung ist kein
  zusätzlicher Netzwerk-Identitätsbeleg. Original- und Zielposition sowie lokale
  Katalogkennung werden zusammen eingefroren, native Objektkeys bleiben flüchtig.
  Vollständige eigene Abschlüsse mit validierter Nullbeute dürfen separat als
  beendete Versuche ohne Beute zählen; keine Deutung opaker Ergebnisbytes oder
  geratene Abbruchursache. Der bestätigte eigene Instanzkontext wird am Abschluss
  eingefroren; solche Versuche bleiben aus der Lifeskill-Erfassung ausgeschlossen.

### Eigener flüchtiger PvP-Killfeed (Nutzerauftrag 2026-10-26)

Der Nutzer beauftragt einen abschaltbaren Killfeed unter der Punkte-Stufe.
Verarbeitet werden ausschließlich separat belegte eigene PvP-Kills, Assists und Tode,
optional der zugehörige Gegnername und die Rangpunktänderung. Keine fremden
Kämpfe, Gruppenanalyse oder Deutung von AP-/Rangdifferenzen als Kill/Assist.
Assists benötigen einen expliziten eigenen Ergebnisbeleg; einzelne Treffer
oder Gruppenzugehörigkeit genügen nicht. Alle drei Arten sind einzeln
abschaltbar; abgewählte Einträge werden entfernt und nicht nachträglich gezeigt.
Die anfängliche Begrenzung auf fünf Einträge für zehn Sekunden ist durch die spätere
Entscheidung unten ersetzt. Gegnernamen und native Identitäten bleiben ausschließlich
im RAM, ohne Journal, Charaktercache oder Log. Abschalten, eigene
Identitätsänderung und Empfangsende leeren die Ansicht. Der gemeinsame bereits
autorisierte passive Empfang erhält keine zweite Erfassung oder Spielanfrage;
ohne eigenen Wirebeleg veröffentlicht der Provider keine Kampfergebnisse.

### Begrenzte eigene PvP-Klassifikation (Nutzerauftrag 2026-10-27)

Der ausdrücklich beauftragte Killnachweis benötigt eine flüchtige PC-Opferzuordnung
im bereits autorisierten eigenen Serverstrom. Maximal 4.096 gleichzeitig lebende
RAM-Einträge binden Entity, Name und Server an aktuelles Own, Welt und tatsächlichen
Transport; Tombstones behalten nur ein Bit je Entity-Key ohne Name oder Server. Keine Speicherung, Netzwerkpositionen, Gruppen- oder Schadensanalyse.
Die Namen dienen ausschließlich der eigenen Killzuordnung und dem flüchtigen Feed.
Entfernungs-/NPC-Signale dürfen Belege nur widerrufen; keine Lebensrekonstruktion.
Assists bleiben ohne ausdrücklichen eigenen Ergebnisbeleg deaktiviert. Klassen-
und Rangmetadaten werden in diesem Schritt nicht angezeigt.

### Verworfenes Feature: DPS-Meter (2026-10-19)

Der Nutzer hat den DPS-Meter vollständig verworfen. Die dafür vorgesehene zusätzliche
Verarbeitung von Gruppen-Kampfdaten ist kein Bestandteil der App.

Aufnahme: `scripts/Record-GameTraffic.ps1`.

## Datenschutz

- Bilddaten bleiben ohne aktivierte Diagnose im Arbeitsspeicher.
- Diagnoseaufzeichnungen (Feed-/Chat-Ausschnitte) sind opt-in, starten ausgeschaltet und liegen
  nur lokal. Chat-Ausschnitte können Spielernamen und Chatnachrichten anderer enthalten. Darauf
  in der UI hinweisen und Aufbewahrung begrenzen (Standard 3 h, wie Grindcrest).
- Overlays sind standardmäßig aus Bildschirmaufnahmen ausgeblendet.

Die Nutzung erfolgt auf eigenes Risiko. Ob Overlays und Bildschirmauswertung erlaubt sind,
entscheiden die Nutzungsbedingungen von NCSOFT.



### Gehaltener eigener Killfeed (Nutzerentscheidung 2026-10-27)

Die neueste Entscheidung ersetzt die frühere Begrenzung auf fünf Einträge/zehn
Sekunden: maximal zehn gewählte eigene Ergebnisse, ohne Zeitablauf, weiter nur
im RAM. Abschalten, Charakter-/Transportwechsel und Empfangsende leeren den Feed.
Bestätigte Gegnerklasse und tatsächlicher Ranggrad werden beim Kill eingefroren
und als originale lokale Icons gezeigt. Der Feed selbst bleibt flüchtig; die spätere
Sessionstatistik unten darf ausschließlich anonyme Klasse-/Rangkategorien speichern.
Keine fremden Kampfauswertungen oder Ableitung aus eigenen Rangpunkten.

### Angreifer beim eigenen Tod und Punkte je Ergebnis (27.10.2026)

Auf Nutzerauftrag werden die bereits flüchtig gelesenen Klasse/Rang-Metadaten
auch für den expliziten PC-Caster des eigenen Todes angezeigt, ausschließlich bei
exakt passender aktueller Spielerzuordnung. Keine neue Erfassung; Gegnernamen und
native Zuordnungskennungen bleiben flüchtig.
Optionale AP-/RP-Beträge im Feed verlangen einen eigenen direkten Ergebnisbeleg;
Wallet-/Totaldifferenzen und Nähe zu einem Tod bleiben unabhängig und unbekannt.

### Anonyme Sessionstatistik und synchroner Replay (Nutzerentscheidung 2026-10-27)

Bestätigte eigene Kills und Tode dürfen ihre beim Ereignis eingefrorene validierte
Gegnerklasse und den expliziten tatsächlichen Ranggrad im lokalen Sessionjournal tragen.
Das sind anonyme Kategorien: keine Gegnernamen, Entity-/Server-/Verbindungskennungen
oder wiedererkennbare Gegnerprofile. Fehlende Kategorien bleiben unbekannt. Die
Sessionauswertung ist unabhängig von Sichtbarkeit, Filtern und Begrenzung des RAM-Feeds.
Ein tatsächlicher Gegner-Ranggrad wird niemals aus eigenen Rangpunkten berechnet.

Die Timeline übernimmt nur bereits qualifizierte eigene AP-/RP-/Kina-Gewinne,
Belohnungen, Sammelergebnisse und Kampfmarker aus derselben passiven Erfassung.
Keine zusätzliche Aufnahme, Spielanfrage oder Korrelation zeitlich benachbarter
Punktedeltas mit Kills/Toden. Punkte-Stufenaufstiege beziehen sich ausschließlich
auf kontinuierlich bestätigte eigene absolute Rangpunkte; Baselines und Quellenlücken
erzeugen keine nachgetragenen Gewinne oder Aufstiege.

Der synchron sichtbare Weg stammt ausschließlich aus frischen eigenen visuellen
Kartenpositionen. Die neue Lifeskill-Laufspur bleibt von der optimierten Sammelstellenroute
getrennt; Sammeln ohne Position zählt weiter, fehlende Wege werden nicht verbunden
oder geschätzt. Bewegung verlängert keine Farmzeit und der Nachlauf bleibt unproduktiv.
Replay und Anzeigeauswahl ändern keine gespeicherten Beute- oder Sessiongesamtwerte.
Altdaten behalten ihre belegte Genauigkeit; unbekannte Mengen/Kanäle und fehlende
Gegner- oder Bewegungsdaten werden nicht rückwirkend erfunden.
