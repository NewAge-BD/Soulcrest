# Soulcrest

**A free companion app for Aion 2 that helps you complete your pets.** Windows only, runs locally.

*Deutsch weiter unten.*

- **Interactive map, offline**: all maps of Elyos, Asmodians and the Abyss with pet spawns, the monsters that drop each pet's soul,
  Kibelisks, Sealed Dungeons, Strongholds, resources and hidden cubes. Search, filters, your own
  targets and saved routes.
- **Player tracking**: Soulcrest finds you on the in-game minimap and follows you on its map. An
  overlay on the in-game map draws lines to your targets with the distance in game metres.
- **Soul tracker**: counts every soul you pick up and credits it to the right pet. It reads the
  game's network traffic (read-only, see below).
- **Pet window scan**: reads your current progress from the in-game pet window, so you do not have
  to type it in.
- **Exploration scan**: reads the Kibelisk, Sealed Dungeon and Stronghold lists from the world map
  and checks off what you have bound, per character.
- **Progression mode**: always leads you to the nearest pet that still needs souls.

The interface is in English and German.

## Safety: what Soulcrest does and does not do

Soulcrest is **passive**. It reads pixels of the game window (Windows Graphics Capture) and draws
its own overlay windows. It never reads or writes game memory, injects nothing and sends no input to
the game.

The soul tracker reads the game's network traffic **read-only** with Npcap: only the server → client
stream of the game, nothing is decrypted, and only your own character's name is read (to switch
profiles). Nothing leaves your PC. You can turn the tracker off in the settings. Details:
[docs/SAFETY.md](docs/SAFETY.md).

Using any third-party tool with an online game is at your own risk.

## Requirements

- Windows 10 2004 or later, 64-bit
- Microsoft Edge WebView2 Runtime (included in Windows 11)
- [Npcap](https://npcap.com/) for the soul tracker. The setup offers to download and install the
  official installer; Npcap is not bundled.
- Windows OCR language pack English and/or German for the pet window and exploration scans. The
  setup checks it and can install it.
- Aion 2 in **borderless window** mode (not exclusive full screen)

## Install

Download the installer from [Releases](../../releases), run it and follow the short setup tutorial
on first start (enlarge and mark the minimap). Your progress is stored in
`%LOCALAPPDATA%\Soulcrest` and never leaves your PC.

## Build from source

```powershell
python scripts/fetch_gamingtools_dataset.py   # map and pet data from the aion2.gaming.tools CDN (about 1 request/s)
python scripts/build_tester_data.py           # builds imports/generated/mapdata
dotnet build Soulcrest.slnx -c Release
dotnet test Soulcrest.slnx -c Release
dotnet run --project src/Soulcrest.App
./scripts/Build-Installer.ps1                  # Inno Setup 7 installer
```

Requires the .NET 10 SDK (pinned in `global.json`), Python 3 and, for the installer, Inno Setup 7. The map data is not part of
this repository; please keep the fetch script's rate limit.

## Credits

- Map, marker, pet portrait and name data: [aion2.gaming.tools](https://aion2.gaming.tools/), used
  with permission.
- [Leaflet](https://leafletjs.com/), [OpenCvSharp](https://github.com/shimat/opencvsharp),
  [SharpPcap](https://github.com/dotpcap/sharppcap) and more, see
  [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Support

Soulcrest is free and stays free. If you would like to support its development:
[ko-fi.com/newage57976](https://ko-fi.com/newage57976)

## License

The Soulcrest source code is licensed under the
[PolyForm Noncommercial License 1.0.0](LICENSE.md): you may use, change and share it, but not
commercially. Game content and the aion2.gaming.tools data are not covered by this license.

Unofficial fan project, not affiliated with or endorsed by NCSOFT. Aion, Aion 2 and all game content
are trademarks or property of NCSOFT Corporation.

---

## Deutsch

**Soulcrest ist ein kostenloser Begleiter für Aion 2, der beim Vervollständigen der Pets hilft.**
Nur Windows, läuft lokal.

- **Interaktive Offline-Karte** mit Pet-Spawns, Soul-Quellen, Kibelisks, Sealed Dungeons,
  Strongholds und Ressourcen, eigenen Zielen und gespeicherten Routen.
- **Spieler-Tracking** über die Minimap, mit Overlay auf der Ingame-Karte und Entfernung in Metern.
- **Soul-Tracker**: zählt jedes aufgesammelte Soul für das richtige Pet (Netzwerkverkehr, nur
  lesend).
- **Pet-Fenster-Scan** und **Erkundungsscan** übernehmen deinen Stand aus dem Spiel.
- **Progressionsmodus** führt immer zum nächsten Pet, das noch Souls braucht.

Soulcrest ist **passiv**: nur Bildpunkte des Spielfensters und eigene Overlay-Fenster, kein
Spielspeicher, keine Injektion, keine Eingaben ans Spiel. Der Netzwerkverkehr wird nur gelesen, nichts
wird entschlüsselt, nichts verlässt deinen PC. Die Nutzung von Zusatzprogrammen in Onlinespielen
geschieht auf eigenes Risiko.

Installation über den Installer unter [Releases](../../releases); beim ersten Start führt ein kurzes
Tutorial durch die Einrichtung. Spenden sind freiwillig:
[ko-fi.com/newage57976](https://ko-fi.com/newage57976). Lizenz: PolyForm Noncommercial 1.0.0.
Inoffizielles Fanprojekt, nicht mit NCSOFT verbunden.
