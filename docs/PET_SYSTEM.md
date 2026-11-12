# Aion 2 Pet-System: Recherche und offene Fragen

Stand der Recherche: 2026-10-02. Öffentliche Quellen widersprechen sich in Details. Alles hier ist
**unbestätigt**, bis es ingame geprüft wurde. Soulcrest hält Schwellenwerte deshalb in Daten
(`data/pets/`), nicht im Code.

## Owned Effects im Combat Score Calculator (09.11.2026)

Die Übersicht verwendet dieselbe globale, verifizierte Pet-Sammlung wie der
Haupt-Pet-Tab. Jede erworbene Stufe liefert einen vollständigen Besitzbonus:

| Gattung | Stufe 1 | Stufe 2 | Stufe 3 (maximal, vom Nutzer bestätigt) |
| --- | --- | --- | --- |
| Cogni | 10 LP | 20 LP, 1 kritischer Treffer | 30 LP, 2 kritischer Treffer, 1 Macht |
| Fera | 1 Boden-Tempo (Reittier) | 2 Tempo, 1 Zusatzpräzision | 3 Tempo, 2 Zusatzpräzision, 1 Agilität |
| Natura | 3 SP | 6 SP, 1 Krit.-Widerstand | 9 SP, 2 Krit.-Widerstand, 1 Präzision |
| Varian | 0,1 % Sprint-SP-Kostenreduktion (Reittier) | 0,2 %, 1 Zusatzausweichen | 0,3 %, 2 Zusatzausweichen, 1 Vitalität |

Nur der aktuelle Stufenvektor zählt, frühere Vektoren werden nicht erneut addiert.
Owned Effects summiert die erworbenen Boni; Missing Owned Effects zeigt die
Differenz zum maximalen Bonus des vorhandenen Katalogs, einschließlich weiterer
Stufen bereits freigeschalteter Pets. Special und Genus-Insight-Analyse gehören
nicht zu diesen vier Kategorien und ihren Basisbonis.

Die Completion Rate je Gattung ist `Summe erworbener Stufen / (Petanzahl × 3)`.
Verfügbare oder historisch gesammelte Souls verleihen keine weitere erworbene Stufe.
Manuelle Korrekturen bleiben erhalten und sichtbar gekennzeichnet. Gespeicherte
Stände bleiben unbestätigt; fehlende Levelbelege werden nicht als Level 0 ausgegeben.

Fehlende Statboni öffnen direkt die entsprechende Gattung im Haupt-Pet-Tab.
Der allgemeine Link öffnet die Pet-Sammlung ohne Fortschrittsänderung.

## Aktueller Netzwerkabgleich (2026-10-13)

Die nachfolgende Recherche ist historisch; Pet-/Loot-OCR ist entfernt. Soulcrest speichert eine
gemeinsame Pet-Sammlung ohne Servertracking (Nutzerentscheidung 2026-10-13) und liest vollständige Stufe/Balance beim Charakterlogin über die passive
Netzwerkquelle. Dafür Soulcrest öffnen, einmal die Charakterauswahl betreten und wieder einloggen.
Gewinne, Verbrauch und absolute Stufenmeldungen werden getrennt übernommen. Ein automatischer
Aufstieg ist beim Erreichen der Schwelle live bestätigt, einschließlich Stufe3/Maximalstufe.

Explizite erfolgreiche Levelup-Ergebnisse korrigieren die Stufe auch ohne Erwerbsmeldung. Sie
verbrauchen selbst keine Souls und zählen nicht als Pickup. Neue eigene Soul-Meldungen zählen
auch ohne vorherigen Login-Abgleich. Prepare/Own erhält die vorher bestätigte Pet-Liste mit
originaler Belegzeit unabhängig von Charakter/Server. Nach echter Lücke bleiben gespeicherte Balances sichtbar unbestätigt, bis ein voller
Abgleich erfolgt. Manuelle Korrekturen bleiben geschützt. Historisch verdiente Gesamt-Souls werden
nach einem Verbrauch nicht als aktuelle Balance ausgegeben.

## Was als gesichert gilt (mehrere Quellen)

- Pets werden **gesammelt**: Monster töten → Soul-Pickups am Boden → aufheben →
  Chatmeldung `Obtained N <Monster> Soul Points.` (im Video bestätigt).
- **Fünf Gattungen (Genus):** Cogni, Fera, Natura, Varian, Special. Die offenen Kartendaten nennen
  sie `creatureIntellect`, `creatureFeral`, `creatureNature`, `creatureTrans`, `creatureSpecial`.
- Anzahl laut wikily.gg: **206 Pets** (200 normale + 6 Special). Verteilung: Cogni 41, Fera 65,
  Natura 48, Varian 46.
- Weitere Quellen: Dungeons (manche Pets nur dort), Quests (Gratis-Pet bei „All Geared Up“, ~Lv 10),
  Black-Cloud-Händler/Shop, Mysteriöse Pet-Boxen, Events, Erfolge.
