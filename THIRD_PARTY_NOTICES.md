# Third-Party Notices

Soulcrest ist ein inoffizielles, privates, nicht-kommerzielles Fanprojekt.

## Aion 2 / NCSOFT

Aion, Aion 2 und NCSOFT sind Marken oder eingetragene Marken der NCSOFT Corporation.
Kartengrafik, Orts-, Monster- und Pet-Namen sind geistiges Eigentum von NCSOFT. Soulcrest ist
nicht mit NCSOFT verbunden und wird von NCSOFT weder unterstützt noch gesponsert.

Die lokalen Item-, Währungs-, Kristall- und Petgrafiken im Loot-Tracker und
Loot-Overlay sind Spielgrafiken © NCSOFT. Die für die Anzeige verkleinerten Bilder
begründen keine freie Grafiklizenz. Die Faktenzuordnung erfolgt über Item-IDs
und Petnummern; zur Laufzeit werden keine Spielgrafiken heruntergeladen.

Das Symbol `Assets/exploration-complete.png` und die Erkundungslisten-Fixtures stammen aus
vom Nutzer bereitgestellten Spielaufnahmen (Oktober 2026). Sie dienen ausschließlich dem lokalen
Bildvergleich und den Erkennungstests. Spielgrafik © NCSOFT; keine freie Grafiklizenz behauptet.

Die Klassen- und Abyss-Rangicons im PvP-Killfeed sind originale Spielgrafiken © NCSOFT. Sie werden lokal gebündelt; eine freie Grafiklizenz wird nicht behauptet.

## aion2.gaming.tools: Kartendaten und Pet-Katalog

- Quelle: <https://aion2.gaming.tools/>, die `.d.json`-Datendateien und die zugehörigen statischen
  Dateien auf `cdn-hosted.gaming.tools/aion2`.
- **Erlaubnis** (Discord, 2026-10-06, Adain von gaming.tools): Die Daten dürfen genutzt werden, wenn
  wir die `.d.json`-Dateien statt Web-Scraping verwenden und in der App sowie auf einer späteren
  Website sichtbar auf gaming.tools verlinken. Auf Nachfrage bestätigt: Kachelpakete, Marker-Icons
  und Pet-Porträts vom CDN sind eingeschlossen. Die Quellenangabe lautet „aion2.gaming.tools“ als
  klickbarer Link.
  - Umgesetzt: `scripts/fetch_gamingtools.py` ruft nur noch das CDN ab. Website-Seiten werden nicht
    mehr geladen, die frühere WebP-Ausnahme von der robots.txt der Website entfällt.
  - Sichtbarer Link in der Kartenfußzeile (`ATTRIBUTION` in `build_tester_data.py`) und unter
    Info → Quellen und Lizenzen. Eine spätere Website muss den Link ebenfalls tragen.
- Questdaten (2026-10-06): Episoden-Quests der Asmodier und Elyos (seit 2026-10-07, dazu die
  Aufstiegsquests) aus `data/{en,de}/quests/<id>.d.json`. Daraus kommen in
  `data/questlines/asmodians.json` und `elyos.json` (seit 2026-10-07 nur im Archiv-Branch
  `archiv/questrunner-2026-10-07`) Titel, Episoden, Ziele, Aufgaben,
  Zielpositionen und (seit 2026-10-07) die Spawnpunkte der Monster von „Defeat …“-Aufgaben.
- Npcap wird nicht mitgeliefert. Das Setup lädt auf Wunsch den offiziellen Installer von npcap.com
  (Version und SHA-256 fest im Setup); seine Lizenz zeigt der Npcap-Installer selbst.
- Verwendet: Kartenkacheln, Monster- und Objektpositionen, Kibelisk- und Ortsnamen, Pet-Porträts,
  englische/deutsche Namen, Soul-Beute. Faktenbelege je Zuordnung in `data/pets/soul-sources.json`.
- Spielgrafiken und Spielinhalte © NCSOFT. Keine freie Grafiklizenz behauptet; die Erlaubnis von
  gaming.tools betrifft deren Aufbereitung, nicht die Rechte von NCSOFT. Rohdaten, Positionen und Grafiken werden nicht ins Git eingecheckt.
