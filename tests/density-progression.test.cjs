const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

const symbol = (id, x, y = 0) => [0, x, y, id, '', 0, id];
const monster = (id, x, y = 0, name = id) => [1, x, y, name, '', -1, id];
const data = (markers, metersPerPixel = 1, id = 'altgard') => ({ id, refZoom: 0,
    def: { metersPerPixel }, categories: [{ group: 'Pets' }, { group: 'Monsters' }], markers });

function fixture(markers, metersPerPixel = 1) {
    const calls = [];
    const context = { window: {}, L: { TileLayer: { extend: () => function () {} } } };
    const source = fs.readFileSync(path.join(__dirname, '../src/Soulcrest.App/wwwroot/js/soulcrest-map.js'), 'utf8')
        .replace('return { debugState, init, show,', `return {
            setup(data) { current = data; progressionCandidates = null; player = null;
                map = { getCenter: () => ({lng:0, lat:0}), closePopup() {} };
                dotnet = { invokeMethodAsync(...args) { window.calls.push(args); } }; },
            move(x, y = 0) { player = { getLatLng: () => ({lng:x, lat:-y}) }; updateProgression(); },
            target: () => progressionTarget, cached: () => progressionDensityAreas.get(current),
            debugState, init, show,`);
    context.window.calls = calls;
    vm.runInNewContext(source, context);
    const api = context.window.soulcrestMap;
    api.setup(data(markers, metersPerPixel));
    const select = (counts = {}, { enabled = true, density = true, goal = 105, souls = {}, missing = {}, closest = false, kinds = ['pets'] } = {}) =>
        api.setProgression(enabled, goal, JSON.stringify(souls), JSON.stringify(kinds), closest, '', JSON.stringify(missing), density, JSON.stringify(counts));
    return { api, calls, select };
}

test('map-wide count beats nearby Biruta and still obeys all fixed soul goals', () => {
    const { api, select } = fixture([symbol('biruta-herbalist', 1), symbol('many', 1000)]);
    for (const goal of [5, 30, 105]) {
        select({ 'biruta-herbalist': 4, many: 50 }, { goal });
        assert.equal(api.target().petId, 'many');
        select({ 'biruta-herbalist': 4, many: 50 }, { goal, souls: { many: goal } });
        assert.equal(api.target().petId, 'biruta-herbalist');
        select({ many: 50 }, { goal, souls: { many: goal, 'biruta-herbalist': goal } });
        assert.equal(api.target(), null);
    }
    select({ 'biruta-herbalist': 4, many: 50 }, { souls: { 'biruta-herbalist': 30 } }); // 0/75, total 30
    assert.equal(api.target().petId, 'many');
});

test('south area with 50 spawns wins over five nearby north spawns, callback uses the southern symbol', () => {
    const markers = [symbol('a', 0), symbol('a', 1000),
        ...Array.from({ length: 5 }, (_, i) => monster('a', i)),
        ...Array.from({ length: 50 }, (_, i) => monster('a', 1000 + i))];
    const { api, calls, select } = fixture(markers);
    api.move(0);
    select({ a: 55 });
    assert.equal(api.target().x, 1000);
    assert.deepEqual([...calls.at(-1)], ['OnProgressionTarget', 'altgard', 'a', 1000, 0, 'a']);
    // Movement and changing souls reuse geometry; neither proximity nor an unrelated soul update picks north.
    const cache = api.cached();
    api.move(1);
    select({ a: 55 }, { souls: { a: 40 } });
    assert.equal(api.cached(), cache);
    assert.equal(api.target().x, 1000);
    select({ a: 55 }, { density: false });
    assert.equal(api.target().x, 0);
});

test('pet ranking uses total counts rather than the count in its best 200-metre area', () => {
    const { api, select } = fixture([symbol('a', 1000), symbol('b', 1),
        monster('a', 1000), ...Array.from({ length: 4 }, (_, i) => monster('b', 1 + i))]);
    select({ a: 55, b: 10 });
    assert.equal(api.target().petId, 'a');
});

test('density precedes missing souls, ties use missing souls and then best-area distance', () => {
    const { api, select } = fixture([symbol('a', 1), symbol('b', 1000), symbol('c', 500), symbol('max', 0)]);
    const options = { closest: true, missing: { a: 1, b: 20, c: 20, max: 0 } };
    select({ a: 4, b: 50, c: 50, max: 1000 }, options);
    assert.equal(api.target().petId, 'c');
    select({ a: 50, b: 50, c: 50 }, options);
    assert.equal(api.target().petId, 'a');
    select({ a: 4, b: 50, c: 50 }, { ...options, missing: { a: 1, b: 2, c: 3 } });
    assert.equal(api.target().petId, 'b');
    select({ a: 50, b: 50 }, { closest: true });
    assert.equal(api.target(), null);
});

for (const metersPerPixel of [0.5, 1, 2, 1.992187]) {
    test(`200 game metres includes the boundary and excludes beyond it at ${metersPerPixel} m/px`, () => {
        const radius = 200 / metersPerPixel;
        const { api, select } = fixture([symbol('a', 0), symbol('a', 2000),
            monster('a', radius - 0.001), monster('a', 2000 + radius), monster('a', 2000 + radius + 0.001)], metersPerPixel);
        select({ a: 3 });
        // One spawn per area: centre distance wins. If the outside point were counted, south would win.
        assert.equal(api.target().x, 0);
        api.move(2000);
        assert.equal(api.target().x, 2000); // the boundary point is counted, tying the northern area
    });
}

