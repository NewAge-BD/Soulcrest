const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sourcePath = process.env.SOULCREST_MAP_SCRIPT || path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js');
const plain = value => JSON.parse(JSON.stringify(value));

function fixture() {
    let now = 10000, nextTimer = 1;
    const timers = new Map(), handlers = new Map(), calls = [], scripts = [], styles = new Map();
    const position = value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value;
    const group = () => {
        const layers = new Set();
        return { addTo(parent) { parent.addLayer(this); return this; }, addLayer(item) { layers.add(item); return this; },
            removeLayer(item) { layers.delete(item); return this; }, hasLayer: item => layers.has(item),
            eachLayer(callback) { [...layers].forEach(callback); }, getLayers: () => [...layers], clearLayers() { layers.clear(); } };
    };
    const bounds = (a, b) => ({ a: position(a), b: position(b), pad() { return this; }, contains: () => true });
    const map = Object.assign(group(), {
        zoom: 0, center: { lat: 0, lng: 0 }, options: {}, fitCount: 0,
        attributionControl: { setPrefix() {} }, createPane() {}, getPane: () => ({ style: {} }),
        on(names, callback) { for (const name of names.split(' ')) { if (!handlers.has(name)) handlers.set(name, []); handlers.get(name).push(callback); } },
        fire(name) { for (const callback of handlers.get(name) || []) callback({ type: name }); },
        getZoom() { return this.zoom; }, getCenter() { return this.center; },
        getBoundsZoom: () => 1,
        getBounds: () => bounds([-10000, -10000], [10000, 10000]),
        setMaxBounds(value) { this.options.maxBounds = value; },
        fitBounds(value) {
            this.fitCount++;
            this.zoom = 1;
            this.center = { lat: (value.a.lat + value.b.lat) / 2, lng: (value.a.lng + value.b.lng) / 2 };
            this.fire('zoom'); this.fire('zoomend'); this.fire('moveend');
        },
        setView(center, zoom) { this.center = { ...position(center) }; this.zoom = zoom; this.fire('zoom'); this.fire('zoomend'); this.fire('moveend'); return this; },
        panTo(center) { this.center = { ...position(center) }; this.fire('moveend'); return this; },
        stop() {},
        _limitCenter: value => value,
        _getCenterOffset(value) { const x = value.lng - this.center.lng, y = value.lat - this.center.lat; return { _trunc: () => ({ x: Math.trunc(x), y: Math.trunc(y) }) }; },
        getContainer: () => ({ style: { setProperty: (key, value) => styles.set(key, value) }, classList: { add() {} } })
    });
    const marker = (at, options) => ({ at: position(at), options, addTo(parent) { parent.addLayer(this); return this; },
        getLatLng() { return this.at; }, setLatLng(value) { this.at = position(value); return this; },
        getElement: () => ({ classList: { toggle() {} } }) });
    const L = { map: () => map, CRS: { Simple: {} }, canvas: () => ({}), layerGroup: group,
        latLng: position, latLngBounds: bounds, marker, divIcon: options => ({ options }),
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } } };
    const definitions = [
        { id: 'a', refZoom: 2, metersPerPixel: 1, size: 4096, minZoom: 0, maxZoom: 6, maxNativeZoom: 5, tiles: 'a' },
        { id: 'b', refZoom: 4, metersPerPixel: .5, size: 8192, minZoom: 1, maxZoom: 5, maxNativeZoom: 6, tiles: 'b' },
        { id: 'c', refZoom: 1, metersPerPixel: 1, size: 2048, minZoom: 0, maxZoom: 5, maxNativeZoom: 4, tiles: 'c' }
    ];
    const payloads = Object.fromEntries(definitions.map(def => [def.id, { id: def.id, refZoom: def.refZoom, markers: [], categories: [], groups: [], regions: [], icons: [] }]));
    let callback = () => Promise.resolve();
    const context = { window: { SoulcrestMaps: { ...payloads } }, L, Date: class extends Date { static now() { return now; } },
        document: { visibilityState: 'visible', createElement: () => ({}), head: { appendChild: script => scripts.push(script) } },
        setTimeout(fn, delay) { const id = nextTimer++; timers.set(id, { fn, at: now + delay }); return id; },
        clearTimeout: id => timers.delete(id) };
    const source = fs.readFileSync(sourcePath, 'utf8').replace('return { debugState, init, show,',
        'return { currentMapId: () => current?.id, fitZoomForTests: () => fitZoom, debugState, init, show,');
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.init('map', JSON.stringify({ version: 'synthetic', maps: definitions }), '/data/', {
        invokeMethodAsync(...args) { calls.push(plain(args)); return callback(...args); }
    });
    return { api, map, calls, scripts, context, payloads, styles,
        setCallback(value) { callback = value; },
        tick(ms) {
            const end = now + ms;
            for (;;) {
                const ready = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at || a[0] - b[0])[0];
                if (!ready) break;
                now = ready[1].at; timers.delete(ready[0]); ready[1].fn();
            }
            now = end;
        },
        move(id, x, y, zoom) { const scale = 2 ** definitions.find(def => def.id === id).refZoom; map.setView([-y / scale, x / scale], zoom); },
        view(id) { const scale = 2 ** definitions.find(def => def.id === id).refZoom; return { x: map.center.lng * scale, y: -map.center.lat * scale, zoom: map.zoom }; },
        saved: () => calls.filter(call => call[0] === 'MapViewChanged')
    };
}

