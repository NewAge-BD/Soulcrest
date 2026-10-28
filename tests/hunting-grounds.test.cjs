const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Exercise the shipped builder and public language/category callbacks. The only added API is
// fixture setup plus access to its Leaflet layers; no production rendering logic is replaced.
function fixture() {
    const created = [], counts = { marker: 0, circle: 0, icon: 0 };
    const group = () => {
        const members = new Set();
        return {
            addTo(parent) { parent.addLayer(this); return this; },
            addLayer(layer) { members.add(layer); return this; },
            removeLayer(layer) { members.delete(layer); return this; },
            hasLayer(layer) { return members.has(layer) || [...members].some(child => child.hasLayer?.(layer)); },
            getLayers: () => [...members],
            clearLayers() { members.clear(); return this; }
        };
    };
    const map = Object.assign(group(), {
        zoom: 0,
        getZoom() { return this.zoom; },
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {}, contains: () => false } }),
        closePopup() {}
    });
    const layer = (kind, position, options) => {
        counts[kind]++;
        const result = {
            kind, position, options,
            bindPopup(content) { this.popup = content; return this; },
            on() { return this; },
            setIcon(icon) { this.options.icon = icon; return this; },
            getElement: () => null
        };
        created.push(result);
        return result;
    };
    const L = {
        TileLayer: { extend: () => function () {} },
        layerGroup: group,
        divIcon(options) { counts.icon++; return { options }; },
        marker: (position, options) => layer('marker', position, options),
        circleMarker: (position, options) => layer('circle', position, options)
    };
    const context = { window: { testMap: map }, L, performance: { now: () => 0 } };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return {
            setup(data) { current = data; base = '/data/'; map = window.testMap; buildMarkers(); },
            layers: () => markerRefs, categories: () => catLayers, visible: () => catVisible,
            zoom: () => rescaleIcons({ type: 'zoomend' }), debugState, init, show,`);
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup({ id: 'test', refZoom: 0, icons: ['ordinary-location.png'], regions: [],
        categories: [
            { group: 'Regions', name: 'Hunting Ground', icon: -1, count: 2 },
            { group: 'Regions', name: 'Village', icon: -1, count: 1 },
            { group: 'Locations', name: 'Hunting Ground', icon: 0, count: 1 },
            { group: 'Regions', name: 'Hunting Grounds', icon: -1, count: 1 }
        ],
        markers: [
            [0, 100, 200, 'Immortal Isle', 'Insel <der> Unsterblichen & mehr', -1],
            [0, 300, 400, 'Black Fragments Area', null, -1],
            [1, 500, 600, 'Village', 'Dorf', -1],
            [2, 700, 800, 'Other category', 'Andere Kategorie', 0],
            [3, 900, 1000, 'Similar category', 'Ähnliche Kategorie', -1]
        ]
    });
    return { api, map, created, counts };
}

const iconOf = layer => layer.options.icon?.options;
const isHunting = layer => !!iconOf(layer)?.className.split(/\s+/).includes('hunting-ground-marker');
const plain = value => JSON.parse(JSON.stringify(value));

test('hunting grounds are visible by default with one combined icon and permanent name per map point', () => {
    const { api, map, created, counts } = fixture();
    const hunting = api.layers().slice(0, 2);
    assert.equal(hunting.every(isHunting), true);
    assert.equal(hunting.every(item => map.hasLayer(item)), true);
    assert.equal(api.visible()[0], true);
    for (const [index, name] of ['Immortal Isle', 'Black Fragments Area'].entries()) {
        assert.match(iconOf(hunting[index]).html, /<img\b[^>]*src="(?:[^"<>]*\/)?icons\/map\/hunting-ground\.png"/);
        assert.match(iconOf(hunting[index]).html, new RegExp(`<[^>]*class="hunting-ground-name"[^>]*>${name}<`));
        assert.equal(hunting[index].kind, 'marker');
        assert.equal(created.filter(item => plain(item.position).join(',') === plain(hunting[index].position).join(',')).length, 1);
    }
    assert.equal(counts.marker, 3); // Two hunting labels and the unchanged ordinary location icon.
    assert.equal(counts.circle, 2); // Only Village and the deliberately nonmatching category remain dots.
    assert.deepEqual(plain(hunting.map(item => item.position)), [[-200, 100], [-400, 300]]);
});

test('other Regions retain their dot/default visibility and a matching name outside Regions is ordinary', () => {
    const { api, map } = fixture();
    const refs = api.layers();
    for (const index of [2, 4]) {
        assert.equal(refs[index].kind, 'circle');
        assert.equal(isHunting(refs[index]), false);
        assert.equal(map.hasLayer(refs[index]), false);
    }
    assert.equal(api.visible()[1], false);
    assert.equal(api.visible()[3], false);
    assert.equal(refs[3].kind, 'marker');
    assert.equal(isHunting(refs[3]), false);
    assert.match(iconOf(refs[3]).html, /ordinary-location\.png/);
    assert.equal(map.hasLayer(refs[3]), true);
});

test('language changes refresh visible hunting names, escape map text and retain English fallbacks', () => {
    const { api, map } = fixture();
    api.setLanguage('de');
    assert.match(iconOf(api.layers()[0]).html, />Insel &lt;der&gt; Unsterblichen &amp; mehr</);
    assert.doesNotMatch(iconOf(api.layers()[0]).html, /<der>/);
    assert.match(iconOf(api.layers()[1]).html, />Black Fragments Area</);
    assert.equal(api.layers().slice(0, 2).every(item => map.hasLayer(item)), true);
    api.setLanguage('en');
    assert.match(iconOf(api.layers()[0]).html, />Immortal Isle</);
    assert.match(iconOf(api.layers()[1]).html, />Black Fragments Area</);
});

test('the legend category toggle hides and restores icon and name together, also after a language change', () => {
    const { api, map } = fixture();
    api.setCategoryVisible(0, false);
    assert.equal(api.layers().slice(0, 2).some(item => map.hasLayer(item)), false);
    assert.equal(api.visible()[0], false);
    api.setLanguage('de');
    assert.equal(api.layers().slice(0, 2).some(item => map.hasLayer(item)), false);
    api.setCategoryVisible(0, true);
    assert.equal(api.layers().slice(0, 2).every(item => map.hasLayer(item)), true);
    assert.match(iconOf(api.layers()[0]).html, />Insel &lt;der&gt; Unsterblichen &amp; mehr</);
    assert.equal(api.categories()[0].getLayers().length, 2);
    assert.equal(map.hasLayer(api.layers()[2]), false); // Village did not become visible with Hunting Ground.
});

test('zooming keeps hunting marker and icon identities instead of rebuilding their DOM symbols', () => {
    const { api, map, counts } = fixture();
    const refs = [...api.layers()], icons = refs.map(iconOf), before = { ...counts };
    for (const zoom of [1, 2, 3, 1, 0]) {
        map.zoom = zoom;
        api.zoom();
        for (const [index, layer] of [...api.layers()].entries()) {
            assert.equal(layer, refs[index]);
            assert.equal(iconOf(layer), icons[index]);
        }
    }
    assert.deepEqual(counts, before);
});
