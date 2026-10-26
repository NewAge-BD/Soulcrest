# Soulcrest – Patch notes

Short and written for players. English edition of `PATCHNOTES.md`, from 0.1.28 on.

## 0.14.0 – 2026-10-26

- **New:** **Loot Tracker** tab: sessions with Kina (bound/unbound), Kina/h, items, souls and the
  route you walked, per character with Combat Score and Gear Score, grouped by farm days
  (07:00–07:00 UTC). Each session can be opened and deleted individually.
- **New:** Open-world PvE is recorded automatically: your own loot starts a session, which ends after
  two minutes without loot (adjustable). After 1,000,000 daily Kina a "Limited Kina Session"
  follows. Dungeon runs get their own entries.
- **New:** Lifeskill sessions: successful and failed gathering builds a farm route between gathering
  spots, which you can show on the map and keep as a route.
- **New:** Loot overlay with daily Kina, session Kina, Kina/h and a loot list with item icons and
  quality colours; white gear is grouped as "Common Gear". Adjustable background transparency,
  optionally shown automatically when a session starts.
- **New:** Special drops (Unique, Epic, Mythic, Special) glow with an animation and announce
  themselves briefly with their own sound; configurable per rarity, including your own MP3.
- **New:** Boss Rush guidance on the in-game world map, minimap and Soulcrest map: spawned bosses in
  green with time since spawn, the next spawn in red with a countdown, more follow-up targets
  adjustable, unconfirmed markers faded. Spawned bosses disappear after one minute; a switch shows
  older ones too.
- **Changed:** "Loot & pet progress" is now called "Pet progress".

## 0.13.0 – 2026-10-24

- **New:** Progression mode: Cogni, Natura, Varian and Fera can be chosen individually for automatic
  pet targets, with their icons. Your choice is saved; with no genus selected only pet targets pause,
  sealed dungeons and strongholds continue.
- **New:** When the map filter "Map-exclusive pets only" is active, a notice appears and the checkbox
  is highlighted, so missing pets don't look like a data error.
- **Fixed:** The level-up animation in the loot overlay was missing in the new themes. It is back and
  now repeats until the notification fades.

## 0.12.0 – 2026-10-22

- **New:** Daevanion overlay: stat highlighting. Pick individual stats to the right of the board;
  matching nodes get a light trail running around them, even with the additions hidden. Your
  selection is saved, and "Clear all highlights" resets it.
- **New:** An "Overlay on/off" switch at the top right of the board hides and shows the node additions
  directly, without Alt or unlocking. Your choice is saved.
- **New:** Activated stat nodes appear brighter with a subtle tint of their node colour.
- **Fixed:** Tooltips over the board no longer make the Daevanion overlay flicker; only the covered
  nodes are left out.
- **Fixed:** Icons on the in-game world map no longer flicker when the world map's window symbol is
  not recognised.

## 0.11.1 – 2026-10-22

- **Changed:** The option "Rest in instances during visual detection" can now also be set with network
  tracking on, as long as no network location detection is available.
- **Fixed:** Damaged map caches are detected and rebuilt automatically.
- **Fixed:** Pet level and soul progress are always shown as one consistent state.
- **Fixed:** Soulcrest shuts down more reliably without the freeze detection lingering.

## 0.11.0 – 2026-10-21

- **New:** Multiple advance warnings for bosses, events, resets and custom alerts: combine 2, 5, 10 and
  15 minutes freely and add your own times. "Off" pauses the reminder and keeps your selection;
  existing settings carry over.
- **New:** The map remembers zoom and view for each map separately, also across restarts.
- **Changed:** Daevanion overlay: stat nodes now have a frame in the node's colour (green, blue, orange)
  with larger numbers; the abbreviation sits below the frame, double bonuses in two columns.
- **Changed:** The network switch is now called "Network tracking".

## 0.10.2 – 2026-10-21

- **New:** The Daevanion overlay now also supports the **Ariel** board in all eight classes, including
  its PvE and boss bonuses and double bonuses.

## 0.10.1 – 2026-10-21

