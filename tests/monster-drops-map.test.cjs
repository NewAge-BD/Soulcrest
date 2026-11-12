const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const plain = value => JSON.parse(JSON.stringify(value));
function payload(id = 'first') {
    return { id, refZoom: 1, icons: ['pet.png'], regions: [],
        categories: [{ group: 'Monsters', name: 'Fera', icon: -1, count: 2 },
            { group: 'Pets', name: 'Fera', icon: 0, count: 1 },
            { group: 'Locations', name: 'Field Boss', icon: 0, count: 1 }],
        groups: [{ name: 'Monsters', categories: [0] }, { name: 'Pets', categories: [1] }, { name: 'Locations', categories: [2] }],
        markers: [[0, 100, 200, 'Beast <Boss>', 'Bestie <Boss>', -1, null, { npcId: '42' }],
            [0, 300, 400, 'Spider', 'Spinne', -1, null, { npcId: '43' }],
            [1, 100, 200, 'Pet', 'Begleiter', 0, 'pet'],
            [2, 500, 600, 'Captain', 'Hauptmann', 0, null, { npcId: '44' }]],
        monsterDrops: { schemaVersion: 1, items: [
            { id: '10', en: 'Fresh <Meat>', de: 'Frisches <Fleisch>', icon: 'icons/meat.png', type: 'Meat' },
            { id: '11', en: 'Balaur Meat', de: null, icon: 'icons/balaur.png', type: 'Meat' },
            { id: '20', en: 'Fruit', de: 'Frucht', icon: 'icons/fruit.png', type: 'Fruit' },
            { id: '30', en: 'Leather', de: 'Leder', icon: 'icons/leather.png', type: 'Leather' },
            { id: '40', en: 'Spiritstone', de: 'Geisterstein', icon: 'icons/stone.png', type: 'Spiritstones' }],
        spawns: [{ marker: 0, npcId: '42', items: ['40', '20', '10', '11', '30'] },
            { marker: 1, npcId: '43', items: ['10'] }, { marker: 3, npcId: '44', items: ['30'] }] } };
}

function fixture(data = payload(), colors = {}) {
    const layers = [], callbacks = [], counters = { groups: 0, refreshes: 0, changes: 0 };
    const group = () => {
        const members = new Set();
        return { addTo(parent) { parent.addLayer(this); return this; },
            addLayer(layer) { members.add(layer); return this; }, removeLayer(layer) { members.delete(layer); return this; },
            hasLayer(layer) { return members.has(layer) || [...members].some(child => child.hasLayer?.(layer)); },
            getLayers: () => [...members], eachLayer(callback) { [...members].forEach(callback); },
            clearLayers() { members.clear(); return this; } };
    };
    const make = (at, options = {}) => {
        const events = new Map();
        const layer = { at, options,
            addTo(parent) { parent.addLayer(this); return this; }, bindPopup(factory) { this.popup = factory; return this; },
            on(name, callback) { events.set(name, callback); return this; }, fire(name, event) { events.get(name)?.(event); },
            bindTooltip(content, settings) { this.tooltipContent = content; this.tooltipSettings = settings; return this; },
            setTooltipContent(content) { this.tooltipContent = content; return this; },
            unbindTooltip() { this.tooltipContent = null; return this; },
            closePopup() {}, setIcon(icon) { this.options.icon = icon; return this; },
            getElement: () => ({ classList: { toggle() {} } }),
            setItems(urls, more) { this.options.urls = urls; this.options.more = more; counters.changes++; return this; },
            refreshAppearance() { counters.refreshes++; return this; } };
        layers.push(layer); return layer;
    };
    const handlers = new Map(), map = Object.assign(group(), {
        options: {}, zoom: 1, center: { lat: 0, lng: 0 },
        on(names, callback) { for (const name of names.split(' ')) {
            if (!handlers.has(name)) handlers.set(name, []); handlers.get(name).push(callback); } return this; },
        fire(name) { for (const callback of handlers.get(name) || []) callback({ type: name }); },
        attributionControl: { setPrefix() {} }, createPane() {}, getPane: () => ({ style: {} }),
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {}, contains: () => false } }),
        setMaxBounds() {}, fitBounds() {}, flyToBounds() {},
        getZoom() { return this.zoom; }, getCenter() { return this.center; },
        getBounds: () => ({ pad() { return this; }, contains: () => true }),
        setView(at, zoom) { this.zoom = zoom; this.center = { lat: at[0], lng: at[1] }; }, closePopup() {}
    });
    const L = { CRS: { Simple: {} }, map: () => map, layerGroup: group, canvas: () => ({}),
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } },
        latLng: at => Array.isArray(at) ? { lat: at[0], lng: at[1] } : at,
        latLngBounds: () => ({ pad() { return this; } }), marker: make, circleMarker: make,
        divIcon: options => ({ options }), DomEvent: { preventDefault(event) { event.prevented = true; } } };
    const context = { window: { SoulcrestMaps: { first: data, second: payload('second') }, soulcrestCanvasIcons: {
        renderer: () => ({}), redraw() {}, create: make,
        createGroup(at, options) { counters.groups++; const layer = make(at, options); layer.drop = true; return layer; }
    } }, L, document: { visibilityState: 'hidden' }, Date, performance, setTimeout, clearTimeout, setInterval: () => 0, clearInterval() {} };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', 'return { state: () => ({ current, monsterDrops, catLayers, catVisible, markerRefs, targetLayer }), debugState, init, show,');
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.init('map', JSON.stringify({ maps: ['first', 'second'].map(id => ({ id, size: 1000, minZoom: 0, maxZoom: 6, maxNativeZoom: 4, tiles: 'tiles' })) }), '/mapdata/',
        { invokeMethodAsync(...args) { callbacks.push(args); } }, colors);
    return { api, map, layers, callbacks, counters, data, show: async (language = 'en', id = 'first') => JSON.parse(await api.show(id, language)) };
}

