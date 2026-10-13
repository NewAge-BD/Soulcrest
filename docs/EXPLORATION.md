# Charakterbezogener Erkundungsfortschritt

## Netzwerk statt OCR (Nutzerauftrag 2026-10-12)

Pet- und Erkundungsscan sind entfernt. Der vorhandene passive Npcap-Serverstrom liefert eigene
Charakteridentität, Quest-Snapshots (`0x8D09`), Teleportlisten (`0x9232`) und direkte
Sealed-Dungeon-/Stronghold-Ergebnisse (`0x6101`). Der Import verbindet
Kartenorte über explizite gaming.tools-IDs mit diesen Daten; Namen und Positionen werden nicht als
Ersatz für eine fehlende ID geraten. Die Erfassung läuft unabhängig von geöffneter Weltkarte oder Liste.

Nutzerpräzisierung 2026-10-13: Für Dungeons und Strongholds zählt nur, ob ein Abschluss
vorliegt. Übersicht und Karte zeigen deshalb einen Abschluss-Haken; die frühere Unterscheidung
unentdeckt/entdeckt ist keine Bedienoption mehr. Bestehende gespeicherte Enumwerte bleiben
kompatibel erhalten, ebenso bisherige Haken und manuelle Korrekturen.

Ein fehlender Haken bedeutet, dass Soulcrest keinen übernommenen Abschluss hat. Er beweist
nicht, dass der Ort noch nie abgeschlossen wurde. Fehlende Belege bleiben intern unbekannt;
die Aktualität einer Beobachtung wird unabhängig davon geführt.

Die kontrollierte Folge an Logger’s Rest Ruins belegt den Übergang von einer fehlenden Zeile
über Status2/Stage1/sechs Nullzähler nach Entdeckung zu Status4/Stage1/[1,0,0,0,0,0] nach
direktem Erfolg. Kontrollorte bleiben unverändert. Neue, vollständig akzeptierte eigene
Questlisten bestätigen daher für exakt zugeordnete Dungeons und Strongholds einen Abschluss
nur mit Status4, Stage1, genau diesem Zählermuster und nichtnegativem Ordinal. Andere Formen
und Status2 werden nur als Rohdaten erhalten. Alte Rohstände werden nicht rückwirkend
projiziert; der vor dem Drana-Lauf gespeicherte stale Status4 bleibt ein historischer Befund.
Fehlende IDs löschen keine vorhandenen Haken und bedeuten keinen belegten Nichtabschluss.
Positive Kibelisk-Präsenz ist durch den unten beschriebenen Bindungstest separat belegt.

## Profile und Migration

Die vorhandenen Charakter-GUIDs, Namen, Orts-IDs und Haken bleiben erhalten. Die eigene
Netzwerkidentität ergänzt Server-ID und stabile Charakter-ID. Ein eindeutig gleichnamiges,
noch ungebundenes Altprofil wird einmalig verknüpft; mehrdeutige Namen werden nicht zusammengelegt.
Dadurch bleiben auch die daran gebundenen Leveling-Routen erhalten. Pet-Sammlung und Fokus gelten
serverweit. Vor dem ersten Speichern im neuen Format entsteht eine lokale Sicherung der alten Datei.
Die Entwicklung verändert keine echten Nutzerdaten; Migration erfolgt beim nächsten App-Start.

## Karte, Ziele und Navigation

Übersicht, Kategorien, Suche, Kartenfixierung und „Spielkarte folgen“ bleiben erhalten. Die
Kartenposition stammt weiterhin aus WGC/OpenCV. „Auf Karte zeigen“ fokussiert einen Ort, ohne
einen Spielabschluss zu setzen. Dungeon-Voraussetzungen bleiben Teil der Progressionsauswahl.
Ankunft im Radius oder Betreten einer Instanz darf seit der Dreistufenanforderung keinen Ort
mehr als erledigt speichern: Das beweist keinen abgeschlossenen Dungeon oder Stronghold.

Nach Ladebildschirm, eigener TCP-Lücke oder Verlust der gebundenen Verbindung bleiben gespeicherte Stände
sichtbar, gelten aber nicht als frische Netzwerkerkennung. Erst die eigene Charakteridentität und
neue vollständige Daten bestätigen den nächsten Kontext. Nutzerbestätigung 2026-10-13: Bei Start
mitten in einer Sitzung muss Soulcrest weiterlaufen, während einmal die Charakterauswahl geöffnet
und der Charakter erneut betreten oder gewechselt wird. Ein einfacher Gebietswechsel reicht für
die Pet-Initialisierung nicht. Quest-/Teleportdaten werden übernommen, sobald das Spiel die
jeweiligen vollständigen Datensätze für den eigenen Charakter sendet; ein periodischer oder
durch bloßes Gebietsbetreten garantierter Abruf ist nicht belegt.
Unverankerte Baselines vor dem eigenen Welteintritt werden nicht rückwirkend zugeordnet.
Der frühe beobachtete Login-Kandidat darf ausschließlich vollständige Pet-/Quest-/Teleportlisten
über genau eine Vorbereitung vormerken. Erst die vollständige eigene Identität mit passender
Charakter-ID und Entity-Key auf demselben Strom bestätigt diese einmalige Transaktion. Server und
Name stammen ausschließlich aus Own. Deltas und beliebige alte Prefixe bleiben ausgeschlossen;
bei Lücke, unbekanntem Format, falscher Identität oder Timeout wird nichts übernommen.