- **Fixed:** The Daevanion overlay disappeared while levelable nodes were glowing blue. It now stays
  visible during the animation.

## 0.10.0 – 2026-10-21

- **New:** Daevanion overlay: when a Daevanion board is open in the game, Soulcrest shows on every node
  what it gives: skill nodes with their skill image and "+1", stat nodes with an abbreviation and the
  exact bonus (e.g. "Crit +5"). For Nezekan, Zikel, Vaizel, Triniel and Azphel in all eight classes, in
  English and German.
- **New:** The overlay aligns itself to the board automatically and is always click-through. Soulcrest
  detects your class by itself; the on/off switch, abbreviation legend and a preview of all boards are
  in the Overlays tab.

## 0.9.1 – 2026-10-20

- **New:** **Overlays** tab: all eight overlays in one place, with settings, previews, language,
  visibility in recordings and hotkeys. The feature views link straight to it.
- **New:** Place mode: all overlays appear with sample data on the game monitor (even without the game
  running) and can be moved and resized, optionally with a 16-pixel grid and snapping. Escape keeps the
  last state.
- **New:** "When to show" for all overlays together: only while AION 2 is in the foreground (default)
  or always.
- **New:** Progression mode: option "Highest mob density first". It picks the pet with the most spawns on
  the current map and leads to its densest farm area (200 m radius) instead of the nearest spawn.
- **New:** Overlay themes in the Overlays tab: Aion2 (stylised or neutral), Obsidian, Northern Lights,
  Runes and Soulcrest legacy. The merchant overlay keeps its previous style.
- **Changed:** The Aion2 theme is the new default. Anyone who explicitly chose the old look before keeps
  "Soulcrest legacy".
- **Improved:** Faded markers on the minimap no longer have background plates and cover less.

## 0.8.3 – 2026-10-20

- **Improved:** After an instance (e.g. Abyss → Altgard) Soulcrest recognises the new map faster: while
  resting in instances it looks every 2 seconds and checks several maps at once.
- **Improved:** "My Routes" is more compact: Repeat is directly accessible, long target lists show the
  first three entries and summarise the rest.
- **Fixed:** A route's Repeat setting is now saved permanently.
- **Fixed:** The merchant overlay flickered in some shop views.

## 0.8.2 – 2026-10-18

- **Removed:** The DPS preview has been removed for now.

## 0.8.1 – 2026-10-18

- **New:** **DPS** tab as an early preview: locally stored fights with selection by run, attempt and
  participant, skill and buff details and boss HP; former group members stay visible. Unknown or
  incomplete values are marked as such, not shown as 0. Measurement is not yet confirmed for all
  fights.
- **Changed:** Boss reminders disappear as soon as their countdown runs out, even without a spawn
  confirmation.

## 0.8.0 – 2026-10-18

- **New:** In-game timer bar: a slim icon bar with countdowns for events and bosses, no background,
  size 60–200 %, selectable sections, with a preview in the timer menu.
- **New:** Boss reminders appear as notices of their own, even with the timer HUD off, and stay until
  the spawn is confirmed. You set the display duration once, centrally.
- **New:** The patch notes tab is a searchable version reader.
- **New:** The merchant overlay shows the pet category above the name and the number of spawn points per
  farm map.
- **Improved:** The boss list sorts confirmed spawns first; boss times are kept permanently. Missing
  times of the Lower Reshanta bosses are filled in from the game calendar.
- **Improved:** Events in Alerts can be expanded; overlay sections are selectable. The boss search also
  finds Reshanta maps under "Abyss". New Dimensional Invasion symbol.
- **Improved:** The interactive map loads more compact data and draws its symbols more smoothly.
- **Changed:** "Loot & Pet Progress" hides itself while there is nothing to show.
- **Removed:** The boss loot pool in the Boss Rush tab.

## 0.7.6 – 2026-10-17

- **Changed:** Cube Groups now show the actual spawn groups of the Hidden Cubes instead of areas:
  59 groups on Altgard, 95 on Verteron. The lines connect the possible locations of the same group;
  spawn order, occupancy and respawn times remain unknown.

