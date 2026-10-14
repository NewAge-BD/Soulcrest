const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the shipped map API with mutable Leaflet layers. Counts distinguish actual reuse
// from a redraw that merely produces the same-looking replacement objects.
function fixture() {
    const counters = { marker: 0, circle: 0, line: 0, tooltip: 0, icon: 0, clears: 0, progression: 0 };
    const position = value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value;
    const point = (x, y) => ({ x, y,
        distanceTo(other) { return Math.hypot(this.x - other.x, this.y - other.y); },
        subtract(other) { return point(this.x - other.x, this.y - other.y); },
        _trunc() { this.x = Math.trunc(this.x); this.y = Math.trunc(this.y); return this; }
    });
    const makeLayer = (kind, at, options = {}) => {
        counters[kind]++;
        const classes = new Set();
        const element = { firstElementChild: { style: {} }, classList: { toggle(name, on) { on ? classes.add(name) : classes.delete(name); } } };
        const layer = { kind, options, at: kind === 'line' ? at.map(position) : position(at), moves: 0, paths: 0, classes,
            addTo(parent) { parent.addLayer(this); return this; },
            setLatLng(value) { this.at = position(value); this.moves++; return this; }, getLatLng() { return this.at; },
            setLatLngs(value) { this.at = value.map(position); this.paths++; return this; },
            setRadius(radius) { this.options.radius = radius; return this; }, getRadius() { return this.options.radius; },
            setIcon(icon) { this.options.icon = icon; return this; }, getElement() { return element; },
            bindPopup(content) { this.popup = content; return this; }, closePopup() {}, openPopup() {},
            bindTooltip(content, settings) { this.tooltip = { content, settings }; return this; },
            setContent(content) { this.content = content; return this; }, on() { return this; }
        };
        return layer;
    };
    const group = () => {
        const members = new Set();
        return { addTo(parent) { parent.addLayer(this); return this; },
            addLayer(layer) { members.add(layer); return this; }, removeLayer(layer) { members.delete(layer); return this; },
            hasLayer(layer) { return members.has(layer) || [...members].some(item => item.hasLayer?.(layer)); },
            getLayers: () => [...members], eachLayer(callback) { [...members].forEach(callback); },
            clearLayers() { counters.clears++; members.clear(); return this; }
        };
    };
    const handlers = new Map();
    const map = Object.assign(group(), {
        options: { maxBounds: null }, center: { lat: 0, lng: 0 }, zoom: 0, pans: [], moves: [], clip: () => true,
        attributionControl: { setPrefix() {} }, createPane() {}, getPane: () => ({ style: {} }),
        on(names, callback) { for (const name of names.split(' ')) { if (!handlers.has(name)) handlers.set(name, []); handlers.get(name).push(callback); } return this; },
        fire(name) { for (const callback of handlers.get(name) || []) callback(); },
        getZoom() { return this.zoom; }, getCenter() { return this.center; },
        getBounds() { return { pad() { return this; }, contains: at => map.clip(position(at)) }; },
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {} } }),
        setMaxBounds(bounds) { this.options.maxBounds = bounds; },
        fitBounds() { this.fire('moveend'); },
        setView(at, zoom) { this.center = position(at); this.zoom = zoom; this.fire('moveend'); },
        panTo(at, options) { this.pans.push({ at: position(at), options }); this.center = position(at); this.fire('moveend'); },
        flyTo(at) { this.moves.push(position(at)); this._flyToFrame = 77; this.fire('movestart'); },
        flyToBounds() { this._flyToFrame = 77; this.fire('movestart'); }, closePopup() {},
        latLngToLayerPoint(at) { const ll = position(at), scale = 2 ** this.zoom; return point(Math.round(ll.lng * scale), Math.round(-ll.lat * scale)); },
        layerPointToLatLng(at) { const scale = 2 ** this.zoom; return { lat: -at.y / scale, lng: at.x / scale }; },
        _limitCenter(at) { return this.limit ? this.limit(at) : at; },
        _getCenterOffset(at) { return this.latLngToLayerPoint(at).subtract(point(this.center.lng * 2 ** this.zoom, -this.center.lat * 2 ** this.zoom)); }
    });
    const L = {
        map: () => map, canvas: () => ({}), CRS: { Simple: {} },
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } },
        latLng: position, latLngBounds: () => ({ pad() { return this; } }), point, layerGroup: group,
        divIcon(options) { counters.icon++; return { options }; },
        marker: (at, options) => makeLayer('marker', at, options),
        circleMarker: (at, options) => makeLayer('circle', at, options),
        polyline: (at, options) => makeLayer('line', at, options),
        tooltip: options => makeLayer('tooltip', null, options)
    };
    const categories = [
        { group: 'Resources', name: 'Gem', icon: 0, count: 1 },
        { group: 'Pets', name: 'Fera', icon: 1, count: 2 },
        { group: 'Monsters', name: 'Fera', icon: -1, count: 1 },
        { group: 'Locations', name: 'Kibelisks', icon: 2, count: 1 }
    ];
    const data = (id, markers) => ({ id, refZoom: 0, icons: ['gem.png', 'pet.png', 'place.png'], categories,
        groups: categories.map((item, i) => ({ name: item.group, categories: [i] })), markers, regions: [] });
    const first = data('first', [[0, 700, 0, 'Gem', 'Juwel', 0], [1, 100, 100, 'Pet A', 'Pet A DE', 1, 'pet-a'],
        [1, 800, 100, 'Pet B', null, 1, 'pet-b'], [2, 250, 200, 'Soul monster', 'Seelenmonster', -1, 'pet-a'],
        [3, 500, 500, 'Kibelisk', null, 2]]);
    const second = data('second', [[0, 700, 0, 'Ruby', null, 2]]);
    const callbacks = [];
    const context = { window: { SoulcrestMaps: { first, second } }, L, document: { visibilityState: 'visible' },
        Date, setTimeout, clearTimeout, setInterval, clearInterval, performance, counters };
    let source = fs.readFileSync(process.env.SOULCREST_MAP_SCRIPT || path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8');
    source = source.replace('function updateProgression() {', 'function updateProgression() { counters.progression++;');
    source = source.replace('return { debugState, init, show,', `return {
        layers: () => targetLayer.getLayers(), playerLayer: () => player,
        debugState, init, show,`);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.init('map', JSON.stringify({ maps: ['first', 'second'].map(id => ({ id, refZoom: 0, size: 1000, minZoom: 0, maxZoom: 4, maxNativeZoom: 3, tiles: 'tiles' })) }), '/data/',
        { invokeMethodAsync(...args) { callbacks.push(args); } });
    return { api, map, counters, context, callbacks };
}

