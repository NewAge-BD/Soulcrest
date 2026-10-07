# Aion 2 Pet-System: Recherche und offene Fragen

Stand der Recherche: 2026-10-02. Öffentliche Quellen widersprechen sich in Details. Alles hier ist
**unbestätigt**, bis es ingame geprüft wurde. Soulcrest hält Schwellenwerte deshalb in Daten
(`data/pets/`), nicht im Code.

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