## 0.7.5 – 2026-10-16

- **New:** **Cube Groups** layer (below Hidden Cube, separate for map, minimap and world map): on
  Altgard and Verteron the Hidden Cubes of one area are joined into a coloured group, so you can fly
  them off easily. Each cube belongs to the nearest village or hunting ground; the lines connect the
  real spawn locations. The "?" in the legend explains the layer.

## 0.7.4 – 2026-10-16

- **New:** Pet level-ups are celebrated in the progress overlay: levels 1 and 2 light up for 30 seconds
  right in the focus or "Recent loot" row, MAX gets its own green success card. Pets at MAX no longer
  appear under "Recent loot".
- **Fixed:** The merchant overlay flickered, dropped out after a while or jumped by one pixel.

## 0.7.2 – 2026-10-15

- **Improved:** The overlay on the open world map follows panning and zooming noticeably more directly:
  fresher frames and a shorter HDR path (about 12 ms less delay on average, measured live).
- **Improved:** The diagnostics package breaks down the world map delay into capture, drawing and
  display.

## 0.7.1 – 2026-10-14

- **New:** The monster search in the map tab lists every monster once, across all maps (EN/DE).
  Selecting one highlights all its spawns and zooms to fit; a compact info card shows the pet's level
  and soul count and switches to its farm map on request.
- **Improved:** The pet layer's level selection is clearer: tick level 1, level 2 and MAX under
  "Show levels".
- **Improved:** Less CPU load: the interactive map draws the player and targets more efficiently, and
  the minimap and world map overlay prepares frames without backlog (measured roughly 10–20 % less load,
  shorter overlay delay).

## 0.7.0 – 2026-10-14

- **New:** At the Black Cloud Merchant every pet offer shows your soul count below it (e.g. 0/5, MAX) and
  where the pet is farmed (Abyss, Altgard, Verteron or "Special" without a farm spot on these maps).
  MAX is green, Special amber and missing pets (0/5) light gold. Turn it on or off under Options →
  Overlays; the overlay is always click-through.
- **Changed:** Pets form one shared collection, no longer split by server. Your existing progress is
  taken over (backed up first) and manual corrections stay.
- **Improved:** Souls now count even when Soulcrest is started in the middle of a game session.

## 0.6.3 – 2026-10-13

- **Fixed:** In the Rift (and on other new server connections) soul counting stopped after about
  13 seconds. New connections are now captured in addition, without resetting the running capture.
- **Improved:** The diagnostics package tells the causes of capture gaps apart more precisely.

## 0.6.2 – 2026-10-13

- **Fixed:** Pet levels and exploration were sometimes not taken over after login (more login variants,
  lost first packets of a new connection). Now confirmed live after several direct character switches.
- **Fixed:** Soul counting could stop for a while; pet level-ups and the first acquisition of a pet are
  taken over reliably, also at max level.
- **Fixed:** A faulty partial list at login now only discards itself; pets, quests and Kibelisks are
  confirmed independently of each other.
- **Fixed:** After an instance (e.g. Abyss → Altgard) the new map was sometimes not recognised.

## 0.6.1 – 2026-10-13

- **New:** Completed sealed dungeons and strongholds are taken over from the game: right when you
  complete them and at login from the full exploration state. Kibelisks are ticked off when you bind
  them and at login. Exploration now only shows "completed" or "not completed".
- **Fixed:** Soul counting sometimes stopped after a few souls when the game opened an additional
  connection. Unknown data formats no longer stop the counting.
- **Fixed:** Boss timers showed as unconfirmed shortly after they arrived; they now stay valid for the
  whole session until the connection drops.
- **Improved:** The setup explains that pets and exploration need the character selection once (log in
  again) and shows per area whether the data has arrived.

## 0.6.0 – 2026-10-12

- **New:** Pet levels, available souls and the exploration progress of the logged-in character are
  detected passively from your own game traffic, without scans. A status card shows whether detection
  is active. Existing progress is kept (a backup is made before the switch) and manual corrections take
  precedence.
