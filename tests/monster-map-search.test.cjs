const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const sourcePath = process.env.SOULCREST_MAP_SCRIPT || path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js');
const plain = value => JSON.parse(JSON.stringify(value));
const categories = [
    { group: 'Monsters', name: 'Fera' },
    { group: 'Monsters', name: 'Wild Monsters' },
    { group: 'Resources', name: 'Gem' },
    { group: 'Locations', name: 'Village' },
    { group: 'Pets', name: 'Fera' }
];
const data = markers => ({ id: 'altgard', refZoom: 2, def: { maxNativeZoom: 6 }, categories, markers });

function fixture(visibility = 'visible') {
    const calls = { flyBounds: [], fitBounds: [], flyTo: [], setView: [], pan: [], closed: 0, callbacks: [], timers: [], popups: 0, opened: [] };
    const clock = { now: 10000 };
    const group = () => {
        const members = new Set();
        return { addTo(parent) { parent.addLayer(this); return this; },
            addLayer(value) { members.add(value); return this; }, clearLayers() { members.clear(); },
            eachLayer(callback) { [...members].forEach(callback); }, getLayers: () => [...members] };
    };
    const focus = group();
    const catLayers = categories.map((_, index) => group().addLayer('existing-' + index));
    const map = {
        addLayer() {}, removeLayer() {}, eachLayer() {}, setMaxBounds() {},
        getZoom: () => 2, getCenter: () => ({ lat: 0, lng: 0 }),
        options: {}, _limitCenter: center => center,
        _getCenterOffset: center => ({ x: center.lng * 4, y: -center.lat * 4,
            _trunc() { this.x = Math.trunc(this.x); this.y = Math.trunc(this.y); return this; } }),
        getContainer: () => ({ style: { setProperty() {} }, classList: { add() {} } }),
        getBounds: () => ({ pad() { return this; }, contains: () => true }),
        closePopup() { calls.closed++; },
        flyToBounds(bounds, options) { calls.flyBounds.push({ bounds, options }); },
        fitBounds(bounds, options) { calls.fitBounds.push({ bounds, options }); },
        flyTo(at, zoom, options) { calls.flyTo.push({ at, zoom, options }); },
        setView(at, zoom, options) { calls.setView.push({ at, zoom, options }); },
        panTo(at, options) { calls.pan.push({ at, options }); }
    };
    const marker = (at, options) => ({ at, options,
        addTo(parent) { parent.addLayer(this); return this; }, bindPopup(factory) { this.popup = factory; return this; },
        setLatLng(value) { this.at = value; return this; },
        getLatLng() { return Array.isArray(this.at) ? { lat: this.at[0], lng: this.at[1] } : this.at; },
        getElement: () => ({ classList: { toggle() {} } })
    });
    const L = {
        TileLayer: { extend: () => class { addTo(parent) { parent.addLayer(this); return this; } } }, divIcon: options => options,
        circleMarker: marker, marker, layerGroup: group,
        latLng: value => Array.isArray(value) ? { lat: value[0], lng: value[1] } : value,
        latLngBounds: points => ({ points, pad(value) { this.padding = value; return this; } }),
        popup() {
            calls.popups++;
            return { setLatLng() { return this; }, setContent(value) { this.content = value; return this; },
                openOn() { calls.opened.push(this.content); return this; } };
        }
    };
    const context = {
        window: { testMap: map, testFocus: focus, testCategoryLayers: catLayers,
            testCategoryVisible: [false, false, true, true, true],
            testDotnet: { invokeMethodAsync(...args) { calls.callbacks.push(args); } } },
        L, document: { visibilityState: visibility },
        Date: class extends Date { static now() { return clock.now; } },
        setTimeout(callback, delay) { calls.timers.push({ callback, delay, cleared: false }); return calls.timers.length; },
        clearTimeout(id) { calls.timers[id - 1].cleared = true; }
    };
    const source = fs.readFileSync(sourcePath, 'utf8').replace('return { debugState, init, show,', `return {
        setup(payload, language = 'en') {
            current = payload; lang = language; base = '/mapdata/'; map = window.testMap;
            focusLayer = window.testFocus; catLayers = window.testCategoryLayers;
            catVisible = window.testCategoryVisible.slice(); dotnet = window.testDotnet;
        }, setupNextMap() {
            manifest = { maps: [{ id: 'next', size: 1000, minZoom: 0, maxZoom: 6, maxNativeZoom: 6, tiles: 'tiles' }] };
            window.SoulcrestMaps = { next: { id: 'next', refZoom: 2, markers: [], categories: [], groups: [], regions: [] } };
        }, categoryVisibility: () => catVisible.slice(), debugState, init, show,`);
    vm.runInNewContext(source, context);
    return { api: context.window.soulcrestMap, calls, clock, focus,
        categoryState: () => ({ visible: plain(context.window.soulcrestMap.categoryVisibility()),
            members: catLayers.map(layer => layer.getLayers()) }) };
}