test('saved native-pixel view restores before fallback and only the final viewport is reported', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 640, y: 1280, zoom: 4 }, revision: 1 });
    assert.deepEqual(f.view('a'), { x: 640, y: 1280, zoom: 4 });
    assert.equal(f.map.fitCount, 0);
    assert.equal(f.api.fitZoomForTests(), 1);
    assert.equal(f.styles.get('--pet-scale'), '2.197');
    assert.equal(f.saved().length, 0);
    f.tick(800);
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 640, 1280, 4, 1]]);
});

test('same-map reload restores and reports the final stop center rather than a pre-stop snapshot', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 2 }, revision: 1 });
    f.tick(800); f.calls.length = 0;
    f.map.stop = () => { f.move('a', 700, 800, 4); };
    await f.api.show('a', 'de', null, { savedView: { x: 10, y: 20, zoom: 1 }, revision: 2 });
    assert.deepEqual(f.view('a'), { x: 700, y: 800, zoom: 4 });
    f.tick(800);
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 700, 800, 4, 2]]);
});

test('switching away reports a synchronous stop adjustment under the outgoing map revision', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 2 }, revision: 1 });
    f.tick(800); f.calls.length = 0;
    f.map.stop = () => { f.move('a', 700, 800, 4); };
    await f.api.show('b', 'de', null, { savedView: { x: 3000, y: 4000, zoom: 3 }, revision: 1 });
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 700, 800, 4, 1]]);
    assert.deepEqual(f.view('b'), { x: 3000, y: 4000, zoom: 3 });
});

test('each map retains its own latest pan and zoom and JS cache wins over an older saved view', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: null, revision: 1 });
    f.move('a', 1200, 1600, 3.5);
    await f.api.show('b', 'en', null, { savedView: { x: 4000, y: 3000, zoom: 2 }, revision: 1 });
    f.move('b', 5000, 3500, 4.5);
    await f.api.show('a', 'de', null, { savedView: { x: 10, y: 20, zoom: 1 }, revision: 2 });
    assert.deepEqual(f.view('a'), { x: 1200, y: 1600, zoom: 3.5 });
    await f.api.show('b', 'de', null, { savedView: null, revision: 2 });
    assert.deepEqual(f.view('b'), { x: 5000, y: 3500, zoom: 4.5 });
});

test('leaving flushes pending outgoing state immediately without storing fitBounds intermediate views', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 300, y: 400, zoom: 2 }, revision: 1 });
    f.move('a', 900, 1100, 4);
    await f.api.show('b', 'en', null, { savedView: { x: 5000, y: 6000, zoom: 3 }, revision: 1 });
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 900, 1100, 4, 1]]);
    f.tick(800);
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 900, 1100, 4, 1], ['MapViewChanged', 'b', 5000, 6000, 3, 1]]);
});

test('a valid player center uses destination saved zoom rather than the departing map scale', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 100, zoom: 6 }, revision: 1 });
    await f.api.show('b', 'en', { x: 512, y: 768 }, { savedView: { x: 4000, y: 3000, zoom: 2.5 }, revision: 1 });
    assert.deepEqual(f.view('b'), { x: 512, y: 768, zoom: 2.5 });
});

test('keepAt without destination history retains metre-scale zoom conversion and works on first show', async () => {
    const f = fixture();
    await f.api.show('a', 'en', { x: 100, y: 200 }, { savedView: null, revision: 1 });
    assert.deepEqual(f.view('a'), { x: 100, y: 200, zoom: 1 });
    f.move('a', 100, 200, 3);
    await f.api.show('b', 'en', { x: 300, y: 400 }, { savedView: null, revision: 1 });
    assert.deepEqual(f.view('b'), { x: 300, y: 400, zoom: 4 });
});

test('destination cache zoom wins for keepAt and restored zoom stays inside the target map range', async () => {
    const f = fixture();
    await f.api.show('b', 'en', null, { savedView: { x: 1000, y: 2000, zoom: 99 }, revision: 1 });
    assert.equal(f.map.getZoom(), 5);
    f.move('b', 2000, 3000, 3.5);
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 1 }, revision: 1 });
    await f.api.show('b', 'en', { x: 4000, y: 5000 }, { savedView: { x: 10, y: 20, zoom: 2 }, revision: 2 });
    assert.deepEqual(f.view('b'), { x: 4000, y: 5000, zoom: 3.5 });
});

