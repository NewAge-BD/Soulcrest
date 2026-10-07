const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function fixture() {
    let icons = 0, adds = 0, removes = 0;
    const layers = [];
    const L = {
        TileLayer: { extend: () => function () {} },
        divIcon: options => ({ options }),
        layerGroup: () => { const members = new Set(); return {
            addLayer(layer) { adds++; members.add(layer); return this; },
            removeLayer(layer) { removes++; members.delete(layer); return this; },
            hasLayer: layer => members.has(layer), addTo() { return this; }
        }; },
        marker: (_, options) => {
            const classes = new Set();
            const layer = { options, bindPopup() {}, on() {}, closePopup() {},
                getElement: () => ({ classList: { toggle(c, on) { on ? classes.add(c) : classes.delete(c); } } }),
                setIcon(icon) { icons++; this.options.icon = icon; }, classes };
            layers.push(layer); return layer;
        },
        circleMarker: () => { throw Error('Unexpected circle marker'); }
    };
    const context = { window: {}, L };
    // Expose only fixture setup; execute the production build/filter functions unchanged.
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', 'return { setup(data) { current = data; base = ""; buildMarkers(); }, debugState, init, show,');
    vm.runInNewContext(source, context);
    context.window.soulcrestMap.setup({ id: 'test', icons: ['pet.png'], categories: [{ group: 'Pets', icon: 0 }],
        markers: Array.from({ length: 313 }, (_, i) => [0, i, i, '', '', 0, `pet-${i}`]) });
    adds = removes = icons = 0;
    return { api: context.window.soulcrestMap, layers, counts: () => ({ icons, adds, removes }) };
}

test('filter toggles keep all 313 marker icons and layer memberships', () => {
    const f = fixture();
    for (let i = 0; i < 20; i++) {
        f.api.setDone('[]', '["pet-0","pet-1"]', null);
        assert(f.layers[0].classes.has('pet-filtered'));
        assert.match(f.layers[0].options.icon.options.className, /pet-filtered/);
        f.api.setDone('[]', '[]', null);
        assert(!f.layers[0].classes.has('pet-filtered'));
    }
    assert.deepEqual(f.counts(), { icons: 0, adds: 0, removes: 0 });
});

test('one changed pet label rebuilds only that pet and retains done/hidden state', () => {
    const f = fixture();
    f.api.setDone('["pet-7"]', '["pet-7"]', '{"pet-7":"Level 2"}');
    f.api.setDone('["pet-7"]', '["pet-7"]', '{"pet-7":"Level 2"}');
    assert.deepEqual(f.counts(), { icons: 1, adds: 0, removes: 0 });
    assert.match(f.layers[7].options.icon.options.className, /done pet-filtered/);
});

test('a map change found by the tracking keeps the scale in game metres', () => {
    const { api } = fixture();
    const altgard = { refZoom: 5, metersPerPixel: 0.996094 }, ishalgen = { refZoom: 4, metersPerPixel: 1.992187 },
        chaotic = { refZoom: 4, metersPerPixel: 0.498047 };
    assert.equal(Math.round(api.keptZoom(6, altgard, ishalgen) * 100) / 100, 6);   // both about 8.2 km wide
    assert.equal(Math.round(api.keptZoom(6, altgard, chaotic) * 100) / 100, 4);    // a quarter of the metres per pixel
    assert.equal(api.keptZoom(5, { refZoom: 4 }, { refZoom: 4 }), 5);             // older packages without metres
});
