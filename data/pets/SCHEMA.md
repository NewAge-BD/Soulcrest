# Pet-Katalog: Format

Der Pet-Katalog der App ist **generiert**: `scripts/build_tester_data.py` schreibt
`imports/generated/mapdata/pets.json` aus den Pet- und Monster-Markern aller Karten. Er liegt
nicht im Git, weil er lokale Positionen und Grafiken von gaming.tools enthält.

`data/pets/` enthält nur das, was nicht aus den Kartendaten kommt:

- `defaults.json`: Stufen-Schwellen und Gattungen (bestätigt, siehe `docs/PET_SYSTEM.md`)
- `altgard.pets.json`: frühe Beobachtungen aus dem Beispielvideo (historisch)

## Eintrag in `pets.json`

```json
{
  "id": "magic-gravi",
  "en": "Magic Gravi",
  "de": null,
  "genus": "natura",
  "iconName": "Gravi 02",
  "icon": "icons/pets-circle/UT_Vehicle_Portrait_Gravi_02.png",
  "spawns": { "verteron": 4, "altgard": 4 },
  "monsters": { "verteron": 35, "elthen": 24, "altgard": 6 }
}
```

| Feld | Bedeutung |
| --- | --- |
| `id` | kebab-case aus dem englischen Pet-Namen; Schlüssel für Fortschritt und Ereignisse |
| `en` / `de` | Anzeigenamen. `de` stammt aus der deutschen gaming.tools-Sprachversion, sonst `null`. **Nicht raten.** |
| `genus` | `cogni` \| `fera` \| `natura` \| `varian` \| `special` |
| `icon` | Pet-Porträt im Datenpaket |
| `spawns` | Pet-Spawnmarker je Karte |
| `monsters` | Monster-Marker je Karte, die die Souls dieses Pets liefern |

Regeln:

- **Soul-Name = Pet-Name.** Die Loot-Feed-Zeile `Soul: <Pet> (Bound) x1` wird gegen `en`/`de`
  abgeglichen (`PetNameMatcher`, mit Lauftext-Fragmenten).
- Ein Monster gehört zu dem Pet, dessen Name das **Ende** des Monsternamens bildet (längster
  Treffer gewinnt): „Purifying Forest Magic Gravi“ gehört zu „Magic Gravi“, nicht zu „Gravi“.
- **Stufen-Schwellen sind global** (5 / 25 / 75, jede Stufe ab 0, Überschuss wird übertragen).
  Pet-spezifische Schwellen gibt es nicht.

## Namen für Platzhalter-Pets (`name-overrides.json`)

39 Pets haben in den Kartendaten nur einen chinesischen Namen. `build_tester_data.py` nimmt dann den
Icon-Namen („Crestlich 01“), den der Loot-Feed nie zeigt. Echte Namen kommen aus zwei Quellen:

1. `data/pets/name-overrides.json` (im Git, in Soulcrest.Core eingebettet): nur mit `sourceRefs`.
2. Gelernt aus dem Pet-Fenster (`%LOCALAPPDATA%\Soulcrest\learned\learned-names.json`): Der Name im
   rechten Bereich gehört zur angeklickten Karte. Deren Porträt passt sicher zum Icon des
   Platzhalters (`PetScanService`).

## Quellenabgleich 2026-10-04

`catalog-identities.json` hält stabile Katalog-IDs und die acht erhaltenen Doppelidentitäten fest.
`tamed-from.json` enthält Pet-Seiten-Belege für den unabhängigen Abgleich mit NPC-Beute.
`soul-sources.json` dokumentiert direkte Soul-Beute und gekennzeichnete Ableitungen mit URL-Belegen.
Diese Faktentabellen enthalten keine Kartenbilder oder Roh-Platzierungsdaten.
