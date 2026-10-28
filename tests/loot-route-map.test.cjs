const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const plain = value => JSON.parse(JSON.stringify(value));

function fixture() {
    const groups = [], fits = [], pans = [];
    const group = () => {
        const entries = [];
        const result = { entries, addTo(parent) { parent.addLayer(this); return this; },
            addLayer(item) { entries.push(item); return this; }, clearLayers() { entries.length = 0; },
            eachLayer(fn) { [...entries].forEach(fn); }, getLayers: () => entries,
            removeLayer(item) { const i = entries.indexOf(item); if (i >= 0) entries.splice(i, 1); },
            hasLayer: item => entries.includes(item) };
        groups.push(result);
        return result;
    };
    const position = value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value;
    const bounds = (a, b) => ({ points: b ? [a, b] : a, pad() { return this; }, contains: () => true });
    const map = Object.assign(group(), {
        options: {}, attributionControl: { setPrefix() {} }, createPane() {}, getPane: () => ({ style: {} }),
        on() {}, getZoom: () => 1, getCenter: () => ({ lat: 0, lng: 0 }), getBoundsZoom: () => 1,
        getBounds: () => bounds([[-1000, 0], [0, 1000]]), setMaxBounds() {}, stop() {},
        fitBounds(value, options) { fits.push({ points: plain(value.points), options }); },
        setView() {}, closePopup() {}, panTo(value) { pans.push(value); },
        _limitCenter: value => value, _getCenterOffset: () => ({ _trunc: () => ({ x: 1, y: 1 }) }),
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {} } })
    });
    const layer = (kind, points, options) => ({ kind, points, options,
        addTo(parent) { parent.addLayer(this); return this; }, bindTooltip(value) { this.tooltip = value; return this; },
        getLatLng() { return position(this.points); }, setLatLng(value) { this.points = value; return this; },
        getElement: () => ({ classList: { toggle() {} } }) });
    const L = { map: () => map, CRS: { Simple: {} }, canvas: () => ({}), layerGroup: group,
        latLng: position, latLngBounds: bounds, divIcon: options => ({ options }),
        marker: (p, o) => layer('player', p, o), polyline: (p, o) => layer('line', p, o),
        circleMarker: (p, o) => layer('point', p, o),
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } } };
    const definitions = ['altgard', 'verteron'].map(id => ({ id, refZoom: 4, size: 4096, minZoom: 0, maxZoom: 6, maxNativeZoom: 5, tiles: id }));
    const payloads = Object.fromEntries(definitions.map(def => [def.id, { id: def.id, refZoom: def.refZoom,
        markers: [], categories: [], groups: [], regions: [], icons: [] }]));
    const context = { L, window: { SoulcrestMaps: payloads },
        document: { visibilityState: 'visible' }, Date: class extends Date { static now() { return 100000; } },
        setTimeout: () => 1, clearTimeout() {} };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8'), context);
    const api = context.window.soulcrestMap;
    api.init('map', JSON.stringify({ maps: definitions }), '', { invokeMethodAsync: () => Promise.resolve() });
    return { api, fits, pans, groups, map,
        sessionMarkers: () => groups.flatMap(g => g.entries).filter(entry => entry.options?.icon?.options?.className?.startsWith('loot-session-marker ')),
        routeLayers: () => groups.flatMap(g => g.entries).filter(entry => entry.kind === 'line' || entry.kind === 'point') };
}

test('recorded route uses native map pixels, fits all segments and never joins gaps', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.fits.length = 0;
    const count = f.api.setLootRoute('altgard', JSON.stringify([
        [{ x: 160, y: 320 }, { x: 320, y: 480 }],
        [{ x: 800, y: 960 }, { x: 960, y: 1120 }]
    ]), 'Session <one>');
    assert.equal(count, 4);
    const lines = f.routeLayers().filter(layer => layer.kind === 'line' && layer.options.color === '#67e8f9');
    assert.deepEqual(plain(lines.map(line => line.points)), [[[-20, 10], [-30, 20]], [[-60, 50], [-70, 60]]]);
    assert.equal(f.fits.length, 1);
    assert.equal(f.fits[0].points.length, 4);
    assert(f.routeLayers().filter(layer => layer.kind === 'point').every(layer => layer.tooltip === 'Session &lt;one&gt;'));
});