test('all source monster types count, other pets and extra pet symbols do not', () => {
    const { api, select } = fixture([symbol('a', 0), symbol('a', 1), symbol('a', 2), symbol('a', 1000),
        monster('a', 0), monster('a', 1000, 0, 'Source one'), monster('a', 1005, 0, 'Source two'),
        ...Array.from({ length: 20 }, (_, i) => monster('b', i))]);
    select({ a: 3 });
    assert.equal(api.target().petId, 'a');
    assert.equal(api.target().x, 1000);
});

test('equal density areas follow the player without rebuilding density, distance uses the best area between pets', () => {
    const { api, select } = fixture([symbol('a', 0), symbol('a', 1000), symbol('b', 500),
        monster('a', 0), monster('a', 1000), monster('b', 500)]);
    select({ a: 2, b: 2 });
    assert.equal(api.target().x, 0); // no player: map centre
    const cache = api.cached();
    api.move(1000);
    assert.equal(api.target().x, 1000);
    assert.equal(api.cached(), cache);
    api.move(500);
    assert.equal(api.target().petId, 'b');
});

test('without pet symbols the densest monster-spawn centre is used', () => {
    const { api, select } = fixture([monster('a', 0), monster('a', 1000), monster('a', 1010), monster('a', 1020)]);
    select({ a: 4 });
    assert.equal(api.target().x, 1000);
    assert.equal(api.target().group, 'Monsters');
    api.move(1020);
    assert.equal(api.target().x, 1020);
});

for (const metersPerPixel of [undefined, null, 0, -1, NaN, Infinity]) {
    test(`invalid map scale ${metersPerPixel} retains nearest-symbol guidance`, () => {
        const { api, select } = fixture([symbol('a', 0), symbol('a', 1000), monster('a', 1000), monster('a', 1010)]);
        api.setup(data([symbol('a', 0), symbol('a', 1000), monster('a', 1000), monster('a', 1010)], metersPerPixel));
        // Omitted scale must really be absent rather than use data()'s fixture default.
        if (metersPerPixel === undefined) api.setup({ ...data([symbol('a', 0), symbol('a', 1000), monster('a', 1000), monster('a', 1010)]), def: {} });
        select({ a: 2 });
        assert.equal(api.target().x, 0);
    });
}

test('missing counts stay eligible at zero and equal fixed-goal totals use distance', () => {
    const { api, select } = fixture([symbol('a', 10), symbol('b', 100)]);
    select();
    assert.equal(api.target().petId, 'a');
    select({ a: -10, b: 1 });
    assert.equal(api.target().petId, 'b');
    select({ a: 2, b: 2 });
    assert.equal(api.target().petId, 'a');
    select({ a: 2 }, { enabled: false });
    assert.equal(api.target(), null);
    select({ a: 2 }, { kinds: [] });
    assert.equal(api.target(), null);
});

test('nearer open places compete with distance to the chosen dense area, not another pet symbol', () => {
    const { api, select } = fixture([]);
    api.setup({ ...data([symbol('a', 0), symbol('a', 1000), monster('a', 1000), monster('a', 1010)]),
        categories: [{ group: 'Pets' }, { group: 'Monsters' }, { group: 'Locations' }],
        markers: [symbol('a', 0), symbol('a', 1000), monster('a', 1000), monster('a', 1010), [2, 500, 0, 'Dungeon']] });
    api.setExploration(JSON.stringify([{ index: 4, id: 'd', kind: 'dungeon', done: false }]));
    select({ a: 2 }, { kinds: ['pets', 'dungeon', 'stronghold'] });
    assert.equal(api.target().petId, 'd');
    api.setExploration(JSON.stringify([{ index: 4, id: 'd', kind: 'dungeon', done: true }]));
    select({ a: 2 }, { kinds: ['pets', 'dungeon', 'stronghold'] });
    assert.equal(api.target().x, 1000);
});

test('map switch replaces cached geometry and uses the destination spawn counts', () => {
    const { api, select } = fixture([symbol('a', 0), symbol('b', 1000)]);
    select({ a: 20, b: 5 });
    const old = api.cached();
    api.setup(data([symbol('a', 0), symbol('b', 1000), symbol('b', 2000), monster('b', 2000), monster('b', 2010)], 1, 'verteron'));
    select({ a: 1, b: 50 });
    assert.notEqual(api.cached(), old);
    assert.equal(api.target().petId, 'b');
    assert.equal(api.target().x, 2000);
});


test('existing pet symbols remain the only area centres even if an unmarked monster cluster is denser', () => {
    const { api, select } = fixture([symbol('a', 0), monster('a', 0),
        ...Array.from({ length: 20 }, (_, i) => monster('a', 1000 + i))]);
    api.move(1000);
    select({ a: 21 });
    assert.equal(api.target().group, 'Pets');
    assert.equal(api.target().x, 0);
});

test('the 200-metre neighbourhood is a circle in both coordinates, not a square or chained cluster', () => {
    const { api, select } = fixture([symbol('a', 0, 0), symbol('a', 1000, 1000),
        monster('a', 141, 141), monster('a', 1200, 1200), monster('a', 1300, 1300)]);
    api.move(1000, 1000);
    select({ a: 3 });
    assert.equal(api.target().x, 0);
    assert.equal(api.target().y, 0);
});
