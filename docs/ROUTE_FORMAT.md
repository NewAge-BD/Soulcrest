# Soulcrest-Routenformat

Soulcrest speichert austauschbare Kartenrouten als UTF-8-JSON mit der Dateiendung
`.soulroute`. Das Format trennt die Routendefinition vom Charakterfortschritt und
enthält ausschließlich Kartenpositionen, Darstellungsdaten und Episodenmetadaten.

```json
{
  "format": "soulcrest.routes",
  "version": 1,
  "coordinates": "map-pixels",
  "routes": [
    {
      "id": "example-route",
      "name": "Asmodian · Episode 2 · Level 10–16",
      "category": "Leveling Routes",
      "repeat": false,
      "color": "#a78bfa",
      "episode": {
        "faction": "asmodian",
        "number": 2,
        "minLevel": 10,
        "maxLevel": 16
      },
      "stops": [
        {
          "id": "example-route:0",
          "map": "altgard",
          "x": 3395.644,
          "y": 2707.848,
          "title": "Finding Nemon",
          "objective": "main-quest",
          "kind": "NPCs · Hero Quest",
          "color": "#facc15"
        }
      ]
    }
  ]
}
```

## Felder und Koordinaten

`format`, `version`, `coordinates` und `routes` sind Pflichtfelder. Unbekannte
Formatversionen oder Koordinatensysteme werden abgelehnt, damit sie nicht
versehentlich als bekannte Pixelpositionen eingelesen werden.

| Ebene | Pflichtfelder | Optionale Felder |
| --- | --- | --- |
| Route | `id`, `name`, `repeat`, `stops` | `category`, `color`, `episode` |
| Episode | `faction`, `number` | `minLevel`, `maxLevel` |
| Station | `id`, `map`, `x`, `y`, `title`, `objective`, `kind` | `icon`, `petId`, `color` |

`map` referenziert die Karten-ID im lokalen Kartenpaket. `x` und `y` sind endliche,
nicht negative Pixelkoordinaten der jeweiligen Karte bei deren `refZoom` und
`size` aus dem Manifest; der Ursprung liegt oben links. Das Format übernimmt
Soulcrests vorhandene Kartenkoordinaten direkt. Es gibt weder eine globale
4096-Pixel-Skalierung noch eine zusätzliche Achsenumkehr. Leaflet erhält diese
Positionen über die vorhandene Kartenumrechnung.

Der Routentitel steht in `name`, der sichtbare Stationstitel in `title`.
Ein leerer Stationstitel ist erlaubt: unbestätigte Spieltexte werden nicht durch
frei erfundene Beschriftungen ersetzt. `kind` bewahrt die bisherige Objektart,
`icon` ist ein relativer Pfad im lokalen Kartenpaket. `color` wird unverändert
übernommen. Für Leveling-Stationen bezeichnet `objective` das Ziel unabhängig von
einem möglicherweise gemeinsam genutzten NPC:

| `objective` | Bedeutung | Leveling-Farbe |
| --- | --- | --- |
| `main-quest` | Hauptquest | `#facc15` |
| `regional-quest` | Regionalquest | `#4ade80` |
| `teleport` | Kibelisk oder Teleport | `#a78bfa` |
| `exploration` | Dungeon, Stronghold oder anderes Weltobjekt | `#ffffff` |
| `waypoint` | Unbeschrifteter Wegpunkt | `#ffffff` |
| `custom` | Gewöhnliche selbst erstellte Route | Vorhandene Routen-/Markerfarbe |

Episoden verwenden `faction: "asmodian"` oder `"elyos"` und eine positive
Episodennummer. Levelgrenzen sind die bereits geprüften empfohlenen Questlevel
der vollständigen Episode. Sie sind keine Freischaltanforderungen. Routen ohne
belegte Episode dürfen das gesamte `episode`-Objekt weglassen.

`episode` und `objective` sind in Version 1 validierte, beschreibende Metadaten.
Die vorhandene App-Domäne bleibt unverändert: Episoden ergeben sich aus
Routenname und Kategorie; Objectives ergeben sich aus der wirksamen
Leveling-Farbe und der Objektart. Widersprüchliche Angaben werden beim Einlesen
abgelehnt. Die Metadaten schaffen keine zweite, abweichende Steuerung der Route.