test('foreign map, malformed input and invalid points cannot create false edges', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    assert.equal(f.api.setLootRoute('verteron', '[[{"x":100,"y":100}]]', ''), 0);
    assert.equal(f.api.setLootRoute('altgard', '{broken', ''), 0);
    assert.equal(f.api.setLootRoute('altgard', JSON.stringify([[{ x: 10, y: 20 }, null, { x: 30, y: 40 },
        { x: 5000, y: 40 }, { x: 50, y: 60 }]]), ''), 3);
    assert.equal(f.routeLayers().filter(layer => layer.kind === 'line').length, 0);
    assert.equal(f.routeLayers().filter(layer => layer.kind === 'point').length, 3);
});

test('preview is independent of ordinary focus and suppresses automatic follow until cleared', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.api.setLootRoute('altgard', '[[{"x":100,"y":200},{"x":300,"y":400}]]', '', false);
    f.api.clearFocus();
    assert(f.routeLayers().length > 0);
    f.api.setPlayer('altgard', 500, 600, true, true);
    assert.equal(f.pans.length, 0);
    f.api.clearLootRoute();
    assert.equal(f.routeLayers().length, 0);
    f.api.setPlayer('altgard', 510, 610, true, true);
    assert.equal(f.pans.length, 1);
});

test('changing maps drops the temporary preview and does not replay it on return', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.api.setLootRoute('altgard', '[[{"x":100,"y":200},{"x":300,"y":400}]]', '', false);
    const routeGroup = f.groups.find(group => group.entries.some(entry => entry.kind === 'line'));
    assert(f.map.hasLayer(routeGroup));
    await f.api.show('verteron', 'en');
    assert(!f.map.hasLayer(routeGroup));
    f.api.setPlayer('verteron', 510, 610, true, true);
    assert.equal(f.pans.length, 1);
    await f.api.show('altgard', 'en');
    assert(!f.map.hasLayer(routeGroup));
});

test('marker-only session focus fits native coordinates and uses three distinct glyphs without route lines', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.fits.length = 0;
    const count = f.api.setLootRoute('altgard', '[]', 'Session', true, JSON.stringify([
        { x: 160, y: 320, kind: 'kill', tooltip: 'Player kill' },
        { x: 320, y: 480, kind: 'death', tooltip: 'Own death' },
        { x: 800, y: 960, kind: 'odyle', tooltip: 'Odyle' }
    ]));
    assert.equal(count, 3);
    assert.equal(f.routeLayers().length, 0);
    assert.deepEqual(plain(f.sessionMarkers().map(marker => marker.points)), [[-20, 10], [-30, 20], [-60, 50]]);
    assert.equal(new Set(f.sessionMarkers().map(marker => marker.options.icon.options.html)).size, 3);
    assert(f.sessionMarkers().every(marker => marker.options.keyboard === false));
    assert.equal(f.fits.length, 1);
    assert.equal(f.fits[0].points.length, 3);
    f.api.setPlayer('altgard', 500, 600, true, true);
    assert.equal(f.pans.length, 0);
    f.api.clearLootRoute();
    assert.equal(f.sessionMarkers().length, 0);
});

test('session markers extend bounds without creating edges or changing the fourth fit argument', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.fits.length = 0;
    const segments = '[[{"x":160,"y":320},{"x":320,"y":480}]]';
    const markers = JSON.stringify([{ x: 1600, y: 3200, kind: 'kill', tooltip: 'Player kill' }]);
    assert.equal(f.api.setLootRoute('altgard', segments, 'Session', false, markers), 3);
    assert.equal(f.fits.length, 0);
    const lines = f.routeLayers().filter(layer => layer.kind === 'line');
    assert.equal(lines.length, 2);
    assert(lines.every(line => line.points.length === 2));
    assert.equal(f.api.setLootRoute('altgard', segments, 'Session', true, markers), 3);
    assert.equal(f.fits[0].points.length, 3);
    assert.deepEqual(f.fits[0].points[2], [-200, 100]);
});

