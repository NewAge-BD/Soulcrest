# AION 2 Hidden Cubes (숨겨진 큐브 / 히든큐브 / 隱藏方塊): spawn groups and how positions are organised

Research date: 2026-10-16. Method note: direct page fetches failed in this environment (WebFetch DNS errors; curl got proxy 403 for games.gg, allthings.how, aion2maps.com, thegameswiki.com). All findings below come from search-engine result snippets and summaries of the cited pages, not from full-page reads. Treat exact figures as "reported by the source as indexed", medium confidence. No proprietary data tables were copied.

## 1. Are spawn groups explicit in the game data, or a community construct?

### Takeaway
Several datamine-based sites say the groups come from the client's map spawn tables: one Hidden Cube per spawn group, each group with its own pool of candidate positions. So "group" most likely reflects real client data, not only a community convention. NCSOFT has not officially published totals or the mechanism, and I found no public raw spawn-table or group-ID dump (e.g. on GitHub).

### Cited Findings
- Metabot says the count comes from the Global client's map spawn tables: "one hidden cube per spawn group, 206 in total", each group with a pool of candidate spots. That is why maps show far more dots than cubes. — [Metabot: Hidden Cube Locations Map](https://metabot.gg/en/aion-2/hidden-cubes); [Metabot farming guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Games.gg: 206 cubes across 4 maps with 3,722 possible spawn points, "based on game data analysis". — [Games.gg guide](https://games.gg/aion-2/guides/aion-2-all-hidden-cube-locations/)
- aion2maps.com describes itself as built from the game client's own map data. It lists cubes as "spots across N groups" per region. — [aion2maps.com](https://aion2maps.com/); [aion2maps Verteron](https://aion2maps.com/verteron/hidden-cubes/); [aion2maps Altgard](https://aion2maps.com/altgard/hidden-cubes/)
- Aion2Atlas: NCSOFT has not published an official total. Cubes rotate between spawn points. Smaller totals seen elsewhere (172, 275) count unique cube groups rather than spawn points. — [Aion2Atlas hidden cube map](https://aion2atlas.app/map/hidden-cube/)
- Dissenting datamine: aion2data.com says the client places only 150 cubes (2,362 possible spots). It says Altgard's client spawn list has only 3 cubes / 11 spots, none on land, and claims the server places Altgard's land cubes, so maps made from client files cannot show them. — [aion2data.com hidden cubes](https://aion2data.com/hidden-cubes)

### Inferences
- The majority datamine position is "group = one spawn slot with N candidate positions". aion2maps, Metabot, Games.gg and Aion2Atlas agree on 95 / 59 groups and 1,440 / 1,371 spots, which suggests a shared client-data origin.
- aion2data's Altgard claim conflicts directly with Metabot and aion2maps, which both show 59 Altgard groups and 1,371 spots. Possible explanations: a different client build or region, or aion2data parsing a different table. Unresolved, low confidence either way.

### Gaps
- No public raw datamine (table names, group IDs, spawn weights) was found. GitHub was not reachable for verification.
- Whether the group/slot structure is identical on KR/TW servers and Global is unverified.

## 2. How community tools define and present groups

### Takeaway
aion2maps.com and Metabot present cubes as groups with candidate spots, labelled "N spots across M groups". Aion2Atlas, the Taiwanese tc-imba map (linked on Inven) and other trackers show only candidate spawn points, without a group layer.

### Cited Findings
- aion2maps.com region pages: Verteron "1,440 spots across 95 groups"; Altgard "1,371 spots across 59 groups". They also give sub-zone spot counts (e.g. Verteron's Aullaeu Village: 115 spots) and a 3D map. Its cube guide says every cube on Global needs a key ("Key: Hidden Cube (Bound)", untradeable). — [aion2maps Verteron](https://aion2maps.com/verteron/hidden-cubes/); [aion2maps Altgard](https://aion2maps.com/altgard/hidden-cubes/); [aion2maps cube guide](https://aion2maps.com/guides/hidden-cubes/)
- Metabot: per-map "cubes" (= groups) and candidate-spot totals, e.g. "All 59 Hidden Cubes in Altgard". — [Metabot Altgard](https://metabot.gg/en/aion-2/map/altgard/hidden-cubes); [Metabot farming guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Sub-zone counts differ between tools: Metabot lists Aullaeu Village at 95 spots and Altamia Canyon at 90; Fextralife and aion2maps list Aullaeu Village at 115 and Verteron Fortress Ruins at 93. — [Metabot guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide); [Fextralife wiki](https://aion2.wiki.fextralife.com/Hidden_Cubes)
- Aion2Atlas shows 2,811 spawn points, Verteron and Altgard only, without groups. — [Aion2Atlas](https://aion2atlas.app/map/hidden-cube/)
- Other point-only trackers report different totals: interactivemap.app "2,864 locations across 5 maps"; gamers4.life "2,772". — [interactivemap.app](https://interactivemap.app/aion2/guides/hidden-cube/); [gamers4.life](https://gamers4.life/aion-2/database/ja/hidden-cubes/)
- Inven tip post recommends a Taiwanese interactive map (tc-imba) that shows Hidden Cube spawn locations and Empyrean Traces with progress marking. — [Inven 팁과 노하우 #382](https://www.inven.co.kr/board/aion2/6444/382)
- game735 (Traditional Chinese guide), early-era hand-collected counts: Altgard 52 cubes, Verteron 71. — [game735](https://www.game735.com/thread-445973-1-1.html)
- gaming.tools: no search result surfaced its AION 2 cube page. I could not verify how it presents cubes. Soulcrest's project context says it lists positions without groups.

### Inferences
- There are two kinds of presentation: group-aware (aion2maps, Metabot) and point-only (Aion2Atlas, tc-imba, interactivemap.app, gamers4.life, apparently gaming.tools). Point-only totals that differ slightly (2,772 / 2,811 / 2,864) probably reflect deduplication, extra maps (Abyss?) or patch differences.
- Hand-collected Korean and Chinese counts (52 / 71, "250개 깐 지도") are found-cube logs, not data-derived. They are not comparable to group counts.

### Gaps
- aion2maps' exact grouping algorithm (raw group IDs vs. proximity clustering) is not stated in anything I could read. Its "built from client data" claim suggests raw IDs.
- I found no Abyss or other-map cube data in any source.

## 3. Counts per map and the "one active cube per group" rule

### Takeaway
The consensus figures across 4 maps are 206 groups and 3,722 candidate spots: Verteron 95 / 1,440, Altgard 59 / 1,371, Eltnen 26 / 456, Morheim 26 / 455. Soulcrest's 1,371 (Altgard) and 1,440 (Verteron) match exactly. "One cube per group at a time" is the majority community reading, but at least one guide claims larger groups can host several cubes at once.

### Cited Findings
- Per-map cubes / candidate spots: Verteron 95 / 1,440; Eltnen 26 / 456; Altgard 59 / 1,371; Morheim 26 / 455. Eltnen and Morheim cubes can be opened without a key "when the regular version spawns". — [Metabot farming guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Same totals (206 / 3,722) and the 95 / 59 / 26 / 26 split appear on Games.gg and wikily.gg. — [Games.gg](https://games.gg/aion-2/guides/aion-2-all-hidden-cube-locations/); [wikily.gg](https://wikily.gg/aion-2/hidden-cubes)
- Verteron has 95 marked spots and Altgard 59 in one guide. That source counts groups as "spots". — [allthings.how](https://allthings.how/aion-2/map/hidden-cubes/)
- When a cube respawns it can appear at any spot in its group, not necessarily where it was opened. — [Metabot farming guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Contrary claim: "most [groups have] one cube for every five spots". A 10-spot group could have up to 2 cubes active and a 30-spot group up to 6. — reported in search summaries of [space4games](https://space4games.com/en/games-en/aion-2-hidden-cubes/) / [aoeah](https://www.aoeah.com/news/4890--aion-2-hidden-cube-locations-map-respawn-time-rewards-sound-keys--farm-route). I could not pin it to one page.
- Only a limited number of cubes are active on a map at once. Map markers are candidate positions, not simultaneous cubes. — [aoeah](https://www.aoeah.com/news/4890--aion-2-hidden-cube-locations-map-respawn-time-rewards-sound-keys--farm-route); [Aion2Atlas](https://aion2atlas.app/map/hidden-cube/)
- aion2data conflicting total: 150 cubes / 2,362 spots (Verteron 95, Altgard 3, Eltnen 26, Morheim 26). — [aion2data.com](https://aion2data.com/hidden-cubes)

### Inferences
- Arithmetic check: 1,440 + 456 + 1,371 + 455 = 3,722, which matches the 206-group total. Aion2Atlas's 2,811 = 1,440 + 1,371 (Verteron + Altgard only), so the point-only and group-aware sources agree on the underlying positions.
- Average spots per group: Verteron about 15.2, Altgard about 23.2, Eltnen/Morheim about 17.5 (my arithmetic).
- "One active per group" is consistent with the "one cube per spawn group" datamine phrasing. The "1 per 5 spots" claim is single-sourced and unverified. It may confuse group size with the number of groups in a sub-zone. Confidence: medium for one per group, low for the 1:5 claim.
- aion2data's 150 = 206 − 56 (Altgard 59 → 3). This is consistent with its claim that Altgard land cubes are absent from the client spawn list it parsed. It does not match the other datamines.

### Gaps
- No controlled experiment (e.g. two observers checking a whole group at once) was found that proves exactly one active cube per group.
- Respawn timer per group is not established officially.

## 4. Practical farming advice

### Takeaway
Players recommend: start with the key-free maps (Eltnen / Morheim), then the dense Verteron / Altgard sub-zones. Sweep all candidate spots of a group, since a respawn can move to any spot in the group. Rotate 2–3 channels per loop, and don't rely on a fixed timer.

### Cited Findings
- Route: Elyos go Eltnen → Verteron, Asmodians Morheim → Altgard. Eltnen order: Eiron Hill (7), Eltnen Barrier Tower Ruins (6), Ruberein Highland (5). Morheim: Nephra Snowfield (7), Iralcig Highland (5), Muspel Volcano Zone (5). Verteron: Altamia Canyon (9), Ellun River Midstream (7), Verteron Fortress Ruins (7), Aullaeu Village (6), which together hold 29 of 95 groups. Altgard starts at Dredgion Crash Site, then Purifying Forest, Battlescar Mound, Western Gribade Highland. — [Metabot farming guide](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Respawn: no fixed timer confirmed. Estimates are 1 min to 1 h per group, or 30 min to 3 h (Korean reports). — [aoeah](https://www.aoeah.com/news/4890--aion-2-hidden-cube-locations-map-respawn-time-rewards-sound-keys--farm-route); [Metabot](https://metabot.gg/en/aion-2/guides/hidden-cubes-guide)
- Official-forum user tip (not NCSOFT staff): the regen time doesn't seem exactly fixed, so just run a loop when you have time. — [plaync 노하우 게시판](https://aion2.plaync.com/ko-kr/board/qnanknowhow/view?articleId=692ff1c127be63556e2a38a8); follow-up [2탄](https://aion2.plaync.com/ko-kr/board/qnanknowhow/view?articleId=69423fbd1294f75cb5c4ec64)
- Bundle 2–3 channels per loop and rotate channels to avoid competition. — [판다랭크](https://pandarank.net/contents/69268bc72b26afcbc18e8c6d)
- Cubes are hidden in high or obscure spots (cliffs, corners, underwater). Flying and diving help. If a spot is empty, check later or switch channel. — [game735](https://www.game735.com/thread-445973-1-1.html); [redwendy](https://redwendy.com/archives/2284)
- Single-source tip: Immortal Isle is called the most consistent Asmodian hotspot. — [aoeah](https://www.aoeah.com/news/4890--aion-2-hidden-cube-locations-map-respawn-time-rewards-sound-keys--farm-route)

### Inferences
- For a companion app, the useful unit is the group: show all candidate spots of a group, and mark the group (not the spot) as looted with a cooldown. Point-only data (gaming.tools) cannot support this without a grouping derived from elsewhere.
- Average group sizes of about 15–23 spots mean a full sweep of one group can be costly. Clustered sub-zone routes (Metabot) are the practical compromise.

### Gaps
- Unverified: whether looting resets per channel or per server. Channel-hopping advice implies per-channel instances.
- I found no source describing how candidate positions within a group are weighted (uniform or not).