- Änderungen: GTPK-Kacheln in Leaflet-Kacheln zerlegt, Koordinaten umgerechnet, Pet-Porträts
  von WebP nach PNG konvertiert, Soul-Quellen nach belegter Beute zugeordnet und gruppiert.
- Frontend-Code wurde nur zur Prüfung des öffentlichen Dateiformats/Datenwegs gelesen;
  kein fremder Renderer wird in Soulcrest eingebunden.


## Komponenten (werden mit Phase 0/1 ergänzt)

| Komponente | Lizenz | Verwendung |
| --- | --- | --- |
| Leaflet | BSD-2-Clause | Kartenansicht (lokal in `wwwroot/lib/leaflet`) |
| OpenCvSharp4 / OpenCV | Apache-2.0 | Bildverarbeitung, Feature-Matching |
| Microsoft.ML.OnnxRuntime | MIT | Paddle-Zweitlesung |
| PaddleOCR `PP-OCRv6_small_rec_onnx` (aus Grindcrest `data/ocr/paddle-v6-small`, inkl. `SOURCES.md` mit Prüfsummen) | Apache-2.0 | Zweitlesung |
| Microsoft.AspNetCore.Components.WebView.WindowsForms | MIT | Blazor-Hybrid-Host |
| SharpPcap 6.3.1 | MIT | Netzwerk-Loot-Quelle (Opt-in): Mitschnitt über Npcap |
| PacketDotNet 1.4.8 | MPL-2.0 | Netzwerk-Loot-Quelle: TCP/IP-Pakete zerlegen |
| Npcap (nicht mitgeliefert, vom Nutzer installiert) | Npcap-Lizenz | Paketmitschnitt unter Windows |
| Pet-IDs von aion2.gaming.tools (`data/pets/pet-ids.json`, Namen + Nummern) | Faktendaten, Quelle genannt | Zuordnung der Soul-Meldungen |
| Explizite Pet-Artikel-IDs von aion2.gaming.tools (`data/pets/merchant-items.json`) | Faktendaten, CDN-Quelle je Eintrag | Händlerartikel → Pet, offline |
| Pet-Farmregionen von aion2.gaming.tools (`data/pets/merchant-farm-regions.json`) | Faktendaten, Quellen je Eintrag | Karten-/NPC-Orte → Händler-Farmkarten, offline |
| Soul-Beute der Abyss-Monster von aion2.gaming.tools (`data/pets/abyss-soul-sources.json`) | Faktendaten, Quelle je Eintrag | Monster → Pet auf den Abyss-Karten |

Lizenztexte werden mit der jeweiligen Komponente unter `licenses/` abgelegt (Muster: Grindcrest).


## Spielgrafiken und Spieltexte

Für Event-Timer, Daevanion-Boards, Jagdgebiete auf der Karte und die Rangentwicklung des Abyss
Rank Trackers bündelt Soulcrest einzelne Spielsymbole, Skillbilder und Spieltexte von Aion 2 lokal. Sie sind Eigentum von NCSOFT; eine freie Lizenz wird nicht behauptet.
Es gibt keine Laufzeitabfragen.

## Noto Sans Bold – Schrift der Timer-Leiste

Noto Sans Bold 2.015, Copyright 2022 The Noto Project Authors, wird unverändert unter der SIL
Open Font License 1.1 lokal für Timer-Leiste und Menüvorschau mitgeliefert. Font, vollständige
Lizenz und Quellenangaben: `src/Soulcrest.App/wwwroot/fonts/timer/`. Keine Font-Abfragen im Netz.
Die zusätzlichen Reset-/Timer-/Boss-Ersatzsymbole sind eigene Soulcrest-Grafiken.



## Dropklang (2026-10-28)

`Assets/loot-drop-standard.wav` ist eine eigene Soulcrest-Synthese
(`scripts/sound/generate_loot_drop.py`): ein kurzer, nach oben gleitender Sinuston mit
leisem Oberton und gefiltertem Anschlag. Es werden keine fremden Aufnahmen oder Samples
verwendet.
