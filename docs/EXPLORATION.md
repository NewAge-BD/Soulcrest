# Charakterbezogener Erkundungsfortschritt

## Netzwerk statt OCR (Nutzerauftrag 2026-10-12)

Pet- und Erkundungsscan sind entfernt. Der vorhandene passive Npcap-Serverstrom liefert eigene
Charakteridentität, Quest-Snapshots (`0x8D09`) und Teleportlisten (`0x9232`). Der Import verbindet
Kartenorte über explizite gaming.tools-IDs mit diesen Daten; Namen und Positionen werden nicht als
Ersatz für eine fehlende ID geraten. Die Erfassung läuft unabhängig von geöffneter Weltkarte oder Liste.

Die drei fachlichen Zustände für Dungeons und Strongholds sind:

- unentdeckt;
- entdeckt, nicht erledigt;
- entdeckt, erledigt.

Daneben gibt es **unbekannt** als fehlenden Beleg, sowie unabhängig davon die Aktualität der
Beobachtung. Ein alter fehlender Haken beweist nicht „unentdeckt“. Bisherige Haken werden als
vorhandener Abschluss übernommen; ihre Aktualität ist zunächst unbekannt. Manuelle Korrekturen
bleiben geschützt.

Die vorhandenen Aufnahmen belegen Questwerte 2 und 4, aber deren sichere Zuordnung zu diesen
Zuständen fehlt. Auch die Bedeutung einer Teleport-ID gegenüber bloßer Entdeckung/Bindung ist
noch nicht ausreichend belegt. Die Nutzerantwort vom 12.10. konnte den konkreten Dungeonstatus
nicht bestätigen. Deshalb werden Queststatus, Stage, Zähler, Ordinal und Teleport-Präsenz gespeichert,
aber daraus kein Abschluss erfunden. Fehlende IDs löschen keine vorhandenen Haken. Die UI kennzeichnet
die offene Zuordnung als unbestätigt und erlaubt die drei Zustände weiterhin manuell.

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

Nach Ladebildschirm, TCP-Lücke, Formatfehler oder Verbindungswechsel bleiben gespeicherte Stände
sichtbar, gelten aber nicht als frische Netzwerkerkennung. Erst die eigene Charakteridentität und
neue vollständige Daten bestätigen den nächsten Kontext. Bei Start mitten in einer Sitzung hilft
ein Gebietswechsel, damit die vollständigen Startnachrichten erneut eintreffen.

## Bereinigung der früheren Abschlusslogik (2026-10-12)

Annäherungsring und manuelle Clear-Aktion für Dungeons/Strongholds auf der Karte sind entfernt.
Die Drei-Zustands-Korrektur in der Erkundungsübersicht bleibt verfügbar, solange die Netzwerk-
Statussemantik offen ist. Kibelisk-Bindung kann weiter gezielt manuell korrigiert werden.

`NavigationArrivalService` verarbeitet ausschließlich markierte Wegpunkte und Leveling-Stationen.
Er hat keine Abhängigkeit von Charakter- oder Erkundungsfortschritt. Der einstellbare
`TargetArrivalRadius` steuert nur diese Navigation; gespeicherte alte Radiuswerte werden gelesen,
beim nächsten Speichern unter dem neuen Namen erhalten und lösen keine Spielabschlüsse aus.