test('drop legend is additive, counts unique spawns, uses item IDs, and includes singleton material rows', async () => {
    const f = fixture(), result = await f.show(), legend = result.groups.at(-1);
    assert.equal(result.monsterDropsAvailable, true);
    assert.equal(result.markers, 4);
    assert.equal(f.data.markers.length, 4);
    assert.equal(f.data.categories.length, 3);
    assert.equal(legend.name, 'Monster Drops');
    assert.equal(legend.count, 3);
    assert.deepEqual(legend.categories.map(c => [c.index, c.name, c.count]), [[3, 'Meat', 2], [4, 'Fruit', 1], [5, 'Leather', 2], [6, 'Spiritstones', 1]]);
    assert.deepEqual(legend.categories[0].kinds.map(k => [k.key, k.count]), [['10', 2], ['11', 1]]);
    assert.equal(legend.categories[1].kinds.length, 1);
    assert.equal(legend.categories[1].de, 'Früchte');
    assert(legend.categories.every(c => c.hidden));
    assert.equal(f.counters.groups, 0); // default-off never allocates individual drop layers
});

test('several material filters produce one ordered icon row per source and restore exact ordinary markers', async () => {
    const f = fixture(), result = await f.show(), categories = result.groups.at(-1).categories;
    f.api.setCategoryVisible(0, true);
    for (const category of categories) f.api.setCategoryVisible(category.index, true);
    const state = f.api.state(), spawn = state.monsterDrops.spawns.get(0), row = spawn.activeLayer;
    assert.equal(f.counters.groups, 3);
    assert.equal(state.monsterDrops.layer.getLayers().length, 3);
    assert.deepEqual(plain(row.options.urls), ['/mapdata/icons/meat.png', '/mapdata/icons/balaur.png', '/mapdata/icons/fruit.png']);
    assert.equal(row.options.more, 2);
    assert.equal(state.catLayers[0].hasLayer(state.markerRefs[0]), false);
    assert.equal(state.catLayers[2].hasLayer(state.markerRefs[3]), false);
    assert.equal(state.catLayers[1].hasLayer(state.markerRefs[2]), true); // pet at same coordinate remains
    f.api.setKindVisible(3, '10', false);
    assert.equal(state.monsterDrops.spawns.get(0).activeLayer, row);
    assert.deepEqual(plain(row.options.urls), ['/mapdata/icons/balaur.png', '/mapdata/icons/fruit.png', '/mapdata/icons/leather.png']);
    assert.equal(row.options.more, 1);
    assert.equal(state.catLayers[0].hasLayer(state.markerRefs[1]), true); // only this now-unselected source returns
    for (const category of categories) f.api.setCategoryVisible(category.index, false);
    assert.equal(state.monsterDrops.layer.getLayers().length, 0);
    assert.equal(state.catLayers[0].hasLayer(state.markerRefs[0]), true);
    assert.equal(state.catLayers[2].hasLayer(state.markerRefs[3]), true);
    f.api.setCategoryVisible(3, true);
    assert.equal(state.monsterDrops.spawns.get(0).activeLayer, row); // reuse after hiding
    assert.equal(f.counters.groups, 3);
});