## Bereinigung der früheren Abschlusslogik (2026-10-12)

Annäherungsring und manuelle Clear-Aktion für Dungeons/Strongholds auf der Karte sind entfernt.
Die Erkundungsübersicht erlaubt eine manuelle Korrektur des Abschluss-Hakens. Die frühere
Drei-Zustands-Auswahl entfällt nach Nutzerpräzisierung vom 2026-10-13. Kibelisk-Bindung kann
weiter gezielt manuell korrigiert werden.

`NavigationArrivalService` verarbeitet ausschließlich markierte Wegpunkte und Leveling-Stationen.
Er hat keine Abhängigkeit von Charakter- oder Erkundungsfortschritt. Der einstellbare
`TargetArrivalRadius` steuert nur diese Navigation; gespeicherte alte Radiuswerte werden gelesen,
beim nächsten Speichern unter dem neuen Namen erhalten und lösen keine Spielabschlüsse aus.

## Direkte Sealed-Dungeon-/Stronghold-Erfolgsmeldung (2026-10-13)

Eigene Livemitschnitte belegen eine Servermeldung mit Instanz-ID und Erfolgsflag für beide Kategorien.
Soulcrest übernimmt erfolgreiche Ergebnisse für exakt zugeordnete Sealed Dungeons und Strongholds direkt
als „entdeckt, erledigt“ ins eigene Charakterprofil. Eine neue Questliste ist dafür nicht nötig;
im ersten geprüften Dungeonlauf wurde keine gesendet. Der Abschluss bleibt nach Verlassen, neuen
Questdaten und Neustart erhalten. Vorhandene manuelle Korrekturen werden weiterhin geschützt.

Sealed Dungeons nutzen das Importfeld `dungeonId`, Strongholds das getrennte
`strongholdInstanceId`. Für Strongholds verlangt der Import das direkte kanonische
Stronghold-Questtarget, eine exakte Explorationquest mit einer eindeutigen positiven Int32-Instanz
und keine andere Explorationquest derselben Instanz. Alle 42 aktiven Strongholdpunkte sind so
zugeordnet. Die Fortschrittsquelle heißt bei Strongholds `network-stronghold-completion`;
vorhandene Profile und manuelle Korrekturen bleiben erhalten.

Latrima Outpost (Quest 2820012 → Instanz 142017) wurde laut Nutzer entdeckt/offen begonnen und
meldet einen eigenen gebundenen Erfolg. Dark Root Cave (Quest 2810034 → Instanz 320034) wurde
vorher als unentdeckt gemeldet, nach Entdeckung mit Queststatus 2 beobachtet und anschließend
ebenfalls durch eigenen Erfolg bestätigt. Beide haben später eine eigene gebundene Questliste
mit Status 4 und erstem Zähler 1. Die ersten Status-2-Listen lagen vor dem ersten Own;
die genaue zeitliche Vertrauensgrenze bleibt dabei erhalten.

Fehlgeschlagene Ergebnisse, unbekannte Instanzen und bloße Laufzustände/Instanzbetreten setzen
keinen Abschluss und stufen einen vorhandenen Abschluss nicht zurück. Eine fehlende Questzeile
beweist keinen Nichtabschluss; eine unentdeckt/entdeckt-Unterscheidung ist nicht mehr erforderlich.
Generischer MapEvent-Status 3 liefert keine exakte Ortszuordnung. Instanzergebnisse enthalten
Belohnungen, erzeugen aber keine zusätzlichen Pet-Souls. Technische Belege: `NETWORK_PROGRESS.md`.

## Direkte Kibelisk-Bindung (2026-10-13)

Der kontrollierte Test an Hidden Cave belegt eine eigene Einzelmeldung beim Binden, gefolgt von
genau einer zusätzlichen Teleport-ID in der nächsten Liste. Soulcrest übernimmt ein vollständig
akzeptiertes `0x9233` mit positiver Teleport-ID für einen exakt zugeordneten Kibelisk als gebunden
(`DiscoveredCompleted`, Quelle `network-kibelisk-binding`). Der Ort wird durch die ID aus dem
aktiven Katalog zugeordnet; Nähe, Objektspawn und Name allein setzen keinen Haken.

Die Einzelmeldung ergänzt ausschließlich diese ID und bestätigt keine vollständige Kibelisk-Baseline.
Der erste eigene Bindungsbeleg wird separat pro Charakter in `TeleportBoundAt` gespeichert.
Spätere Listen, fehlende IDs, Gebietswechsel und Neustart löschen ihn nicht. Bestehende manuelle
Korrekturen und Charakterprofile bleiben geschützt; die Entwicklung schreibt nicht in echte Nutzerdaten.

Dieser Test verbindet Einzelmeldung und Listenbestand: Positive Präsenz einer exakt katalogisierten
Kibelisk-ID in einer neuen, akzeptierten eigenen Teleportliste bestätigt ebenfalls die Bindung
(`network-kibelisk-baseline`). Dadurch werden beim Login auch bestehende Bindungen abgeglichen.
Bereits gespeicherte direkte Belege behalten ihre Herkunft und ihren ersten Beobachtungszeitpunkt.
Fehlende IDs werden nicht als unentdeckt oder ungebunden gespeichert und löschen keine Bindung.
Dungeon-/Instanzteleports und unbekannte IDs erhalten durch diese Regel keinen Erkundungshaken.
