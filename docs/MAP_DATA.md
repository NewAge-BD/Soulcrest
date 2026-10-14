# Kartendaten: gaming.tools, vollständig offline

## Quelle und Abruf

Karten, Marker, Pet-Porträts, EN/DE-Namen und Soul-Beute stammen aus den `.d.json`-Dateien von
<https://aion2.gaming.tools/> und den statischen Dateien, auf die sie zeigen. Der Betreiber hat das am
2026-10-06 erlaubt, mit zwei Bedingungen: kein Web-Scraping, und ein sichtbarer, klickbarer Link
„aion2.gaming.tools“ (siehe `THIRD_PARTY_NOTICES.md`). Website-Seiten ruft der Import nicht ab.
Der Basis-Pfad ist `https://cdn-hosted.gaming.tools/aion2`; Detailkarten liegen unter `/tiles/*.gtpk`,
lokalisierte Daten unter `/data/{en,de}/*.d.json`. Das sind öffentliche Dateien, keine Account-API.

`scripts/fetch_gamingtools_dataset.py` sichert die neun bisher unterstützten, in der Quelle
identifizierbaren Karten, deutsche und englische Karten-/Pet-Kataloge, 436 Beutetabellen aus den
vorhandenen „Tamed From“-Belegen und Marker-Icons. `prepare_gamingtools_portraits.py` sichert die
207 Porträts. `fetch_gamingtools.py` cached jeden Abruf, prüft Pfade und drosselt auf mindestens
1,05 Sekunden Abstand. Bei 403/429 wird abgebrochen. Keine Anmeldung, Cookies, Tokens oder
Spielzugriffe. WebP ist vom Nutzer am 2026-10-04 ausdrücklich freigegeben; die Ausnahme ist auf
öffentliche Bilddateien begrenzt. robots.txt wurde gelesen, CDN-robots.txt lieferte 404.

Alle Rohdaten/Grafiken liegen git-ignoriert unter `imports/gamingtools/`. Die App ruft nichts ab.
Der aktive Builder liest ausschließlich diesen Cache und belegte Faktentabellen unter `data/pets`.
Alte vom Nutzer beschaffte Importverzeichnisse werden weder gelesen noch gelöscht.

## Reproduktion

```powershell
python scripts/prepare_gamingtools_portraits.py
python scripts/fetch_gamingtools_dataset.py
python scripts/build_tester_data.py --out imports/generated/mapdata-gamingtools
python -m unittest discover -s tests -p "test_*.py"
node --test tests/map-filter.test.cjs
dotnet test Soulcrest.slnx -c Release
python scripts/preview_map.py 5191
```

Der Builder ist offline. `--skip-tiles` aktualisiert nur Marker/Katalog/Manifest und setzt ein
bereits vollständiges Kachelpaket voraus. Der Standard-Ausgabeordner ist `imports/generated/mapdata`.

## Kacheln und Koordinaten

GTPK v2 ist ein unverschlüsselter Tile-Container: 48 Byte Header, 24 Byte pro Indexeintrag,
anschließend WebP-Dateien. Header und Offsets werden geprüft. Jede 512-Pixel-Kachel wird in vier
256-Pixel-Kacheln bei Zoom z+1 zerlegt. Ihre dekodierten Pixel bleiben erhalten; z0 wird verkleinert.
Nur die Kachelstruktur wird konvertiert. Die 256×256-Dateien unter `/images/maps/` sind Vorschaubilder.

Weltpositionen werden über `bounds`, `imageWidth` und `imageHeight` in native Bildpixel umgerechnet.
Die Daten verwenden `transformType=original` (beide Achsen unvertauscht); andere Transformtypen
lösen einen Fehler aus. Für die App bleibt die logische Weltgröße 8192 für Altgard, sonst 4096.
Native Bildpixel werden durch die gesamte Kachelpyramiden-Ausdehnung geteilt und entsprechend
skaliert. Leaflet: `lat=-y/2^refZoom`, `lng=x/2^refZoom`; `refZoom=5` für Altgard, sonst 4.
Maximale native Zoomstufe und Referenz-Zoom sind getrennt. Die Kartenerkennung darf keine
Referenz oberhalb von `maxNativeZoom` anfordern.