- **New:** Boss timers show right at the boss symbols on the interactive map.
- **New:** The map legend has three columns: minimap, interactive map and world map can be set
  separately, also for places, NPCs and monsters.
- **New:** Sealed dungeon stops of the leveling routes carry their name instead of "Stop".
- **Improved:** Boss Rush, exploration progress and Alerts are laid out more clearly for 16:9; the
  legend is more compact.
- **Removed:** The pet scan and exploration scan by text recognition and the Windows OCR language packs
  in the installer. Dungeons and strongholds are no longer ticked off by approaching or entering them;
  their status comes from the game and can be corrected in the exploration overview.

## 0.5.0 – 2026-10-12

- **New:** **Alerts** tab with event timers for EU servers (German time): Shugo Festival, Dimensional
  Invasion, Spacetime Rift (with portal phase), Abyss Siege plus daily and weekly reset. Timeline over
  12, 24 or 48 hours, running and next events, opt-in reminders with a warning ahead.
- **New:** Your own reminders: hourly, daily, weekly, every N days, at a minute interval or once, each
  with overlay and/or sound.
- **New:** Boss reminders from the captured boss timers, shared between Alerts and Boss Rush: per boss
  a warning off, 2, 5, 10, 15 minutes or a time of your own.
- **New:** Sounds: a short standard double tone or your own MP3 files from a shared library, chosen and
  tested right on every event and boss card.
- **New:** Overlays for timers, short notifications (visible even when the minimap is hidden) and an
  event timeline (off by default); all compact, freely resizable with a drag handle, with lock, Alt
  unlock and hotkeys of their own.
- **New:** The map tab shows a compact timer list of all events and selected bosses.
- **Fixed:** The interactive map kept jumping back to the Abyss after teleporting away (stale position
  or capture).

## 0.4.7 – 2026-10-11

- **Improved:** The installer shows both OCR languages (English and German) with their status; packs
  already installed are marked as such. German is optional and helps when the game runs in German or you
  switch the game language. What matters is the game's language, not Soulcrest's interface language.

## 0.4.6 – 2026-10-11

- **New:** The loot overlay shows the soul name in small grey text under the monster name
  (`Soul: <Pet>`). The focus pet no longer appears under "Recent loot" as well; its drops of the last
  minute show as +N on the focus card.
- **New:** The pet scan runs right inside the setup; marked results are applied on "Next".
- **Improved:** The exploration scan detects the map and the open list by itself and follows them, also
  when they change. Its start now sits at the top of the Exploration tab.
- **Improved:** Cut-off place names (long dungeon names) are matched against the full English and German
  names; map labels next to them no longer get in the way. The completion tick is found at very small
  and very large UI scales too.
- **Fixed:** An overlay could sometimes still be moved after Alt was released. The lock now shows the
  real state and locks at once.

## 0.4.5 – 2026-10-11

- **New:** Exploration overview in the Exploration tab: counters per category, search (EN/DE),
  Open/Done/Locked filters and a checklist. It follows the map you are on in the game automatically (can
  be pinned), suggests the nearest open place and names the requirements of locked dungeons.
- **Improved:** The progression mode follows a map change in the game (e.g. Altgard → Abyss), even with
  "Follow player" turned off.
- **Improved:** Reworked pet scan tab: three steps, search and filters, results to check first,
  corrections right in the row, applying all marked results at once.
- **Fixed:** The pet list only shows pets with a confirmed soul ID; duplicates (e.g. Modified Karnif) are
  merged and "Gatekeeper" is no longer a pet of its own. Cut-off names such as "Aultrossts Doppelga…"
  are matched correctly.
- **Changed:** Player tracking and "Follow player" are now on by default; saved settings stay.

## 0.4.4 – 2026-10-11

- **Fixed:** Switching maps could leave the interactive map stuck with an error (stale target markers of
  the previous map).
- **Improved:** The exploration scan reads greyed-out, low-contrast exploration lists and ultrawide
  resolutions more reliably; completing still needs two unambiguous readings.
- **Improved:** New scan tile with three steps, start/stop and separate counters; the in-game scan helper
  has a clear legend (green = name read twice).