test('monster search deduplicates across categories before the limit and retains non-monster rows', () => {
    const f = fixture();
    const markers = Array.from({ length: 120 }, (_, index) => [index % 2, index, index * 2, 'Spider', 'Spinne']);
    markers.push([0, 800, 900, 'Spider Queen', 'Spinnenkönigin'],
        [2, 1, 2, 'Spider', 'Spinne'], [2, 3, 4, 'Spider', 'Spinne'],
        [3, 5, 6, 'Spider', 'Spinne'], [4, 7, 8, 'Spider', 'Spinne']);
    f.api.setup(data(markers));
    const hits = JSON.parse(f.api.search('spider'));
    assert.equal(hits.length, 6);
    assert.deepEqual(hits.filter(hit => hit.monster).map(hit => hit.key), ['Spider', 'Spider Queen']);
    assert.equal(hits[0].i, 0);
    assert.deepEqual(hits.slice(2), [
        { i: 121, name: 'Spider', cat: 'Resources · Gem', group: 'Resources', petId: null },
        { i: 122, name: 'Spider', cat: 'Resources · Gem', group: 'Resources', petId: null },
        { i: 123, name: 'Spider', cat: 'Locations · Village', group: 'Locations', petId: null },
        { i: 124, name: 'Spider', cat: 'Pets · Fera', group: 'Pets', petId: null }
    ]);
});

test('forty distinct monster results remain reachable after many repeated spawn points', () => {
    const f = fixture();
    const markers = Array.from({ length: 120 }, (_, index) => [index % 2, index, index, 'Common 0', null]);
    for (let index = 1; index < 50; index++) markers.push([0, index, index, 'Common ' + index, null]);
    f.api.setup(data(markers));
    const hits = JSON.parse(f.api.search('common'));
    assert.equal(hits.length, 40);
    assert.equal(new Set(hits.map(hit => hit.key)).size, 40);
    assert.equal(hits.at(-1).key, 'Common 39');
});

test('localized monster search keeps its exact English key and the existing query rules', () => {
    const f = fixture();
    f.api.setup(data([[0, 10, 20, 'Spider', 'Spinne'], [1, 30, 40, 'Spider', 'Spinne']]), 'de');
    assert.deepEqual(JSON.parse(f.api.search('  SPINNE ')), [
        { i: 0, name: 'Spinne', cat: 'Monsters · Fera', group: 'Monsters', petId: null, monster: true, key: 'Spider' }
    ]);
    assert.equal(JSON.parse(f.api.search('spider')).length, 1);
    assert.deepEqual(JSON.parse(f.api.search('s')), []);
    assert.deepEqual(JSON.parse(f.api.search(null)), []);
});

for (const visibility of ['visible', 'hidden']) {
    test('monster focus fits every exact spawn across categories while ' + visibility, () => {
        const f = fixture(visibility);
        f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne'], [1, 200, 120, 'Spider', 'Spinne'],
            [0, -20, 300, 'Spider', 'Spinne'], [0, 1000, 1000, 'Spider Queen', 'Spinnenkönigin'],
            [2, 2000, 2000, 'Spider', 'Spinne']]));
        const before = f.categoryState();
        f.focus.addLayer({ previous: true });
        assert.equal(f.api.focusMonster('Spider'), 3);
        assert.equal(f.focus.getLayers().length, 3);
        assert(f.focus.getLayers().every(layer => layer.options.color === '#facc15'));
        const moves = visibility === 'visible' ? f.calls.flyBounds : f.calls.fitBounds;
        assert.equal(moves.length, 1);
        assert.equal((visibility === 'visible' ? f.calls.fitBounds : f.calls.flyBounds).length, 0);
        assert.deepEqual(plain(moves[0].bounds.points), [[-20, 10], [-30, 50], [-75, -5]]);
        assert(moves[0].bounds.padding > 0);
        assert.equal(moves[0].options.maxZoom, 6);
        if (visibility === 'hidden') assert.equal(moves[0].options.animate, false);
        assert.deepEqual(f.categoryState(), before);
        assert.equal(f.calls.closed, 1);
        assert.equal(f.calls.popups, 0);
        assert.equal(f.calls.timers.length, 0);
        assert.deepEqual(f.calls.callbacks, []);
    });

    test('a single monster spawn still uses a bounded fit while ' + visibility, () => {
        const f = fixture(visibility);
        f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne']]));
        assert.equal(f.api.focusMonster('Spider'), 1);
        const moves = visibility === 'visible' ? f.calls.flyBounds : f.calls.fitBounds;
        assert.equal(moves.length, 1);
        assert.deepEqual(plain(moves[0].bounds.points), [[-20, 10]]);
        assert.equal(moves[0].options.maxZoom, 6);
        assert.equal(f.calls.flyTo.length + f.calls.setView.length, 0);
    });
}