test('marker tooltips are escaped and unknown kinds or invalid coordinates cannot inject markup or points', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    const malicious = '<img src=x onerror="alert(1)">&';
    const markers = [
        { x: 160, y: 320, kind: 'kill', tooltip: malicious },
        { x: 200, y: 300, kind: 'unknown', tooltip: 'ignored' },
        { x: 200, y: 300, kind: '__proto__', tooltip: 'ignored' },
        { x: 200, y: 300, kind: 'constructor', tooltip: 'ignored' },
        { x: 200, y: 300, kind: '<script>', tooltip: 'ignored' },
        { x: -1, y: 300, kind: 'death' },
        { x: 5000, y: 300, kind: 'death' },
        { x: '100', y: 300, kind: 'odyle' },
        { x: null, y: 300, kind: 'odyle' }, null
    ];
    assert.equal(f.api.setLootRoute('altgard', '[]', '', true, JSON.stringify(markers)), 1);
    assert.equal(f.sessionMarkers()[0].tooltip, '&lt;img src=x onerror=&quot;alert(1)&quot;&gt;&amp;');
    assert(!f.sessionMarkers()[0].options.icon.options.html.includes('onerror'));
    assert.equal(f.api.setLootRoute('verteron', '[]', '', true, JSON.stringify(markers)), 0);
});

test('malformed marker payload does not break valid routes and filtered redraw removes old marker kinds', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    const segments = '[[{"x":160,"y":320},{"x":320,"y":480}]]';
    for (const invalid of ['{broken', '{}', 'null']) {
        assert.equal(f.api.setLootRoute('altgard', segments, '', false, invalid), 2);
        assert.equal(f.sessionMarkers().length, 0);
    }
    const markers = [
        { x: 160, y: 320, kind: 'kill' }, { x: 320, y: 480, kind: 'death' }, { x: 800, y: 960, kind: 'odyle' }
    ];
    f.api.setLootRoute('altgard', segments, '', false, JSON.stringify(markers));
    assert.equal(f.sessionMarkers().length, 3);
    f.api.setLootRoute('altgard', segments, '', false, JSON.stringify(markers.filter(marker => marker.kind === 'odyle')));
    assert.equal(f.sessionMarkers().length, 1);
    assert(f.sessionMarkers()[0].options.icon.options.className.endsWith('-odyle'));
    assert.equal(f.routeLayers().filter(layer => layer.kind === 'line').length, 2);
});

test('map switch removes marker-only focus without restoring it on return', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.api.setLootRoute('altgard', '[]', '', false, '[{"x":160,"y":320,"kind":"kill"}]');
    const preview = f.groups.find(group => group.entries.some(entry => entry.options?.icon?.options?.className?.startsWith('loot-session-marker ')));
    assert(f.map.hasLayer(preview));
    await f.api.show('verteron', 'en');
    assert(!f.map.hasLayer(preview));
    await f.api.show('altgard', 'en');
    assert(!f.map.hasLayer(preview));
});

test('unified gathering markers draw alongside combat and legacy odyle without contributing route edges', async () => {
    const f = fixture();
    await f.api.show('altgard', 'en');
    f.fits.length = 0;
    const markers = [
        { x: 160, y: 320, kind: 'kill' }, { x: 320, y: 480, kind: 'death' },
        { x: 800, y: 960, kind: 'odyle' }, { x: 960, y: 1120, kind: 'gathering', tooltip: 'Resource <node>' }
    ];
    assert.equal(f.api.setLootRoute('altgard', '[]', 'Session', true, JSON.stringify(markers)), 4);
    assert.equal(f.routeLayers().length, 0);
    const gathering = f.sessionMarkers().find(marker => marker.options.icon.options.className.endsWith('-gathering'));
    assert(gathering);
    assert.deepEqual(plain(gathering.points), [-70, 60]);
    assert(gathering.options.icon.options.html.includes('✦'));
    assert(gathering.options.icon.options.html.includes('#86d6a5'));
    assert.equal(gathering.tooltip, 'Resource &lt;node&gt;');
    assert.equal(new Set(f.sessionMarkers().map(marker => marker.options.icon.options.html)).size, 4);
    assert.equal(f.fits[0].points.length, 4);
    // The two gathering kinds share one UI filter. A filtered redraw removes both, retaining combat.
    assert.equal(f.api.setLootRoute('altgard', '[]', 'Session', false,
        JSON.stringify(markers.filter(marker => !['gathering', 'odyle'].includes(marker.kind)))), 2);
    assert(f.sessionMarkers().every(marker => !/-gathering$|-odyle$/.test(marker.options.icon.options.className)));
});