test('individual item selection, popup language and exact navigation retain all source drops', async () => {
    const f = fixture(); await f.show();
    f.api.setKindVisible(3, '11', false);
    f.api.setCategoryVisible(3, true);
    const spawn = f.api.state().monsterDrops.spawns.get(0), row = spawn.activeLayer;
    const english = row.popup();
    assert.match(english, /Beast &lt;Boss&gt;/);
    assert.match(english, /✓ <span[^>]*>Fresh &lt;Meat&gt;/);
    assert.match(english, /Balaur Meat/);
    assert.match(english, /Fruit/);
    assert.doesNotMatch(english, /✓ Balaur/);
    f.api.setLanguage('de');
    assert.match(row.popup(), /Bestie &lt;Boss&gt;/);
    assert.match(row.popup(), /✓ <span[^>]*>Frisches &lt;Fleisch&gt;/);
    assert.match(row.popup(), /Balaur Meat/); // missing DE keeps exact English label
    assert.match(row.popup(), /Mögliche Crafting-Materialien/);
    const event = { shiftKey: true };
    row.fire('contextmenu', { originalEvent: event });
    assert.equal(event.prevented, true);
    assert.deepEqual(plain(f.callbacks.at(-1)), ['OnToggleTarget', 'first', 100, 200, 'Bestie <Boss>', 'Monster Drops', 'icons/meat.png', true, null]);
});

test('item names receive the shared exact-ID foreground without coloring counts or accepting CSS input', async () => {
    const f = fixture(payload(), { 10: '#FF6C00', 11: 'red;position:fixed', 20: '#19E048' });
    const legend = await f.show();
    const meat = legend.groups.find(group => group.name === 'Monster Drops').categories.find(category => category.name === 'Meat');
    assert.equal(meat.kinds[0].itemId, 10);
    f.api.setCategoryVisible(meat.index, true);
    const popup = f.api.state().monsterDrops.spawns.get(0).activeLayer.popup();
    assert.match(popup, /data-item-id="10" style="color:#FF6C00">Fresh &lt;Meat&gt;/);
    assert.match(popup, /data-item-id="20" style="color:#19E048">Fruit/);
    assert.match(popup, /data-item-id="11">Balaur Meat/);
    assert.doesNotMatch(popup, /position:fixed|opacity:\.6/);
    assert.equal(meat.count, 2);
});

test('zoom and repeated filter snapshots retain drop layer identity; map switches reset presentation', async () => {
    const f = fixture(); await f.show(); f.api.setCategoryVisible(3, true);
    const row = f.api.state().monsterDrops.spawns.get(0).activeLayer, before = { ...f.counters };
    f.api.setCategoryVisible(3, true); f.api.setKindVisible(3, '10', true);
    assert.deepEqual(f.counters, before);
    f.map.zoom = 3; f.map.fire('zoomend');
    assert.equal(f.api.state().monsterDrops.spawns.get(0).activeLayer, row);
    assert(f.counters.refreshes > before.refreshes);
    assert.equal(row.options.appearance().scale, 1.2); // dense drop groups stay compact when zoomed in
    const next = await f.show('de', 'second');
    assert(next.groups.at(-1).categories.every(c => c.hidden));
    assert.equal(f.api.state().monsterDrops.layer, null);
    assert.equal(f.data.markers.length, 4);
});

test('ordinary monster search focus remains available while material markers are displayed', async () => {
    const f = fixture(); await f.show(); f.api.setCategoryVisible(3, true);
    assert.equal(f.api.focusMonster('Beast <Boss>'), 1);
    assert.equal(f.api.debugState().focus, 1);
    assert.equal(JSON.parse(f.api.search('Beast'))[0].group, 'Monsters');
    assert.equal(f.api.state().monsterDrops.layer.getLayers().length, 2);
});