const serial = value => JSON.parse(JSON.stringify(value));
const target = (id, x, y = 0, extra = {}) => ({ id, map: 'first', x, y, name: id, color: '#4ade80', ...extra });
const arrows = api => api.layers().filter(layer => layer.options.icon?.options.className === 'route-arrow');
const lines = api => api.layers().filter(layer => layer.kind === 'line');
const symbols = api => api.layers().filter(layer => layer.kind === 'marker' && layer.options.icon.options.className !== 'route-arrow');

test('player fixes retain target layers, move line endpoints and arrow positions without recreating icons', async () => {
    const { api, counters } = fixture();
    await api.show('first', 'en');
    api.setPlayer('first', 0, 0, false, true);
    api.setTargets(JSON.stringify([target('gem', 700)]));
    const original = api.layers(), originalArrow = arrows(api)[0], originalLine = lines(api)[0];
    const before = { ...counters }, player = api.playerLayer();
    api.setPlayer('first', 10, 20, false, true);
    assert.deepEqual(api.layers(), original);
    assert.deepEqual(serial(originalLine.at), [{ lat: -20, lng: 10 }, { lat: 0, lng: 700 }]);
    assert.notDeepEqual(serial(originalArrow.at), { lat: 0, lng: 35 });
    assert.match(originalArrow.options.icon.options.html, /transform:rotate\(/);
    assert.match(originalArrow.getElement().firstElementChild.style.transform, /rotate\(/);
    for (const kind of ['marker', 'circle', 'line', 'tooltip', 'icon', 'clears']) assert.equal(counters[kind], before[kind], kind);
    assert.equal(counters.progression, before.progression + 1);
    const moves = player.moves, paths = originalLine.paths, arrowMoves = originalArrow.moves;
    api.setPlayer('first', 10, 20, false, false);
    assert.equal(player.moves, moves);
    assert.equal(originalLine.paths, paths);
    assert.equal(originalArrow.moves, arrowMoves);
    assert.equal(player.classes.has('lost'), true);
    assert.equal(counters.progression, before.progression + 2); // freshness never short-circuits logic
    api.setPlayer('first', 10, 20, false, true);
    assert.equal(player.classes.has('lost'), false);
    assert.deepEqual(api.layers(), original);
});

test('identical target snapshots and moveend retain identity; viewport clipping reuses pooled arrows', async () => {
    const { api, map, counters } = fixture();
    await api.show('first', 'en');
    api.setPlayer('first', 0, 0, false, true);
    const json = JSON.stringify([target('gem', 700)]);
    api.setTargets(json);
    const original = api.layers(), arrowPool = arrows(api), before = { ...counters };
    api.setTargets(json);
    map.fire('moveend');
    assert.deepEqual(api.layers(), original);
    assert.equal(counters.clears, before.clears);
    map.clip = at => at.lng < 180;
    map.fire('moveend');
    assert.equal(arrows(api).length, 3);
    assert.deepEqual(arrows(api), arrowPool.slice(0, 3));
    map.clip = () => true;
    map.fire('moveend');
    assert.deepEqual(arrows(api), arrowPool);
    assert.equal(counters.marker, before.marker);
    assert.equal(counters.icon, before.icon);
});

test('zoom updates pet rings and exact arrow spacing while retaining static symbols and tooltips', async () => {
    const { api, map } = fixture();
    await api.show('first', 'en');
    api.setPlayer('first', 0, 0, false, true);
    api.setTargets(JSON.stringify([target('level', 700, 0, { leveling: true, group: 'Pets', icon: 'pet.png' })]));
    const staticLayers = api.layers().filter(layer => !arrows(api).includes(layer));
    const firstArrow = arrows(api)[0];
    const rings = api.layers().filter(layer => layer.options.fill === false);
    assert.equal(rings[0].options.radius, 25);
    map.zoom = 1;
    map.fire('zoomend');
    assert.deepEqual(api.layers().filter(layer => !arrows(api).includes(layer)), staticLayers);
    assert.equal(rings[0].options.radius, 30);
    assert.equal(arrows(api)[0], firstArrow);
    assert.equal(arrows(api)[0].at.lng, 17.5);
    assert.equal(arrows(api)[1].at.lng - arrows(api)[0].at.lng, 35);
});

test('chains, completed history and monster branches keep their start rules through movement and route switches', async () => {
    const { api } = fixture();
    await api.show('first', 'en');
    api.setPlayer('first', 0, 0, false, true);
    api.setTargets(JSON.stringify([
        target('past', 100, 0, { leveling: true, completed: true }),
        target('next', 300, 0, { leveling: true }),
        target('later', 500, 0, { leveling: true, after: 'next' }),
        target('spawn', 250, 200, { questMonster: true }),
        target('branch', 650, 0, { questMonster: true, monsterBranch: true, branchX: 200, branchY: 100 })
    ]));
    const originalLines = lines(api), originalSymbols = symbols(api);
    assert.equal(originalLines.length, 6);
    assert.equal(originalSymbols[0].options.opacity, 0.28);
    api.setPlayer('first', 20, 30, false, true);
    assert.deepEqual(lines(api), originalLines);
    assert.deepEqual(serial(originalLines.map(layer => layer.at[0])), [
        { lat: -30, lng: 20 }, { lat: -30, lng: 20 },
        { lat: 0, lng: 300 }, { lat: 0, lng: 300 },
        { lat: -100, lng: 200 }, { lat: -100, lng: 200 }
    ]);
    assert.equal(originalLines[5].options.dashArray, '10 8');
    api.setTargets(JSON.stringify([target('new-route', 400, 50, { leveling: true, completed: true })]));
    assert.equal(lines(api).length, 0);
    assert.equal(arrows(api).length, 0);
    assert.equal(symbols(api).length, 1);
    assert.equal(api.layers().some(layer => originalLines.includes(layer)), false);
    assert.match(api.layers().find(layer => layer.kind === 'tooltip').content, /new-route/);
});

test('map switches discard copied icons and player loss removes only player-rooted navigation', async () => {
    const { api } = fixture();
    await api.show('first', 'en');
    api.setPlayer('first', 0, 0, false, true);
    api.setTargets(JSON.stringify([target('gem', 700), target('later', 900, 0, { after: 'gem' })]));
    const oldSymbol = symbols(api)[0];
    assert.match(oldSymbol.options.icon.options.html, /gem\.png/);
    api.setPlayer('second', 0, 0, false, false);
    assert.equal(api.playerLayer(), null);
    assert.equal(lines(api).length, 2);
    assert.deepEqual(serial(lines(api)[0].at), [{ lat: 0, lng: 700 }, { lat: 0, lng: 900 }]);
    api.setTargets(JSON.stringify([target('ruby', 700, 0, { map: 'second' })]));
    await api.show('second', 'en');
    assert.equal(symbols(api).length, 1);
    assert.notEqual(symbols(api)[0], oldSymbol);
    assert.match(symbols(api)[0].options.icon.options.html, /place\.png/);
    assert.equal(lines(api).length, 0);
    api.setPlayer('second', 0, 0, false, true);
    assert.equal(lines(api).length, 2);
});

test('category, pet labels, exploration state and language refresh copied symbols immediately', async () => {
    const { api } = fixture();
    await api.show('first', 'en');
    api.setTargets(JSON.stringify([target('pet', 100, 100, { petId: 'pet-a' })]));
    assert.equal(symbols(api).length, 0); // ordinary pet category is visible
    api.setCategoryVisible(1, false);
    assert.equal(symbols(api).length, 1);
    api.setDone('["pet-a"]', '[]', '{"pet-a":"Lv. 3 · max"}');
    assert.match(symbols(api)[0].options.icon.options.html, /Lv\. 3 · max/);
    assert.match(symbols(api)[0].options.icon.options.className, /done/);
    api.setCategoryVisible(1, true);
    assert.equal(symbols(api).length, 0);
    api.setCategoryVisible(3, false);
    api.setTargets(JSON.stringify([target('place', 500, 500)]));
    api.setExploration(JSON.stringify([{ index: 4, kind: 'kibelisk', id: 'place', done: false }]));
    assert.match(symbols(api)[0].options.icon.options.className, /kibelisk-unbound/);
    api.setExploration(JSON.stringify([{ index: 4, kind: 'kibelisk', id: 'place', done: true }]));
    assert.doesNotMatch(symbols(api)[0].options.icon.options.className, /kibelisk-unbound/);
    api.setTargets(JSON.stringify([target('monster', 250, 200, { group: 'Monsters', petId: 'pet-a' })]));
    assert.match(api.layers().find(layer => layer.kind === 'tooltip').content, /Pet A/);
    api.setLanguage('de');
    assert.match(api.layers().find(layer => layer.kind === 'tooltip').content, /Pet A DE/);
});

test('every verified fix still selects the nearest progression target with the map hidden', async () => {
    const { api, callbacks, context, counters } = fixture();
    await api.show('first', 'en');
    context.document.visibilityState = 'hidden';
    api.setProgression(true, 5, '{}');
    api.setPlayer('first', 790, 100, false, true);
    assert.equal(callbacks.at(-1)[2], 'pet-b');
    api.setPlayer('first', 110, 100, false, true);
    assert.equal(callbacks.at(-1)[2], 'pet-a');
    const before = counters.progression;
    api.setPlayer('first', 110, 100, false, true);
    assert.equal(counters.progression, before + 1);
    api.setProgression(false, 5, '{}');
    assert.equal(callbacks.at(-1)[2], null);
    assert.equal(api.layers().length, 0);
});

test('follow skips only Leaflet zero-pixel offsets and keeps animation, bounds and freshness semantics', async () => {
    const { api, map, context } = fixture();
    await api.show('first', 'en');
    map.center = { lat: -0.2, lng: 0.2 };
    api.setPlayer('first', 0.69, 0.69, true, true);
    assert.equal(map.pans.length, 0); // projected offset 0.8 truncates to zero in Leaflet
    api.setPlayer('first', 1.51, 0.69, true, true);
    assert.equal(map.pans.length, 1);
    assert.deepEqual(serial(map.pans[0].options), { animate: true, duration: 0.25, easeLinearity: 1, noMoveStart: true });
    api.setPlayer('first', 1.51, 0.69, true, false);
    assert.equal(map.pans.length, 1);
    map._panAnim = { _inProgress: true };
    api.setPlayer('first', 1.51, 0.69, true, true);
    assert.equal(map.pans.length, 2); // panTo must still stop the previous animation
    map._panAnim._inProgress = false;
    map._flyToFrame = 77; // completed flyTo leaves this ID in Leaflet
    api.setPlayer('first', 1.51, 0.69, true, true);
    assert.equal(map.pans.length, 2);
    map.fire('movestart');
    api.setPlayer('first', 1.51, 0.69, true, true);
    assert.equal(map.pans.length, 3);
    map.limit = () => map.center;
    api.setPlayer('first', 9999, 9999, true, true);
    assert.equal(map.pans.length, 3); // the bounds-limited center has no effective movement
    map.limit = null;
    context.document.visibilityState = 'hidden';
    api.setPlayer('first', 20, 20, true, true);
    assert.equal(map.pans.at(-1).options.animate, false);
});
