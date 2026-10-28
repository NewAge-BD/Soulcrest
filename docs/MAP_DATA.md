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
Der aktive Builder liest diesen Cache, belegte Faktentabellen unter `data/pets` und die eigene
Cube-Pooltabelle unter `data/maps` (siehe unten).
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
anschließend WebP-Dateien. Header und Offsets werden geprüft. Seit 2026-10-18 werden die originalen
512-Pixel-WebP-Kacheln bytegleich unter `tiles512/` gespeichert, ohne Neukodierung. Dateistufe z+1,
`tileSize=512` und `minNativeZoom=1` erhalten dieselbe logische Ausdehnung und Referenz-Zoomstufe.
Leaflet und die Trackingreferenz berücksichtigen die Kachelgröße; alte 256-Pixel-Pakete bleiben lesbar.
Die 256×256-Dateien unter `/images/maps/` sind Vorschaubilder. Ein vollständiger erfolgreicher Build
entfernt ausschließlich erkannte generierte alte `tiles/`-Verzeichnisse im Ausgabeordner.
`--skip-tiles` setzt einmalig das neue vollständige Layout voraus.

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

### Sichtbarer Exklusivfilter und Progressionsgattungen (2026-10-24)

Bei aktivem „Nur kartenexklusive Pets“ erklärt eine hervorgehobene Zeile über der
Karte, dass Pets mit Fundorten auf mehreren Karten ausgeblendet sind. Die Checkbox
bleibt als aktiv gekennzeichnet. Das X schließt nur den Hinweis für die laufende
App-Sitzung, auch über Karten-/Tabwechsel; Neustart oder Aus-/Einschalten zeigt ihn
wieder. Minimap/Weltkarte erhalten keinen zusätzlichen Hinweis. Ein-/Ausblenden
berechnet die Kartenfläche neu, ohne Zoom oder Ausschnitt zurückzusetzen.

Die Pet-Progression hat eigene Checkboxen für Cogni, Natura, Varian und Fera, anfangs
alle ausgewählt. Die gespeicherte Auswahl begrenzt ausschließlich automatische
Pet-Ziele vor Soul-, Abschlussnähe- und Dichtebewertung. Leere Auswahl pausiert diese
Ziele; gewählte Dungeons und Strongholds bleiben aktiv. Ein abgewähltes aktuelles
Pet-Ziel wird sofort verworfen, alte Rückmeldungen werden gegen die aktuelle Auswahl
geprüft. Katalog-Aliasse übernehmen die Gattung ihres primären Pets. Kartenfilter,
manuelle Ziele, Routen und Pet-/Loot-Fortschritt bleiben unabhängig.
Kartenlegende und Progressionsauswahl verwenden für die vier Gattungen dieselben
lokalen Symbole und dieselbe gemeinsame Bildzuordnung wie der Pets-Tab.

Boss Rush: Der Builder verknüpft `maps/{id}.namedSpawns` über die exakte NPC-ID mit den
Boss-Punkten aus `interactive-map/{id}`. Je Karte entsteht `bosses.json` mit `mapId` (numerische
Quellkarte) und `bosses[{spawnId,npcId,en,de,x,y}]`; die Koordinaten durchlaufen dieselbe
Umrechnung wie die Marker. `data.js` enthält diese Angaben ebenfalls additiv. Keine Zuordnung
nach ähnlichem Namen. Fehlende Daten bleiben ohne Boss-Ziel; alte Pakete müssen neu gebaut werden.
Mehrere Spawn-IDs je NPC bleiben ohne belegbare Zuordnung und werden ausgelassen. Eine eindeutig
identifizierte Spawn-ID mit mehreren unterschiedlichen Quellpositionen bleibt dagegen im Katalog:
`x` und `y` sind dann beide `null`. Timer, Porträt und Loot sind verfügbar, ein Navigationsziel und
eine Timerzuordnung zu einem einzelnen Kartenmarker entstehen nicht. Gleiche Quellpositionen
werden nur einmal übernommen; keine willkürliche Position wird gewählt. So sind auch Watcher Kaira
und Guardian Lord Nahma in Abyss A enthalten. Der belegte Katalog umfasst 24 Verteron-Bosse,
8 Bosse in Abyss A und 5 in Chaotic Reshanta Middle; Upper hat keine belegten NamedSpawns.