test('several exact English keys sharing a German name focus all corresponding monsters once', () => {
    const f = fixture();
    f.api.setup(data([[0, 40, 80, 'Forest Spider', 'Spinne'], [1, 200, 120, 'Cave Spider', 'Spinne'],
        [0, 300, 200, 'Queen Spider', 'Königin'], [2, 500, 600, 'Forest Spider', 'Spinne']]), 'de');
    assert.equal(f.api.focusMonster(['Forest Spider', 'Cave Spider', 'Forest Spider']), 2);
    assert.deepEqual(plain(f.calls.flyBounds[0].bounds.points), [[-20, 10], [-30, 50]]);
    assert.equal(f.focus.getLayers().length, 2);
    assert.equal(f.api.focusMonster('Forest Spider'), 1);
});

test('an absent exact English monster clears old focus without moving or opening a popup', () => {
    const f = fixture();
    f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne']]));
    assert.equal(f.api.focusMonster('Spider'), 1);
    assert.equal(f.api.focusMonster('Spinne'), 0);
    assert.equal(f.api.focusMonster('spider'), 0);
    assert.equal(f.focus.getLayers().length, 0);
    assert.equal(f.calls.flyBounds.length, 1);
    assert.equal(f.calls.closed, 3);
    assert.equal(f.calls.popups + f.calls.timers.length, 0);
});

test('monster focus without a loaded map safely returns zero', () => {
    const f = fixture();
    assert.equal(f.api.focusMonster('Spider'), 0);
    assert.deepEqual(JSON.parse(f.api.search('spider')), []);
    assert.equal(f.calls.closed, 0);
});

test('monster focus temporarily pauses following for six seconds and then following resumes', () => {
    const f = fixture();
    f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne']]));
    f.api.focusMonster('Spider');
    f.api.setPlayer('altgard', 10, 20, true, true);
    assert.equal(f.calls.pan.length, 0);
    f.clock.now += 6000;
    f.api.setPlayer('altgard', 11, 21, true, true);
    assert.equal(f.calls.pan.length, 0);
    f.clock.now++;
    f.api.setPlayer('altgard', 12, 22, true, true);
    assert.equal(f.calls.pan.length, 1);
});

test('pet focus keeps its original following behavior unless the new pause argument is supplied', () => {
    const payload = data([[0, 40, 80, 'Spider', 'Spinne', -1, 'pet-a'], [4, 80, 120, 'Pet A', 'Pet A', -1, 'pet-a']]);
    const original = fixture();
    original.api.setup(payload);
    assert.deepEqual(JSON.parse(original.api.focusPet('pet-a')), { pets: 1, monsters: 1 });
    original.api.setPlayer('altgard', 10, 20, true, true);
    assert.equal(original.calls.pan.length, 1);
    assert.equal(original.calls.callbacks[0][0], 'OnPetFocused');
    const paused = fixture();
    paused.api.setup(payload);
    paused.api.focusPet('pet-a', true);
    paused.api.setPlayer('altgard', 10, 20, true, true);
    assert.equal(paused.calls.pan.length, 0);
    paused.clock.now += 6001;
    paused.api.setPlayer('altgard', 11, 21, true, true);
    assert.equal(paused.calls.pan.length, 1);
});