Die Quelle meldet unbestätigte Kalibrierungen mit Restfehlern von 61,9 bis 548,8 nativen Pixeln.
Das sind Quellangaben, keine eigene Genauigkeitsmessung. Der Import reproduziert diese Koordinaten;
eine Verbesserung gegenüber dem Spiel ist erst mit Passpunkten/Live-Test belegt.

## Marker und Namen

`interactive-map/{id}` liefert benannte Einzelpunkte, Dorf-/Jagdgebietslabels und nach Entity
zusammengefasste Positionen. Kategorien umfassen Kibelisken, Teleportation, Dungeons, Bosse,
Sammelobjekte, Händler, NPCs, Monster und Ressourcen. Rang/Gattung sind Facetten desselben
Monsters: Es wird nicht doppelt gezeichnet. EN/DE-Namen werden anhand identischer Entity-IDs
verknüpft. Kibelisken übernehmen ihre veröffentlichten Namen. Gebietsgrenzen/Polygone fehlen;
Mittelpunkte werden nicht als Grenzen interpretiert.

Ressourcen-Marker tragen ihre Unterart als Namen (`en`/`de`, z. B. Gem → Sapphire/Saphir) und deren
Item-Icon als `iconIndex` (`group.iconPath` der gatherables-Gruppe). Die Legende listet Unterarten,
sobald eine Kategorie mindestens zwei hat; ausgeblendete Unterarten speichert
`MapHiddenKinds` als `Group/Kategorie/Unterart` je Karte.

## Pet-Zuordnung und gespeicherter Fortschritt

- Nur Beuteeinträge `Soul: <Pet> (Bound)` erzeugen direkte Monster→Pet-Zuordnungen.
- „Tamed From“ wird gegengeprüft. Widersprüche landen im Audit und werden nicht aufgelöst.
- Ableitungen nur bei exakt gleichem Monsternamen, eindeutigem direkten Beleg und überprüft
  fehlender Beute; `derivedFrom` bleibt sichtbar. Keine Zuordnung anhand einer Namensendung.
- Pet-Symbole stehen auf einer realen Monsterposition nahe der Mitte einer Spawn-Gruppe
  (3 Prozent der Monsterausdehnung, zusammenhängende Nachbarschaft).
- `catalog-identities.json` bewahrt die bisherigen IDs; neue Einträge nutzen den englischen Slug.
  Acht historische Doppelkennungen bleiben im Import als Kompatibilitätsdaten erhalten.
  Seit der Duplikatmeldung vom 2026-10-11 zeigt die App pro eindeutig belegter numerischer Pet-ID
  genau einen primären Eintrag: `legacyDuplicate` wird nur bei genau einer primären Kennung
  derselben `numericId` zum Alias. Gleicher Name oder gleiches Bild allein genügt nicht.
  Alte Profilwerte bleiben beim reinen Laden unverändert; die Anzeige nutzt den höchsten
  gespeicherten Soul-Stand und vereint Fokusmarkierungen. Erst eine ausdrückliche Speicheraktion
  für dieses Pet normalisiert seine Kennungen. Werte werden nie addiert. Kartensymbole und
  Scan-Referenzen verwenden dieselbe primäre Identität.
- Porträts kommen aus gaming.tools, WebP→PNG für GDI+-Overlays. PNG bewahrt die dekodierten Pixel.

Datenvertrag: `data.js` mit `groups`, `categories`, `icons`, `regions`, `markers`;
Marker `[category,x,y,en,de,iconIndex,petId,extra]`. `pets.json` behält seine bisherigen Felder.
Neue Prüfmetadaten sind additive Felder.

Boss Rush: Der Builder verknüpft `maps/{id}.namedSpawns` über die exakte NPC-ID mit den
Boss-Punkten aus `interactive-map/{id}`. Je Karte entsteht `bosses.json` mit `mapId` (numerische
Quellkarte) und `bosses[{spawnId,npcId,en,de,x,y}]`; die Koordinaten durchlaufen dieselbe
Umrechnung wie die Marker. `data.js` enthält diese Angaben ebenfalls additiv. Keine Zuordnung
nach ähnlichem Namen. Fehlende Daten bleiben ohne Boss-Ziel; alte Pakete müssen neu gebaut werden.
Mehrere Spawn-IDs je NPC oder mehrere Punkte je Spawn-ID werden als nicht eindeutig ausgelassen.

