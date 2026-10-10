// Soulcrest map: Leaflet (CRS.Simple) over the local map data package.
// Data comes from https://mapdata.soulcrest/ (WebView2 virtual host -> mapdata folder).
window.soulcrestMap = (() => {
    const GROUP_COLORS = {
        Regions: '#94a3b8', Locations: '#f8fafc', Collectibles: '#c084fc', Quests: '#facc15',
        Pets: '#5eead4', 'City Services': '#fde68a', NPCs: '#9ca3af', NPC: '#9ca3af', Resources: '#4ade80',
        Monsters: '#f87171', Spawns: '#fb923c',
    };
    const MONSTER_COLORS = {
        Fera: '#ef4444', Cogni: '#3b82f6', Natura: '#22c55e', Special: '#67e8f9', Varian: '#eab308',
        'Wild Monsters': '#a8a29e', 'Named Bosses': '#f97316',
    };
    const RESOURCE_COLORS = ['#4ade80', '#60a5fa', '#f87171', '#fbbf24', '#e5e7eb', '#a78bfa', '#2dd4bf', '#fb7185', '#a3e635', '#38bdf8', '#f472b6', '#facc15', '#c4b5fd'];
    // Groups drawn with their own icons; everything else is a fast canvas dot.
    // Resources and collectibles (hidden cubes) use their legend icons too, not dots (user request 2026-10-04).
    const ICON_GROUPS = new Set(['Pets', 'Locations', 'City Services', 'Quests', 'Resources', 'Collectibles']);
    // Pet hunting first: everything else starts hidden and can be switched on in the sidebar.
    const DEFAULT_VISIBLE_GROUPS = new Set(['Pets', 'Locations']);

    let map, base, manifest, dotnet, canvas;
    let current = null;          // map payload
    let tiles = null;
    let catLayers = [];          // per category L.LayerGroup
    let catVisible = [];
    // Resource kinds (Gem: Sapphire, Diamond, Ruby): per category a Map kind name -> L.LayerGroup inside
    // the category layer, so each kind can be hidden on its own (user request 2026-10-05).
    let kindLayers = [];
    let petRefs = [];
    let markerRefs = [];         // per marker index -> leaflet layer
    let markerByPos = new Map(); // "x,y" -> marker index (marked targets find their symbol)
    let regionLayer = null, focusLayer = null;
    let doneSet = new Set();
    let hiddenSet = new Set();
    let lang = 'en';
    const ui = (de, en) => lang === 'de' ? de : en;
    let fitZoom = 0;             // zoom after fitting the playable area: icons have their base size there
    let player = null;
    let followPausedUntil = 0;
    let petLabels = {};          // petId -> "St. 1 · 6/25"
    let targets = [];            // marked targets [{id, map, x, y, name, color}]
    let targetLayer = null;
    // Pets of the marked targets and of the progression target: all their spawns get a ring and their
    // soul monsters a dot, and level filters do not hide them (user request 2026-10-06).
    let markedPets = new Set();
    let spawnLayer = null;
    let spawnKey = '';
    // Progression mode: always the nearest spawn of a pet that has fewer souls than the goal (5/30/105).
    let progression = { enabled: false, goal: 5, souls: {} };
    let explorationPlaces = [];
    // Markers the progression mode may pick, rebuilt only when map, souls or exploration change: the
    // nearest one is looked for on every player position (10 per second), not all 25,000 markers.
    let progressionCandidates = null;
    let progressionTarget = null;   // {petId, x, y, name}

    // Icons grow when zooming in (user request 2026-10-03): ×1.3 per zoom level above the overview,
    // capped so they never hide the map. Below the overview they keep their base size.
    const iconScale = () => Math.min(2.4, Math.pow(1.3, Math.max(0, map.getZoom() - fitZoom)));

    const loadScript = src => new Promise((resolve, reject) => {
        const s = document.createElement('script');
        s.src = src;
        s.onload = resolve;
        s.onerror = () => reject(new Error(ui('Kartendaten nicht ladbar: ', 'Cannot load map data: ') + src));
        document.head.appendChild(s);
    });

    // Tile layer that also loads `edgeBufferTiles` rows beyond the visible edge, so panning and zooming
    // do not show empty borders that fill in late (local files load fast, the extra tiles cost little).
    const BufferedTiles = L.TileLayer.extend({
        _initTile(tile) {
            L.TileLayer.prototype._initTile.call(this, tile);
            // Fractional zoom/pan transforms can expose a subpixel gap between adjacent images.
            // Overlap their display boxes; tile coordinates and source pixels stay unchanged.
            const size = this.getTileSize();
            tile.style.width = `${size.x + 1}px`;
            tile.style.height = `${size.y + 1}px`;
        },
        _getTiledPixelBounds(center) {
            const bounds = L.TileLayer.prototype._getTiledPixelBounds.call(this, center);
            const pad = (this.options.edgeBufferTiles || 0) * this.getTileSize().x;
            return new L.Bounds(bounds.min.subtract([pad, pad]), bounds.max.add([pad, pad]));
        },
    });

    const scale = () => Math.pow(2, current.refZoom);
    const toLatLng = (x, y) => [-y / scale(), x / scale()];
    const fromLatLng = ll => [ll.lng * scale(), -ll.lat * scale()];
    const nameOf = m => (lang === 'de' && m[4]) ? m[4] : m[3];
    const esc = s => String(s ?? '').replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

    function colorFor(cat, index) {
        if (cat.group === 'Monsters') return MONSTER_COLORS[cat.name] || '#f87171';
        if (cat.group === 'Resources') return RESOURCE_COLORS[index % RESOURCE_COLORS.length];
        if (cat.group === 'Pets') return MONSTER_COLORS[cat.name] || '#5eead4';
        return GROUP_COLORS[cat.group] || '#e5e7eb';
    }

    function init(elementId, manifestJson, baseUrl, dotnetRef) {
        manifest = JSON.parse(manifestJson);
        base = baseUrl;
        dotnet = dotnetRef;
        map = L.map(elementId, { crs: L.CRS.Simple, zoomSnap: 0.5, zoomDelta: 0.5, wheelPxPerZoomLevel: 90, attributionControl: true, fadeAnimation: false });
        canvas = L.canvas({ padding: 0.4 });
        map.attributionControl.setPrefix('Leaflet');
        map.on('zoom zoomend', rescaleIcons);
        // While the user drags the map, following the player pauses for a few seconds.
        map.on('dragstart', () => { followPausedUntil = Date.now() + 6000; });
        map.on('zoomend moveend', drawTargets);
        map.on('moveend', () => { if (!player) updateProgression(); });
        map.on('contextmenu', () => { }); // a listener keeps the browser menu away
        // Target rings above the (enlarged) symbols; they never take clicks.
        map.createPane('targets');
        map.getPane('targets').style.zIndex = 650;
        map.getPane('targets').style.pointerEvents = 'none';
    }

    // keepAt {x, y}: the tracking found the player on another map. The view keeps its scale in game
    // metres and centres on the player instead of showing the whole map (user request 2026-10-06).
    async function show(mapId, language, keepAt = null) {
        lang = language;
        const before = current && map ? { zoom: map.getZoom(), def: current.def } : null;
        const def = manifest.maps.find(m => m.id === mapId);
        if (!def) throw new Error(ui('Unbekannte Karte ', 'Unknown map ') + mapId);
        if (!window.SoulcrestMaps || !window.SoulcrestMaps[mapId]) await loadScript(`${base}${mapId}/data.js?v=${manifest.version || ''}`);
        explorationPlaces = [];
        progressionCandidates = null;
        current = window.SoulcrestMaps[mapId];
        current.def = def;

        map.eachLayer(l => map.removeLayer(l));
        const size = def.size / scale();
        const bounds = L.latLngBounds([-size, 0], [0, size]);
        tiles = new BufferedTiles(`${base}${def.tiles}`, {
            tileSize: 256, minZoom: def.minZoom, maxZoom: def.maxZoom, maxNativeZoom: def.maxNativeZoom,
            noWrap: true, bounds, attribution: def.attribution, keepBuffer: 6, edgeBufferTiles: 2,
            updateWhenZooming: false, updateWhenIdle: false, updateInterval: 100,
        }).addTo(map);
        map.setMaxBounds(bounds.pad(0.25));
        // Start on the playable area (the tile world has wide empty margins).
        const xs = current.markers.map(m => m[1]), ys = current.markers.map(m => m[2]);
        const content = xs.length
            ? L.latLngBounds(toLatLng(Math.min(...xs), Math.max(...ys)), toLatLng(Math.max(...xs), Math.min(...ys)))
            : bounds;
        map.fitBounds(content.pad(0.05));
        fitZoom = map.getZoom();
        if (keepAt && before) {
            const zoom = Math.min(def.maxZoom, Math.max(def.minZoom, keptZoom(before.zoom, before.def, def)));
            map.setView(toLatLng(keepAt.x, keepAt.y), zoom, { animate: false });
        }
        player = null;

        applyIconScale();
        buildMarkers();
        buildRegions();
        focusLayer = L.layerGroup().addTo(map);
        spawnLayer = L.layerGroup().addTo(map);
        spawnKey = '';
        markedPets = new Set();
        targetLayer = L.layerGroup().addTo(map);
        updateLabelVisibility();
        progressionTarget = null;
        updateProgression();
        drawTargets();

        return JSON.stringify({
            groups: current.groups.map(g => ({
                name: g.name,
                categories: g.categories.map(i => {
                    const c = current.categories[i];
                    return { index: i, name: c.name, de: c.de || null, count: c.count, hidden: !catVisible[i], color: colorFor(c, i), icon: c.icon >= 0 ? base + current.icons[c.icon] : null, kinds: kindsOf(i) };
                }),
            })),
            regions: current.regions.length,
            markers: current.markers.length,
        });
    }

    // Same game metres per screen pixel on the new map: screen px per metre = 2^(zoom - refZoom) / metersPerPixel.
    // Altgard (refZoom 5, 1 m/px) and Ishalgen (refZoom 4, 2 m/px) keep the same zoom number.
    function keptZoom(zoom, from, to) {
        const metres = d => d.metersPerPixel || 1;
        return zoom + (to.refZoom - from.refZoom) + Math.log2(metres(to) / metres(from));
    }

    function setLanguage(language) {
        lang = language;
        if (!current) return;
        // Keep pan, zoom, selected layers and targets. Popup factories read lang on opening.
        const regionsVisible = regionLayer && map.hasLayer(regionLayer);
        if (regionLayer) map.removeLayer(regionLayer);
        buildRegions();
        if (!regionsVisible && regionLayer) map.removeLayer(regionLayer);
        map.closePopup();
        progressionTarget = null;
        updateProgression();
    }

    function popupFor(m, cat, index) {
        const de = lang === 'de' && m[4] && m[4] !== m[3] ? `<br><span style="color:#8b96a8">${esc(m[3])}</span>` : '';
        const pet = m[6] && cat.group === 'Monsters' ? `<br>${ui('Soul für Pet:', 'Soul for pet:')} <b>${esc(m[6])}</b>` : '';
        const link = m[6] ? `<br><a href="#" onclick="window.soulcrestMap.focusPet('${m[6]}');return false;">${ui('Alle Spawns dieses Pets', 'All spawns of this pet')}</a>` : '';
        const source = m[7] && m[7].en ? `<br>${ui('Soul-Quelle:', 'Soul source:')} ${esc(lang === 'de' && m[7].de ? m[7].de : m[7].en)}` : '';
        const level = m[6] && cat.group === 'Pets' && petLabels[m[6]] ? `<br>${ui('Stand:', 'Progress:')} <b>${esc(petLabels[m[6]])}</b>` : '';
        return `<b>${esc(nameOf(m) || cat.name)}</b>${de}<br>${esc(cat.group)} · ${esc(lang === 'de' && cat.de ? cat.de : cat.name)}${level}${pet}${source}${link}${explorationLine(index)}<br><span style="color:#8b96a8">${ui('Rechtsklick: als Ziel markieren', 'Right-click: set target')}</span>`;
    }

    // Kibelisk bound / sealed dungeon or stronghold done, and the button that changes it for the active
    // character (before 2026-10-07 a right-click menu; now right click always marks the target).
    function explorationLine(index) {
        const place = explorationPlaces.find(p => p.index === index);
        if (!place) return '';
        const kibelisk = place.kind === 'kibelisk';
        const state = kibelisk ? (place.done ? ui('Gebunden', 'Bound') : ui('Nicht gebunden', 'Not bound'))
            : (place.done ? ui('Erledigt', 'Done') : ui('Nicht erledigt', 'Not done'));
        const action = kibelisk ? (place.done ? ui('Bindung entfernen', 'Remove binding') : ui('Als gebunden markieren', 'Mark as bound'))
            : (place.done ? ui('Haken entfernen', 'Remove check') : ui('Als erledigt abhaken', 'Mark as done'));
        const needs = place.needs || [];
        const locked = !place.done && place.locked && needs.length
            ? `<br><span class="exploration-locked">🔒 ${ui('Öffnet erst nach', 'Opens only after')} ${needs.length <= 3 ? needs.map(esc).join(', ') : ui(`${needs.length} weiteren Sealed Dungeons`, `${needs.length} more sealed dungeons`)}</span>`
            : '';
        return `<br>${ui('Status:', 'Status:')} <b>${state}</b> · <a href="#" class="exploration-toggle" onclick="window.soulcrestMap.toggleExplored(${index});return false;">${action}</a>${locked}`;
    }

    function toggleExplored(index) {
        const place = explorationPlaces.find(p => p.index === index);
        if (!place) return;
        map.closePopup();
        // The symbol changes when the exploration state comes back (setExploration): colour or grey, check mark.
        dotnet.invokeMethodAsync('OnExplorationDone', place.id, !place.done);
    }

    // Kinds of a resource category for the legend; only listed when there are at least two.
    function kindsOf(index) {
        const kinds = kindLayers[index];
        if (!kinds || kinds.size < 2) return null;
        return [...kinds.values()].map(k => ({ name: k.kind, de: k.kindDe, icon: k.icon >= 0 ? base + current.icons[k.icon] : null, count: k.getLayers().length }));
    }

    const kindOf = (cat, m) => cat.group === 'Resources' && m[3] ? m[3] : null;

    function buildMarkers() {
        catLayers = current.categories.map(() => L.layerGroup());
        kindLayers = current.categories.map(() => null);
        catVisible = current.categories.map(c => !c.hidden && DEFAULT_VISIBLE_GROUPS.has(c.group));
        markerRefs = new Array(current.markers.length);
        markerByPos = new Map();
        petRefs = [];
        current.markers.forEach((m, i) => {
            const cat = current.categories[m[0]];
            const ll = toLatLng(m[1], m[2]);
            let layer;
            const iconIndex = m[5] >= 0 ? m[5] : cat.icon;
            if (ICON_GROUPS.has(cat.group) && iconIndex >= 0) {
                layer = L.marker(ll, { icon: iconFor(cat, iconIndex, m), title: nameOf(m) || (lang === 'de' && cat.de ? cat.de : cat.name) });
                layer.iconIndex = iconIndex;
            } else {
                layer = L.circleMarker(ll, {
                    renderer: canvas, radius: cat.group === 'Monsters' ? 3.5 : 4.5, weight: 1, color: '#000',
                    fillColor: colorFor(cat, m[0]), fillOpacity: 0.9,
                });
            }
            layer.bindPopup(() => popupFor(m, cat, i));
            layer.on('contextmenu', e => {
                if (e.originalEvent) L.DomEvent.preventDefault(e.originalEvent);
                const icon = iconIndex >= 0 ? current.icons[iconIndex] : null;
                // Shift + right click chains the target to the last one (player → A → B).
                const chain = !!(e.originalEvent && e.originalEvent.shiftKey);
                // Every symbol, Kibelisks and sealed dungeons too: right click marks it (user request 2026-10-07).
                // Binding / done marks are set in the left-click popup.
                dotnet.invokeMethodAsync('OnToggleTarget', current.id, m[1], m[2], nameOf(m) || cat.name, `${cat.group} · ${cat.name}`, icon, chain, cat.group === 'Pets' ? (m[6] || null) : null);
            });
            layer.markerIndex = i;
            markerRefs[i] = layer;
            markerByPos.set(m[1] + ',' + m[2], i);
            if (cat.group === 'Pets') {
                layer.petLabel = petLabels[m[6]];
                petRefs.push({ m, layer });
            }
            const kind = kindOf(cat, m);
            if (kind) {
                const kinds = kindLayers[m[0]] || (kindLayers[m[0]] = new Map());
                let group = kinds.get(kind);
                if (!group) {
                    group = L.layerGroup();
                    Object.assign(group, { kind, kindDe: m[4] && m[4] !== m[3] ? m[4] : null, icon: m[5] });
                    kinds.set(kind, group);
                    catLayers[m[0]].addLayer(group);
                }
                group.addLayer(layer);
            } else {
                catLayers[m[0]].addLayer(layer);
            }
        });
        catLayers.forEach((layer, i) => { if (catVisible[i]) layer.addTo(map); });
        applyDoneFilter();
    }

    // Symbols keep one DOM element; their size follows the CSS variables --pet-scale / --place-scale on
    // the map container. Rebuilding 300+ icons on every zoom step made zooming stutter.
    function iconFor(cat, iconIndex, m) {
        const pet = cat.group === 'Pets';
        const done = pet && doneSet.has(m[6]);
        // Kibelisks: bound in colour, not bound grey; no check mark (user request 2026-10-06).
        const kibelisk = !pet && m.explorationKind === 'kibelisk';
        const explored = !pet && !kibelisk && m.explorationDone;
        // Pets: portrait plus current level and souls beside it at every zoom level.
        const label = pet && m[6] && petLabels[m[6]] ? `<span class="pet-label">${esc(petLabels[m[6]])}</span>` : '';
        return L.divIcon({
            className: (pet ? 'pet-marker' : 'place-marker') + (!pet && m[5] >= 0 && cat.group === 'Resources' ? ' resource-kind' : '') + (done ? ' done' : '') + (explored ? ' exploration-done' : '') + (kibelisk && !m.explorationDone ? ' kibelisk-unbound' : '') + (pet && hiddenSet.has(m[6]) ? ' pet-filtered' : ''),
            html: `<img class="${pet ? 'pet-icon' : 'place-icon'}" src="${base + current.icons[iconIndex]}" alt="">${label}${explored ? '<span class="exploration-check">✓</span>' : ''}`,
            iconSize: pet ? [30, 30] : [24, 24],
        });
    }

    function applyIconScale() {
        const scale = iconScale();
        const el = map.getContainer();
        el.style.setProperty('--pet-scale', scale.toFixed(3));
        // Place, resource and cube icons grow like the pet portraits (user report 2026-10-05: "sehr klein").
        el.style.setProperty('--place-scale', scale.toFixed(3));
        updateLabelVisibility();
    }

    function updateLabelVisibility() {
        map.getContainer().classList.add('show-pet-labels');
    }

    function rescaleIcons() {
        if (current) applyIconScale();
    }

    function buildRegions() {
        regionLayer = L.layerGroup();
        for (const r of current.regions) {
            if (!r.rings || !r.rings.length) continue;
            const poly = L.polygon(r.rings.map(ring => ring.map(p => toLatLng(p[0], p[1]))), {
                color: '#e2e8f0', weight: 1, opacity: 0.55, fillOpacity: 0.03, dashArray: '4 4', interactive: false,
            });
            poly.bindTooltip((lang === 'de' && r.de) ? r.de : r.name, { permanent: true, direction: 'center', className: 'region' });
            regionLayer.addLayer(poly);
        }
        if (current.regions.length) regionLayer.addTo(map);
    }

    function setCategoryVisible(index, visible) {
        catVisible[index] = visible;
        if (visible) catLayers[index].addTo(map); else map.removeLayer(catLayers[index]);
    }

    function setKindVisible(index, kind, visible) {
        const group = kindLayers[index] && kindLayers[index].get(kind);
        if (!group) return;
        if (visible) catLayers[index].addLayer(group); else catLayers[index].removeLayer(group);
    }

    // done: pets on MAX (styled), hidden: pets of the levels the user hides (removed from the map).
    function setDone(doneJson, hiddenJson, labelsJson) {
        doneSet = new Set(JSON.parse(doneJson));
        hiddenSet = new Set(JSON.parse(hiddenJson || '[]'));
        const labels = labelsJson ? JSON.parse(labelsJson) : petLabels;
        petLabels = labels;
        if (current) {
            applyDoneFilter();
        }
    }

    // ---- marked targets: ring on the target, line with direction arrows from the player
    function setTargets(json) {
        targets = JSON.parse(json || '[]');
        drawTargets();
    }

    const spawnRingRadius = () => Math.round(15 * iconScale()) + 7;

    // Shared with right-click pet marking: quest hints use the very same spawn symbol.
    function monsterSpawn(layer, x, y) {
        return L.circleMarker(toLatLng(x, y), { renderer: canvas, radius: 4.5,
            color: '#facc15', weight: 1.5, fillColor: '#f87171', fillOpacity: 0.95 }).addTo(layer);
    }

    function updateMarkedPets() {
        if (!spawnLayer) return;
        const pets = new Set(targets.filter(t => t.map === current.id && t.petId).map(t => t.petId));
        if (progressionTarget && progressionTarget.petId && progression.enabled && !isPlace(progressionTarget)) pets.add(progressionTarget.petId);
        const key = current.id + ':' + [...pets].sort().join('|');
        if (key === spawnKey) {
            const radius = spawnRingRadius();
            spawnLayer.eachLayer(l => { if (l.spawnRing && l.getRadius() !== radius) l.setRadius(radius); });
            return;
        }
        spawnKey = key;
        markedPets = pets;
        spawnLayer.clearLayers();
        if (pets.size) {
            for (const m of current.markers) {
                if (!m[6] || !pets.has(m[6])) continue;
                const cat = current.categories[m[0]];
                if (cat.group === 'Monsters') {
                    monsterSpawn(spawnLayer, m[1], m[2]).bindPopup(() => popupFor(m, cat));
                } else if (cat.group === 'Pets') {
                    for (const style of [{ color: '#000', weight: 6, opacity: 0.5 }, { color: '#5eead4', weight: 3, opacity: 1, dashArray: '6 4' }]) {
                        const ring = L.circleMarker(toLatLng(m[1], m[2]), { pane: 'targets', radius: spawnRingRadius(), fill: false, interactive: false, ...style });
                        ring.spawnRing = true;
                        ring.addTo(spawnLayer);
                    }
                }
            }
        }
        applyDoneFilter();
    }

    function drawTargets() {
        if (!targetLayer || !current) return;
        updateMarkedPets();
        targetLayer.clearLayers();
        const here = targets.filter(t => t.map === current.id);
        if (progressionTarget)
            here.push({ id: 'progression', map: current.id, x: progressionTarget.x, y: progressionTarget.y, name: progressionTarget.name, color: '#ffffff', dashed: true, group: progressionTarget.group, petId: progressionTarget.petId });
        if (progressionTarget && progression.enabled && isPlace(progressionTarget) && progression.completionRadius > 0) {
            const center = toLatLng(progressionTarget.x, progressionTarget.y);
            const radius = Math.min(500, progression.completionRadius) / scale();
            const ring = Array.from({ length: 64 }, (_, i) => {
                const angle = i * Math.PI * 2 / 64;
                return [center[0] + Math.sin(angle) * radius, center[1] + Math.cos(angle) * radius];
            });
            L.polygon(ring, { pane: 'targets', color: '#5eead4', weight: 1.5, fillOpacity: 0.08, dashArray: '4 4', interactive: false }).addTo(targetLayer);
        }
        const playerAt = player ? player.getLatLng() : null;
        const view = map.getBounds().pad(0.3);
        for (const t of here) {
            const ll = L.latLng(toLatLng(t.x, t.y));
            if (t.questMonster && !t.monsterBranch) {
                monsterSpawn(targetLayer, t.x, t.y).bindTooltip(esc(t.name), { direction: 'top', offset: [0, -6] });
                continue; // Spawn hints never enter the navigation chain.
            }
            // A chained target starts at its predecessor, all others at the player.
            const before = t.after ? here.find(o => o.id === t.after) : null;
            const from = Number.isFinite(t.branchX) && Number.isFinite(t.branchY) ? L.latLng(toLatLng(t.branchX, t.branchY)) : before ? L.latLng(toLatLng(before.x, before.y)) : t.completed ? null : playerAt;
            // Ring just outside the symbol: pet portraits grow with the zoom, monster dots do not.
            const pet = (t.group || t.kind || '').startsWith('Pets');
            const radius = pet ? Math.round(15 * iconScale()) + 10 : 11;
            const preview = t.id === 'boss-next';
            const dashed = preview || t.monsterBranch;
            const opacity = t.completed ? 0.28 : 1;
            if (t.id === 'boss-rush' || preview) {
                L.marker(ll, { pane: 'targets', zIndexOffset: 2500, interactive: false, keyboard: false,
                    icon: L.divIcon({ className: 'boss-rush-marker' + (preview ? ' boss-preview' : ''), html: '<span></span>', iconSize: [26, 32], iconAnchor: [13, 32] }) }).addTo(targetLayer);
                L.tooltip({ permanent: true, direction: 'top', offset: [0, -35], className: 'boss-rush-label' + (preview ? ' boss-preview' : ''), opacity: preview ? 0.45 : 0.9, pane: 'targets' })
                    .setLatLng(ll).setContent(esc(t.name)).addTo(targetLayer);
            }
            if (t.group === 'Monsters') {
                // Soul monster of the progression target: its layer is usually hidden, so draw the dot
                // and say what it is (user report: "der Kreis liegt neben seinem Ziel").
                L.circleMarker(ll, { pane: 'targets', radius: 5, color: '#000', weight: 1, fillColor: '#f87171', fillOpacity: 1, interactive: false }).addTo(targetLayer);
                L.tooltip({ permanent: true, direction: 'top', offset: [0, -radius - 2], className: 'progression-label', pane: 'targets' })
                    .setLatLng(ll).setContent(`${esc(t.name)} → ${esc(petName(t.petId))}`).addTo(targetLayer);
            }
            if (t.monsterBranch) {
                L.tooltip({ permanent: true, direction: 'top', offset: [0, -15], className: 'progression-label', pane: 'targets' })
                    .setLatLng(ll).setContent(esc(t.name)).addTo(targetLayer);
            } else if (!preview) targetSymbol(t);
            L.circleMarker(ll, { pane: 'targets', radius, color: '#000', weight: 6, opacity: (preview ? 0.12 : 0.5) * opacity, fill: false, dashArray: dashed ? '6 6' : null, interactive: false }).addTo(targetLayer);
            L.circleMarker(ll, { pane: 'targets', radius, color: t.color, weight: 3.5, opacity: (preview ? 0.45 : 1) * opacity, dashArray: dashed ? '6 6' : null, fill: false, interactive: false }).addTo(targetLayer);
            if (!from) continue;
            L.polyline([from, ll], { color: '#000', weight: 7, opacity: (preview ? 0.12 : 0.45) * opacity, dashArray: dashed ? '10 8' : null, interactive: false }).addTo(targetLayer);
            L.polyline([from, ll], { color: t.color, weight: 3.5, opacity: (preview ? 0.4 : 0.95) * opacity, interactive: false, dashArray: t.dashed || dashed ? '10 8' : null }).addTo(targetLayer);
            // Arrows every 70 screen pixels, only where they can be seen.
            const a = map.latLngToLayerPoint(from), b = map.latLngToLayerPoint(ll);
            const length = a.distanceTo(b);
            const angle = Math.atan2(b.y - a.y, b.x - a.x) * 180 / Math.PI;
            let shown = 0;
            for (let d = 35; d < length - 20 && shown < 80; d += 70) {
                const p = map.layerPointToLatLng(L.point(a.x + (b.x - a.x) * d / length, a.y + (b.y - a.y) * d / length));
                if (!view.contains(p)) continue;
                shown++;
                L.marker(p, {
                    icon: L.divIcon({ className: 'route-arrow', html: `<span style="opacity:${(preview ? 0.4 : 1) * opacity};transform:rotate(${angle}deg);border-left-color:${t.color}"></span>`, iconSize: [16, 16] }),
                    interactive: false, keyboard: false,
                }).addTo(targetLayer);
            }
        }
    }

    // A marked target keeps its symbol when its layer is hidden or filtered (user request 2026-10-07:
    // stops of a route show their icons although the category is switched off).
    function targetSymbol(t) {
        if (t.leveling) {
            const style = t.icon ? `--objective-icon:url(${JSON.stringify(base + t.icon)});color:${t.color}` : `color:${t.color}`;
            L.marker(toLatLng(t.x, t.y), { pane: 'targets', zIndexOffset: 1200, interactive: false, keyboard: false,
                opacity: t.completed ? 0.28 : 1,
                icon: L.divIcon({ className: 'leveling-marker', iconSize: [24, 24], iconAnchor: [12, 12],
                    html: `<span class="leveling-marker-disc" style="${esc(style)}"><span class="${t.icon ? 'objective-icon' : 'objective-dot'}"></span></span>` })
            }).addTo(targetLayer);
            if (t.name && t.name.trim()) {
                L.tooltip({ permanent: true, direction: 'top', offset: [0, -15], className: 'leveling-objective-label',
                    opacity: t.completed ? 0.28 : 0.95, pane: 'targets' })
                    .setLatLng(toLatLng(t.x, t.y)).setContent(`<span style="color:${t.color}">${esc(t.name)}</span>`).addTo(targetLayer);
            }
            return;
        }
        const i = markerByPos.get(t.x + ',' + t.y);
        if (i === undefined) return;
        const layer = markerRefs[i];
        if (layer.iconIndex === undefined || (map.hasLayer(layer) && !layer.petHidden)) return;
        const m = current.markers[i];
        L.marker(toLatLng(m[1], m[2]), { icon: iconFor(current.categories[m[0]], layer.iconIndex, m), interactive: false, keyboard: false })
            .addTo(targetLayer);
    }

    // Display name of a pet from its spawn markers on this map (the soul monster's own name differs).
    function petName(petId) {
        const spawn = current.markers.find(m => m[6] === petId && current.categories[m[0]].group === 'Pets');
        return spawn ? (nameOf(spawn) || petId) : petId;
    }

    function setExploration(json) {
        explorationPlaces = JSON.parse(json);
        progressionCandidates = null;
        if (!current) return;
        for (const p of explorationPlaces) {
            const m = current.markers[p.index], layer = markerRefs[p.index];
            if (!m || !layer) continue;
            if (!!m.explorationDone !== p.done || m.explorationKind !== p.kind) {
                m.explorationDone = p.done;
                m.explorationKind = p.kind;
                if (layer.setIcon && layer.iconIndex >= 0) layer.setIcon(iconFor(current.categories[m[0]], layer.iconIndex, m));
            }
        }
    }

    // Any selection of 'pets', 'dungeon' and 'stronghold' (user request 2026-10-10). Pets: nearest below the
    // goal, or with petsClosest the pet missing the fewest souls to finish its level, the nearest one on a
    // tie (user request 2026-10-05). Between the kinds the nearest open target comes first.
    const includes = kind => (progression.kinds || []).includes(kind);
    const isPlace = target => !!target && target.group === 'Locations';

    function setProgression(enabled, goal, soulsJson, kindsJson = '["pets"]', petsClosest = false, completionRadius = 15, character = '', missingJson = '{}') {
        if (progression.character !== character) progressionTarget = null;
        progression = { enabled, goal, souls: JSON.parse(soulsJson || '{}'), kinds: JSON.parse(kindsJson || '[]'), petsClosest, completionRadius, character, missing: JSON.parse(missingJson || '{}') };
        progressionCandidates = null;
        drawTargets();
        updateProgression();
    }

    // Nearest spawn (pet spawn or soul monster) of a pet below the goal, measured from the player, or from
    // the view centre while the player is not on this map. Reports changes to .NET for the in-game overlay.
    function updateProgression() {
        let next = null;
        if (progression.enabled && current) {
            const [ox, oy] = player ? fromLatLng(player.getLatLng()) : fromLatLng(map.getCenter());
            if (!progressionCandidates) progressionCandidates = buildProgressionCandidates();
            // The best pet (fewest missing souls first when petsClosest) and the nearest place; then the nearer one.
            let pet = null, petDistance = Infinity, fewest = Infinity, place = null, placeDistance = Infinity;
            for (const c of progressionCandidates) {
                const d = (c.x - ox) * (c.x - ox) + (c.y - oy) * (c.y - oy);
                if (isPlace(c)) {
                    if (d < placeDistance) { placeDistance = d; place = c; }
                    continue;
                }
                const missing = c.missing ?? 0;
                if (missing < fewest || (missing === fewest && d < petDistance)) { fewest = missing; petDistance = d; pet = c; }
            }
            next = petDistance <= placeDistance ? pet : place;
        }
        const changed = (next && next.petId + next.x + next.y) !== (progressionTarget && progressionTarget.petId + progressionTarget.x + progressionTarget.y);
        progressionTarget = next;
        if (changed) {
            drawTargets();
            dotnet.invokeMethodAsync('OnProgressionTarget', current ? current.id : '', next ? next.petId : null, next ? next.x : 0, next ? next.y : 0, next ? next.name : null);
        }
    }

    function buildProgressionCandidates() {
        const result = [];
        for (const p of explorationPlaces) {
            // Sealed dungeons and strongholds as chosen; Kibelisks are only checked off.
            // A sealed dungeon that opens only after others is skipped until they are done (user information 2026-10-07).
            if (p.done || p.locked || !(p.kind === 'dungeon' || p.kind === 'stronghold') || !includes(p.kind)) continue;
            const m = current.markers[p.index];
            if (m) result.push({ petId: p.id, x: m[1], y: m[2], name: nameOf(m), group: 'Locations' });
        }
        if (!includes('pets')) return result;
        // The pet symbol is the target (user report: a nearby soul monster looked like a miss).
        // Soul monsters only stand in for pets without a symbol on this map.
        const withSymbol = new Set(current.markers.filter(m => m[6] && current.categories[m[0]].group === 'Pets').map(m => m[6]));
        for (const m of current.markers) {
            const petId = m[6];
            if (!petId) continue;
            const closest = !!progression.petsClosest;
            // Closest to completion: every pet below max (missing > 0); otherwise below the chosen goal.
            if (closest ? !(progression.missing[petId] > 0) : (progression.souls[petId] || 0) >= progression.goal) continue;
            const group = current.categories[m[0]].group;
            if (group !== 'Pets' && !(group === 'Monsters' && !withSymbol.has(petId))) continue;
            result.push({ petId, x: m[1], y: m[2], name: m[3] || petId, group, missing: closest ? progression.missing[petId] : 0 });
        }
        return result;
    }

    function flyToProgression() {
        if (progressionTarget) map.setView(toLatLng(progressionTarget.x, progressionTarget.y), Math.max(map.getZoom(), current.def.maxNativeZoom - 1));
    }

    function flyToTarget(id) {
        const t = targets.find(x => x.id === id);
        if (!t || !current || t.map !== current.id) return;
        map.setView(toLatLng(t.x, t.y), Math.max(map.getZoom(), current.def.maxNativeZoom - 1));
    }

    function applyDoneFilter() {
        for (const { m, layer } of petRefs) {
            const hidden = hiddenSet.has(m[6]) && !markedPets.has(m[6]);
            if (layer.iconIndex !== undefined) {
                // Rebuild only the changed pet's label. Filtering keeps its DOM and image intact.
                if (layer.petLabel !== petLabels[m[6]]) {
                    layer.petLabel = petLabels[m[6]];
                    layer.setIcon(iconFor(current.categories[m[0]], layer.iconIndex, m));
                }
                const classes = 'pet-marker' + (doneSet.has(m[6]) ? ' done' : '') + (hidden ? ' pet-filtered' : '');
                layer.options.icon.options.className = classes; // survives category hide/show
                const el = layer.getElement();
                if (el) {
                    el.classList.toggle('done', doneSet.has(m[6]));
                    el.classList.toggle('pet-filtered', hidden);
                }
                if (hidden && !layer.petHidden) layer.closePopup();
            } else {
                // Iconless pets use Leaflet's canvas; update membership only when it changes.
                const group = catLayers[m[0]];
                if (hidden && group.hasLayer(layer)) group.removeLayer(layer);
                else if (!hidden && !group.hasLayer(layer)) group.addLayer(layer);
            }
            layer.petHidden = hidden;
        }
    }

    function search(text) {
        const q = (text || '').trim().toLowerCase();
        if (q.length < 2 || !current) return '[]';
        const out = [];
        for (let i = 0; i < current.markers.length && out.length < 40; i++) {
            const m = current.markers[i];
            if ((m[3] && m[3].toLowerCase().includes(q)) || (m[4] && m[4].toLowerCase().includes(q))) {
                const cat = current.categories[m[0]];
                out.push({ i, name: nameOf(m) || cat.name, cat: `${cat.group} · ${cat.name}` });
            }
        }
        return JSON.stringify(out);
    }

    // A search hit highlights every place of that name (all spawns of "Ruins Marauder Spider") without
    // switching layers on: a monster hit used to show its whole category, 4,902 dots, and keep it
    // (user report 2026-10-06). The focus box in the sidebar takes it away again.
    function flyToMarker(i) {
        const m = current.markers[i];
        const cat = current.categories[m[0]];
        const same = current.markers.filter(x => x[0] === m[0] && x[3] === m[3]);
        focusLayer.clearLayers();
        same.forEach(x => L.circleMarker(toLatLng(x[1], x[2]), { renderer: canvas, radius: 6, color: '#facc15', weight: 2, fillColor: colorFor(cat, x[0]), fillOpacity: 0.9 })
            .bindPopup(() => popupFor(x, cat)).addTo(focusLayer));
        L.circleMarker(toLatLng(m[1], m[2]), { renderer: canvas, radius: 13, color: '#facc15', weight: 3, fillOpacity: 0.1, interactive: false }).addTo(focusLayer);
        const zoom = Math.max(map.getZoom(), current.def.maxNativeZoom);
        if (document.visibilityState === 'visible') map.flyTo(toLatLng(m[1], m[2]), zoom, { duration: 0.8 });
        else map.setView(toLatLng(m[1], m[2]), zoom, { animate: false });
        setTimeout(() => L.popup({ offset: [0, -6] }).setLatLng(toLatLng(m[1], m[2])).setContent(popupFor(m, cat)).openOn(map), 600);
        dotnet.invokeMethodAsync('OnNameFocused', nameOf(m) || (lang === 'de' && cat.de ? cat.de : cat.name), same.length);
        return cat.name;
    }

    function flash(markers, color) {
        focusLayer.clearLayers();
        markers.forEach(m => {
            L.circleMarker(toLatLng(m[1], m[2]), { renderer: canvas, radius: 13, color, weight: 3, fillOpacity: 0.1, interactive: false }).addTo(focusLayer);
        });
    }

    // Highlights all spawns of a pet: pet spawn markers and the monsters that drop its soul.
    function focusPet(petId) {
        if (!current) return JSON.stringify({ pets: 0, monsters: 0 });
        const hits = current.markers.filter(m => m[6] === petId);
        const pets = hits.filter(m => current.categories[m[0]].group === 'Pets');
        const monsters = hits.filter(m => current.categories[m[0]].group === 'Monsters');
        focusLayer.clearLayers();
        monsters.forEach(m => L.circleMarker(toLatLng(m[1], m[2]), { renderer: canvas, radius: 7, color: '#facc15', weight: 2, fillColor: '#f87171', fillOpacity: 0.85 })
            .bindPopup(() => popupFor(m, current.categories[m[0]])).addTo(focusLayer));
        pets.forEach(m => L.circleMarker(toLatLng(m[1], m[2]), { renderer: canvas, radius: 16, color: '#5eead4', weight: 3, fillOpacity: 0.15, interactive: false }).addTo(focusLayer));
        if (hits.length) {
            const target = L.latLngBounds(hits.map(m => toLatLng(m[1], m[2]))).pad(0.3);
            // Animations pause in hidden views; jump directly then.
            if (document.visibilityState === 'visible') map.flyToBounds(target, { maxZoom: current.def.maxNativeZoom, duration: 0.8 });
            else map.fitBounds(target, { maxZoom: current.def.maxNativeZoom, animate: false });
        }
        dotnet.invokeMethodAsync('OnPetFocused', petId, pets.length, monsters.length);
        return JSON.stringify({ pets: pets.length, monsters: monsters.length });
    }

    function clearFocus() { if (focusLayer) focusLayer.clearLayers(); if (map) map.closePopup(); }


    // Player position from the in-game map (MapTrackingService). follow: keep the player in view.
    function setPlayer(mapId, x, y, follow, found) {
        if (!current) return;
        if (mapId !== current.id) { if (player) { map.removeLayer(player); player = null; drawTargets(); } return; }
        const ll = toLatLng(x, y);
        if (!player) {
            player = L.marker(ll, {
                icon: L.divIcon({ className: 'player-marker', html: '<span></span>', iconSize: [22, 22] }),
                zIndexOffset: 2000, interactive: false, keyboard: false,
            }).addTo(map);
        } else {
            player.setLatLng(ll);
        }
        const el = player.getElement();
        if (el) el.classList.toggle('lost', !found);
        drawTargets();
        updateProgression();
        // Follow: the player stays in the centre and the map moves along, like the in-game map; the
        // zoom is kept. Paused for a moment after the user dragged the map.
        if (follow && found && Date.now() > followPausedUntil) {
            const animate = document.visibilityState === 'visible';
            map.panTo(ll, { animate, duration: 0.25, easeLinearity: 1, noMoveStart: true });
        }
    }

    function centerOnPlayer() {
        if (player) map.setView(player.getLatLng(), Math.max(map.getZoom(), current.def.maxNativeZoom - 1));
    }

    function elementSize(selector) {
        const el = document.querySelector(selector);
        if (!el) return [0, 0];
        const r = el.getBoundingClientRect();
        return [r.width, r.height];
    }

    function invalidate() { if (map) map.invalidateSize(); }

    const debugState = () => ({ center: map.getCenter(), zoom: map.getZoom(), focus: focusLayer ? focusLayer.getLayers().length : 0, map });

    return { debugState, init, show, setLanguage, setPlayer, centerOnPlayer, elementSize, setTargets, flyToTarget, setExploration, setProgression, flyToProgression, setCategoryVisible, setKindVisible, setDone, search, flyToMarker, focusPet, toggleExplored, clearFocus, invalidate, keptZoom };
})();