## Identität und Fortschritt

Routen-IDs bleiben bei der Konvertierung unverändert. Die Reihenfolge der
Stationen ist maßgeblich. Version 1 verwendet Stations-IDs nach dem Schema
`<routeId>:<index>` mit einem bei null beginnenden Index. Aufeinanderfolgende
Besuche desselben NPC behalten eigene Stations-IDs und werden nicht dedupliziert.

Der Charakterfortschritt liegt weiterhin gesondert in
`leveling-progress.json`; laufende Markierungen liegen in `targets.json`.
Beide Dateien gehören nicht in ein austauschbares Routenpaket. Eine reine
Formatkonvertierung verändert weder Routen-IDs noch Stationsreihenfolge und
benötigt daher keine Umschreibung der vorhandenen Episoden-Fortschrittswerte.
Künftige Änderungen, die Stationen hinzufügen, löschen oder umsortieren,
benötigen eine explizite Fortschrittsmigration; die Index-IDs dürfen nicht
ungeprüft als dauerhaft positionsunabhängige Identität behandelt werden.

## Bereinigtes Leveling-Paket

[`data/routes/leveling.soulroute`](../data/routes/leveling.soulroute) ersetzt die
elf ursprünglichen Routenimporte. Es enthält die zehn geprüften Episodenrouten
für Asmodian und Elyos, jeweils Episode 2 bis 6, mit insgesamt **867 Stationen**.
Die 160 bereits entfernten Trace-Punkte und die ursprünglichen
Anweisungstitel sind nicht enthalten. 458 Stationen besitzen geprüfte
Questtitel, 409 Stationstitel bleiben leer. Namen alter Charaktere oder Autoren
gehören nicht zum Paket.

Die Quest „Empyrean Monolith“ ist erhalten: Sie ist eine Hauptquest und kein
entfernter Trace-Punkt. Die Entfernung automatisch erzeugter Monsterzweige
ändert die Stationenliste nicht. Das Paket enthält keine Monsterzweige oder
Auslösesperren für nahe beieinander liegende Quests.

## Speicherung in der App

Die lokale Bibliothek liegt als `routes.soulroute` im Soulcrest-Datenordner.
Beim ersten Start mit dem neuen Format konvertiert die App die bisherige
`routes.json` atomar, liest das Ergebnis zurück und prüft alle Routenwerte.
Erst danach wird die alte Datei entfernt. Eigene Routen, IDs, Reihenfolge und
Farben bleiben erhalten; Charakterfortschritt wird gesondert weiterverwendet.
Eine vorhandene `.soulroute`-Bibliothek hat Vorrang. Unbekannte Versionen oder
beschädigte Dateien werden nicht mit einer leeren Bibliothek überschrieben.

Nur wenn weder die neue noch die alte Bibliothek existiert, legt die App die
mitgelieferten Leveling-Episoden an. Bewusst gelöschte Routen oder eine leere
Bibliothek werden beim nächsten Start nicht wieder aufgefüllt.

## Konvertierung

```powershell
python scripts/convert_routes.py <routes.json> <export.soulroute>
python scripts/convert_routes.py <routes.json> <leveling.soulroute> --leveling-only
python scripts/convert_routes.py <input.soulroute> <copy.soulroute>
```

Der Offline-Konverter akzeptiert das bisherige Soulcrest-`SavedRoute[]` und
bereits gültige `.soulroute`-Dokumente. Er schreibt atomar in eine separate
Zieldatei. Die Eingabedatei, Charakterfortschritt und laufende Markierungen
bleiben unberührt. Die Konvertierung übernimmt Namen, Koordinaten, Reihenfolge
und Farben; sie führt keine neue Questzuordnung oder Positionserkennung durch.
Die Leveling-Episodenmetadaten werden aus den bereits geprüften Episodennamen
übernommen. `--leveling-only` beschränkt den Export auf die entsprechende
Kategorie und verändert keine gewöhnlichen Routen.

Unbearbeitete Markerdateien eines anderen Kartenprogramms werden bewusst
abgelehnt. Sie enthalten weder die geprüften NPC-Positionen noch die
Questzuordnung und Episodengrenzen des bereinigten Pakets. Für diese Routen ist
das mitgelieferte `.soulroute`-Paket die maßgebliche Importquelle.