`manifest.json` → `maps[].metersPerPixel`: Spielmeter je Kartenpixel aus den gaming.tools-Grenzen
(Welteinheiten = Zentimeter). Werte: Altgard, Poeta und Abyss A 0,996; Verteron, Ishalgen, Eltnen und
Morheim 1,992; Chaotic Reshanta 0,498. Im Spiel geprüft am 2026-10-06 auf Altgard: 258/998/906 m
gegen 259/997/907 m. Das Spiel misst räumlich (mit Höhe), Soulcrest nur über die Karte.

## Offene Datenlücken

Eltnen/Morheim besitzen deutlich weniger Platzierungen als der vorherige Datenbestand.
Abyss Reshanta B fehlt im neuen Index. Unbelegte Monster bleiben ohne Pet-Zuordnung.
Eigene gesetzte Kartenpunkte können durch geänderte Kalibrierung abweichen; sie werden nicht
stillschweigend überschrieben.

Boss-Rush-Erweiterung (2026-10-09): jeder Eintrag in `bosses.json` trägt zusätzlich `icon`
(lokaler NPC-Porträtpfad), `loot` und `sourceRefs` (NPC-Detailseite). Loot-Items enthalten
`id`, `en`, `de`, `icon` und `rarity`; mehrere Tabellen desselben Items werden per exakter ID
zu einer Pool-Zeile zusammengeführt. Der Import berechnet keine Dropchancen. `loot: null`
bedeutet fehlende Daten; `loot: []` eine vom NPC-Datensatz bestätigte leere Liste. Fetch und
Bildkonvertierung bleiben vom Laufzeitprogramm getrennt; sämtliche Icons kommen aus dem
aktiven, genehmigten gaming.tools-Cache.
Lootdetails stehen ausschließlich in `bosses.json`; `data.js` behält nur die schlanken
Boss-Metadaten für die Karte.

## Monstersuche im Map-Tab (2026-10-14)

Die Suche fasst Monster kartenübergreifend nach Namen zusammen, bevor das Limit von 40 Treffern
greift. EN und DE bleiben durchsuchbar; gleiche sichtbare Namen erscheinen einmal, ihre exakten
englischen Quellnamen bleiben gemeinsam erhalten. Ein belegter gleichnamiger Pet-Treffer wird
in der Monsterkarte mitgeführt; eigenständige NPC-/Orts-Treffer behalten ihre eigene Aktion.
Der Index liest vorhandene lokale data.js-Dateien einmal im Hintergrund. Kein neuer Abruf.

Beim Anklicken werden alle Spawns des konkreten Monsters auf der gewählten Karte hervorgehoben
und vollständig eingepasst, unabhängig von ausgeblendeten Kategorien. Die aktuelle Spielerkarte
hat Vorrang, wenn frische Minimap-/Positionsevidenz vorliegt und dieses Monster dort vorkommt.
Sonst bleibt eine passende aktuell betrachtete Karte erhalten, danach die erste belegte Karte.
Eine betrachtete Spiel-Weltkarte oder veraltete Position beweist keinen neuen Spielerort.
Der Follow-Schalter bleibt gespeichert wie zuvor; der Fokus pausiert das Folgen wie Kartenziehen
sechs Sekunden. Alte verzögerte Marker-Popups dürfen einen neuen Fokus nicht wieder verdecken.

Die Infocard zeigt ein spezifisches Marker-Icon oder das zugeordnete Pet-Porträt, aktuelle
Stufe und verfügbare Souls kompakt in einer Zeile. Die zusätzliche Bedarfsübersicht je Aufstieg
entfällt auf Nutzerwunsch. Historisch verdiente Gesamt-Souls ersetzen nie die verfügbare Balance. MAX,
unbekannt, gespeichert/unbestätigt und manuell bleiben getrennt. Zuordnungen folgen ausschließlich
expliziten Marker-Pet-IDs einschließlich belegter Katalog-Aliasse, nicht Namensähnlichkeiten.
Ohne Zuordnung wird kein Soul-Stand erfunden. Farmkarten bieten zusätzlich belegte andere
Quellmonster desselben Pets an; dort zeigt die Notiz ausdrücklich Pet-Farmorte.