- Das Ingame-Pet-Fenster hat einen **Quelle-Tab**. Er nennt Item und Monster, z. B. „Soul: Fungen
  [Bound], looted from Lv. 12 Red Cap Fungen in Eastern Cantas Valley“, und hat einen Kartenpin.
- **Pet-Verständnis (Pet Insight)** steigt mit Souls. Jede Stufe schaltet den nächsten
  Besitz-Effekt der Gattung frei und gibt Gattungsverständnis-EXP.
- **Gattungsverständnis (Genus Insight)** ist eine zweite Ebene: Analyse mit 9 Slots, bezahlt mit
  Soul-Kristallen der Gattung (Cogni blau, Fera rot, Natura grün, Varian gelb, Special hellblau)
  und Kina.
- Pet-Besitz und Gattungsverständnis sind laut mmoexp **serverweit über Charaktere geteilt**.
  Daraus folgt: ein Profil pro Server.
- Beim Freischalten erscheint ein Bildschirmhinweis, z. B. „Natura Pet Genus Insight Level Up“.
  Das ist ein möglicher zweiter OCR-Bestätigungspunkt.

## Pet-Stufen (vom Nutzer bestätigt, 2026-10-03)

| Stufe | Souls für diese Stufe | Gesamt bis hier |
| --- | --- | --- |
| 1 (freigeschaltet) | 5 | 5 |
| 2 | 25 **neue** | 30 |
| 3 (max) | 75 **neue** | 105 |

Jede Stufe zählt von 0. Die Souls der vorherigen Stufe werden **nicht** übernommen. Das löst
den Widerspruch der Quellen (allthings.how: 5, wikily: 25/75). Max-Stufe Global = 3
(KR/TW laut mein-mmo 5). Die Schwellen bleiben datengetrieben (`SoulThresholds`,
`data/pets/defaults.json`).

Soulcrest speichert die Gesamtzahl und zeigt wie im Spiel **Stufe + Souls in der Stufe** an.
Eingabe in der Pet-Liste ebenso.

**Überzählige Souls gehen in die nächste Stufe über** (vom Nutzer bestätigt, 2026-10-03). Ein
Beispiel: 4/5 plus 3 Souls ergibt Stufe 1 mit 2/25. Die Gesamtzahl ist deshalb die richtige
Speichergröße.

**Die Schwellen gelten für alle Pets gleich** (vom Nutzer bestätigt, 2026-10-03). Es gibt also
keine pet-spezifischen Schwellen.

**Noch offen:** Zählt ein
Soul-Pickup für alle Charaktere des Servers? Soul Points pro Pickup sind variabel (Video: 1, 2,
4) und werden immer aus der Meldung gelesen.

## Soul-Name ≠ Monstername (Befund 2026-10-02)

Der Name in der Soul-Meldung ist der **Grundname der Art**. Die Monster auf der Karte und im
Spiel tragen dagegen Zusätze für Gebiet oder Eigenschaft:

| Soul-Meldung (Feed/Chat) | Monster in Altgard (historisch beobachtet) | Gattung |
| --- | --- | --- |
| Magic Gravi | Purifying Forest Magic Gravi | Natura |
| Crasaur | Sensitive Crasaur, Crasaur | Fera |
| Superior Water Spirit | Unstable Superior Water Spirit | Natura |

Folgen:

- Die Pet-DB ordnet jedem Pet einen **Soul-Namen** zu und dazu eine Liste von Monsterarten mit
  Spawnmarkern. Das OCR-Matching läuft gegen den Soul-Namen.
- Vorschläge für die Zuordnung Monsterart → Soul-Name entstehen automatisch, wenn der Soul-Name
  als Wortfolge am Ende des Monsternamens steht (Präfix entfernen). Sie werden als
  `confirmed=false` markiert und ingame oder per Video bestätigt.
- Ob „Unstable Superior Water Spirit“ und „Superior Water Spirit“ wirklich dieselbe Soul liefern,
  ist eine Annahme. Sie wird in Phase 2 geprüft.

## Gebiete

Die Kartendaten (aion2-interactive-map, `public/data/regions/World_D_A.yaml`) enthalten
**26 Altgard-Gebiete mit Polygonen**, unter anderem `PurifyingForest`, `MoslanForest`,
`BasfeltRuins`, `IdunsLake`, `BlackClawVillage`, `LagtaFortress`. Die Pet-DB verweist auf diese
`regionId`s. Die Karte hebt Gebiete hervor, nicht exakte Spawnpunkte.

## Quellen

