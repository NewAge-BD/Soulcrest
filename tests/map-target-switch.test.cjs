const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

function fixture(sourcePath) {
    const point = (x, y) => ({ x, y, distanceTo(other) { return Math.hypot(x - other.x, y - other.y); } });
    const position = value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value;
    const layer = (at, options = {}) => ({ options, at: position(at),
        addTo(parent) { parent.addLayer(this); return this; },
        bindPopup() { return this; }, bindTooltip() { return this; }, on() { return this; }, closePopup() {},
        setLatLng(value) { this.at = position(value); return this; }, getLatLng() { return this.at; },
        setContent(text) { this.text = text; return this; },
        getElement() { return { classList: { toggle() {} } }; }
    });
    const group = () => {
        const members = new Set();
        return { addTo(parent) { parent.addLayer(this); return this; },
            addLayer(item) { members.add(item); return this; }, removeLayer(item) { members.delete(item); return this; },
            getLayers: () => [...members], eachLayer(callback) { [...members].forEach(callback); },
            clearLayers() { members.clear(); }, hasLayer: item => members.has(item)
        };
    };
    const map = Object.assign(group(), {
        getZoom: () => 0, getCenter: () => ({ lat: 0, lng: 0 }), getBounds: () => ({ pad() { return this; }, contains: () => true }),
        setMaxBounds() {}, fitBounds() { this.onMove(); }, setView() { this.onMove(); },
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {} } }),
        latLngToLayerPoint: value => point(position(value).lng, position(value).lat),
        layerPointToLatLng: value => ({ lat: value.y, lng: value.x })
    });
    const L = {
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } },
        latLng: position, latLngBounds: () => ({ pad() { return this; } }), point,
        divIcon: options => ({ options }), layerGroup: group,
        marker: layer, circleMarker: layer, polyline: layer, tooltip: options => layer(null, options)
    };
    const data = (id, markers) => ({ id, refZoom: 0, icons: ['gem.png', 'ruby.png'],
        categories: [{ group: 'Resources', name: 'Gem', icon: 0, count: markers.length }],
        markers, groups: [{ name: 'Resources', categories: [0] }], regions: []
    });
    const old = data('old', [[0, 10, 20, 'First', null, 0], [0, 30, 40, 'Second', null, 0], [0, 100, 200, 'Old shared', null, 0]]);
    const next = data('next', [[0, 100, 200, 'New shared', null, 1]]);
    const context = { window: { SoulcrestMaps: { old, next } }, L };
    const source = fs.readFileSync(sourcePath, 'utf8').replace('return { debugState, init, show,', `return {
        setup(m, defs) { map = m; manifest = { maps: defs }; base = '/data/'; dotnet = { invokeMethodAsync() {} }; m.onMove = drawTargets; },
        targetLayers() { return targetLayer.getLayers(); },
        debugState, init, show,`);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    const defs = ['old', 'next'].map(id => ({ id, refZoom: 0, size: 1000, minZoom: 0, maxZoom: 4, maxNativeZoom: 3, tiles: 'tiles' }));
    api.setup(map, defs);
    return { api, context, next };
}

const sourcePath = process.env.SOULCREST_MAP_SCRIPT || path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js');

test('switching to fewer markers never renders old marker indexes during Leaflet movement events', async () => {
    const { api } = fixture(sourcePath);
    await api.show('old', 'en');
    api.setTargets(JSON.stringify([{ id: 'new-target', map: 'next', x: 100, y: 200, name: 'New shared', color: '#4ade80' }]));
    const shown = JSON.parse(await api.show('next', 'en'));
    assert.equal(shown.markers, 1);
    assert.doesNotThrow(() => api.setPlayer('next', 0, 0, false, true));
    const symbols = api.targetLayers().filter(item => item.options.icon?.options.className?.startsWith('place-marker'));
    assert.equal(symbols.length, 1);
    assert.deepEqual(JSON.parse(JSON.stringify(symbols[0].at)), { lat: -200, lng: 100 });
    assert.match(symbols[0].options.icon.options.html, /ruby\.png/);
});

test('map switches retain free-position leveling objectives and their navigation chain', async () => {
    const { api } = fixture(sourcePath);
    await api.show('old', 'en');
    api.setTargets(JSON.stringify([
        { id: 'route-first', map: 'next', x: 100, y: 200, name: 'First quest', color: '#facc15', leveling: true },
        { id: 'route-next', after: 'route-first', map: 'next', x: 300, y: 400, name: 'Next quest', color: '#a78bfa', leveling: true }
    ]));
    await api.show('next', 'en', { x: 0, y: 0 });
    api.setPlayer('next', 0, 0, false, true);
    const symbols = api.targetLayers().filter(item => item.options.icon?.options.className === 'leveling-marker');
    assert.equal(symbols.length, 2);
    assert.deepEqual(JSON.parse(JSON.stringify(symbols.map(item => item.at))), [{ lat: -200, lng: 100 }, { lat: -400, lng: 300 }]);
    assert.match(symbols[0].options.icon.options.html, /#facc15/);
    assert.match(symbols[1].options.icon.options.html, /#a78bfa/);
    assert.equal(api.targetLayers().filter(item => item.options.weight === 3.5 && item.options.opacity === 0.95).length, 2);
});

test('a reordered map rebuilds shared-coordinate target symbols and supports switching back', async () => {
    const { api, next } = fixture(sourcePath);
    next.markers.push([0, 300, 400, 'Other', null, 0], [0, 500, 600, 'Third', null, 0]);
    await api.show('old', 'en');
    api.setTargets(JSON.stringify([{ id: 'new-target', map: 'next', x: 100, y: 200, name: 'New shared', color: '#4ade80' }]));
    await api.show('next', 'en', { x: 0, y: 0 });
    let symbols = api.targetLayers().filter(item => item.options.icon?.options.className?.startsWith('place-marker'));
    assert.equal(symbols.length, 1);
    assert.match(symbols[0].options.icon.options.html, /ruby\.png/);
    api.setTargets(JSON.stringify([{ id: 'old-target', map: 'old', x: 100, y: 200, name: 'Old shared', color: '#4ade80' }]));
    await api.show('old', 'en');
    api.setPlayer('old', 1, 1, false, true);
    symbols = api.targetLayers().filter(item => item.options.icon?.options.className?.startsWith('place-marker'));
    assert.equal(symbols.length, 1);
    assert.match(symbols[0].options.icon.options.html, /gem\.png/);
    assert.deepEqual(JSON.parse(JSON.stringify(symbols[0].at)), { lat: -200, lng: 100 });
});
