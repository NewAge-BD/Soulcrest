const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture() {
    const drawn = [];
    const layer = (kind, position, options) => ({ kind, position, options,
        addTo() { drawn.push(this); return this; },
        setLatLng(p) { this.position = p; return this; },
        setContent(text) { this.text = text; return this; }
    });
    const context = { window: {}, L: {
        TileLayer: { extend: () => function () {} }, latLng: p => p, divIcon: o => o,
        marker: (p, o) => layer('marker', p, o), tooltip: o => layer('tooltip', null, o),
        circleMarker: (p, o) => layer('ring', p, o), polyline: (p, o) => layer('line', p, o)
    }};
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return { setup(mapId) {
            current = { id: mapId, refZoom: 4, markers: [], categories: [] };
            map = { getBounds: () => ({ pad() { return this; } }), latLngToLayerPoint: p => ({ x: p[1], y: p[0], distanceTo: () => 10 }) };
            targetLayer = { clearLayers() { window.drawn.length = 0; } };
        }, debugState, init, show,`);
    context.window.drawn = drawn;
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup('altgard');
    return { api, drawn };
}

test('boss target stays visible without its category, uses red pin and escapes its name', () => {
    const { api, drawn } = fixture();
    api.setTargets(JSON.stringify([{ id: 'boss-rush', map: 'altgard', x: 160, y: 320, name: 'Boss <one>', color: '#ef4444' }]));
    assert.equal(drawn.filter(x => x.kind === 'marker' && x.options.icon.className === 'boss-rush-marker').length, 1);
    assert.equal(drawn.find(x => x.kind === 'tooltip').text, 'Boss &lt;one&gt;');
    assert.deepEqual(Array.from(drawn.find(x => x.kind === 'marker').position), [-20, 10]);
    assert(drawn.some(x => x.kind === 'ring' && x.options.color === '#ef4444'));
    api.setTargets('[]');
    assert.equal(drawn.length, 0);
});

test('boss target never appears on another map and replacement removes old marker', () => {
    const { api, drawn } = fixture();
    const target = { id: 'boss-rush', map: 'altgard', x: 160, y: 320, name: 'A', color: '#ef4444' };
    api.setTargets(JSON.stringify([target]));
    api.setTargets(JSON.stringify([{ ...target, name: 'B', x: 320 }]));
    assert.equal(drawn.filter(x => x.kind === 'marker').length, 1);
    assert.equal(drawn.find(x => x.kind === 'tooltip').text, 'B');
    api.setup('verteron');
    api.setTargets(JSON.stringify([target]));
    assert.equal(drawn.length, 0);
});

test('next spawn is faded and dashed and chains from the active boss without requiring a player', () => {
    const { api, drawn } = fixture();
    api.setTargets(JSON.stringify([
        { id: 'boss-rush', map: 'altgard', x: 160, y: 320, name: 'Alive', color: '#ef4444' },
        { id: 'boss-next', after: 'boss-rush', map: 'altgard', x: 320, y: 640, name: 'Upcoming', color: '#ef4444' }
    ]));
    const marker = drawn.find(x => x.kind === 'marker' && x.options.icon.className.includes('boss-preview'));
    assert(marker);
    const ring = drawn.find(x => x.kind === 'ring' && x.options.color === '#ef4444' && x.options.dashArray);
    assert.equal(ring.options.dashArray, '6 6');
    assert(ring.options.opacity < 0.5);
    const line = drawn.find(x => x.kind === 'line' && x.options.color === '#ef4444');
    assert.equal(line.options.dashArray, '10 8');
    assert(line.options.opacity < 0.5);
    assert.deepEqual(Array.from(line.position[0]), [-20, 10]); // active boss, not the player
    assert.deepEqual(Array.from(line.position[1]), [-40, 20]);
});
