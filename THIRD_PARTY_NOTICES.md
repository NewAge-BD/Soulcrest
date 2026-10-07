# Third-Party Notices

Soulcrest ist ein inoffizielles, privates, nicht-kommerzielles Fanprojekt.

## Aion 2 / NCSOFT

Aion, Aion 2 und NCSOFT sind Marken oder eingetragene Marken der NCSOFT Corporation.
Kartengrafik, Orts-, Monster- und Pet-Namen sind geistiges Eigentum von NCSOFT. Soulcrest ist
nicht mit NCSOFT verbunden und wird von NCSOFT weder unterstützt noch gesponsert.

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
| Soul-Beute der Abyss-Monster von aion2.gaming.tools (`data/pets/abyss-soul-sources.json`) | Faktendaten, Quelle je Eintrag | Monster → Pet auf den Abyss-Karten |

Lizenztexte werden mit der jeweiligen Komponente unter `licenses/` abgelegt (Muster: Grindcrest).