test('pet focus accepts several pet IDs while retaining its counts and first-ID callback', () => {
    const f = fixture();
    f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne', -1, 'pet-a'],
        [1, 200, 120, 'Beetle', 'Käfer', -1, 'pet-b'],
        [4, 80, 120, 'Pet A', 'Pet A', -1, 'pet-a'],
        [4, 160, 240, 'Pet B', 'Pet B', -1, 'pet-b'],
        [0, 1000, 1000, 'Another Spider', null, -1, 'pet-c']]));
    assert.deepEqual(JSON.parse(f.api.focusPet(['pet-a', 'pet-b', 'pet-a'], true)), { pets: 2, monsters: 2 });
    assert.deepEqual(plain(f.calls.flyBounds[0].bounds.points), [[-20, 10], [-30, 50], [-30, 20], [-60, 40]]);
    assert.deepEqual(plain(f.calls.callbacks), [['OnPetFocused', 'pet-a', 2, 2]]);
    assert.equal(f.focus.getLayers().length, 4);
    f.api.setPlayer('altgard', 10, 20, true, true);
    assert.equal(f.calls.pan.length, 0);
});

test('search exposes each result group and only its existing pet association', () => {
    const f = fixture();
    f.api.setup(data([[0, 40, 80, 'Spider', 'Spinne', -1, 'pet-a'],
        [4, 80, 120, 'Spider', 'Spinne', -1, 'pet-a'],
        [3, 120, 160, 'Spider', 'Spinne'], [2, 160, 200, 'Spider', 'Spinne', -1, null]]));
    const hits = JSON.parse(f.api.search('Spider'));
    assert.deepEqual(hits.map(({ group, petId }) => ({ group, petId })), [
        { group: 'Monsters', petId: 'pet-a' }, { group: 'Pets', petId: 'pet-a' },
        { group: 'Locations', petId: null }, { group: 'Resources', petId: null }
    ]);
    assert.equal(hits.length, 4);
    assert.equal(hits.filter(hit => hit.monster).length, 1);
});

for (const selection of ['monster', 'pet', 'clear']) {
    test('a delayed marker popup cannot reopen after ' + selection + ' selection', () => {
        const f = fixture();
        f.api.setup(data([[2, 40, 80, 'Spiderstone', null], [0, 80, 120, 'Spider', null, -1, 'pet-a']]));
        f.api.flyToMarker(0);
        const pending = f.calls.timers[0];
        if (selection === 'monster') f.api.focusMonster('Spider');
        else if (selection === 'pet') f.api.focusPet('pet-a');
        else f.api.clearFocus();
        assert.equal(pending.cleared, true);
        // A callback already dispatched by the browser must be harmless as well.
        pending.callback();
        assert.equal(f.calls.popups, 0);
    });
}

test('a delayed marker popup cannot reopen after a successful map switch', async () => {
    const f = fixture();
    f.api.setup(data([[2, 40, 80, 'Spiderstone', null]]));
    f.api.setupNextMap();
    f.api.flyToMarker(0);
    const pending = f.calls.timers[0];
    await f.api.show('next', 'en');
    assert.equal(pending.cleared, true);
    pending.callback();
    assert.equal(f.calls.popups, 0);
});

test('a new marker selection cancels the old delayed popup while its own popup still opens', () => {
    const f = fixture();
    f.api.setup(data([[2, 40, 80, 'Spiderstone', null], [3, 80, 120, 'Town', null]]));
    f.api.flyToMarker(0);
    f.api.flyToMarker(1);
    assert.equal(f.calls.timers[0].cleared, true);
    assert.equal(f.calls.timers[1].cleared, false);
    f.calls.timers[0].callback();
    assert.equal(f.calls.popups, 0);
    f.calls.timers[1].callback();
    assert.equal(f.calls.popups, 1);
    assert.match(f.calls.opened[0], /<b>Town<\/b>/);
});

test('non-monster search and marker focus retain individual rows, their original navigation and callback', () => {
    const f = fixture();
    f.api.setup(data([[2, 40, 80, 'Spiderstone', 'Spinnenstein'], [2, 200, 120, 'Spiderstone', 'Spinnenstein']]));
    const hits = JSON.parse(f.api.search('spiderstone'));
    assert.deepEqual(hits, [{ i: 0, name: 'Spiderstone', cat: 'Resources · Gem', group: 'Resources', petId: null },
        { i: 1, name: 'Spiderstone', cat: 'Resources · Gem', group: 'Resources', petId: null }]);
    assert.equal(f.api.flyToMarker(hits[0].i), 'Gem');
    assert.equal(f.calls.flyTo.length, 1);
    assert.deepEqual(plain(f.calls.flyTo[0].at), [-20, 10]);
    assert.equal(f.calls.flyBounds.length + f.calls.fitBounds.length, 0);
    assert.equal(f.calls.timers[0].delay, 600);
    assert.deepEqual(plain(f.calls.callbacks), [['OnNameFocused', 'Spiderstone', 2]]);
});
