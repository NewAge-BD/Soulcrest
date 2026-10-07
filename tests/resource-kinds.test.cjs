const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

// Resource kinds (Gem: Sapphire, Diamond, Ruby) get their own layer inside the category, their item icon
// on the map and a legend entry each (user request 2026-10-05).
function fixture() {
    const groups = [];
    const L = {
        TileLayer: { extend: () => function () {} },
        divIcon: options => ({ options }),
        layerGroup: () => { const members = new Set(); const g = {
            addLayer(layer) { members.add(layer); return this; },
            removeLayer(layer) { members.delete(layer); return this; },
            hasLayer: layer => members.has(layer), getLayers: () => [...members], addTo() { return this; }
        }; groups.push(g); return g; },
        marker: (_, options) => ({ options, bindPopup() {}, on() {} }),
        circleMarker: () => { throw Error('Unexpected circle marker'); }
    };
    const context = { window: {}, L };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', 'return { setup(data) { current = data; base = "b/"; buildMarkers(); return { catLayers, kindsOf }; }, debugState, init, show,');
    vm.runInNewContext(source, context);
    const state = context.window.soulcrestMap.setup({ id: 'test', icons: ['gem.png', 'sapphire.png', 'ruby.png', 'ore.png'],
        categories: [{ group: 'Resources', name: 'Gem', icon: 0 }, { group: 'Resources', name: 'Ore', icon: 3 }],
        markers: [[0, 1, 1, 'Sapphire', 'Saphir', 1, null], [0, 2, 2, 'Sapphire', 'Saphir', 1, null], [0, 3, 3, 'Ruby', 'Rubin', 2, null],
                  [1, 4, 4, 'Orichalcum', 'Oreichalkos', -1, null]] });
    return { api: context.window.soulcrestMap, ...state };
}

test('kinds are listed with their item icon and German name, single kinds are not', () => {
    const f = fixture();
    assert.deepEqual(JSON.parse(JSON.stringify(f.kindsOf(0))), [
        { name: 'Sapphire', de: 'Saphir', icon: 'b/sapphire.png', count: 2 },
        { name: 'Ruby', de: 'Rubin', icon: 'b/ruby.png', count: 1 }]);
    assert.equal(f.kindsOf(1), null);
});

test('a kind can be hidden and shown inside its category', () => {
    const f = fixture();
    const gem = f.catLayers[0];
    assert.equal(gem.getLayers().length, 2);
    f.api.setKindVisible(0, 'Ruby', false);
    assert.equal(gem.getLayers().length, 1);
    f.api.setKindVisible(0, 'Ruby', true);
    assert.equal(gem.getLayers().length, 2);
    f.api.setKindVisible(0, 'Unknown', false); // ignored
    assert.equal(gem.getLayers().length, 2);
});