## 0.4.3 – 2026-10-11

- **New:** Sealed dungeons are ticked off when you enter them, even without the progression mode: an
  instance has to follow a position within the completion radius. Walking past no longer ticks them off.
  Strongholds still only need the radius.
- **Improved:** Target markers for sealed dungeons, Kibelisks and quests now look like the resource
  markers: a ring around the game's symbol, the line starting at its rim, icon and distance beside the
  ring. This applies in every mode (marked, progression, leveling routes).

## 0.4.2 – 2026-10-10

- **Fixed:** No extra symbol sits inside the ring of marked resources and hidden cubes when their category
  is shown on the map; the game's own symbol stays visible.

## 0.4.1 – 2026-10-10

- **Improved:** In-game target markers no longer cover the resource symbols: the ring lies around the
  symbol, and arrows start and end at its rim. Marked resources and hidden cubes show their icon beside
  the ring instead of their name; the distance stays.

## 0.4.0 – 2026-10-10

- **New:** Leveling routes for Asmodians and Elyos, episodes 2–6 with their level range. The route leads
  stop by stop across the map, remembers the progress per character and continues after a restart.
  Monster tasks branch off to the nearest matching spawn.
- **New:** In-game leveling overlay with the current stop, progress and Next/Back.
- **New:** The progression mode follows several targets at once: pets, sealed dungeons and strongholds in
  any combination, the nearest open target first. "Closest to the full level" is now an option under
  "Goal per pet".
- **New:** Overlays can be operated directly: a lock button to unlock them, moving with Alt held. All
  overlay shortcuts can be set freely under Options.
- **New:** While the ESC menu is open, Soulcrest hides its overlays.
- **New:** Routes are saved in Soulcrest's own `.soulroute` format. Existing routes are taken over
  automatically, and the leveling routes are added.
- **Improved:** The pet scan overlay marks every card the result list asks you to check, including
  uncertainly recognised and unknown locked pets.
- **Improved:** Tidier map tab: explanations sit behind a ? symbol.
- **Improved:** Boss Rush loot sorted by rarity and colour-coded.
- **Fixed:** The pet scan overlay also appears when the pet window covers the minimap.

## 0.2.8 – 2026-10-09

- **New:** Boss Rush with its own tab. Soulcrest reads the field boss spawns passively from the game
  traffic (open the in-game boss list once), leads you from boss to boss in spawn order and links the
  next spawn as a dashed line. It can also be toggled in the right map sidebar.
- **New:** Boss overlay with the upcoming spawns, portraits and countdowns; count, size and position
  adjustable.
- **New:** Alerts for selected bosses with advance warning, display duration and optional sound.
- **New:** Expandable loot pools for each boss with item icons, German/English names and search.
- **Improved:** Boss timers survive teleports and an app restart.
- **Improved:** Modern in-game overlays. Loot tiles show their rarity in colour; the loot focus follows
  the marked pet or the progression target.
- **Improved:** Ctrl+Alt+L unlocks the loot and boss overlays together for moving.
- **Improved:** While setup installs the Windows OCR package, the bar keeps moving and a note says this
  can take a few minutes.
- **Changed:** The "Pet window" tab is now called "Pet Scan".

## 0.2.7 – 2026-10-08

- **New:** All progress scans (pets, Kibelisks, dungeons, strongholds) work with the German game client.
- **New:** The pet scan shows the recognised name and value on every card. Ctrl + left-click on the value
  corrects it right away.
- **New:** The language of the names in the loot overlay can be chosen under Options; by default it follows
  the game.
- **Improved:** The pet scan reads card values much more reliably: it checks the level in the circle
  against the counter, ignores pictures taken while scrolling and marks conflicting values for checking.
- **Improved:** The pet scan starts faster.
- **Improved:** When the dungeon or stronghold list shows 100%, the exploration scan completes the whole
  category; no scrolling needed. Below 100%, only places with a completion check in the same row count.
- **Improved:** Truncated German place names and "Eingang zu …" are matched more reliably.
- **Changed:** New Soulcrest logo.

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