Seit 2026-10-18 enthalten die sieben belegten Lower-Reshanta-Kalenderbosse optional
`calendarSchedule: {kind:2,timeZoneId:"Europe/Berlin",times:["HH:mm:ss"],weekdays:[0..6]}`.
`kind:2` ist die gemeinsame Weekly-Regel; Wochentag 0 ist Sonntag. Kaira: acht Tageszeiten ab
01:00 alle 180 Minuten; Nahma: Freitag/Sonntag 21:00; Executors: Montag/Donnerstag/Samstag 21:30.
Die Aufnahmezeit und Restzeiten wurden vom Nutzer für die EU-Region bestätigt; aktive Cache-Regeln
und separat geprüfte Kalenderkonfiguration stimmen überein. Der Import verlangt die exakte
Map-/Spawn-/NPC-/Schedule-/Regelsignatur dieser sieben Belege. Keine Kalenderzuordnung über Namen,
ID-Präfixe oder Ähnlichkeit; andere oder veränderte Regeln bleiben ohne Kalender. Das Quellflag
`timezoneVerified:false` bleibt unverändert, die EU-Freigabe beruht auf den zusätzlichen Belegen.
Diese Angaben ergänzen fehlende Timer, bestätigen jedoch weder einen Netzwerkempfang noch einen Spawn.

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

## Cube-Gruppen (2026-10-17)

Die Cube Groups verbinden die möglichen Positionen derselben Hidden-Cube-Spawngruppe.

| Karte | Gruppen | Vorhandene Cube-Marker | Verbindungen |
| --- | ---: | ---: | ---: |
| Altgard | 59 | 1.371 | 1.312 |
| Verteron | 95 | 1.440 | 1.345 |

Der Laufzeitvertrag ist `hiddenCubeGroups[{id,number,name,nameDe,color,markers,connections}]`
plus `marker[7].hiddenCubeGroup`; Marker und Kanten verweisen auf vorhandene Markerindizes.
IDs folgen `<karte>-cubes-spawn-<Zahl>`. Ortsnamen sind nur Anzeigelabels (nächstgelegenes Dorf
oder Jagdgebiet zum Gruppenmittelpunkt). Richtungslose minimale Spannbäume verbinden nur Cubes
derselben Gruppe; benachbarte Gruppen erhalten unterscheidbare Farben. Keine Spawnfolge, aktuelle
Belegung, Spawnkapazität oder Respawnzeit. Die Kategorie `Collectibles/Cube Groups` und die drei
getrennten Ebenenschalter bleiben erhalten; es entstehen keine neuen Marker.


## Kartenleistung (2026-10-18)

Ressourcen-, Orts-, Quest- und Sammelicons werden über gemeinsame Leaflet-Canvas-Pfade gezeichnet.
Bilder werden pro URL gemeinsam geladen; nur Symbole im Zeichenbereich werden gezeichnet. Leaflet
behält Popup, Rechtsklick, Filter und Trefferprüfung. Erkundungsstatus, Kibelisk-Graustufen und
Cube-Gruppenfarben werden beim Zeichnen berücksichtigt. Pet-Porträts behalten ihre DOM-Beschriftungen.
Zoom verändert die Treffer- und Zeichenbounds ohne neue Bildobjekte. Ebenenwechsel bündeln Redraws.
Positionsbestimmung, Spielaufnahme, SIFT und Weltkoordinaten bleiben unverändert; die ursprünglichen
Bildpixel und die Abmessungen der zusammengesetzten Trackingreferenz sind identisch.

Die gemeinsame Zeichenfläche hat einen Puffer und wird beim Verschieben innerhalb dieses Puffers
weiterverwendet. Bei Überschreiten, Zoom, Kartenwechsel oder Ansichtsreset wird neu gezeichnet.
Icon-Rastergrößen werden je Quelle begrenzt zwischengespeichert. Pet-DOM und native Overlays
behalten ihre bisherigen Bildformate. Gemessenes vollständiges lokales Paket: 139,19 statt 374,67 MiB.

## Jagdgebietsüberschriften (2026-10-27)

Marker der exakten Kategorie `Regions/Hunting Ground` zeigen das mitgelieferte
Hunting-Ground-Icon über einem großen, dauerhaft sichtbaren EN-/DE-Namen.
Vorhandene Quellpositionen und Namen bleiben unverändert; fehlende deutsche
Namen verwenden Englisch. Icon und Text bilden einen einzigen Marker mit
bestehendem Popup, Rechtsklick und gemeinsamem Legendenfilter. Bei bisher
unbearbeiteten Karten sind Jagdgebiete anfangs sichtbar; gespeicherte
Kategoriesichtbarkeit wird weiter respektiert. Dörfer behalten ihre Darstellung.

Eine eigene Leaflet-Ebene über Pet-Porträts hält die Schrift lesbar, während der
Text keine Kartenklicks abfängt. Beim Zoomen skaliert das bestehende Element
begrenzt mit, ohne neue Marker zu erzeugen. Sprachwechsel aktualisiert nur die
betroffenen Namen. Grenzen werden nicht aus den Mittelpunkten erfunden.

Das lokale Originalicon einschließlich blau-goldener Raute ist © NCSOFT;
die App benötigt dafür keine Spieldateien. Vorhandene Marker werden verwendet,
sodass kein neues Kartenpaket für die Beschriftungen erforderlich ist.