- [wikily.gg – Pets, Pet Souls and Where to Get Them](https://wikily.gg/aion-2/pets)
- [wikily.gg – Genus Insight](https://wikily.gg/aion-2/genus-insight)
- [allthings.how – Unlock Any Pet by Farming Pet Souls](https://allthings.how/aion-2-how-to-unlock-any-pet-by-farming-pet-souls/)
- [game8 – How to Get Pets Fast](https://game8.co/games/Aion-2/archives/615788)
- [game8 – List of All Pets](https://game8.co/games/Aion-2/archives/624704)
- [mmoexp – Global Pets Guide](https://www.mmoexp.com/News/aion-2-global-pets-guide-genus-insight-full-list-how-to-get-them.html)
- [mein-mmo – Pets und Gattungsverständnis](https://mein-mmo.de/en/aion-2-arkana-daevanion-pet-you-need-to-understand-and-master-these-3-systems,1589883/3/)
- [aion2kina – Pet Collection Guide](https://www.aion2kina.com/en/guides/aion-2-pet-collection-companion-guide/)

Für die Pet-DB werden nur **Fakten** übernommen (Namen, Gattung, Monster, Gebiet, Zahlen), mit
Quelle pro Eintrag. Keine Texte oder Bilder aus diesen Seiten kopieren.

Aktueller Kartenimport: Zuordnung ausschließlich aus veröffentlichter Soul-Beute; keine Namensendungen. Siehe MAP_DATA.md.

## Dungeonquellen im Pet-Katalog (2026-11-11)

Der vorhandene Quellenkatalog umfasst jetzt auch ältere belegte Dungeon-Souls.
Eine Zuordnung setzt die eindeutige Pet-/Soul-Identität, einen direkten
Soul-Beuteeintrag des Quellmonsters und dessen tatsächliche Dungeonplatzierung
voraus. Bloßes Auftreten eines Monsters, Namensähnlichkeit oder eine
Belohnungsvorschau reichen nicht aus. Raidquellen werden als Raid ausgewiesen;
Abyss-Farmkarten, Event- und Festungsinstanzen werden dadurch keine Dungeons.

Der Filter „Mit Dungeonquelle“ und die Suche nach Dungeon-/Quellmonsternamen
verwenden dieselben gebündelten Fakten wie die Quellenangaben auf den Petkarten
und im Detail. Dungeonbelege ergänzen die Weltkartenquellen; sie erzeugen keine
fiktiven Kartenmarker oder Spawnzahlen und verändern keinen Fortschritt.

Global-Verfügbarkeit bleibt je Kartenvariante getrennt: verfügbar, zukünftig
oder unbestätigt, jeweils mit dem Datum der vorhandenen Prüfung. Die Anzeige
„Dungeon“ allein setzt mindestens eine als verfügbar belegte Variante voraus.
Bei ausschließlich zukünftigen oder ungeklärten Varianten ist die Kennzeichnung
ausdrücklich qualifiziert. Ein historisches Ergänzungsdatum ist optional, damit
ältere Quellen kein erfundenes Veröffentlichungsdatum erhalten. Dropchance und
Schwierigkeitsstufe bleiben unbekannt, solange sie nicht eigenständig belegt sind.

Die auswählbare Pet-Diagnose enthält nur anonyme Anzahlen belegter Dungeon-Pets
und ihrer Verfügbarkeitszustände; sie startet keine Quelle und exportiert keine
NPC-Identitäten oder Herkunftsdaten.

## Dungeon-Fundorte anzeigen (2026-11-12)

Bei Pets mit Dungeonquelle öffnet „Im Dungeon Zeigen“ oberhalb von „Auf der Karte
zeigen“ einen eigenen Kartendialog. Der Dialog zeigt alle belegten Pet-Fundpunkte
der gewählten Dungeonvariante und hebt das gewählte Pet hervor. Dungeons und
Varianten sind wechselbar; verfügbare Varianten mit vielen ausgewählten Punkten
werden bevorzugt. Die Kamera passt alle Positionen dieses Pets ein. Zoom,
Verschieben und erneutes Einpassen bleiben direkt in der Ansicht möglich.

Originale Variantenbezeichnungen werden erhalten. Gleiche oder fehlende Labels
erhalten generische Variantennummern, keine erfundene Schwierigkeit. Zukünftige
und unbestätigte Varianten bleiben ausdrücklich gekennzeichnet. Die Ansicht
erzeugt weder Farmorte auf der Weltkarte noch einen Freigabebeleg im Spiel.

Der Weltkartenbutton bleibt unabhängig. Schließen und Escape stellen den Fokus
wieder her; Tabs, Tracking, Kartenausschnitte, Routen und globaler Petfortschritt
werden nicht geändert. Fehlende oder veraltete Kartenpakete ergeben einen
Hinweis und einen deaktivierten Dungeonbutton. Die separat auswählbare Diagnose
„Dungeon-Petkarten“ liest Bild-/Positionsabdeckung sowie den flüchtigen Lade-/
Darstellungszustand. Sie startet keine Quelle und exportiert keine Auswahlidentität.
