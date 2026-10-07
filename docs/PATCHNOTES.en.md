# Soulcrest – Patch notes

Short and written for players. English edition of `PATCHNOTES.md`, from 0.1.28 on.

## 0.2.5 – 2026-10-07

- **New:** The setup now also leads to the pet scan and asks about exploration: if your character has
  explored everything already, one click checks it all off (with undo); otherwise it takes you to the
  exploration scan.
- **Changed:** The loot tracker overlay shows the name of the monster you hunt instead of the pet's name
  (e.g. "Soft Breeze Spirit" instead of "Lesser Wind Spirit").
- **Improved:** Small, strongly zoomed minimaps over plain ground (coast, sand) no longer keep losing the
  position; while the map moves along cleanly, Soulcrest holds the position.
- **Improved:** The scans' hints show in recordings when "Show overlays in recordings" is on.
- **Fixed:** The pet scan no longer mixes up pets that differ only in colour (e.g. Stone Spirit and Odyle
  Stone Spirit).
- **Fixed:** An unknown pet briefly showed another pet's progress during the scan.
- **Improved:** The diagnosis package contains the last picture of the pet scan.

## 0.2.4 – 2026-10-07

- **New:** Guard danger zones. The guards at the Kibelisks kill players of the other faction with one hit;
  Soulcrest shows a red, half-transparent zone around them on the minimap and the full-screen map. In the
  setup you pick your faction: Elyos see the zones on Altgard, Asmodians on Verteron. Radius (default
  40 m) and maps under Options → Overlays.
- **New:** The pet overlay can be dragged bigger or smaller at any corner while unlocked (Ctrl+Alt+L);
  everything scales along.
- **Changed:** Marked pets carry the name of the monster that drops their soul in the overlay (e.g.
  "Drana Mutant" instead of "Drana Mutant Brute").

## 0.2.3 – 2026-10-07

- **Improved:** The overlay now follows the map almost without delay (about 20 instead of 150 ms). The
  capture often dropped new game pictures and worked with old ones, especially with HDR.
- **Improved:** While the world map is dragged, the overlay stays on the map; before, the list and bars
  at the edges held it back.
- **Improved:** Switching between the world map and the small map takes about 0.2 instead of 2–3 seconds.
- **Improved:** Clearly less processor load while the world map is open.

## 0.2.2 – 2026-10-07

- **Improved:** With the world map open, the in-game overlay follows the map smoothly and no longer
  trails behind while it is moved.

## 0.2.1 – 2026-10-07

- **New:** Patch notes are now available in English too; Soulcrest shows them in the interface language,
  also in the update notice.
- **Improved:** The Windows title bar is dark like Soulcrest; on Windows 11 it takes the colour of the
  top bar.
- **Fixed:** When a running route is ended with "Remove all" or replaced by another one, its repeat
  switch is off afterwards.
- **Fixed:** An error that keeps recurring no longer opens notice after notice: at most one at a time,
  the same error at most once a minute.

## 0.2.0 – 2026-10-07

- **New:** Soulcrest 0.2 – the first public stage: free on GitHub, with updates right from the app.
- **New:** Progression mode follows the unlock order of the sealed dungeons. On Verteron, Distorted
  Cave → Fissure Cave → Rift Cave open one after the other, on Altgard Lost Ruin → Twisted Pit → Rift
  Fissure; the Ruins of the Ancient City of Ru only after every other sealed dungeon of the map. Locked
  dungeons are skipped, and the map popup says what is still missing.
- **Changed:** Right-clicking Kibelisks, sealed dungeons and strongholds marks them as a target again.
  Binding or checking them off is now in the left-click popup.
- **Fixed:** The "Regions" switch is gone; it did not show anything. Region names are switched by the
  "Regions" layer.

## 0.1.31 – 2026-10-07

- **New:** HDR capture. If your monitor runs in HDR, Soulcrest captures the game in full HDR depth and
  converts it for recognition. Before, the picture arrived heavily overexposed on HDR monitors.
- **New:** Routes have one colour. Click the colour dot in front of a route to pick it; its stops and
  lines on the map and in the game take it on. New routes get the next free colour.
- **Improved:** Updates download only about 100 MB instead of 400 MB as long as the map data stays the
  same. The update runs quietly and restarts Soulcrest afterwards.
- **Improved:** Marked targets and route stops show their icon even when their layer is hidden.
- **Changed:** Tidier map page: tracking as well as targets and routes each share one tile, explanations
  sit behind "Help". The pet filters (levels, map-exclusive only) are now under "Pets" in the layers.

## 0.1.30 – 2026-10-07

- **New:** Soulcrest is public on GitHub: <https://github.com/NewAge-BD/Soulcrest>. Every version is
  available there as a Windows installer under "Releases".
- **New:** Update search. After the start Soulcrest looks for a new version and shows its patch notes;
  "Update now" downloads the installer, checks it and starts it. Under Info you find "Check for updates"
  and the switch for the search at start.
- **New:** Under Info you can support Soulcrest on Ko-fi, entirely voluntarily.
- **Changed:** Licence PolyForm Noncommercial 1.0.0 (no commercial use).
- **Removed:** The old loot tracking by text recognition; souls are counted by the network tracker only.

## 0.1.29 – 2026-10-07

- **New:** The "Patch notes" tab shows what changed in each version. The installed version is marked.

## 0.1.28 – 2026-10-07

- **Improved:** Player tracking needs about half the CPU. On the move the load dropped from 1.4 to 0.7
  processor cores in the test, standing still to less than half a core.
- **Fixed:** A file locked for a moment (virus scanner, backup) no longer crashes Soulcrest or stops the
  tracking. Soulcrest retries the save and keeps counting the souls.
- **Fixed:** The loot tracker no longer misses souls while a network connection is being opened.
- **New:** Unexpected errors go to `logs\errors.log` (also in the diagnosis package). For errors in the
  window a notice appears and Soulcrest keeps running.