test('a superseded asynchronous show cannot poison another map cache or send its viewport', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 2 }, revision: 1 });
    delete f.context.window.SoulcrestMaps.b;
    const stale = f.api.show('b', 'en', null, { savedView: { x: 7777, y: 7777, zoom: 5 }, revision: 1 });
    f.move('a', 800, 900, 4);
    await f.api.show('c', 'en', null, { savedView: { x: 500, y: 600, zoom: 3 }, revision: 1 });
    f.context.window.SoulcrestMaps.b = f.payloads.b;
    f.scripts[0].onload();
    assert.equal(JSON.parse(await stale).cancelled, true);
    assert.equal(f.api.currentMapId(), 'c');
    f.tick(800);
    assert.equal(f.saved().filter(call => call[1] === 'b').length, 0);
    await f.api.show('b', 'en', null, { savedView: { x: 1000, y: 2000, zoom: 2 }, revision: 2 });
    assert.deepEqual(f.view('b'), { x: 1000, y: 2000, zoom: 2 });
    await f.api.show('a', 'en', null, { savedView: { x: 10, y: 20, zoom: 1 }, revision: 2 });
    assert.deepEqual(f.view('a'), { x: 800, y: 900, zoom: 4 });
});

test('an older per-map requested revision cannot supersede a newer displayed view', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 500, y: 700, zoom: 3 }, revision: 4 });
    const stale = JSON.parse(await f.api.show('a', 'de', null, { savedView: { x: 999, y: 999, zoom: 1 }, revision: 3 }));
    assert.equal(stale.cancelled, true);
    assert.deepEqual(f.view('a'), { x: 500, y: 700, zoom: 3 });
    f.tick(800);
    assert.deepEqual(f.saved(), [['MapViewChanged', 'a', 500, 700, 3, 4]]);
});

test('continuous following batches persistence rather than writing once per position', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 3 }, revision: 1 });
    f.tick(800); f.calls.length = 0;
    for (let i = 0; i < 120; i++) {
        f.api.setPlayer('a', 200 + i * 40, 400 + i * 40, true, true);
        f.tick(100);
    }
    assert.equal(f.saved().length, 2);
    f.tick(800);
    assert.equal(f.saved().length, 3);
    assert.deepEqual(f.saved().at(-1), ['MapViewChanged', 'a', 4960, 5160, 3, 1]);
});

test('invalid coordinates or revisions never reach the persistence callback', async () => {
    for (const revision of [null, -1, NaN, Infinity, 1.25, '2', Number.MAX_SAFE_INTEGER + 1]) {
        const f = fixture();
        await f.api.show('a', 'en', null, { savedView: { x: NaN, y: 200, zoom: 2 }, revision });
        f.tick(6000);
        assert.equal(f.saved().length, 0);
    }
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: null, revision: 1 });
    f.tick(800); f.calls.length = 0;
    f.map.center = { lat: NaN, lng: Infinity }; f.map.fire('moveend');
    f.tick(6000);
    assert.equal(f.saved().length, 0);
});

test('synchronous callback failure and rejected saves do not break map changes', async () => {
    const f = fixture();
    f.setCallback(() => { throw new Error('synthetic save failure'); });
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 2 }, revision: 1 });
    assert.doesNotThrow(() => f.tick(800));
    f.setCallback(() => Promise.reject(new Error('synthetic rejected save')));
    await f.api.show('b', 'en', null, { savedView: { x: 3000, y: 4000, zoom: 3 }, revision: 1 });
    f.tick(800); await Promise.resolve(); await Promise.resolve();
    assert.deepEqual(f.view('b'), { x: 3000, y: 4000, zoom: 3 });
    await f.api.show('a', 'en', null, { savedView: null, revision: 2 });
    assert.deepEqual(f.view('a'), { x: 100, y: 200, zoom: 2 });
});

test('a late physical-context change cancels keepAt while still restoring independent browsing history', async () => {
    const f = fixture();
    await f.api.show('a', 'en', null, { savedView: { x: 100, y: 200, zoom: 2 }, revision: 1 });
    f.api.setNavigationContext(4, true);
    delete f.context.window.SoulcrestMaps.b;
    const pending = f.api.show('b', 'en', { x: 999, y: 999 }, { savedView: { x: 3000, y: 4000, zoom: 3 }, revision: 1 });
    f.api.setNavigationContext(5, false);
    f.context.window.SoulcrestMaps.b = f.payloads.b;
    f.scripts[0].onload();
    await pending;
    assert.deepEqual(f.view('b'), { x: 3000, y: 4000, zoom: 3 });
});