test('boss timer transfers to the active material row and returns without duplicate labels', async () => {
    const data = payload(); data.bosses = [{ spawnId: 44, npcId: 44, x: 500, y: 600 }];
    const f = fixture(data); await f.show();
    const state = f.api.state(), normal = state.markerRefs[3];
    f.api.setBossTimers('first', JSON.stringify([{ spawnId: 44, remaining: 60, age: 0, spawned: false, cached: false }]));
    assert.match(normal.tooltipContent, />01:00</);
    f.api.setCategoryVisible(5, true);
    const row = state.monsterDrops.spawns.get(3).activeLayer;
    assert.equal(normal.tooltipContent, null);
    assert.match(row.tooltipContent, />0[01]:[05][09]</);
    assert.equal(row.tooltipSettings.permanent, true);
    f.api.setLanguage('de');
    assert.match(row.tooltipContent, /Erfasst im Spiel/);
    f.api.setTargets(JSON.stringify([{ id: 'boss', map: 'first', bossSpawnId: 44, bossRole: 'next', name: 'Captain', x: 500, y: 600 }]));
    const navigation = f.layers.find(layer => layer.options.icon?.options.className === 'boss-rush-marker boss-role-next');
    assert.equal(row.tooltipContent, null);
    assert.match(navigation.tooltipContent, /Captain/);
    f.api.setTargets('[]');
    assert.equal(navigation.tooltipContent, null);
    assert.match(row.tooltipContent, /Erfasst im Spiel/);
    f.api.setCategoryVisible(5, false);
    assert.equal(row.tooltipContent, null);
    assert.match(normal.tooltipContent, /Erfasst im Spiel/);
    f.api.setBossTimers('first', '[]');
    assert.equal(normal.tooltipContent, null);
});

for (const [name, modify, available] of [
    ['missing', data => { delete data.monsterDrops; }, false],
    ['unsupported', data => { data.monsterDrops.schemaVersion = 2; }, false],
    ['empty', data => { data.monsterDrops = { schemaVersion: 1, items: [], spawns: [] }; }, true],
    ['NPC mismatch', data => { data.monsterDrops.spawns[0].npcId = '99'; }, false],
    ['unknown material type', data => { data.monsterDrops.items[0].type = 'Unknown'; }, false],
    ['unknown item reference', data => { data.monsterDrops.spawns[0].items.push('999'); }, false],
    ['duplicate item', data => { data.monsterDrops.items.push({ ...data.monsterDrops.items[0] }); }, false],
    ['numeric German text', data => { data.monsterDrops.items[0].de = 12; }, false],
    ['object icon', data => { data.monsterDrops.items[0].icon = {}; }, false],
    ['missing monster name', data => { data.markers[0][3] = ''; }, false],
    ['duplicate spawn', data => { data.monsterDrops.spawns.push({ ...data.monsterDrops.spawns[0] }); }, false],
    ['pet instead of monster', data => { data.monsterDrops.spawns[0].marker = 2; }, false]
]) test(`${name} drop metadata is distinguished without altering ordinary map data`, async () => {
    const data = payload(); modify(data);
    const f = fixture(data), result = await f.show();
    assert.equal(result.monsterDropsAvailable, available);
    assert.equal(result.groups.at(-1).count, 0);
    assert.equal(result.groups.at(-1).categories.length, 0);
    assert.equal(result.markers, 4);
    assert.equal(f.data.categories.length, 3);
});


