const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

test('leveling symbols, labels, rings and lines fade together; history has no player line', () => {
    const symbols = [], labels = [], rings = [], lines = [];
    const point = (x, y) => ({ x, y, distanceTo(p) { return Math.hypot(x - p.x, y - p.y); } });
    const L = {
        TileLayer: { extend: () => function () {} },
        latLng: v => Array.isArray(v) ? { lat: v[0], lng: v[1] } : v,
        point, divIcon: options => options,
        marker: (at, options) => { symbols.push({ at, options }); return { addTo() {}, bindTooltip(text) { this.tooltip = text; return this; } }; },
        circleMarker: (at, options) => { rings.push(options); return { addTo() { return this; }, bindPopup() { return this; }, bindTooltip() { return this; } }; },
        polyline: (at, options) => { lines.push({ at, options }); return { addTo() {} }; },
        tooltip: options => { labels.push(options); return { setLatLng() { return this; }, setContent() { return this; }, addTo() {} }; }
    };
    const map = {
        getZoom: () => 0,
        getBounds: () => ({ pad() { return this; }, contains: () => true }),
        latLngToLayerPoint: ll => point(ll.lng, ll.lat), layerPointToLatLng: p => ({ lat: p.y, lng: p.x })
    };
    const context = { window: {}, L };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', 'return { setup(m, marked = false) { current = { id: "altgard", refZoom: 0, categories: [{ group: "Monsters" }], markers: [[0, 150, 250, "Gribade Mumu Warrior", null, -1, "mumu-warrior"]] }; if (marked) spawnLayer = { clearLayers() {}, eachLayer() {} }; base = "/mapdata/"; map = m; targetLayer = { clearLayers() {} }; player = { getLatLng() { return { lat: 0, lng: 0 }; } }; }, debugState, init, show,');
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup(map);
    const t = (id, x, completed, after = null) => ({ id, map: 'altgard', x, y: 100, name: id, color: '#facc15', icon: 'npc.png', leveling: true, completed, after });
    api.setTargets(JSON.stringify([t('past', 100, true), t('next', 200, false), t('later', 300, false, 'next')]));
    const objectives = symbols.filter(s => s.options.icon.className === 'leveling-marker');
    assert.equal(objectives.length, 3);
    assert.equal(objectives[0].options.opacity, 0.28);
    assert.equal(objectives[1].options.opacity, 1);
    assert.match(objectives[0].options.icon.html, /objective-icon/);
    assert.equal(labels[0].opacity, 0.28);
    assert.equal(rings[1].opacity, 0.28);
    assert.equal(rings[3].opacity, 1);
    assert.equal(lines.length, 4); // two lines per upcoming point; none from the player to history
    assert.equal(lines[0].at[1].lng, 200);
    assert.equal(lines[2].at[0].lng, 200);
    const labelCount = labels.length;
    api.setTargets(JSON.stringify([{ ...t('waypoint', 400, false), name: '' }]));
    assert.equal(labels.length, labelCount); // the waypoint remains visible without an empty title plate
    assert.equal(symbols.filter(s => s.options.icon.className === 'leveling-marker').length, 4);
    const oldLines = lines.length, oldRings = rings.length;
    api.setTargets(JSON.stringify([{ id: 'quest-monster:2400580', map: 'altgard', x: 150, y: 250,
        name: 'Searcher · ×6', color: '#ffffff', icon: 'monster.png', questMonster: true }]));
    const monster = rings.at(-1);
    assert.equal(monster.radius, 4.5);
    assert.equal(monster.fillColor, '#f87171');
    assert.equal(monster.color, '#facc15');
    assert.equal(lines.length, oldLines); // spawn hints are never chained navigation targets
    assert.equal(rings.length, oldRings + 1);
    api.setup(map, true);
    api.setTargets(JSON.stringify([{ id: 'manual-mumu', map: 'altgard', x: 400, y: 400,
        name: 'Mumu Warrior', petId: 'mumu-warrior', kind: 'Pets · Cogni', color: '#5eead4' }]));
    const manual = rings.slice(-3)[0]; // spawn followed by the two navigation rings
    assert.deepEqual(monster, manual); // quest and manual right-click spawns render identically
    api.setTargets(JSON.stringify([{ id: 'quest-branch', map: 'altgard', x: 300, y: 250,
        name: 'Monster task', icon: 'monster.png', color: '#ffffff', questMonster: true,
        monsterBranch: true, branchX: 100, branchY: 150 }]));
    const branchLine = lines.at(-1);
    assert.equal(branchLine.options.color, '#ffffff');
    assert.equal(branchLine.options.dashArray, '10 8');
    assert.equal(rings.at(-1).color, '#ffffff');
    assert.equal(rings.at(-1).dashArray, '6 6');
    assert.equal(branchLine.at[0].lng, 100);
    assert.equal(branchLine.at[1].lng, 300);


});