test('metadata icon paths cannot request remote URLs or traverse outside the local icon cache', async () => {
    const data = payload(); data.monsterDrops.items[0].icon = 'https://example.invalid/private.png';
    data.monsterDrops.items[1].icon = 'icons/../../private.png';
    const f = fixture(data), result = await f.show();
    assert.equal(result.monsterDropsAvailable, true);
    assert.equal(result.groups.at(-1).categories[0].kinds[0].icon, null);
    f.api.setCategoryVisible(3, true);
    const row = f.api.state().monsterDrops.spawns.get(0).activeLayer;
    assert.deepEqual(plain(row.options.urls), [null, null]);
    assert.doesNotMatch(row.popup(), /example\.invalid|\.\.\//);
});


test('a saved material target retains its icon after filtering, without duplicating an already visible drop icon', async () => {
    const f = fixture(); await f.show(); f.api.setCategoryVisible(3, true);
    const target = { id: 'material-target', map: 'first', x: 100, y: 200, name: 'Beast <Boss>', kind: 'Monster Drops', icon: 'icons/meat.png' };
    const copies = () => f.api.state().targetLayer.getLayers().filter(layer => layer.options.icon?.options.className === 'place-marker monster-drop-target');
    f.api.setTargets(JSON.stringify([target]));
    assert.equal(copies().length, 0);
    f.api.setCategoryVisible(3, false);
    assert.equal(copies().length, 1);
    assert.deepEqual(plain(copies()[0].at), [-100, 50]);
    assert.match(copies()[0].options.icon.options.html, /src="\/mapdata\/icons\/meat\.png"/);
    f.api.setCategoryVisible(3, true);
    assert.equal(copies().length, 0);
    f.api.setKindVisible(3, '10', false);
    assert.equal(copies().length, 1); // another selected material at the same source cannot replace this target's icon
    assert(f.api.state().monsterDrops.spawns.get(0).activeLayer);
    f.api.setTargets(JSON.stringify([{ ...target, icon: 'https://example.invalid/remote.png' }]));
    assert.equal(copies().length, 0);
});


function legacyPayload() {
    const data = payload();
    data.monsterDrops.items.push(
        { id: '50', en: 'Retired Refining Stone', de: 'Entfernter Veredelungsstein', icon: 'icons/refining.png', type: 'Refining Stones' },
        { id: '60', en: 'Retired Balaur Essence', de: 'Entfernte Balaur-Essenz', icon: 'icons/essence.png', type: 'Balaur Materials' },
        { id: '70', en: 'Retired Other Material', de: 'Entferntes sonstiges Material', icon: 'icons/other.png', type: 'Other Materials' });
    data.monsterDrops.spawns[0].items.push('50', '60', '70');
    data.monsterDrops.spawns[1].items = ['50', '60', '70'];
    return data;
}

test('older seven-type packages stay valid while retired materials disappear from legend, counts, popups and markers', async () => {
    const data = legacyPayload(), f = fixture(data), result = await f.show(), legend = result.groups.at(-1);
    assert.equal(result.monsterDropsAvailable, true);
    assert.equal(result.markers, 4);
    assert.equal(legend.count, 2); // the source with only retired materials no longer counts
    assert.deepEqual(legend.categories.map(c => [c.name, c.count]), [['Meat', 1], ['Fruit', 1], ['Leather', 2], ['Spiritstones', 1]]);
    assert.deepEqual(legend.categories.flatMap(c => c.kinds.map(k => k.key)), ['10', '11', '20', '30', '40']);
    const state = f.api.state();
    assert.deepEqual(Array.from(state.monsterDrops.items.keys()), ['10', '11', '20', '30', '40']);
    assert.equal(state.monsterDrops.spawns.has(1), false);
    f.api.setCategoryVisible(0, true);
    for (const category of legend.categories) f.api.setCategoryVisible(category.index, true);
    assert.equal(state.monsterDrops.layer.getLayers().length, 2);
    assert.equal(state.catLayers[0].hasLayer(state.markerRefs[1]), true); // ordinary monster presentation remains
    const row = state.monsterDrops.spawns.get(0).activeLayer;
    assert.equal(row.options.more, 2); // excluded items never enter +N
    assert.match(row.popup(), /Spiritstone/);
    assert.doesNotMatch(row.popup(), /Retired|refining\.png|essence\.png|other\.png/);
    f.api.setKindVisible(6, '50', true); // a stale item choice cannot reactivate a retired ID
    assert.doesNotMatch(row.popup(), /Retired/);
    assert.equal(data.monsterDrops.items.length, 8); // raw package identity/rows are preserved
    assert.deepEqual(data.monsterDrops.spawns[1].items, ['50', '60', '70']);
});

test('a valid older package containing only retired drops produces an empty catalog and preserves explicit manual targets', async () => {
    const data = legacyPayload();
    for (const spawn of data.monsterDrops.spawns) spawn.items = ['50', '60', '70'];
    const f = fixture(data), result = await f.show(), legend = result.groups.at(-1);
    assert.equal(result.monsterDropsAvailable, true);
    assert.equal(legend.count, 0);
    assert.equal(legend.categories.length, 0);
    assert.equal(f.api.state().monsterDrops.spawns.size, 0);
    assert.equal(f.counters.groups, 0);
    f.api.setTargets(JSON.stringify([{ id: 'saved-target', map: 'first', x: 100, y: 200, name: 'Saved target',
        kind: 'Monster Drops', icon: 'icons/refining.png' }]));
    const saved = f.api.state().targetLayer.getLayers().filter(layer => layer.options.icon?.options.className === 'place-marker monster-drop-target');
    assert.equal(saved.length, 1); // explicit user targets are retained, never migrated or silently deleted
    assert.match(saved[0].options.icon.options.html, /refining\.png/);
});

for (const [name, modify] of [
    ['a duplicate retired item', data => data.monsterDrops.items.push({ ...data.monsterDrops.items.at(-1) })],
    ['a wrong NPC on a retired-only source', data => { data.monsterDrops.spawns[1].npcId = '99'; }],
    ['an unknown item on a retired-only source', data => data.monsterDrops.spawns[1].items.push('999')]
]) test(`legacy contract validation still rejects ${name} before projecting retained materials`, async () => {
    const data = legacyPayload(); modify(data);
    const f = fixture(data), result = await f.show();
    assert.equal(result.monsterDropsAvailable, false);
    assert.equal(result.groups.at(-1).count, 0);
    assert.equal(f.counters.groups, 0);
});
